using LiveAssistant.Config;
using LiveAssistant.Database;
using LiveAssistant.Services;

namespace LiveAssistant.Admin;

public sealed class AdminAppContext
{
    public required ConfigManager Config { get; init; }
    public required LiveAppHost Host { get; init; }
    public required CommandQueueService Commands { get; init; }
    public required SettingsStore Settings { get; init; }
    public required UserRepository Users { get; init; }
    public required GiftRepository Gifts { get; init; }
    public required GiftRuleRepository GiftRules { get; init; }
    public required RandomPoolRepository RandomPool { get; init; }
    public required ReplyTemplateRepository ReplyTemplates { get; init; }
    public required SongBlacklistRepository SongBlacklist { get; init; }
    public required KeywordReplyRepository KeywordReplies { get; init; }
    public required BanVoteRepository BanVotes { get; init; }
    public required LevelPermissionRepository LevelPermissions { get; init; }
}
