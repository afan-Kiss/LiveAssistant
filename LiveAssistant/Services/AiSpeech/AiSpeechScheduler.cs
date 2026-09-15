namespace LiveAssistant.Services.AiSpeech;

/// <summary>
/// 优先级队列：Priority 升序，同优先级按 EnqueuedAt；超限丢最低优最旧。
/// 同类型连续出队限流：避免礼物洪峰饿死弹幕。
/// </summary>
public sealed class AiSpeechScheduler
{
    private readonly object _gate = new();
    private readonly List<AiSpeechTask> _items = new();
    private int _maxSize = 5;
    private int _maxAgeSeconds = 30;
    private int _maxConsecutiveSameKind = 3;
    private long _maxSeen;
    private long _expiredTotal;
    private long _droppedTotal;

    private AiSpeechEventKind? _lastDequeuedKind;
    private int _consecutiveSameKind;

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

    /// <summary>同 Kind 连续出队上限；达到后优先让其它类型进入（默认 3）。</summary>
    public int MaxConsecutiveSameKind
    {
        get { lock (_gate) return _maxConsecutiveSameKind; }
        set { lock (_gate) _maxConsecutiveSameKind = Math.Clamp(value, 1, 20); }
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

    public long ExpiredTotal
    {
        get { lock (_gate) return _expiredTotal; }
    }

    public long DroppedTotal
    {
        get { lock (_gate) return _droppedTotal; }
    }

    /// <summary>当前同类型连续已出队次数（测试/诊断用）。</summary>
    public int ConsecutiveSameKindCount
    {
        get { lock (_gate) return _consecutiveSameKind; }
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

    /// <summary>出队；同时返回本轮过期丢弃数（供指标）。</summary>
    public bool TryDequeue(out AiSpeechTask? task, out int expiredThisCall)
    {
        lock (_gate)
        {
            expiredThisCall = Expire_NoLock();
            if (_items.Count == 0)
            {
                task = null;
                return false;
            }

            var bestIdx = SelectIndex_NoLock();
            task = _items[bestIdx];
            _items.RemoveAt(bestIdx);
            NoteDequeued_NoLock(task.Kind);
            return true;
        }
    }

    public bool TryDequeue(out AiSpeechTask? task)
        => TryDequeue(out task, out _);

    public AiSpeechTask? Peek()
    {
        lock (_gate)
        {
            Expire_NoLock();
            if (_items.Count == 0)
            {
                return null;
            }

            return _items[SelectIndex_NoLock()];
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _items.Clear();
            _lastDequeuedKind = null;
            _consecutiveSameKind = 0;
        }
    }

    /// <summary>重置同类型连出计数（测试用）。</summary>
    public void ResetFairness()
    {
        lock (_gate)
        {
            _lastDequeuedKind = null;
            _consecutiveSameKind = 0;
        }
    }

    private int SelectIndex_NoLock()
    {
        var forceOtherKind = _lastDequeuedKind is { } last
                             && _consecutiveSameKind >= _maxConsecutiveSameKind
                             && _items.Exists(t => t.Kind != last);

        var bestIdx = -1;
        for (var i = 0; i < _items.Count; i++)
        {
            if (forceOtherKind && _items[i].Kind == _lastDequeuedKind)
            {
                continue;
            }

            if (bestIdx < 0 || Compare(_items[i], _items[bestIdx]) < 0)
            {
                bestIdx = i;
            }
        }

        // 理论上 forceOtherKind 时必有异类；兜底回退全量最优
        if (bestIdx < 0)
        {
            bestIdx = 0;
            for (var i = 1; i < _items.Count; i++)
            {
                if (Compare(_items[i], _items[bestIdx]) < 0)
                {
                    bestIdx = i;
                }
            }
        }

        return bestIdx;
    }

    private void NoteDequeued_NoLock(AiSpeechEventKind kind)
    {
        if (_lastDequeuedKind == kind)
        {
            _consecutiveSameKind++;
        }
        else
        {
            _lastDequeuedKind = kind;
            _consecutiveSameKind = 1;
        }
    }

    private int Expire_NoLock()
    {
        var now = DateTime.UtcNow;
        var maxAge = TimeSpan.FromSeconds(_maxAgeSeconds);
        var removed = _items.RemoveAll(t => now - t.EnqueuedAt > maxAge);
        if (removed > 0)
        {
            _expiredTotal += removed;
        }

        return removed;
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
            var a = _items[i];
            var b = _items[worstIdx];
            var cmp = ((int)a.Priority).CompareTo((int)b.Priority);
            if (cmp > 0 || (cmp == 0 && a.EnqueuedAt <= b.EnqueuedAt))
            {
                worstIdx = i;
            }
        }

        _items.RemoveAt(worstIdx);
        _droppedTotal++;
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
