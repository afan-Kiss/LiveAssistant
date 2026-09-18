namespace LiveAssistant.Services;

/// <summary>
/// 记录近期出站回复，供弹幕侧过滤机器人回显。
/// 内容匹配按房间（RoomKey）隔离，避免抖音/快手互相误杀。
/// </summary>
public sealed class OutboundReplyTracker
{
    private readonly TimeSpan _ttl;
    private readonly object _lock = new();
    private readonly Dictionary<string, DateTime> _messageIds = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTime> _contentKeys = new(StringComparer.Ordinal);

    public OutboundReplyTracker(TimeSpan? ttl = null)
    {
        _ttl = ttl ?? TimeSpan.FromMinutes(2);
    }

    public void Track(string replyId, string content, string? platformMessageId = null, string? roomKey = null)
    {
        if (string.IsNullOrWhiteSpace(replyId)
            && string.IsNullOrWhiteSpace(content)
            && string.IsNullOrWhiteSpace(platformMessageId))
        {
            return;
        }

        var now = DateTime.UtcNow;
        lock (_lock)
        {
            PurgeExpired(now);
            RememberId(replyId, now);
            RememberId(platformMessageId, now);

            var key = ContentKey(roomKey, content);
            if (!string.IsNullOrEmpty(key))
            {
                _contentKeys[key] = now;
            }
        }
    }

    public bool MatchesTrackedMessageId(string? msgId)
    {
        var id = msgId?.Trim() ?? "";
        if (id.Length == 0)
        {
            return false;
        }

        lock (_lock)
        {
            PurgeExpired(DateTime.UtcNow);
            return _messageIds.ContainsKey(id);
        }
    }

    public bool HasRecentOutboundContent(string content, string? roomKey = null)
    {
        var key = ContentKey(roomKey, content);
        if (string.IsNullOrEmpty(key))
        {
            return false;
        }

        lock (_lock)
        {
            PurgeExpired(DateTime.UtcNow);
            return _contentKeys.ContainsKey(key);
        }
    }

    public bool IsRecentOutbound(string? msgId, string content, string? roomKey = null)
    {
        // 仅 msg_id / reply_id 命中；正文匹配留给登录号回显路径显式调用 HasRecentOutboundContent
        _ = content;
        _ = roomKey;
        return MatchesTrackedMessageId(msgId);
    }

    public static string NormalizeContent(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return "";
        }

        var s = content.Replace("\r", "", StringComparison.Ordinal)
            .Replace("\n", "", StringComparison.Ordinal)
            .Trim();
        while (s.Contains("  ", StringComparison.Ordinal))
        {
            s = s.Replace("  ", " ", StringComparison.Ordinal);
        }

        return s;
    }

    private static string ContentKey(string? roomKey, string? content)
    {
        var text = NormalizeContent(content);
        if (text.Length == 0)
        {
            return "";
        }

        var room = (roomKey ?? "").Trim();
        return room.Length == 0 ? text : $"{room}\u001f{text}";
    }

    private void RememberId(string? id, DateTime now)
    {
        var key = id?.Trim() ?? "";
        if (key.Length == 0)
        {
            return;
        }

        _messageIds[key] = now;
    }

    private void PurgeExpired(DateTime now)
    {
        foreach (var dead in _messageIds.Where(kv => now - kv.Value >= _ttl).Select(kv => kv.Key).ToList())
        {
            _messageIds.Remove(dead);
        }

        foreach (var dead in _contentKeys.Where(kv => now - kv.Value >= _ttl).Select(kv => kv.Key).ToList())
        {
            _contentKeys.Remove(dead);
        }
    }
}
