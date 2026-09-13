namespace LiveAssistant.Models;

public sealed class KeywordReplyRule
{
    public long Id { get; set; }
    public string Keyword { get; set; } = "";
    public string TemplateKey { get; set; } = "";
    public string ReplyContent { get; set; } = "";
    public bool Enabled { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.Now;
}
