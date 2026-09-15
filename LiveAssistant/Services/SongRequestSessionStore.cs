using LiveAssistant.Models;

namespace LiveAssistant.Services;

public sealed class SongRequestSessionStore
{
    private readonly object _gate = new();
    private readonly Dictionary<string, SongRequestSession> _sessions = new(StringComparer.Ordinal);
    private readonly TimeSpan _ttl;

    public SongRequestSessionStore(TimeSpan? ttl = null)
    {
        _ttl = ttl ?? TimeSpan.FromMinutes(5);
    }

    public SongRequestSession? Get(string webRid, string userId)
        => Get(PendingSongKey.Create(webRid, userId));

    public SongRequestSession? Get(PendingSongKey key)
    {
        if (!key.IsValid)
        {
            return null;
        }

        lock (_gate)
        {
            PurgeExpiredLocked();
            if (!_sessions.TryGetValue(key.StorageKey, out var session))
            {
                return null;
            }

            if (session.IsExpired)
            {
                _sessions.Remove(key.StorageKey);
                return null;
            }

            return session;
        }
    }

    public void Set(SongRequestSession session)
    {
        var key = session.Key;
        if (!key.IsValid)
        {
            return;
        }

        session.ExpiresAt = DateTime.UtcNow.Add(_ttl);
        lock (_gate)
        {
            PurgeExpiredLocked();
            _sessions[key.StorageKey] = session;
        }
    }

    public void Clear(string webRid, string userId)
        => Clear(PendingSongKey.Create(webRid, userId));

    public void Clear(PendingSongKey key)
    {
        if (!key.IsValid)
        {
            return;
        }

        lock (_gate)
        {
            _sessions.Remove(key.StorageKey);
            PurgeExpiredLocked();
        }
    }

    private void PurgeExpiredLocked()
    {
        foreach (var key in _sessions.Where(kv => kv.Value.IsExpired).Select(kv => kv.Key).ToList())
        {
            _sessions.Remove(key);
        }
    }
}
