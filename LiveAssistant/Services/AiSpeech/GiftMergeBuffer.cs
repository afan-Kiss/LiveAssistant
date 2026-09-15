namespace LiveAssistant.Services.AiSpeech;

/// <summary>
/// 同用户同礼物在窗口内合并件数。
/// </summary>
public sealed class GiftMergeBuffer : IDisposable
{
    public readonly record struct GiftMergeItem(
        string UserId,
        string Nickname,
        string GiftName,
        int Count,
        DateTime FirstAtUtc,
        DateTime LastAtUtc);

    private readonly object _gate = new();
    private readonly Dictionary<string, GiftMergeItem> _pending = new(StringComparer.Ordinal);
    private readonly List<GiftMergeItem> _ready = new();
    private System.Threading.Timer? _timer;
    private int _mergeSeconds = 3;
    private int _disposed;

    public event Action<GiftMergeItem>? Flushed;

    public int MergeSeconds
    {
        get => _mergeSeconds;
        set => _mergeSeconds = Math.Clamp(value, 1, 30);
    }

    public void Add(string userId, string nickname, string giftName, int count = 1)
    {
        userId = (userId ?? "").Trim();
        giftName = (giftName ?? "").Trim();
        if (userId.Length == 0 || giftName.Length == 0)
        {
            return;
        }

        count = Math.Max(1, count);
        var key = $"{userId}|{giftName}";
        var now = DateTime.UtcNow;
        lock (_gate)
        {
            if (_pending.TryGetValue(key, out var existing))
            {
                _pending[key] = existing with
                {
                    Nickname = string.IsNullOrWhiteSpace(nickname) ? existing.Nickname : nickname,
                    Count = existing.Count + count,
                    LastAtUtc = now
                };
            }
            else
            {
                _pending[key] = new GiftMergeItem(userId, nickname ?? "", giftName, count, now, now);
            }

            EnsureTimer_NoLock();
        }
    }

    public IReadOnlyList<GiftMergeItem> DrainReady()
    {
        lock (_gate)
        {
            FlushExpired_NoLock(DateTime.UtcNow);
            if (_ready.Count == 0)
            {
                return Array.Empty<GiftMergeItem>();
            }

            var list = _ready.ToList();
            _ready.Clear();
            return list;
        }
    }

    public void FlushAll()
    {
        List<GiftMergeItem> items;
        lock (_gate)
        {
            items = _pending.Values.ToList();
            _pending.Clear();
            _ready.AddRange(items);
        }

        foreach (var item in items)
        {
            try { Flushed?.Invoke(item); } catch { /* ignore */ }
        }
    }

    private void EnsureTimer_NoLock()
    {
        _timer ??= new System.Threading.Timer(_ => Tick(), null, 400, 400);
    }

    private void Tick()
    {
        List<GiftMergeItem> flushed;
        lock (_gate)
        {
            flushed = FlushExpired_NoLock(DateTime.UtcNow);
        }

        foreach (var item in flushed)
        {
            try { Flushed?.Invoke(item); } catch { /* ignore */ }
        }
    }

    private List<GiftMergeItem> FlushExpired_NoLock(DateTime now)
    {
        var window = TimeSpan.FromSeconds(_mergeSeconds);
        var done = new List<GiftMergeItem>();
        foreach (var kv in _pending.ToList())
        {
            if (now - kv.Value.FirstAtUtc >= window)
            {
                done.Add(kv.Value);
                _pending.Remove(kv.Key);
                _ready.Add(kv.Value);
            }
        }

        return done;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try { _timer?.Dispose(); } catch { /* ignore */ }
        FlushAll();
    }
}
