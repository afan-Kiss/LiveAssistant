namespace LiveAssistant.Services.AiSpeech;

/// <summary>
/// 直播间近期语义弹幕窗口（按平台分桶，跳过 666 等噪音）。
/// </summary>
public sealed class RoomConversationContext
{
    private readonly object _gate = new();
    private readonly Dictionary<string, LinkedList<RoomMessage>> _byPlatform = new(StringComparer.Ordinal);
    private int _maxCount;
    private TimeSpan _window;

    public readonly record struct RoomMessage(
        string UserId,
        string Nickname,
        string Content,
        DateTime AtUtc,
        string Platform);

    public RoomConversationContext(int maxCount = 40, int windowSeconds = 60)
    {
        Configure(maxCount, windowSeconds);
    }

    public void Configure(int maxCount, int windowSeconds)
    {
        _maxCount = Math.Clamp(maxCount, 5, 100);
        _window = TimeSpan.FromSeconds(Math.Clamp(windowSeconds, 10, 600));
    }

    public void Add(string userId, string nickname, string content, string? platform = null)
    {
        content = (content ?? "").Trim();
        if (content.Length == 0 || IsNoise(content))
        {
            return;
        }

        var p = NormalizePlatform(platform);
        lock (_gate)
        {
            if (!_byPlatform.TryGetValue(p, out var list))
            {
                list = new LinkedList<RoomMessage>();
                _byPlatform[p] = list;
            }

            var now = DateTime.UtcNow;
            Prune_NoLock(list, now);
            list.AddLast(new RoomMessage(userId ?? "", nickname ?? "", content, now, p));
            while (list.Count > _maxCount)
            {
                list.RemoveFirst();
            }
        }
    }

    public IReadOnlyList<RoomMessage> GetRecent(string? platform = null, int? max = null)
    {
        var p = NormalizePlatform(platform);
        lock (_gate)
        {
            if (!_byPlatform.TryGetValue(p, out var list))
            {
                return Array.Empty<RoomMessage>();
            }

            Prune_NoLock(list, DateTime.UtcNow);
            var take = max is > 0 ? Math.Min(max.Value, list.Count) : list.Count;
            return list.Skip(Math.Max(0, list.Count - take)).ToList();
        }
    }

    public IReadOnlyList<(string Role, string Content)> AsChatMessages(string? platform = null, int? max = null)
    {
        return GetRecent(platform, max)
            .Select(m => ("user", $"{SpeechNameCleaner.Clean(m.Nickname)}: {m.Content}"))
            .ToList();
    }

    public int Count(string? platform = null)
    {
        var p = NormalizePlatform(platform);
        lock (_gate)
        {
            if (!_byPlatform.TryGetValue(p, out var list))
            {
                return 0;
            }

            Prune_NoLock(list, DateTime.UtcNow);
            return list.Count;
        }
    }

    /// <summary>全平台合计（仅诊断）。</summary>
    public int TotalCount
    {
        get
        {
            lock (_gate)
            {
                var now = DateTime.UtcNow;
                var n = 0;
                foreach (var list in _byPlatform.Values)
                {
                    Prune_NoLock(list, now);
                    n += list.Count;
                }

                return n;
            }
        }
    }

    public void Clear(string? platform = null)
    {
        lock (_gate)
        {
            if (string.IsNullOrWhiteSpace(platform))
            {
                _byPlatform.Clear();
                return;
            }

            _byPlatform.Remove(NormalizePlatform(platform));
        }
    }

    private void Prune_NoLock(LinkedList<RoomMessage> list, DateTime now)
    {
        while (list.Count > 0 && now - list.First!.Value.AtUtc > _window)
        {
            list.RemoveFirst();
        }
    }

    private static string NormalizePlatform(string? platform)
    {
        var p = (platform ?? "").Trim().ToLowerInvariant();
        return p is "kuaishou" or "ks" ? "kuaishou" : "douyin";
    }

    public static bool IsNoise(string content)
    {
        var t = content.Trim();
        if (t.Length == 0 || t.Length > 80)
        {
            return true;
        }

        var lower = t.ToLowerInvariant();
        if (lower is "666" or "6666" or "66666" or "233" or "2333" or "hahaha" or "hhhh")
        {
            return true;
        }

        if (t is "哈哈" or "哈哈哈" or "哈哈哈哈" or "哦" or "嗯" or "好" or "来了" or "+1")
        {
            return true;
        }

        if (t.Distinct().Count() <= 1 && t.Length <= 6)
        {
            return true;
        }

        return false;
    }
}
