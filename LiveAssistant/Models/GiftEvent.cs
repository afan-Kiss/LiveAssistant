namespace LiveAssistant.Models;

public sealed class GiftEvent
{
    public long Id { get; set; }
    public string EventId { get; set; } = "";
    public int Sequence { get; set; }
    public string UserId { get; set; } = "";
    public string Nickname { get; set; } = "";
    public string GiftId { get; set; } = "";
    public string GiftName { get; set; } = "";
    public int Count { get; set; }

    /// <summary>本次礼物总钻石价值（DiamondCount * Count）。</summary>
    public int Value { get; set; }

    public DateTime Time { get; set; } = DateTime.Now;
    public DateTime CreatedAt { get; set; } = DateTime.Now;

    /// <summary>连击累计次数（来自 WebcastGiftMessage.repeat_count）。</summary>
    public int RepeatCount { get; set; }

    /// <summary>单件钻石价值（来自 GiftStruct.diamond_count）。</summary>
    public int DiamondCount { get; set; }

    /// <summary>礼物时间戳（UTC）。优先 send_time / common.create_time。</summary>
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;

    /// <summary>连击分组 ID（来自 group_id）。</summary>
    public string GroupId { get; set; } = "";

    /// <summary>是否连击结束帧（repeat_end == 1）。</summary>
    public bool RepeatEnd { get; set; }

    public static string BuildEventId(string webRid, int sequence, string userId, string giftId, string giftName, int count, string? time)
    {
        if (sequence > 0 && !string.IsNullOrWhiteSpace(webRid))
        {
            return $"{webRid.Trim()}:{sequence}";
        }

        var payload = $"{webRid}|{userId}|{giftId}|{giftName}|{count}|{time ?? ""}";
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(payload)));
    }
}
