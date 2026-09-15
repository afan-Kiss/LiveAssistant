namespace LiveAssistant.Services.AiSpeech;

/// <summary>
/// 优先级队列：Priority 升序，同优先级按 EnqueuedAt；超限丢最低优最旧。
/// </summary>
public sealed class AiSpeechScheduler
{
    private readonly object _gate = new();
    private readonly List<AiSpeechTask> _items = new();
    private int _maxSize = 5;
    private int _maxAgeSeconds = 30;
    private long _maxSeen;

    public int MaxSize
    {
        get { lock (_gate) return _maxSize; }
        set { lock (_gate) _maxSize = Math.Clamp(value, 1, 50); }
    }

    public int MaxAgeSeconds
    {
        get { lock (_gate) return _maxAgeSeconds; }
        set { lock (_gate) _maxAgeSeconds = Math.Clamp(value, 5, 300); }
    }

    public int Count
    {
        get
        {
            lock (_gate)
            {
                Expire_NoLock();
                return _items.Count;
            }
        }
    }

    public long PeekMaxSeen
    {
        get { lock (_gate) return _maxSeen; }
    }

    public bool Enqueue(AiSpeechTask task)
    {
        if (task == null)
        {
            return false;
        }

        lock (_gate)
        {
            Expire_NoLock();
            _items.Add(task);
            _maxSeen = Math.Max(_maxSeen, _items.Count);
            while (_items.Count > _maxSize)
            {
                DropLowest_NoLock();
            }

            return true;
        }
    }

    public bool TryDequeue(out AiSpeechTask? task)
    {
        lock (_gate)
        {
            Expire_NoLock();
            if (_items.Count == 0)
            {
                task = null;
                return false;
            }

            var bestIdx = 0;
            for (var i = 1; i < _items.Count; i++)
            {
                if (Compare(_items[i], _items[bestIdx]) < 0)
                {
                    bestIdx = i;
                }
            }

            task = _items[bestIdx];
            _items.RemoveAt(bestIdx);
            return true;
        }
    }

    public AiSpeechTask? Peek()
    {
        lock (_gate)
        {
            Expire_NoLock();
            if (_items.Count == 0)
            {
                return null;
            }

            var best = _items[0];
            for (var i = 1; i < _items.Count; i++)
            {
                if (Compare(_items[i], best) < 0)
                {
                    best = _items[i];
                }
            }

            return best;
        }
    }

    public void Clear()
    {
        lock (_gate) _items.Clear();
    }

    private void Expire_NoLock()
    {
        var now = DateTime.UtcNow;
        var maxAge = TimeSpan.FromSeconds(_maxAgeSeconds);
        _items.RemoveAll(t => now - t.EnqueuedAt > maxAge);
    }

    private void DropLowest_NoLock()
    {
        if (_items.Count == 0)
        {
            return;
        }

        var worstIdx = 0;
        for (var i = 1; i < _items.Count; i++)
        {
            // 丢弃优先级更差（数值更大）且更旧的
            var a = _items[i];
            var b = _items[worstIdx];
            var cmp = ((int)a.Priority).CompareTo((int)b.Priority);
            if (cmp > 0 || (cmp == 0 && a.EnqueuedAt <= b.EnqueuedAt))
            {
                worstIdx = i;
            }
        }

        _items.RemoveAt(worstIdx);
    }

    private static int Compare(AiSpeechTask a, AiSpeechTask b)
    {
        var p = ((int)a.Priority).CompareTo((int)b.Priority);
        if (p != 0)
        {
            return p;
        }

        return a.EnqueuedAt.CompareTo(b.EnqueuedAt);
    }
}
