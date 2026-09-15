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

    public SongRequestSession? Get(string userId)
    {
        if (string.IsNullOrWhiteSpace(userId))
        {
            return null;
        }

        lock (_gate)
        {
            PurgeExpiredLocked();
            if (!_sessions.TryGetValue(userId, out var session))
            {
                return null;
            }

            if (session.IsExpired)
            {
                _sessions.Remove(userId);
                return null;
            }

            return session;
        }
    }

    public void Set(SongRequestSession session)
    {
        session.ExpiresAt = DateTime.UtcNow.Add(_ttl);
        lock (_gate)
        {
            PurgeExpiredLocked();
            _sessions[session.UserId] = session;
        }
    }

    public void Clear(string userId)
    {
        if (string.IsNullOrWhiteSpace(userId))
        {
            return;
        }

        lock (_gate)
        {
            _sessions.Remove(userId);
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
