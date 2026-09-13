using LiveAssistant.Models;

namespace LiveAssistant.Services;

/// <summary>
/// 进程内 GiftEvent.EventId 去重，防止 reconnect / cursor 恢复 / 重试导致重复积分。
/// </summary>
public sealed class GiftEventDeduplicator
{
    private readonly TimeSpan _ttl;
    private readonly object _gate = new();
    private readonly Dictionary<string, DateTime> _seen = new(StringComparer.Ordinal);

    public GiftEventDeduplicator(TimeSpan? ttl = null)
    {
        _ttl = ttl ?? TimeSpan.FromMinutes(30);
    }

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _seen.Count;
            }
        }
    }

    /// <summary>
    /// 若 event_id 在 TTL 内已见过，返回 false；否则登记并返回 true。
    /// </summary>
    public bool TryAdmit(GiftEvent gift, DateTime? utcNow = null)
    {
        ArgumentNullException.ThrowIfNull(gift);
        var id = gift.EventId?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(id))
        {
            return true;
        }

        var now = utcNow ?? DateTime.UtcNow;
        lock (_gate)
        {
            PruneExpiredLocked(now);
            if (_seen.TryGetValue(id, out var seenAt) && now - seenAt < _ttl)
            {
                return false;
            }

            _seen[id] = now;
            return true;
        }
    }

    public bool Contains(string eventId, DateTime? utcNow = null)
    {
        if (string.IsNullOrWhiteSpace(eventId))
        {
            return false;
        }

        var now = utcNow ?? DateTime.UtcNow;
        lock (_gate)
        {
            PruneExpiredLocked(now);
            return _seen.TryGetValue(eventId.Trim(), out var seenAt) && now - seenAt < _ttl;
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _seen.Clear();
        }
    }

    private void PruneExpiredLocked(DateTime now)
    {
        if (_seen.Count == 0)
        {
            return;
        }

        var expired = _seen
            .Where(kv => now - kv.Value >= _ttl)
            .Select(kv => kv.Key)
            .ToList();
        foreach (var key in expired)
        {
            _seen.Remove(key);
        }
    }
}
