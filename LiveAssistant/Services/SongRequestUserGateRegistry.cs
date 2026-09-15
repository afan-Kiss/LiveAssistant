using System.Collections.Concurrent;

namespace LiveAssistant.Services;

/// <summary>
/// 点歌用户级锁：用 lease/refCount 保护 Semaphore 生命周期，避免 Prune 与 Acquire 竞态 Dispose。
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
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var entry = _gates.GetOrAdd(userId, _ => new GateEntry());
            if (!entry.TryAddLease())
            {
                // 已被标记删除，换新 entry
                continue;
            }

            try
            {
                await entry.Semaphore.WaitAsync(ct).ConfigureAwait(false);
            }
            catch
            {
                entry.ReleaseLease();
                throw;
            }

            entry.Touch();
            return new ReleaseHandle(this, userId, entry);
        }
    }

    private void Release(string userId, GateEntry entry)
    {
        entry.Touch();
        try
        {
            entry.Semaphore.Release();
        }
        finally
        {
            entry.ReleaseLease();
            PruneIdle();
        }
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

            // 无人持有 lease，且 semaphore 空闲（CurrentCount==1）
            if (!entry.TryBeginDispose())
            {
                continue;
            }

            if (_gates.TryRemove(kv.Key, out var removed) && ReferenceEquals(removed, entry))
            {
                try
                {
                    removed.Semaphore.Dispose();
                }
                catch
                {
                    // ignore
                }
            }
            else
            {
                // 移除失败：恢复，避免泄漏不可用 entry
                entry.CancelDispose();
            }
        }
    }

    private sealed class GateEntry
    {
        private int _leaseCount;
        private int _disposing;

        public SemaphoreSlim Semaphore { get; } = new(1, 1);
        public DateTime LastUsedUtc { get; private set; } = DateTime.UtcNow;

        public void Touch() => LastUsedUtc = DateTime.UtcNow;

        public bool TryAddLease()
        {
            while (true)
            {
                var current = Volatile.Read(ref _leaseCount);
                if (Volatile.Read(ref _disposing) != 0 || current < 0)
                {
                    return false;
                }

                if (Interlocked.CompareExchange(ref _leaseCount, current + 1, current) == current)
                {
                    return true;
                }
            }
        }

        public void ReleaseLease() => Interlocked.Decrement(ref _leaseCount);

        /// <summary>仅当 lease==0 且未在 dispose 时标记为 disposing。</summary>
        public bool TryBeginDispose()
        {
            if (Interlocked.CompareExchange(ref _disposing, 1, 0) != 0)
            {
                return false;
            }

            if (Interlocked.CompareExchange(ref _leaseCount, -1, 0) != 0)
            {
                Volatile.Write(ref _disposing, 0);
                return false;
            }

            // 再次确认 semaphore 无人等待/持有
            if (Semaphore.CurrentCount != 1)
            {
                Volatile.Write(ref _leaseCount, 0);
                Volatile.Write(ref _disposing, 0);
                return false;
            }

            return true;
        }

        public void CancelDispose()
        {
            Volatile.Write(ref _leaseCount, 0);
            Volatile.Write(ref _disposing, 0);
        }
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
