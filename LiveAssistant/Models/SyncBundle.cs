using LiveAssistant.Config;

namespace LiveAssistant.Models;

public sealed class SyncBundle
{
    public string Version { get; set; } = "";
    public AppSettings Settings { get; set; } = new();
    public Dictionary<string, string> ReplyTemplates { get; set; } = new();
    public List<RandomPoolItem> RandomPool { get; set; } = new();
    public List<GiftRule> GiftRules { get; set; } = new();
    public SongRequestPolicySettings SongRequestPolicy { get; set; } = new();
    public List<LevelPermission> LevelPermissions { get; set; } = new();
}
