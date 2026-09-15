using LiveAssistant.Config;
using LiveAssistant.Database;
using LiveAssistant.Models;
using LiveAssistant.Services;
using Xunit;

namespace LiveAssistant.Tests;

public sealed class PointsLedgerTests : IDisposable
{
    private readonly string _dir;
    private readonly AppDatabase _db;
    private readonly PointsLedgerRepository _ledger;
    private readonly UserRepository _users;

    public PointsLedgerTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "la-ledger-" + Guid.NewGuid().ToString("N"));
        _db = new AppDatabase(_dir);
        _ledger = new PointsLedgerRepository(_db);
        _users = new UserRepository(_db, _ledger);
    }

    [Fact]
    public void AtomicDeduct_InsufficientBalance_FailsWithoutLedger()
    {
        _users.EnsureUser("u1", "测试");
        _users.AddPoints("u1", "测试", 5);

        var ok = _users.TryDeductPoints(
            "u1", "测试", 10, PointsTransactionType.SongRequest, "点歌", "q1", out _);

        Assert.False(ok);
        Assert.Equal(5, _users.GetUser("u1")!.Points);
        Assert.DoesNotContain(_ledger.ListByUser("u1"), e => e.Type == PointsTransactionType.SongRequest);
    }

    [Fact]
    public void ConcurrentDeduct_OnlyConsumesAvailableBalance()
    {
        _users.EnsureUser("u1", "测试");
        _users.AddPoints("u1", "测试", 15);

        var success = 0;
        Parallel.For(0, 3, _ =>
        {
            if (_users.TryDeductPoints(
                    "u1", "测试", 10, PointsTransactionType.SongRequest, "点歌", null, out _))
            {
                Interlocked.Increment(ref success);
            }
        });

        Assert.Equal(1, success);
        Assert.Equal(5, _users.GetUser("u1")!.Points);
        Assert.Single(_ledger.ListByUser("u1"), e => e.Type == PointsTransactionType.SongRequest);
    }

    [Fact]
    public void GiftDuplicate_DoesNotDoubleLedgerOrPoints()
    {
        var gifts = new GiftRepository(_db, _ledger);
        var config = new ConfigManager();
        config.Load();
        var giftService = new GiftService(
            config,
            new DouyinService(config.Settings.Douyin, new LogService(_dir)),
            gifts,
            _users,
            new UserLevelService(config, _users),
            new GiftRuleRepository(_db),
            new LogService(_dir),
            new SystemMessageService(50));
        giftService.Start("room1");

        var gift = new GiftEvent
        {
            EventId = "evt-repeat-1",
            UserId = "u1",
            Nickname = "测试",
            GiftName = "玫瑰",
            GiftId = "g1",
            Count = 5,
            Value = 5
        };

        Assert.True(giftService.HandleGiftEvent(gift));
        Assert.False(giftService.HandleGiftEvent(gift));
        Assert.Equal(5, _users.GetUser("u1")!.Points);

        var entries = _ledger.ListByUser("u1");
        Assert.Single(entries);
        Assert.Equal(5, entries[0].Delta);
        Assert.Equal(PointsTransactionType.Gift, entries[0].Type);
        Assert.Equal("evt-repeat-1", entries[0].RefId);
    }

    [Fact]
    public void PointsQuery_IncludesRecentLedgerDetails()
    {
        _users.EnsureUser("u1", "测试");
        _users.AddPoints("u1", "测试", 100);
        _users.TryDeductPoints("u1", "测试", 10, PointsTransactionType.SongRequest, "点歌扣积分", "1", out _);

        var config = new ConfigManager();
        config.Load();
        config.ReplyTemplates["pointsQuery"] = "积分 {score} Lv{level} 明细:{details}";

        var sent = new List<string>();
        var replyQueue = new ReplyQueue(
            new DouyinService(config.Settings.Douyin, new LogService(_dir)),
            new LogService(_dir),
            config.Settings.Reply,
            sendMention: (_, _, content, _) =>
            {
                sent.Add(content);
                return Task.FromResult(true);
            });

        var svc = new PointsQueryService(_users, _ledger);
        svc.TryHandle(new DanmakuItem { UserId = "u1", Nickname = "测试", Content = "查积分" },
            "room1", new ReplyService(config), replyQueue);

        for (var i = 0; i < 20 && sent.Count == 0; i++)
        {
            Thread.Sleep(50);
        }

        Assert.Single(sent);
        Assert.Contains("90", sent[0]);
        Assert.Contains("-10", sent[0]);
    }

    [Fact]
    public void RecordSuccessfulRequest_InsufficientPoints_ReturnsFalse()
    {
        var config = new ConfigManager();
        config.Load();
        config.Settings.SongRequestPolicy.Mode = SongRequestPolicyMode.Points;
        config.Settings.SongRequestPolicy.PointsCost = 10;

        _users.EnsureUser("u1", "测试");
        _users.AddPoints("u1", "测试", 5);

        var permission = new SongRequestPermissionService(
            config,
            _users,
            new QueueService(_db),
            new SongBlacklistService(new SongBlacklistRepository(_db)),
            new LevelPermissionRepository(_db),
            new UserLevelService(config, _users));

        var ok = permission.RecordSuccessfulRequest(
            new DanmakuItem { UserId = "u1", Nickname = "测试", Content = "确定" }, 99);

        Assert.False(ok);
        Assert.Equal(5, _users.GetUser("u1")!.Points);
        Assert.DoesNotContain(_ledger.ListByUser("u1"), e => e.Type == PointsTransactionType.SongRequest);
    }

    public void Dispose()
    {
        _db.Dispose();
        try
        {
            Directory.Delete(_dir, true);
        }
        catch
        {
            // ignore
        }
    }
}
