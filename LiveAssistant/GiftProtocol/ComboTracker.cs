using Douyin.Live;
using LiveAssistant.Models;

namespace LiveAssistant.GiftProtocol;

/// <summary>
/// 连击礼物状态跟踪。key = UserId + GiftId + GroupId。
/// repeat_end=0 仅更新累计；repeat_end=1 输出最终事件；超时自动结算。
/// </summary>
public sealed class ComboTracker
{
    private readonly TimeSpan _timeout;
    private readonly Dictionary<string, ComboState> _states = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    public ComboTracker(TimeSpan? timeout = null)
    {
        _timeout = timeout ?? TimeSpan.FromSeconds(10);
    }

    public int ActiveComboCount
    {
        get
        {
            lock (_gate)
            {
                return _states.Count;
            }
        }
    }

    /// <summary>
    /// 处理一条原始 GiftMessage，返回本轮应对外输出的最终 GiftEvent（可能为空）。
    /// </summary>
    public IReadOnlyList<GiftEvent> Process(GiftMessage message, DateTime? utcNow = null)
    {
        ArgumentNullException.ThrowIfNull(message);
        var now = utcNow ?? DateTime.UtcNow;
        var normalized = GiftNormalizer.NormalizeGiftEvent(message);

        lock (_gate)
        {
            var emitted = new List<GiftEvent>();
            emitted.AddRange(FlushExpiredLocked(now));

            var key = BuildKey(normalized.UserId, normalized.GiftId, normalized.GroupId);
            if (string.IsNullOrWhiteSpace(normalized.UserId) || string.IsNullOrWhiteSpace(normalized.GiftId))
            {
                // 无法构成稳定 key 时，直接输出，避免丢失。
                emitted.Add(normalized);
                return emitted;
            }

            if (normalized.RepeatEnd)
            {
                if (_states.TryGetValue(key, out var pending))
                {
                    _states.Remove(key);
                    emitted.Add(Merge(pending.Latest, normalized));
                }
                else
                {
                    emitted.Add(normalized);
                }

                return emitted;
            }

            if (_states.TryGetValue(key, out var existing))
            {
                existing.Latest = PreferNewer(existing.Latest, normalized);
                existing.LastUpdatedUtc = now;
            }
            else
            {
                _states[key] = new ComboState
                {
                    Latest = normalized,
                    LastUpdatedUtc = now
                };
            }

            return emitted;
        }
    }

    /// <summary>
    /// 结算超过超时时间仍未收到 repeat_end 的连击。
    /// </summary>
    public IReadOnlyList<GiftEvent> FlushExpired(DateTime? utcNow = null)
    {
        var now = utcNow ?? DateTime.UtcNow;
        lock (_gate)
        {
            return FlushExpiredLocked(now);
        }
    }

    /// <summary>
    /// 强制结算全部未完成连击（例如断开连接时）。
    /// </summary>
    public IReadOnlyList<GiftEvent> FlushAll()
    {
        lock (_gate)
        {
            if (_states.Count == 0)
            {
                return Array.Empty<GiftEvent>();
            }

            var list = _states.Values.Select(s => s.Latest).ToList();
            _states.Clear();
            return list;
        }
    }

    public static string BuildKey(string userId, string giftId, string groupId)
        => $"{userId}|{giftId}|{groupId}";

    private List<GiftEvent> FlushExpiredLocked(DateTime now)
    {
        if (_states.Count == 0)
        {
            return new List<GiftEvent>();
        }

        var expiredKeys = _states
            .Where(kv => now - kv.Value.LastUpdatedUtc >= _timeout)
            .Select(kv => kv.Key)
            .ToList();

        var result = new List<GiftEvent>(expiredKeys.Count);
        foreach (var key in expiredKeys)
        {
            if (_states.Remove(key, out var state))
            {
                var ev = state.Latest;
                ev.RepeatEnd = true;
                result.Add(ev);
            }
        }

        return result;
    }

    private static GiftEvent PreferNewer(GiftEvent current, GiftEvent incoming)
    {
        // 连击过程中保留更大的累计数量。
        if (incoming.RepeatCount >= current.RepeatCount || incoming.Count >= current.Count)
        {
            return incoming;
        }

        return current;
    }

    private static GiftEvent Merge(GiftEvent pending, GiftEvent finalFrame)
    {
        // 结束帧优先；若结束帧数量偏小则保留过程中的最大累计。
        var count = Math.Max(finalFrame.Count, pending.Count);
        var repeat = Math.Max(finalFrame.RepeatCount, pending.RepeatCount);
        if (repeat > count)
        {
            count = repeat;
        }

        finalFrame.Count = count;
        finalFrame.RepeatCount = repeat;
        finalFrame.RepeatEnd = true;
        if (finalFrame.DiamondCount <= 0 && pending.DiamondCount > 0)
        {
            finalFrame.DiamondCount = pending.DiamondCount;
        }

        // Value 保持单价，与 GiftService 的 Value * Count 公式对齐
        if (finalFrame.DiamondCount > 0)
        {
            finalFrame.Value = finalFrame.DiamondCount;
        }
        else if (pending.Value > finalFrame.Value)
        {
            finalFrame.Value = pending.Value;
        }

        if (string.IsNullOrWhiteSpace(finalFrame.GiftName) || finalFrame.GiftName == "礼物")
        {
            finalFrame.GiftName = pending.GiftName;
        }

        if (string.IsNullOrWhiteSpace(finalFrame.EventId))
        {
            finalFrame.EventId = pending.EventId;
        }

        return finalFrame;
    }

    private sealed class ComboState
    {
        public required GiftEvent Latest { get; set; }
        public DateTime LastUpdatedUtc { get; set; }
    }
}
