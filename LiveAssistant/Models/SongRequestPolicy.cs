using System.Text.Json.Serialization;

namespace LiveAssistant.Models;

public enum SongRequestPolicyMode
{
    Free,
    Points,
    GiftUnlock
}

public sealed class SongRequestPolicySettings
{
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public SongRequestPolicyMode Mode { get; set; } = SongRequestPolicyMode.Free;
    public int PointsCost { get; set; } = 10;
    public int GiftUnlockMinPoints { get; set; } = 100;
    public int MinLevel { get; set; }
    public string RequiredGiftName { get; set; } = "";
}

public sealed class LevelPermission
{
    public int Level { get; set; }
    public bool CanRequest { get; set; } = true;
    public int CooldownSeconds { get; set; } = 30;
    public int MinPoints { get; set; }
    public int QueuePriority { get; set; }
    public int PointsCostOverride { get; set; } = -1;
}
