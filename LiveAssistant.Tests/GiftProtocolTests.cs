using System.IO.Compression;
using Douyin.Live;
using Google.Protobuf;
using LiveAssistant.GiftProtocol;
using LiveAssistant.Models;
using Xunit;

namespace LiveAssistant.Tests;

public sealed class GiftProtocolTests
{
    [Fact]
    public void SingleGift_ParsesAndEmitsImmediately_OnRepeatEnd()
    {
        var msg = BuildGiftMessage(
            msgId: 1001,
            roomId: 88,
            userId: 11,
            nickname: "Alice",
            giftId: 5655,
            giftName: "小心心",
            diamond: 1,
            repeatCount: 1,
            groupId: 1,
            repeatEnd: 1,
            sendTime: 1_700_000_000UL);

        var pipeline = new GiftProtocolPipeline();
        var events = pipeline.ProcessGiftMessage(msg);

        Assert.Single(events);
        var ev = events[0];
        Assert.Equal("11", ev.UserId);
        Assert.Equal("Alice", ev.Nickname);
        Assert.Equal("5655", ev.GiftId);
        Assert.Equal("小心心", ev.GiftName);
        Assert.Equal(1, ev.Count);
        Assert.Equal(1, ev.RepeatCount);
        Assert.Equal(1, ev.DiamondCount);
        Assert.Equal(1, ev.Value);
        Assert.Equal("1001", ev.EventId);
        Assert.True(ev.RepeatEnd);
        Assert.Equal("1", ev.GroupId);
        Assert.True(ev.Timestamp > DateTime.UnixEpoch);
    }

    [Fact]
    public void ComboGift_UpdatesOnly_UntilRepeatEnd()
    {
        var pipeline = new GiftProtocolPipeline();
        var mid1 = BuildGiftMessage(2001, 1, 22, "Bob", 123, "玫瑰", 5, repeatCount: 3, groupId: 9, repeatEnd: 0);
        var mid2 = BuildGiftMessage(2002, 1, 22, "Bob", 123, "玫瑰", 5, repeatCount: 7, groupId: 9, repeatEnd: 0);
        var end = BuildGiftMessage(2003, 1, 22, "Bob", 123, "玫瑰", 5, repeatCount: 10, groupId: 9, repeatEnd: 1, totalCount: 10);

        Assert.Empty(pipeline.ProcessGiftMessage(mid1));
        Assert.Empty(pipeline.ProcessGiftMessage(mid2));
        Assert.Equal(1, pipeline.Combo.ActiveComboCount);

        var finals = pipeline.ProcessGiftMessage(end);
        Assert.Single(finals);
        Assert.Equal(10, finals[0].Count);
        Assert.Equal(10, finals[0].RepeatCount);
        Assert.Equal(5, finals[0].Value);
        Assert.Equal(5, finals[0].DiamondCount);
        Assert.Equal(0, pipeline.Combo.ActiveComboCount);
    }

    [Fact]
    public void ComboGift_Timeout_AutoSettles()
    {
        var pipeline = new GiftProtocolPipeline(TimeSpan.FromSeconds(10));
        var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var mid = BuildGiftMessage(3001, 1, 33, "Carol", 9, "跑车", 100, repeatCount: 50, groupId: 3, repeatEnd: 0);

        Assert.Empty(pipeline.ProcessGiftMessage(mid, t0));
        Assert.Empty(pipeline.FlushExpired(t0.AddSeconds(9)));

        var settled = pipeline.FlushExpired(t0.AddSeconds(10));
        Assert.Single(settled);
        Assert.Equal(50, settled[0].Count);
        Assert.Equal(50, settled[0].RepeatCount);
        Assert.Equal(100, settled[0].Value);
        Assert.True(settled[0].RepeatEnd);
        Assert.Equal(0, pipeline.Combo.ActiveComboCount);
    }

    [Fact]
    public void LargeComboCount_PreservesQuantity()
    {
        var pipeline = new GiftProtocolPipeline();
        var mid = BuildGiftMessage(4001, 1, 44, "Dan", 777, "嘉年华", 520, repeatCount: 100, groupId: 77, repeatEnd: 0);
        var end = BuildGiftMessage(4002, 1, 44, "Dan", 777, "嘉年华", 520, repeatCount: 100, groupId: 77, repeatEnd: 1, totalCount: 100);

        Assert.Empty(pipeline.ProcessGiftMessage(mid));
        var finals = pipeline.ProcessGiftMessage(end);
        Assert.Single(finals);
        Assert.Equal(100, finals[0].Count);
        Assert.Equal(520, finals[0].Value);
        Assert.Equal(520, finals[0].DiamondCount);
    }

