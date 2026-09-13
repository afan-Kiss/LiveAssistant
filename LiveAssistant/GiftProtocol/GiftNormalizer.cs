using Douyin.Live;
using LiveAssistant.Models;

namespace LiveAssistant.GiftProtocol;

/// <summary>
/// 将开源 proto 的 <see cref="GiftMessage"/> 标准化为统一 <see cref="GiftEvent"/>。
/// 所有解析路径必须经过此处，避免字段格式分叉。
/// </summary>
public static class GiftNormalizer
{
    public const string WebcastGiftMethod = "WebcastGiftMessage";

    public static GiftEvent NormalizeGiftEvent(GiftMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);

        var user = message.User ?? message.Common?.User;
        var userId = ResolveUserId(user);
        var nickname = user?.NickName?.Trim() ?? "";
        var giftId = ResolveGiftId(message);
        var giftName = ResolveGiftName(message);
        var diamond = (int)(message.Gift?.DiamondCount ?? 0);
        var repeatCount = (int)message.RepeatCount;
        var count = ResolveCount(message);
        if (count <= 0)
        {
            count = 1;
        }

        var repeatEnd = message.RepeatEnd == 1;
        var groupId = message.GroupId.ToString();
        var timestamp = ResolveTimestamp(message);
        var roomId = message.Common?.RoomId.ToString() ?? "";
        var eventId = ResolveEventId(message, roomId, userId, giftId, timestamp);

        return new GiftEvent
        {
            EventId = eventId,
            UserId = userId,
            Nickname = nickname,
            GiftId = giftId,
            GiftName = giftName,
            Count = count,
            // 单价（钻石）；GiftService 积分公式为 Value * PointsPerValue * Count
            Value = diamond,
            DiamondCount = diamond,
            RepeatCount = repeatCount > 0 ? repeatCount : count,
            GroupId = groupId,
            RepeatEnd = repeatEnd,
            Timestamp = timestamp,
            Time = timestamp.ToLocalTime(),
            CreatedAt = DateTime.Now
        };
    }

    internal static string ResolveUserId(User? user)
    {
        if (user == null)
        {
            return "";
        }

        if (!string.IsNullOrWhiteSpace(user.IdStr))
        {
            return user.IdStr.Trim();
        }

        return user.Id > 0 ? user.Id.ToString() : "";
    }

    internal static string ResolveGiftId(GiftMessage message)
    {
        if (message.GiftId > 0)
        {
            return message.GiftId.ToString();
        }

        if (message.Gift?.Id > 0)
        {
            return message.Gift.Id.ToString();
        }

        return "";
    }

    internal static string ResolveGiftName(GiftMessage message)
    {
        var name = message.Gift?.Name?.Trim();
        if (!string.IsNullOrEmpty(name))
        {
            return name;
        }

        var describe = message.Gift?.Describe?.Trim();
        if (!string.IsNullOrEmpty(describe))
        {
            return describe;
        }

        return "礼物";
    }

    internal static int ResolveCount(GiftMessage message)
    {
        if (message.TotalCount > 0)
        {
            return (int)message.TotalCount;
        }

        if (message.RepeatCount > 0)
        {
            return (int)message.RepeatCount;
        }

        if (message.GroupCount > 0)
        {
            return (int)message.GroupCount;
        }

        if (message.ComboCount > 0)
        {
            return (int)message.ComboCount;
        }

        return 1;
    }

    internal static DateTime ResolveTimestamp(GiftMessage message)
    {
        if (message.SendTime > 0)
        {
            return FromEpoch(message.SendTime);
        }

        if (message.Common?.CreateTime > 0)
        {
            return FromEpoch(message.Common.CreateTime);
        }

        return DateTime.UtcNow;
    }

    internal static string ResolveEventId(GiftMessage message, string roomId, string userId, string giftId, DateTime timestamp)
    {
        if (message.Common?.MsgId > 0)
        {
            return message.Common.MsgId.ToString();
        }

        var ts = timestamp.ToUniversalTime().Ticks;
        return $"{roomId}:{userId}:{giftId}:{ts}";
    }

    private static DateTime FromEpoch(ulong value)
    {
        // Douyin may send seconds or milliseconds.
        if (value > 10_000_000_000UL)
        {
            return DateTimeOffset.FromUnixTimeMilliseconds((long)value).UtcDateTime;
        }

        return DateTimeOffset.FromUnixTimeSeconds((long)value).UtcDateTime;
    }
}
