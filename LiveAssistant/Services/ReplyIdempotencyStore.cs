namespace LiveAssistant.Services;

/// <summary>
/// 记录已成功发送的 reply_id，保证同一 reply_id 只成功发送一次。
/// </summary>
public sealed class ReplyIdempotencyStore
{
    private readonly TimeSpan _ttl;
    private readonly object _gate = new();
    private readonly Dictionary<string, DateTime> _succeeded = new(StringComparer.Ordinal);

    public ReplyIdempotencyStore(TimeSpan? ttl = null)
    {
        _ttl = ttl ?? TimeSpan.FromMinutes(30);
    }

    public bool HasSucceeded(string? replyId, DateTime? utcNow = null)
    {
        var id = replyId?.Trim() ?? "";
        if (id.Length == 0)
        {
            return false;
        }

        var now = utcNow ?? DateTime.UtcNow;
        lock (_gate)
        {
            PruneLocked(now);
            return _succeeded.TryGetValue(id, out var at) && now - at < _ttl;
        }
    }

    public void MarkSucceeded(string? replyId, DateTime? utcNow = null)
    {
        var id = replyId?.Trim() ?? "";
        if (id.Length == 0)
        {
            return;
        }

        var now = utcNow ?? DateTime.UtcNow;
        lock (_gate)
        {
            PruneLocked(now);
            _succeeded[id] = now;
        }
    }

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _succeeded.Count;
            }
        }
    }

    private void PruneLocked(DateTime now)
    {
        if (_succeeded.Count == 0)
        {
            return;
        }

        var expired = _succeeded
            .Where(kv => now - kv.Value >= _ttl)
            .Select(kv => kv.Key)
            .ToList();
        foreach (var key in expired)
        {
            _succeeded.Remove(key);
        }
    }
}
