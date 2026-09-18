namespace LiveAssistant.Services.AiSpeech;

/// <summary>
/// 点赞累计：按平台分轨，按间隔刷新（最短 20 秒）。
/// </summary>
public sealed class LikeAccumulateBuffer : IDisposable
{
    public readonly record struct LikeBatch(int Count, DateTime FlushedAtUtc, string Platform);

    private readonly object _gate = new();
    private readonly Dictionary<string, (int Count, DateTime WindowStartUtc)> _byPlatform =
        new(StringComparer.Ordinal);
    private System.Threading.Timer? _timer;
    private int _intervalSeconds = 60;
    private int _disposed;

    public event Action<LikeBatch>? Flushed;

    public int IntervalSeconds
    {
        get => _intervalSeconds;
        set => _intervalSeconds = Math.Max(20, Math.Clamp(value, 20, 600));
    }

    public void Add(int count = 1, string platform = "douyin")
    {
        if (count <= 0)
        {
            return;
        }

        platform = NormalizePlatform(platform);
        lock (_gate)
        {
            if (!_byPlatform.TryGetValue(platform, out var state) || state.Count == 0)
            {
                _byPlatform[platform] = (count, DateTime.UtcNow);
            }
            else
            {
                _byPlatform[platform] = (state.Count + count, state.WindowStartUtc);
            }

            EnsureTimer_NoLock();
        }
    }

    public LikeBatch? DrainReady()
    {
        lock (_gate)
        {
            var now = DateTime.UtcNow;
            var interval = TimeSpan.FromSeconds(_intervalSeconds);
            foreach (var kv in _byPlatform.ToList())
            {
                if (kv.Value.Count <= 0)
                {
                    continue;
                }

                if (now - kv.Value.WindowStartUtc < interval)
                {
                    continue;
                }

                _byPlatform.Remove(kv.Key);
                return new LikeBatch(kv.Value.Count, now, kv.Key);
            }

            return null;
        }
    }

    public void FlushNow()
    {
        List<LikeBatch> batches;
        lock (_gate)
        {
            batches = TakeAll_NoLock();
        }

        foreach (var b in batches)
        {
            try { Flushed?.Invoke(b); } catch { /* ignore */ }
        }
    }

    private void EnsureTimer_NoLock()
    {
        _timer ??= new System.Threading.Timer(_ => Tick(), null, 1000, 1000);
    }

    private void Tick()
    {
        List<LikeBatch> batches;
        lock (_gate)
        {
            batches = TakeReady_NoLock();
        }

        foreach (var b in batches)
        {
            try { Flushed?.Invoke(b); } catch { /* ignore */ }
        }
    }

    private List<LikeBatch> TakeReady_NoLock()
    {
        var now = DateTime.UtcNow;
        var interval = TimeSpan.FromSeconds(_intervalSeconds);
        var done = new List<LikeBatch>();
        foreach (var kv in _byPlatform.ToList())
        {
            if (kv.Value.Count <= 0)
            {
                continue;
            }

            if (now - kv.Value.WindowStartUtc < interval)
            {
                continue;
            }

            done.Add(new LikeBatch(kv.Value.Count, now, kv.Key));
            _byPlatform.Remove(kv.Key);
        }

        return done;
    }

    private List<LikeBatch> TakeAll_NoLock()
    {
        var now = DateTime.UtcNow;
        var done = _byPlatform
            .Where(kv => kv.Value.Count > 0)
            .Select(kv => new LikeBatch(kv.Value.Count, now, kv.Key))
            .ToList();
        _byPlatform.Clear();
        return done;
    }

    private static string NormalizePlatform(string? platform)
    {
        var p = (platform ?? "").Trim().ToLowerInvariant();
        return p is "kuaishou" or "ks" ? "kuaishou" : "douyin";
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try { _timer?.Dispose(); } catch { /* ignore */ }
        FlushNow();
    }
}
