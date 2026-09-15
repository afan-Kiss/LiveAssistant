using System.Collections.Concurrent;

namespace LiveAssistant.Services;

/// <summary>
/// 点歌用户级锁：空闲且无等待时自动释放 Semaphore，避免 24 小时直播内存累积。
/// </summary>
public sealed class SongRequestUserGateRegistry
{
    private readonly TimeSpan _idleTtl;
    private readonly ConcurrentDictionary<string, GateEntry> _gates = new(StringComparer.Ordinal);

    public SongRequestUserGateRegistry(TimeSpan? idleTtl = null)
    {
        _idleTtl = idleTtl ?? TimeSpan.FromMinutes(10);
    }

    internal int ActiveCount => _gates.Count;

    public async Task<IDisposable> AcquireAsync(string userId, CancellationToken ct)
    {
        var entry = _gates.GetOrAdd(userId, _ => new GateEntry());
        await entry.Semaphore.WaitAsync(ct);
        entry.Touch();
        return new ReleaseHandle(this, userId, entry);
    }

    private void Release(string userId, GateEntry entry)
    {
        entry.Touch();
        entry.Semaphore.Release();
        PruneIdle();
    }

    private void PruneIdle()
    {
        var cutoff = DateTime.UtcNow - _idleTtl;
        foreach (var kv in _gates)
        {
            var entry = kv.Value;
            if (entry.LastUsedUtc >= cutoff)
            {
                continue;
            }

            // CurrentCount==1 表示无人持有、无人等待
            if (entry.Semaphore.CurrentCount != 1)
            {
                continue;
            }

            if (_gates.TryRemove(kv.Key, out var removed))
            {
                removed.Semaphore.Dispose();
            }
        }
    }

    private sealed class GateEntry
    {
        public SemaphoreSlim Semaphore { get; } = new(1, 1);
        public DateTime LastUsedUtc { get; private set; } = DateTime.UtcNow;

        public void Touch() => LastUsedUtc = DateTime.UtcNow;
    }

    private sealed class ReleaseHandle : IDisposable
    {
        private readonly SongRequestUserGateRegistry _registry;
        private readonly string _userId;
        private readonly GateEntry _entry;
        private int _released;

        public ReleaseHandle(SongRequestUserGateRegistry registry, string userId, GateEntry entry)
        {
            _registry = registry;
            _userId = userId;
            _entry = entry;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) != 0)
            {
                return;
            }

            _registry.Release(_userId, _entry);
        }
    }
}
