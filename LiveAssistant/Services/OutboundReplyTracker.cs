namespace LiveAssistant.Services;

/// <summary>
/// 记录近期出站回复，供弹幕侧过滤机器人回显。
/// </summary>
public sealed class OutboundReplyTracker
{
    private readonly TimeSpan _ttl;
    private readonly object _lock = new();
    private readonly Dictionary<string, DateTime> _replyIds = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTime> _contentKeys = new(StringComparer.Ordinal);

    public OutboundReplyTracker(TimeSpan? ttl = null)
    {
        _ttl = ttl ?? TimeSpan.FromMinutes(2);
    }

    public void Track(string replyId, string content)
    {
        if (string.IsNullOrWhiteSpace(replyId) && string.IsNullOrWhiteSpace(content))
        {
            return;
        }

        var now = DateTime.UtcNow;
        lock (_lock)
        {
            PurgeExpired(now);
            if (!string.IsNullOrWhiteSpace(replyId))
            {
                _replyIds[replyId.Trim()] = now;
            }

            var key = NormalizeContent(content);
            if (!string.IsNullOrEmpty(key))
            {
                _contentKeys[key] = now;
            }
        }
    }

    public bool IsRecentOutbound(string? msgId, string content)
    {
        var now = DateTime.UtcNow;
        lock (_lock)
        {
            PurgeExpired(now);

            if (!string.IsNullOrWhiteSpace(msgId) && _replyIds.ContainsKey(msgId.Trim()))
            {
                return true;
            }

            var key = NormalizeContent(content);
            if (string.IsNullOrEmpty(key))
            {
                return false;
            }

            return _contentKeys.ContainsKey(key);
        }
    }

    private void PurgeExpired(DateTime now)
    {
        foreach (var key in _replyIds.Keys.ToList())
        {
            if (now - _replyIds[key] > _ttl)
            {
                _replyIds.Remove(key);
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
