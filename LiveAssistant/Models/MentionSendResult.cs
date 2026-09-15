namespace LiveAssistant.Models;

public sealed class MentionSendResult
{
    public bool Ok { get; init; }
    public int? HttpStatus { get; init; }
    public string ErrorReason { get; init; } = "";
    public string ReplyType { get; init; } = "mention";
    /// <summary>侧车若返回平台真实弹幕 id，则填入；多数环境可能为空。</summary>
    public string? PlatformMessageId { get; init; }
}
