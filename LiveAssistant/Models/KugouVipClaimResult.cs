namespace LiveAssistant.Models;

public sealed class KugouVipClaimResult
{
    public bool Claimed { get; init; }
    public bool Upgraded { get; init; }
    public bool AlreadyClaimed { get; init; }
    public string Message { get; init; } = "";
    public string VipLabel { get; init; } = "";
}
