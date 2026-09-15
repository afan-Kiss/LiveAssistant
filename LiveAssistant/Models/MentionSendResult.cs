namespace LiveAssistant.Models;

public sealed class MentionSendResult
{
    public bool Ok { get; init; }
    public int? HttpStatus { get; init; }
    public string ErrorReason { get; init; } = "";
    public string ReplyType { get; init; } = "mention";
}
