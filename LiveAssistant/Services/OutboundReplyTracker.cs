namespace LiveAssistant.Services;

/// <summary>
/// 记录近期出站回复，供弹幕侧过滤机器人回显。
/// 内容匹配不得单独作为过滤依据，必须配合 msg_id 或登录账号身份。
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

    public void Track(string replyId, string content, string? platformMessageId = null)
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

            var key = NormalizeContent(content);
            if (!string.IsNullOrEmpty(key))
            {
                _contentKeys[key] = now;
            }
        }
    }

    /// <summary>仅当 msg_id / reply_id / platform id 精确命中时判定为机器人回显。</summary>
    public bool MatchesTrackedMessageId(string? msgId)
    {
        if (string.IsNullOrWhiteSpace(msgId))
        {
            return false;
        }

        var now = DateTime.UtcNow;
        lock (_lock)
        {
            PurgeExpired(now);
            return _messageIds.ContainsKey(msgId.Trim());
        }
    }

    /// <summary>内容辅助证据；不得单独用于过滤真人。</summary>
    public bool HasRecentOutboundContent(string content)
    {
        var key = NormalizeContent(content);
        if (string.IsNullOrEmpty(key))
        {
            return false;
        }

        var now = DateTime.UtcNow;
        lock (_lock)
        {
            PurgeExpired(now);
            return _contentKeys.ContainsKey(key);
        }
    }

    /// <summary>
    /// 兼容旧调用：只按消息 id 判定。内容相同不再返回 true。
    /// </summary>
    public bool IsRecentOutbound(string? msgId, string content)
    {
        _ = content;
        return MatchesTrackedMessageId(msgId);
    }

    private void RememberId(string? id, DateTime now)
    {
        if (!string.IsNullOrWhiteSpace(id))
        {
            _messageIds[id.Trim()] = now;
        }
    }

    private void PurgeExpired(DateTime now)
    {
        foreach (var key in _messageIds.Keys.ToList())
        {
            if (now - _messageIds[key] > _ttl)
            {
                _messageIds.Remove(key);
            }
        }

        foreach (var key in _contentKeys.Keys.ToList())
        {
            if (now - _contentKeys[key] > _ttl)
            {
                _contentKeys.Remove(key);
            }
        }
    }

    internal static string NormalizeContent(string content)
    {
        content = content.Trim();
        while (content.StartsWith('@'))
        {
            var space = content.IndexOf(' ');
            if (space < 0)
            {
                break;
            }

            content = content[(space + 1)..].Trim();
        }

        return CollapseWhitespace(content);
    }

    internal static string CollapseWhitespace(string content)
        => string.Concat(content.Where(c => !char.IsWhiteSpace(c)));
}
