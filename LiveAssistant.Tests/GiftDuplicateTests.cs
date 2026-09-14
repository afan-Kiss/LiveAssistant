using Xunit;
using LiveAssistant.Config;
using LiveAssistant.Database;
using LiveAssistant.Models;
using LiveAssistant.Services;

namespace LiveAssistant.Tests;

public sealed class GiftDuplicateTests : IDisposable
{
    private readonly string _dataDir;
    private readonly AppDatabase _db;
    private readonly GiftRepository _gifts;
    private readonly UserRepository _users;
    private readonly GiftService _giftService;

    public GiftDuplicateTests()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), "la_test_" + Guid.NewGuid().ToString("N"));
        _db = new AppDatabase(_dataDir);
        _gifts = new GiftRepository(_db);
        _users = new UserRepository(_db);
        var config = new ConfigManager();
        config.Load();
        _giftService = new GiftService(
            config,
            new DouyinService(config.Settings.Douyin, new LogService(_dataDir)),
            _gifts,
            _users,
            new UserLevelService(config, _users),
            new GiftRuleRepository(_db),
            new LogService(_dataDir),
            new SystemMessageService(50));
        _giftService.Start("room123");
    }

    [Fact]
    public void DuplicateGiftEvent_DoesNotIncreasePointsTwice()
    {
        var gift = new GiftEvent
        {
            EventId = "room123:1",
            Sequence = 1,
            UserId = "u1",
            Nickname = "测试用户",
            GiftName = "小心心",
            GiftId = "g1",
            Count = 1,
            Value = 1
        };

        var first = _giftService.HandleGiftEvent(gift);
        var second = _giftService.HandleGiftEvent(gift);

        Assert.True(first);
        Assert.False(second);
        Assert.Equal(1, _users.GetUser("u1")?.Points);
        Assert.True(_gifts.ExistsByEventId("room123:1"));
    }

    public void Dispose()
    {
        _giftService.Dispose();
        _db.Dispose();
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
