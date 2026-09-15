namespace LiveAssistant.Services.AiSpeech;

/// <summary>
/// 点赞累计：按间隔刷新（最短 20 秒）。
/// </summary>
public sealed class LikeAccumulateBuffer : IDisposable
{
    public readonly record struct LikeBatch(int Count, DateTime FlushedAtUtc);

    private readonly object _gate = new();
    private int _count;
    private DateTime _windowStartUtc = DateTime.UtcNow;
    private System.Threading.Timer? _timer;
    private int _intervalSeconds = 60;
    private int _disposed;

    public event Action<LikeBatch>? Flushed;

    public int IntervalSeconds
    {
        get => _intervalSeconds;
        set => _intervalSeconds = Math.Max(20, Math.Clamp(value, 20, 600));
    }

    public void Add(int count = 1)
    {
        if (count <= 0)
        {
            return;
        }

        lock (_gate)
        {
            if (_count == 0)
            {
                _windowStartUtc = DateTime.UtcNow;
            }

            _count += count;
            EnsureTimer_NoLock();
        }
    }

    public LikeBatch? DrainReady()
    {
        lock (_gate)
        {
            if (_count <= 0)
            {
                return null;
            }

            if (DateTime.UtcNow - _windowStartUtc < TimeSpan.FromSeconds(_intervalSeconds))
            {
                return null;
            }

            return Take_NoLock();
        }
    }

    public void FlushNow()
    {
        LikeBatch? batch;
        lock (_gate)
        {
            batch = _count <= 0 ? null : Take_NoLock();
        }

        if (batch is { } b)
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
        LikeBatch? batch = null;
        lock (_gate)
        {
            if (_count > 0 && DateTime.UtcNow - _windowStartUtc >= TimeSpan.FromSeconds(_intervalSeconds))
            {
                batch = Take_NoLock();
            }
        }

        if (batch is { } b)
        {
            try { Flushed?.Invoke(b); } catch { /* ignore */ }
        }
    }

    private LikeBatch Take_NoLock()
    {
        var n = _count;
        _count = 0;
        _windowStartUtc = DateTime.UtcNow;
        return new LikeBatch(n, DateTime.UtcNow);
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
