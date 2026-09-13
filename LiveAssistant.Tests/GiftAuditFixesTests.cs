using LiveAssistant.Config;
using LiveAssistant.Database;
using LiveAssistant.Models;
using LiveAssistant.Services;
using Xunit;

namespace LiveAssistant.Tests;

public sealed class GiftAuditFixesTests : IDisposable
{
    private readonly string _dir;

    public GiftAuditFixesTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "la_audit_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    [Fact]
    public void Deduplicator_UsesPersistentStore_AfterRestart()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal) { "persisted-1" };
        var deduper = new GiftEventDeduplicator(TimeSpan.FromMinutes(30), id => seen.Contains(id));
        var gift = new GiftEvent { EventId = "persisted-1", UserId = "u", GiftName = "g", Count = 1, Value = 1 };
        Assert.False(deduper.TryAdmit(gift));
        Assert.True(deduper.Contains("persisted-1"));
    }

    [Fact]
    public void CursorStore_SaveAndRestore_PerWebRid()
    {
        var store = new GiftCursorStore(_dir);
        store.Save("roomA", "111", "c-a", "ext-a");
        store.Save("roomB", "222", "c-b", "ext-b");

        var a = store.Load("roomA");
        var b = store.Load("roomB");
        Assert.NotNull(a);
        Assert.Equal("c-a", a!.Cursor);
        Assert.Equal("ext-a", a.InternalExt);
        Assert.Equal("111", a.RoomId);
        Assert.Equal("c-b", b!.Cursor);

        // 模拟重启：新 store 实例读同一文件
        var store2 = new GiftCursorStore(_dir);
        Assert.Equal("c-a", store2.Load("roomA")!.Cursor);
    }

    [Fact]
    public void GiftService_RejectsAbnormalAmounts()
    {
        using var ctx = CreateCtx();
        ctx.Config.Settings.Gift.MaxGiftCount = 100;
        ctx.Config.Settings.Gift.MaxGiftValue = 1000;

        Assert.False(ctx.Gifts.HandleGiftEvent(new GiftEvent
        {
            EventId = "bad-count",
            UserId = "u",
            Nickname = "n",
            GiftName = "g",
            Count = 9999,
            Value = 10
        }));

        Assert.False(ctx.Gifts.HandleGiftEvent(new GiftEvent
        {
            EventId = "bad-value",
            UserId = "u",
            Nickname = "n",
            GiftName = "g",
            Count = 1,
            Value = 9_999_999
        }));

        Assert.False(ctx.Gifts.HandleGiftEvent(new GiftEvent
        {
            EventId = "bad-user",
            UserId = "",
            GiftName = "g",
            Count = 1,
            Value = 1
        }));
    }

    [Fact]
    public void GiftService_CorrectsInconsistentValue_Once()
    {
        using var ctx = CreateCtx();
        var gift = new GiftEvent
        {
            EventId = "fix-value",
            UserId = "u1",
            Nickname = "n",
            GiftId = "1",
            GiftName = "玫瑰",
            Count = 10,
            DiamondCount = 5,
            Value = 1 // 错误：应为 50
        };

        Assert.True(ctx.Gifts.HandleGiftEvent(gift));
        Assert.Equal(50, gift.Value);
        Assert.Equal(50, ctx.Users.GetUser("u1")?.Points);
    }

    [Fact]
    public void RoomSwitch_LoadsSeparateCursors()
    {
        var store = new GiftCursorStore(_dir);
        store.Save("old", "1", "cursor-old", "ext-old");
        store.Save("new", "2", "cursor-new", "ext-new");

        Assert.Equal("cursor-old", store.Load("old")!.Cursor);
        Assert.Equal("cursor-new", store.Load("new")!.Cursor);
        store.Remove("old");
        Assert.Null(store.Load("old"));
        Assert.NotNull(store.Load("new"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* ignore */ }
    }

    private GiftCtx CreateCtx()
    {
        var data = Path.Combine(_dir, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(data);
        return new GiftCtx(data);
    }

    private sealed class GiftCtx : IDisposable
    {
        public ConfigManager Config { get; }
        public GiftService Gifts { get; }
        public UserRepository Users { get; }
        private readonly AppDatabase _db;

        public GiftCtx(string data)
        {
            Config = new ConfigManager();
            Config.Load();
            Config.Settings.Gift.PointsPerValue = 1;
            Config.Settings.Gift.MaxGiftCount = 10000;
            Config.Settings.Gift.MaxGiftValue = 5_000_000;
            Config.Settings.Gift.MaxPointsDelta = 5_000_000;
            var log = new LogService(data);
            _db = new AppDatabase(data);
            Users = new UserRepository(_db);
            Gifts = new GiftService(
                Config,
                new DouyinService(Config.Settings.Douyin, log),
                new GiftRepository(_db),
                Users,
                new UserLevelService(Config, Users),
                new GiftRuleRepository(_db),
                log,
                new SystemMessageService(20));
            Gifts.BindRoom("t");
        }

        public void Dispose() => _db.Dispose();
    }
}
