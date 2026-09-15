namespace LiveAssistant.Services.AiSpeech;

/// <summary>
/// 内存中的直播间短摘要（可由消息启发式或模型更新）。
/// </summary>
public sealed class RoomContextSummary
{
    private readonly object _gate = new();
    private string _summary = "";
    private int _version;

    public string Summary
    {
        get { lock (_gate) return _summary; }
    }

    public int Version
    {
        get { lock (_gate) return _version; }
    }

    public void SetFromModel(string text)
    {
        text = (text ?? "").Trim();
        lock (_gate)
        {
            _summary = text.Length > 200 ? text[..200] : text;
            _version++;
        }
    }

    public void UpdateFromMessages(IEnumerable<RoomConversationContext.RoomMessage> messages, int maxChars = 120)
    {
        var list = messages?.ToList() ?? new List<RoomConversationContext.RoomMessage>();
        if (list.Count == 0)
        {
            return;
        }

        var topics = list
            .Select(m => m.Content.Trim())
            .Where(c => c.Length >= 2)
            .TakeLast(8)
            .ToList();
        if (topics.Count == 0)
        {
            return;
        }

        var joined = string.Join("；", topics);
        if (joined.Length > maxChars)
        {
            joined = joined[..maxChars] + "…";
        }

        lock (_gate)
        {
            _summary = $"最近在聊：{joined}";
            _version++;
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _summary = "";
            _version++;
        }
    }
}
