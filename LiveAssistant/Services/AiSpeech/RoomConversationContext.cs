namespace LiveAssistant.Services.AiSpeech;

/// <summary>
/// 直播间近期语义弹幕窗口（跳过 666 等噪音）。
/// </summary>
public sealed class RoomConversationContext
{
    private readonly object _gate = new();
    private readonly LinkedList<RoomMessage> _messages = new();
    private int _maxCount;
    private TimeSpan _window;

    public readonly record struct RoomMessage(string UserId, string Nickname, string Content, DateTime AtUtc);

    public RoomConversationContext(int maxCount = 40, int windowSeconds = 60)
    {
        Configure(maxCount, windowSeconds);
    }

    public void Configure(int maxCount, int windowSeconds)
    {
        _maxCount = Math.Clamp(maxCount, 5, 100);
        _window = TimeSpan.FromSeconds(Math.Clamp(windowSeconds, 10, 600));
    }

    public void Add(string userId, string nickname, string content)
    {
        content = (content ?? "").Trim();
        if (content.Length == 0 || IsNoise(content))
        {
            return;
        }

        lock (_gate)
        {
            var now = DateTime.UtcNow;
            Prune_NoLock(now);
            _messages.AddLast(new RoomMessage(userId ?? "", nickname ?? "", content, now));
            while (_messages.Count > _maxCount)
            {
                _messages.RemoveFirst();
            }
        }
    }

    public IReadOnlyList<RoomMessage> GetRecent(int? max = null)
    {
        lock (_gate)
        {
            Prune_NoLock(DateTime.UtcNow);
            var take = max is > 0 ? Math.Min(max.Value, _messages.Count) : _messages.Count;
            return _messages.Skip(Math.Max(0, _messages.Count - take)).ToList();
        }
    }

    public IReadOnlyList<(string Role, string Content)> AsChatMessages(int? max = null)
    {
        return GetRecent(max)
            .Select(m => ("user", $"{SpeechNameCleaner.Clean(m.Nickname)}: {m.Content}"))
            .ToList();
    }

    public int Count
    {
        get { lock (_gate) { Prune_NoLock(DateTime.UtcNow); return _messages.Count; } }
    }

    public void Clear()
    {
        lock (_gate) _messages.Clear();
    }

    private void Prune_NoLock(DateTime now)
    {
        while (_messages.Count > 0 && now - _messages.First!.Value.AtUtc > _window)
        {
            _messages.RemoveFirst();
        }
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

        // 纯表情/重复字符
        if (t.Distinct().Count() <= 1 && t.Length <= 6)
        {
            return true;
        }

        return false;
    }
}