    [Fact]
    public void PushFrame_GzipResponse_ParsesWebcastGiftMessage()
    {
        var gift = BuildGiftMessage(5001, 42, 55, "Eve", 1, "点赞票", 1, 1, 5, 1);
        var frameBytes = BuildPushFrame(gift, gzip: true);

        var parsed = WebcastGiftParser.ParseGiftMessages(frameBytes);
        Assert.True(parsed.Success, parsed.Error);
        Assert.Single(parsed.Gifts);

        var pipeline = new GiftProtocolPipeline();
        var events = pipeline.ProcessPushFrame(frameBytes);
        Assert.Single(events);
        Assert.Equal("55", events[0].UserId);
        Assert.Equal("点赞票", events[0].GiftName);
    }

    [Fact]
    public void ProtobufParseFailure_ReturnsEmpty_DoesNotThrow()
    {
        var pipeline = new GiftProtocolPipeline();
        var events = pipeline.ProcessPushFrame(new byte[] { 0x00, 0x01, 0x02, 0xFF });
        Assert.Empty(events);

        Assert.False(WebcastGiftParser.TryParsePushFrame(new byte[] { 0xFF, 0xFF }, out _, out var error));
        Assert.False(string.IsNullOrWhiteSpace(error));

        Assert.False(WebcastGiftParser.TryParseGiftMessage(new byte[] { 0xAB, 0xCD }, out _, out var giftError));
        Assert.False(string.IsNullOrWhiteSpace(giftError));
    }

    [Fact]
    public void EventId_FallsBack_WhenMsgIdMissing()
    {
        var msg = BuildGiftMessage(0, 99, 66, "Frank", 8, "啤酒", 2, 1, 2, 1, sendTime: 1_700_000_100UL);
        msg.Common.MsgId = 0;
        var ev = GiftNormalizer.NormalizeGiftEvent(msg);
        Assert.StartsWith("99:66:8:", ev.EventId);
    }

    [Fact]
    public void NormalizeGiftEvent_UsesStandardProtoFields()
    {
        var msg = new GiftMessage
        {
            Common = new Common { MsgId = 9, RoomId = 1, CreateTime = 1_700_000_000UL },
            GiftId = 2,
            RepeatCount = 5,
            ComboCount = 5,
            GroupId = 11,
            RepeatEnd = 1,
            User = new User { Id = 7, NickName = "Gina" },
            Gift = new GiftStruct { Id = 2, Name = "粉丝灯牌", DiamondCount = 99 }
        };

        var ev = GiftNormalizer.NormalizeGiftEvent(msg);
        Assert.Equal("2", ev.GiftId);
        Assert.Equal("粉丝灯牌", ev.GiftName);
        Assert.Equal(5, ev.Count);
        Assert.Equal(5, ev.RepeatCount);
        Assert.Equal(99, ev.DiamondCount);
        Assert.Equal(99, ev.Value);
        Assert.Equal("11", ev.GroupId);
        Assert.True(ev.RepeatEnd);
    }

    private static GiftMessage BuildGiftMessage(
        ulong msgId,
        ulong roomId,
        ulong userId,
        string nickname,
        ulong giftId,
        string giftName,
        uint diamond,
        ulong repeatCount,
        ulong groupId,
        uint repeatEnd,
        ulong? totalCount = null,
        ulong? sendTime = null)
    {
        var msg = new GiftMessage
        {
            Common = new Common
            {
                Method = GiftNormalizer.WebcastGiftMethod,
                MsgId = msgId,
                RoomId = roomId,
                CreateTime = sendTime ?? 1_700_000_000UL
            },
            GiftId = giftId,
            RepeatCount = repeatCount,
            ComboCount = repeatCount,
            GroupCount = 1,
            GroupId = groupId,
            RepeatEnd = repeatEnd,
            SendTime = sendTime ?? 0,
            TotalCount = totalCount ?? 0,
            User = new User
            {
                Id = userId,
                NickName = nickname,
                IdStr = userId.ToString()
            },
            Gift = new GiftStruct
            {
                Id = giftId,
                Name = giftName,
                DiamondCount = diamond,
                Combo = true
            }
        };
        return msg;
    }

    private static byte[] BuildPushFrame(GiftMessage gift, bool gzip)
    {
        var message = new Message
        {
            Method = GiftNormalizer.WebcastGiftMethod,
            Payload = gift.ToByteString(),
            MsgId = (long)(gift.Common?.MsgId ?? 0)
        };
        var response = new Response();
        response.MessagesList.Add(message);

        var responseBytes = response.ToByteArray();
        byte[] payload = responseBytes;
        var encoding = "";
        if (gzip)
        {
            using var ms = new MemoryStream();
            using (var gz = new GZipStream(ms, CompressionLevel.SmallestSize, leaveOpen: true))
            {
                gz.Write(responseBytes, 0, responseBytes.Length);
            }

            payload = ms.ToArray();
            encoding = "gzip";
        }

        var frame = new PushFrame
        {
            SeqId = 1,
            LogId = 1,
            PayloadEncoding = encoding,
            PayloadType = "msg",
            Payload = ByteString.CopyFrom(payload)
        };
        return frame.ToByteArray();
    }
}
