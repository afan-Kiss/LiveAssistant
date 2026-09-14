using Xunit;
using LiveAssistant.Config;
using LiveAssistant.Database;
using LiveAssistant.Models;
using LiveAssistant.Services;

namespace LiveAssistant.Tests;

public sealed class SongRequestCooldownTests : IDisposable
{
    private readonly string _dataDir;
    private readonly ConfigManager _config;
    private readonly UserRepository _users;
    private readonly SongRequestPermissionService _permission;

    public SongRequestCooldownTests()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), "la_cool_" + Guid.NewGuid().ToString("N"));
        var db = new AppDatabase(_dataDir);
        _config = new ConfigManager();
        _config.Load();
        _config.Settings.Queue.SongRequestCooldownSeconds = 30;
        _users = new UserRepository(db);
        _permission = new SongRequestPermissionService(
            _config,
            _users,
            new QueueService(db),
            new SongBlacklistService(new SongBlacklistRepository(db)),
            new LevelPermissionRepository(db),
            new UserLevelService(_config, _users));
    }

    [Fact]
    public void SecondRequestWithinCooldown_IsRejected()
    {
        var item = new DanmakuItem { UserId = "u1", Nickname = "普通用户", Content = "点歌 测试" };
        var first = _permission.Evaluate(item);
        Assert.True(first.Allowed);

        _users.RecordSuccessfulRequest("u1", "普通用户");

        var second = _permission.Evaluate(item);
        Assert.False(second.Allowed);
        Assert.Equal("songRequestCooldown", second.TemplateKey);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dataDir, true);
        }
        catch
        {
            // ignore
        }
    }
}
