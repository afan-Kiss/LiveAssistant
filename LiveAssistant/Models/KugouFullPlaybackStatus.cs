namespace LiveAssistant.Models;

public sealed class KugouFullPlaybackStatus
{
    public bool LoggedIn { get; set; }
    public bool FullPlaybackAvailable { get; set; }
    public string Reason { get; set; } = "";
    public string VipLabel { get; set; } = "";
    public DateTime CheckedAtUtc { get; set; } = DateTime.UtcNow;
}
