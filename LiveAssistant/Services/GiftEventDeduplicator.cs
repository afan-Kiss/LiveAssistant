using LiveAssistant.Models;

namespace LiveAssistant.Services;

/// <summary>
/// GiftEvent.EventId 去重。
/// 内存 TTL + 可选持久层（如 SQLite gift_events），支持进程重启后恢复去重。
/// </summary>
public sealed class GiftEventDeduplicator
{
    private readonly TimeSpan _ttl;
    private readonly Func<string, bool>? _existsInStore;
    private readonly object _gate = new();
    private readonly Dictionary<string, DateTime> _seen = new(StringComparer.Ordinal);

    public GiftEventDeduplicator(TimeSpan? ttl = null, Func<string, bool>? existsInStore = null)
    {
        _ttl = ttl ?? TimeSpan.FromMinutes(30);
        _existsInStore = existsInStore;
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
    /// 若 event_id 在 TTL 内存或持久层已见过，返回 false；否则登记并返回 true。
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

            if (_existsInStore != null && _existsInStore(id))
            {
                _seen[id] = now;
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

        var id = eventId.Trim();
        var now = utcNow ?? DateTime.UtcNow;
        lock (_gate)
        {
            PruneExpiredLocked(now);
            if (_seen.TryGetValue(id, out var seenAt) && now - seenAt < _ttl)
            {
                return true;
            }
        }

        return _existsInStore?.Invoke(id) == true;
    }

    public void ClearMemory()
    {
        lock (_gate)
        {
            _seen.Clear();
        }
    }

    public void Clear() => ClearMemory();

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
