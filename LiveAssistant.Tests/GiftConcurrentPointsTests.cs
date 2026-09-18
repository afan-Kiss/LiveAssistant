using LiveAssistant.Config;
using LiveAssistant.Database;
using LiveAssistant.Models;
using LiveAssistant.Services;
using Xunit;

namespace LiveAssistant.Tests;

public sealed class GiftConcurrentPointsTests : IDisposable
{
    private readonly string _dir;
    private readonly AppDatabase _db;
    private readonly UserRepository _users;
    private readonly GiftRepository _gifts;
    private readonly PointsLedgerRepository _ledger;
    private readonly GiftService _giftService;

    public GiftConcurrentPointsTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "la_gift_conc_" + Guid.NewGuid().ToString("N"));
        _db = new AppDatabase(_dir);
        _ledger = new PointsLedgerRepository(_db);
        _users = new UserRepository(_db, _ledger);
        _gifts = new GiftRepository(_db, _ledger);
        var config = new ConfigManager();
        config.Load();
        config.Settings.Gift.PointsPerValue = 1;
        _giftService = new GiftService(
            config,
            new DouyinService(config.Settings.Douyin, new LogService(_dir)),
            _gifts,
            _users,
            new UserLevelService(config, _users),
            new GiftRuleRepository(_db),
            new LogService(_dir),
            new SystemMessageService(50));
        _giftService.Start("room-conc");
    }

    [Fact]
    public void ConcurrentTwoGifts_BalanceAfterSequenceEndsAt20()
    {
        var g1 = new GiftEvent
        {
            EventId = "e-conc-1",
            UserId = "u-conc",
            Nickname = "并发",
            GiftName = "小心心",
            GiftId = "1",
            Count = 1,
            Value = 10,
            DiamondCount = 10,
            Time = DateTime.Now
        };
        var g2 = new GiftEvent
        {
            EventId = "e-conc-2",
            UserId = "u-conc",
            Nickname = "并发",
            GiftName = "小心心",
            GiftId = "1",
            Count = 1,
            Value = 10,
            DiamondCount = 10,
            Time = DateTime.Now
        };

        var t1 = Task.Run(() => _giftService.HandleGiftEvent(g1));
        var t2 = Task.Run(() => _giftService.HandleGiftEvent(g2));
        Task.WaitAll(t1, t2);
        Assert.True(t1.Result);
        Assert.True(t2.Result);

        var user = _users.GetUser("u-conc");
        Assert.NotNull(user);
        Assert.Equal(20, user!.Points);

        var cfg = new ConfigManager();
        cfg.Load();
        var expectedLevel = new UserLevelService(cfg, _users).CalculateLevel(20);
        Assert.Equal(expectedLevel, user.Level);

        var ledger = _ledger.ListByUser("u-conc", 10)
            .Where(e => e.Type == PointsTransactionType.Gift)
            .OrderBy(e => e.Id)
            .ToList();
        Assert.Equal(2, ledger.Count);
        Assert.Contains(20, ledger.Select(e => e.BalanceAfter));
        Assert.Equal(new HashSet<int> { 10, 20 }, ledger.Select(e => e.BalanceAfter).ToHashSet());

        // gift_events.points_after 不允许两条都错误写成 10
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT points_after FROM gift_events WHERE user_id = 'u-conc' ORDER BY id";
        var afters = new List<int>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) afters.Add(reader.GetInt32(0));
        Assert.Equal(2, afters.Count);
        Assert.Contains(20, afters);
        Assert.False(afters.All(x => x == 10));
    }

    [Fact]
    public void DuplicateEventId_StillIdempotent()
    {
        var gift = new GiftEvent
        {
            EventId = "e-idem",
            UserId = "u-idem",
            Nickname = "幂等",
            GiftName = "玫瑰",
            Count = 1,
            Value = 5,
            DiamondCount = 5,
            Time = DateTime.Now
        };
        Assert.True(_giftService.HandleGiftEvent(gift));
        Assert.False(_giftService.HandleGiftEvent(gift));
        Assert.Equal(5, _users.GetUser("u-idem")!.Points);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* ignore */ }
    }
}
