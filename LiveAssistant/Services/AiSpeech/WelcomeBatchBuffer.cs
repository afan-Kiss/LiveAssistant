namespace LiveAssistant.Services.AiSpeech;

/// <summary>
/// 进房昵称批处理：按间隔刷新，最多 N 个名字。
/// </summary>
public sealed class WelcomeBatchBuffer : IDisposable
{
    public readonly record struct WelcomeBatch(IReadOnlyList<string> Nicknames, DateTime FlushedAtUtc);

    private readonly object _gate = new();
    private readonly List<(string UserId, string Nickname)> _pending = new();
    private readonly HashSet<string> _seenIds = new(StringComparer.Ordinal);
    private System.Threading.Timer? _timer;
    private DateTime _windowStartUtc = DateTime.UtcNow;
    private int _intervalSeconds = 20;
    private int _maxNames = 3;
    private int _disposed;

    public event Action<WelcomeBatch>? Flushed;

    public int IntervalSeconds
    {
        get => _intervalSeconds;
        set => _intervalSeconds = Math.Clamp(value, 5, 300);
    }

    public int MaxNames
    {
        get => _maxNames;
        set => _maxNames = Math.Clamp(value, 1, 10);
    }

    public void Add(string userId, string nickname)
    {
        userId = (userId ?? "").Trim();
        nickname = SpeechNameCleaner.Clean(nickname);
        if (nickname.Length == 0)
        {
            return;
        }

        lock (_gate)
        {
            if (userId.Length > 0 && !_seenIds.Add(userId))
            {
                return;
            }

            if (_pending.Count == 0)
            {
                _windowStartUtc = DateTime.UtcNow;
            }

            _pending.Add((userId, nickname));
            EnsureTimer_NoLock();
        }
    }

    public WelcomeBatch? DrainReady()
    {
        lock (_gate)
        {
            if (_pending.Count == 0)
            {
                return null;
            }

            if (DateTime.UtcNow - _windowStartUtc < TimeSpan.FromSeconds(_intervalSeconds)
                && _pending.Count < _maxNames)
            {
                return null;
            }

            return TakeBatch_NoLock();
        }
    }

    public void FlushNow()
    {
        WelcomeBatch? batch;
        lock (_gate)
        {
            batch = _pending.Count == 0 ? null : TakeBatch_NoLock();
        }

        if (batch is { } b)
        {
            try { Flushed?.Invoke(b); } catch { /* ignore */ }
        }
    }

    private void EnsureTimer_NoLock()
    {
        _timer ??= new System.Threading.Timer(_ => Tick(), null, 500, 500);
    }

    private void Tick()
    {
        WelcomeBatch? batch = null;
        lock (_gate)
        {
            if (_pending.Count == 0)
            {
                return;
            }

            if (DateTime.UtcNow - _windowStartUtc >= TimeSpan.FromSeconds(_intervalSeconds)
                || _pending.Count >= _maxNames)
            {
                batch = TakeBatch_NoLock();
            }
        }

        if (batch is { } b)
        {
            try { Flushed?.Invoke(b); } catch { /* ignore */ }
        }
    }

    private WelcomeBatch TakeBatch_NoLock()
    {
        var names = _pending
            .Select(p => p.Nickname)
            .Where(n => n.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .Take(_maxNames)
            .ToList();
        _pending.Clear();
        _seenIds.Clear();
        _windowStartUtc = DateTime.UtcNow;
        return new WelcomeBatch(names, DateTime.UtcNow);
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
