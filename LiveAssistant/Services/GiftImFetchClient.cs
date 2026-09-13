using System.Net;
using Douyin.Live;
using LiveAssistant.GiftProtocol;

namespace LiveAssistant.Services;

/// <summary>
/// 礼物专用 Douyin webcast/im/fetch 客户端（不处理弹幕/点赞/进场）。
/// </summary>
public sealed class GiftImFetchClient
{
    public const string LiveHost = "https://live.douyin.com";

    private readonly HttpClient _http;

    public GiftImFetchClient(HttpClient? http = null)
    {
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
    }

    public async Task<ImFetchResult> FetchAsync(
        string roomId,
        string webRid,
        string cookie,
        string userUniqueId,
        string cursor,
        string internalExt,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(roomId))
        {
            throw new ArgumentException("room_id required", nameof(roomId));
        }

        if (string.IsNullOrWhiteSpace(cookie) || !SidecarCookieStore.HasSessionId(cookie))
        {
            throw new CookieInvalidException("Cookie 无效或缺少 sessionid");
        }

        var url = BuildUrl(roomId, userUniqueId, cursor, internalExt);
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.TryAddWithoutValidation("User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
        req.Headers.TryAddWithoutValidation("Referer", $"{LiveHost}/{webRid.Trim()}");
        req.Headers.TryAddWithoutValidation("Cookie", cookie);

        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        var body = await resp.Content.ReadAsByteArrayAsync(ct);

        if (resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            throw new CookieInvalidException($"im/fetch HTTP {(int)resp.StatusCode}");
        }

        if ((int)resp.StatusCode >= 400)
        {
            throw new HttpRequestException($"im/fetch HTTP {(int)resp.StatusCode}");
        }

        var parsed = WebcastGiftParser.ParseImFetchBody(body);
        if (!parsed.Success)
        {
            throw new InvalidDataException(parsed.Error ?? "im/fetch protobuf parse failed");
        }

        var nextCursor = parsed.Response?.Cursor ?? cursor;
        var nextExt = parsed.Response?.InternalExt ?? internalExt;
        return new ImFetchResult(parsed.Gifts, nextCursor, nextExt, parsed.Response);
    }

    public static string BuildUrl(string roomId, string userUniqueId, string cursor, string internalExt)
    {
        var q = new List<string>
        {
            "resp_content_type=protobuf",
            "did_rule=3",
            "device_id=",
            "app_name=douyin_web",
            "endpoint=live_pc",
            "support_wrds=1",
            "user_unique_id=" + Uri.EscapeDataString(userUniqueId),
            "identity=audience",
            "need_persist_msg_count=50",
            "insert_task_id=",
            "live_reason=",
            "room_id=" + Uri.EscapeDataString(roomId),
            "version_code=180800",
            "last_rtt=0",
            "live_id=1",
            "aid=6383",
            "fetch_rule=1",
            "cursor=" + Uri.EscapeDataString(cursor ?? ""),
            "internal_ext=" + Uri.EscapeDataString(internalExt ?? ""),
            "device_platform=web",
            "cookie_enabled=true",
            "screen_width=1920",
            "screen_height=1080",
            "browser_language=zh-CN",
            "browser_platform=Win32",
            "browser_name=Mozilla",
            "browser_version=5.0%20(Windows)",
            "browser_online=true",
            "tz_name=Asia/Shanghai"
        };
        return $"{LiveHost}/webcast/im/fetch/?{string.Join("&", q)}";
    }

    public static string NewUserUniqueId()
    {
        // 观众侧匿名 ID；无需登录 UID。
        var r = Random.Shared.NextInt64(1_000_000_000_000_000_000L, long.MaxValue);
        return unchecked((ulong)r).ToString();
    }

    public readonly record struct ImFetchResult(
        IReadOnlyList<GiftMessage> Gifts,
        string Cursor,
        string InternalExt,
        Response? Response);
}

public sealed class CookieInvalidException : Exception
{
    public CookieInvalidException(string message) : base(message) { }
}
