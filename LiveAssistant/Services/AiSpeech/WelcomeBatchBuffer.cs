namespace LiveAssistant.Services.AiSpeech;

/// <summary>
/// 进房昵称批处理：按平台分轨（独立窗口与人数上限），按间隔刷新。
/// </summary>
public sealed class WelcomeBatchBuffer : IDisposable
{
    public readonly record struct WelcomeBatch(
        IReadOnlyList<string> Nicknames,
        DateTime FlushedAtUtc,
        string Platform);

    private sealed class PlatformBucket
    {
        public List<(string UserId, string Nickname)> Pending { get; } = new();
        public HashSet<string> SeenIds { get; } = new(StringComparer.Ordinal);
        public DateTime WindowStartUtc { get; set; } = DateTime.UtcNow;
    }

    private readonly object _gate = new();
    private readonly Dictionary<string, PlatformBucket> _byPlatform = new(StringComparer.Ordinal);
    private System.Threading.Timer? _timer;
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

    public void Add(string userId, string nickname, string platform = "douyin")
        => TryAdd(userId, nickname, platform, out _);

    /// <summary>返回 false 时 skipReason：empty_nickname / duplicate_user。</summary>
    public bool TryAdd(string userId, string nickname, string platform, out string skipReason)
    {
        userId = (userId ?? "").Trim();
        nickname = SpeechNameCleaner.Clean(nickname);
        platform = NormalizePlatform(platform);
        if (nickname.Length == 0)
        {
            skipReason = "empty_nickname";
            return false;
        }

        lock (_gate)
        {
            if (!_byPlatform.TryGetValue(platform, out var bucket))
            {
                bucket = new PlatformBucket();
                _byPlatform[platform] = bucket;
            }

            if (userId.Length > 0 && !bucket.SeenIds.Add(userId))
            {
                skipReason = "duplicate_user";
                return false;
            }

            if (bucket.Pending.Count == 0)
            {
                bucket.WindowStartUtc = DateTime.UtcNow;
            }

            bucket.Pending.Add((userId, nickname));
            EnsureTimer_NoLock();
        }

        skipReason = "";
        return true;
    }

    /// <summary>兼容旧签名。</summary>
    public bool TryAdd(string userId, string nickname, out string skipReason)
        => TryAdd(userId, nickname, "douyin", out skipReason);

    /// <summary>取出一个已到期平台批次；不会丢弃其它平台。</summary>
    public WelcomeBatch? DrainReady()
    {
        lock (_gate)
        {
            var ready = TakeReady_NoLock(takeAllReady: false);
            return ready.Count > 0 ? ready[0] : null;
        }
    }

    public void FlushNow()
    {
        List<WelcomeBatch> batches;
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
        _timer ??= new System.Threading.Timer(_ => Tick(), null, 500, 500);
    }

    private void Tick()
    {
        List<WelcomeBatch> batches;
        lock (_gate)
        {
            batches = TakeReady_NoLock(takeAllReady: true);
        }

        foreach (var b in batches)
        {
            try { Flushed?.Invoke(b); } catch { /* ignore */ }
        }
    }

    private WelcomeBatch TakePlatformBatch_NoLock(string platform, PlatformBucket bucket)
    {
        var names = bucket.Pending
            .Select(p => p.Nickname)
            .Where(n => n.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .Take(_maxNames)
            .ToList();
        bucket.Pending.Clear();
        bucket.SeenIds.Clear();
        bucket.WindowStartUtc = DateTime.UtcNow;
        return new WelcomeBatch(names, DateTime.UtcNow, platform);
    }

    private List<WelcomeBatch> TakeReady_NoLock(bool takeAllReady)
    {
        var now = DateTime.UtcNow;
        var interval = TimeSpan.FromSeconds(_intervalSeconds);
        var result = new List<WelcomeBatch>();

        foreach (var kv in _byPlatform.ToList())
        {
            var platform = kv.Key;
            var bucket = kv.Value;
            if (bucket.Pending.Count == 0)
            {
                continue;
            }

            var due = now - bucket.WindowStartUtc >= interval
                      || bucket.Pending.Count >= _maxNames;
            if (!due)
            {
                continue;
            }

            var batch = TakePlatformBatch_NoLock(platform, bucket);
            if (batch.Nicknames.Count == 0)
            {
                continue;
            }

            result.Add(batch);
            if (!takeAllReady)
            {
                break;
            }
        }

        return result;
    }

    private List<WelcomeBatch> TakeAll_NoLock()
    {
        var result = new List<WelcomeBatch>();
        foreach (var kv in _byPlatform.ToList())
        {
            if (kv.Value.Pending.Count == 0)
            {
                continue;
            }

            var batch = TakePlatformBatch_NoLock(kv.Key, kv.Value);
            if (batch.Nicknames.Count == 0)
            {
                continue;
            }

            result.Add(batch);
        }

        return result;
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
