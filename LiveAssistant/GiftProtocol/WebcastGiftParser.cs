using System.IO.Compression;
using Douyin.Live;
using Google.Protobuf;
using LiveAssistant.Models;

namespace LiveAssistant.GiftProtocol;

/// <summary>
/// 抖音直播 WebSocket / IM payload 礼物解析：
/// PushFrame → (gzip) Response → Message → GiftMessage → GiftEvent。
/// 依赖 https://github.com/opedium/douyin-live-proto，不手写字段号。
/// </summary>
public static class WebcastGiftParser
{
    public static bool TryParsePushFrame(byte[] data, out PushFrame? frame, out string? error)
    {
        frame = null;
        error = null;
        if (data == null || data.Length == 0)
        {
            error = "empty payload";
            return false;
        }

        try
        {
            frame = PushFrame.Parser.ParseFrom(data);
            return true;
        }
        catch (InvalidProtocolBufferException ex)
        {
            error = ex.Message;
            return false;
        }
    }

    public static bool TryParseResponse(byte[] payload, string? payloadEncoding, out Response? response, out string? error)
    {
        response = null;
        error = null;
        if (payload == null || payload.Length == 0)
        {
            error = "empty response payload";
            return false;
        }

        try
        {
            var bytes = MaybeGunzip(payload, payloadEncoding);
            response = Response.Parser.ParseFrom(bytes);
            return true;
        }
        catch (InvalidProtocolBufferException ex)
        {
            error = ex.Message;
            return false;
        }
        catch (InvalidDataException ex)
        {
            error = ex.Message;
            return false;
        }
    }

    public static bool TryParseGiftMessage(byte[] payload, out GiftMessage? gift, out string? error)
    {
        gift = null;
        error = null;
        if (payload == null || payload.Length == 0)
        {
            error = "empty gift payload";
            return false;
        }

        try
        {
            gift = GiftMessage.Parser.ParseFrom(payload);
            return true;
        }
        catch (InvalidProtocolBufferException ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// 解析 im/fetch HTTP 响应体：可能是 gzip、PushFrame 或裸 Response。
    /// 仅提取 WebcastGiftMessage。
    /// </summary>
    public static ParseResult ParseImFetchBody(byte[] body)
    {
        if (body == null || body.Length == 0)
        {
            return ParseResult.Fail("empty im/fetch body");
        }

        byte[] working;
        try
        {
            working = MaybeGunzip(body, null);
        }
        catch (InvalidDataException ex)
        {
            return ParseResult.Fail("gzip decompress failed: " + ex.Message);
        }

        // PushFrame 优先；嵌套 Response 失败时回退裸 Response（避免误识别导致整轮失败）
        if (TryParsePushFrame(working, out var frame, out _) && frame != null && frame.Payload.Length > 0)
        {
            if (string.Equals(frame.PayloadType, "hb", StringComparison.OrdinalIgnoreCase))
            {
                return ParseResult.Ok(Array.Empty<GiftMessage>(), frame, null);
            }

            if (TryParseResponse(frame.Payload.ToByteArray(), frame.PayloadEncoding, out var nested, out _) && nested != null)
            {
                return ParseResult.Ok(ExtractGifts(nested), frame, nested);
            }
        }

        if (TryParseResponse(working, null, out var response, out var error) && response != null)
        {
            return ParseResult.Ok(ExtractGifts(response), null, response);
        }

        // 若 gunzip 过仍失败，再试原始 body（兼容误判 gzip）
        if (!ReferenceEquals(working, body)
            && TryParseResponse(body, null, out var rawResp, out _) && rawResp != null)
        {
            return ParseResult.Ok(ExtractGifts(rawResp), null, rawResp);
        }

        return ParseResult.Fail(error ?? "im/fetch body parse failed");
    }

    /// <summary>
    /// 解析 PushFrame 字节流中的全部礼物消息（不做连击合并）。
    /// </summary>
    public static ParseResult ParseGiftMessages(byte[] pushFrameBytes)
        => ParseImFetchBody(pushFrameBytes);

    private static IReadOnlyList<GiftMessage> ExtractGifts(Response response)
    {
        var gifts = new List<GiftMessage>();
        foreach (var message in response.MessagesList)
        {
            if (!IsGiftMethod(message.Method))
            {
                continue;
            }

            if (!TryParseGiftMessage(message.Payload.ToByteArray(), out var gift, out _) || gift == null)
            {
                continue;
            }

            gifts.Add(gift);
        }

        return gifts;
    }

    /// <summary>
    /// 解析并标准化为 <see cref="GiftEvent"/>（不做连击合并）。
    /// </summary>
    public static IReadOnlyList<GiftEvent> NormalizeGiftsFromPushFrame(byte[] pushFrameBytes)
    {
        var parsed = ParseGiftMessages(pushFrameBytes);
        if (!parsed.Success)
        {
            return Array.Empty<GiftEvent>();
        }

        return parsed.Gifts.Select(GiftNormalizer.NormalizeGiftEvent).ToList();
    }

    public static bool IsGiftMethod(string? method)
        => string.Equals(method, GiftNormalizer.WebcastGiftMethod, StringComparison.Ordinal);

    internal static byte[] MaybeGunzip(byte[] payload, string? encoding)
    {
        var looksGzip = payload.Length >= 2 && payload[0] == 0x1F && payload[1] == 0x8B;
        var encodingGzip = !string.IsNullOrWhiteSpace(encoding)
            && encoding.Contains("gzip", StringComparison.OrdinalIgnoreCase);

        if (!looksGzip && !encodingGzip)
        {
            return payload;
        }

        try
        {
            using var input = new MemoryStream(payload);
            using var gzip = new GZipStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            gzip.CopyTo(output);
            return output.ToArray();
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException)
        {
            // 明确 gzip 却解压失败：不要把压缩字节再当 protobuf 解析
            throw new InvalidDataException("gzip payload decompress failed", ex);
        }
    }

    public sealed class ParseResult
    {
        public bool Success { get; init; }
        public string? Error { get; init; }
        public PushFrame? Frame { get; init; }
        public Response? Response { get; init; }
        public IReadOnlyList<GiftMessage> Gifts { get; init; } = Array.Empty<GiftMessage>();

        public static ParseResult Ok(IReadOnlyList<GiftMessage> gifts, PushFrame? frame, Response? response)
            => new() { Success = true, Gifts = gifts, Frame = frame, Response = response };

        public static ParseResult Fail(string error, PushFrame? frame = null)
            => new() { Success = false, Error = error, Frame = frame };
    }
}
