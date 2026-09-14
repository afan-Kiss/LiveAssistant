namespace LiveAssistant.Services;

/// <summary>短时去重：同一用户重复发送相同点歌弹幕。</summary>
public sealed class SongRequestDeduper
{
    private readonly TimeSpan _window;
    private readonly object _gate = new();
    private readonly Dictionary<string, DateTime> _recent = new(StringComparer.Ordinal);

    public SongRequestDeduper(TimeSpan? window = null)
    {
        _window = window ?? TimeSpan.FromSeconds(15);
    }

    public bool TryAdmit(string userId, string content)
    {
        var uid = userId?.Trim() ?? "";
        var text = content?.Trim() ?? "";
        if (uid.Length == 0 || text.Length == 0)
        {
            return true;
        }

        var key = $"{uid}|{text}";
        var now = DateTime.UtcNow;
        lock (_gate)
        {
            Prune(now);
            if (_recent.TryGetValue(key, out var seenAt) && now - seenAt < _window)
            {
                return false;
            }

            _recent[key] = now;
            return true;
        }
    }

    private void Prune(DateTime now)
    {
        if (_recent.Count == 0)
        {
            return;
        }

        foreach (var key in _recent.Where(kv => now - kv.Value >= _window).Select(kv => kv.Key).ToList())
        {
            _recent.Remove(key);
        }
    }
}
