namespace LiveAssistant.Models;

public sealed class DanmakuItem
{
    public string MsgId { get; set; } = "";
    public string Content { get; set; } = "";
    public string Nickname { get; set; } = "";
    public string UserId { get; set; } = "";
    public string MsgType { get; set; } = "chat";
    public DateTime Timestamp { get; set; } = DateTime.Now;

    public string DisplayLine
    {
        get
        {
            var icon = MsgType switch
            {
                "gift" => "🎁",
                "member" => "👋",
                _ => "😊"
            };
            var text = TruncateSingleLine(Content, 120);
            return $"{icon} {Nickname}：{text}";
        }
    }

    private static string TruncateSingleLine(string text, int max)
    {
        text = text.Replace("\r", " ").Replace("\n", " ").Trim();
        return text.Length <= max ? text : text[..max] + "…";
    }
}
