namespace LiveAssistant.Models;

public enum AdminCommandType
{
    Skip,
    Pause,
    Resume,
    SetRandomFill,
    SetPlaybackMode,
    DeleteQueueItem,
    PinQueueItem,
    PlayNow,
    ReloadConfig
}

public sealed class AdminCommand
{
    public long Id { get; set; }
    public AdminCommandType Type { get; set; }
    public string? Payload { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.Now;
}
