using System.Net;
using Douyin.Live;
using Google.Protobuf;
using LiveAssistant.Config;
using LiveAssistant.Database;
using LiveAssistant.GiftProtocol;
using LiveAssistant.Models;
using LiveAssistant.Services;
using Xunit;

namespace LiveAssistant.Tests;

public sealed class GiftCollectorLifecycleTests : IDisposable
{
    private readonly string _tempDir;

    public GiftCollectorLifecycleTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "la_life_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    [Fact]
    public void Deduplicator_BlocksSameEventId_Within30Minutes()
    {
        var deduper = new GiftEventDeduplicator(TimeSpan.FromMinutes(30));
        var gift = new GiftEvent { EventId = "e1", UserId = "u", GiftName = "g", Count = 1, Value = 1 };
        Assert.True(deduper.TryAdmit(gift));
        Assert.False(deduper.TryAdmit(gift));
        Assert.True(deduper.Contains("e1"));
    }

    [Fact]
    public void Deduplicator_ExpiresAfterTtl()
    {
        var deduper = new GiftEventDeduplicator(TimeSpan.FromMinutes(30));
        var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var gift = new GiftEvent { EventId = "e2", UserId = "u", GiftName = "g", Count = 1, Value = 1 };
        Assert.True(deduper.TryAdmit(gift, t0));
        Assert.False(deduper.TryAdmit(gift, t0.AddMinutes(29)));
        Assert.True(deduper.TryAdmit(gift, t0.AddMinutes(30)));
    }

    [Fact]
    public void GiftService_ValueIsTotalDiamonds_NotMultipliedAgain()
    {
        using var ctx = new GiftTestContext(_tempDir);
        var gift = new GiftEvent
        {
            EventId = "v1",
            UserId = "u1",
            Nickname = "N",
            GiftId = "9",
            GiftName = "玫瑰",
            Count = 10,
            Value = 50, // 总钻石 = 5*10
            DiamondCount = 5
        };

        Assert.True(ctx.Gifts.HandleGiftEvent(gift));
        Assert.Equal(50, ctx.Users.GetUser("u1")?.Points); // PointsPerValue=1, 不再 * Count
    }

    [Fact]
    public async Task Lifecycle_StartStop_AndRoomSwitch()
    {
        using var ctx = new GiftTestContext(_tempDir);
        var cookies = new FakeCookieProvider("sessionid=ok");
        var rooms = new FakeRoomResolver();
        rooms.Map["roomA"] = "1001";
        rooms.Map["roomB"] = "2002";

        var fetches = 0;
        var im = new GiftImFetchClient(new HttpClient(new ScriptedHandler(_ =>
        {
            Interlocked.Increment(ref fetches);
            return OkEmpty();
        })));

        using var collector = new GiftCollectorService(
            ctx.Config, ctx.Douyin, ctx.Gifts, ctx.Log, im, cookies, rooms, new GiftEventDeduplicator(),
            new GiftCursorStore(_tempDir));

        collector.StartGiftCollector("roomA");
        Assert.True(collector.IsRunning);
        Assert.Equal("roomA", collector.CurrentWebRid);
        await Task.Delay(80);
        collector.StopGiftCollector();
        Assert.False(collector.IsRunning);

        collector.StartGiftCollector("roomB");
        Assert.True(collector.IsRunning);
        Assert.Equal("roomB", collector.CurrentWebRid);
        await Task.Delay(80);
        collector.StopGiftCollector();
        Assert.Contains("roomA", rooms.Resolved);
        Assert.Contains("roomB", rooms.Resolved);
        Assert.True(fetches >= 1);
    }

    [Fact]
    public async Task Lifecycle_LiveSessionRoomIdChange_ResetsCursor()
    {
        using var ctx = new GiftTestContext(_tempDir);
        var dataDir = Path.Combine(_tempDir, "room-change");
        Directory.CreateDirectory(dataDir);
        var store = new GiftCursorStore(dataDir);
        store.Save("r1", "OLD_ROOM", "cursor-old", "ext-old");

        var cookies = new FakeCookieProvider("sessionid=ok");
        var rooms = new FakeRoomResolver();
        rooms.Map["r1"] = "NEW_ROOM";

        string? firstFetchedRoomId = null;
        string? firstFetchedCursor = null;
        var fetchCount = 0;
        var im = new GiftImFetchClient(new HttpClient(new ScriptedHandler(req =>
        {
            var n = Interlocked.Increment(ref fetchCount);
            var query = req.RequestUri?.Query ?? "";
            if (n == 1)
            {
                firstFetchedRoomId = ReadQuery(query, "room_id");
                firstFetchedCursor = ReadQuery(query, "cursor");
            }

            return OkEmpty();
        })));

        using var collector = new GiftCollectorService(
            ctx.Config, ctx.Douyin, ctx.Gifts, ctx.Log, im, cookies, rooms, new GiftEventDeduplicator(),
            store);
        collector.StartGiftCollector("r1");
        await Task.Delay(250);
        collector.StopGiftCollector();

        Assert.True(fetchCount >= 1);
        Assert.Equal("NEW_ROOM", firstFetchedRoomId);
        Assert.NotEqual("cursor-old", firstFetchedCursor);
        Assert.True(string.IsNullOrEmpty(firstFetchedCursor), $"first fetch should use empty cursor, got '{firstFetchedCursor}'");
        var saved = store.Load("r1");
        Assert.NotNull(saved);
        Assert.Equal("NEW_ROOM", saved!.RoomId);
        Assert.NotEqual("cursor-old", saved.Cursor);
    }

    [Fact]
    public async Task Lifecycle_Reconnect_AfterFetchError_ContinuesRunning()
    {
        using var ctx = new GiftTestContext(_tempDir);
        ctx.Config.Settings.Gift.ReconnectDelayMs = 20;
        ctx.Config.Settings.Gift.IdleImFetchIntervalMs = 20;
        var cookies = new FakeCookieProvider("sessionid=ok");
        var rooms = new FakeRoomResolver { Map = { ["r1"] = "11" } };

        var n = 0;
        var im = new GiftImFetchClient(new HttpClient(new ScriptedHandler(_ =>
        {
            Interlocked.Increment(ref n);
            return new HttpResponseMessage(HttpStatusCode.InternalServerError);
        })));

        using var collector = new GiftCollectorService(
            ctx.Config, ctx.Douyin, ctx.Gifts, ctx.Log, im, cookies, rooms,
            cursorStore: new GiftCursorStore(_tempDir));
        collector.StartGiftCollector("r1");
        Assert.True(await WaitUntil(() => Volatile.Read(ref n) >= 2, TimeSpan.FromSeconds(5)),
            $"fetch calls={n}");
        Assert.True(collector.IsRunning);
        collector.StopGiftCollector();
    }

    [Fact]
    public async Task Lifecycle_CookieInvalid_RetriesWithoutCrashing()
    {
        using var ctx = new GiftTestContext(_tempDir);
        ctx.Config.Settings.Gift.ReconnectDelayMs = 20;
        ctx.Config.Settings.Gift.IdleImFetchIntervalMs = 20;
        // 持续失效：验证会反复取 Cookie 且采集循环不退出
        var cookies = new FakeCookieProvider(cookie: "sessionid=ok", failTimes: 1000);
        var rooms = new FakeRoomResolver { Map = { ["r1"] = "11" } };
        var im = new GiftImFetchClient(new HttpClient(new ScriptedHandler(_ => OkEmpty())));

        using var collector = new GiftCollectorService(
            ctx.Config, ctx.Douyin, ctx.Gifts, ctx.Log, im, cookies, rooms,
            cursorStore: new GiftCursorStore(_tempDir));
        collector.StartGiftCollector("r1");
        Assert.True(await WaitUntil(() => Volatile.Read(ref cookies.Calls) >= 3, TimeSpan.FromSeconds(5)),
            $"cookie calls={cookies.Calls}");
        Assert.True(collector.IsRunning);
        collector.StopGiftCollector();
    }

    [Fact]
    public async Task Lifecycle_DuplicateEvent_FromReconnect_NotDoubleCounted()
    {
        using var ctx = new GiftTestContext(_tempDir);
        ctx.Config.Settings.Gift.IdleImFetchIntervalMs = 30;
        ctx.Config.Settings.Gift.ActiveImFetchIntervalMs = 30;
        var cookies = new FakeCookieProvider("sessionid=ok");
        var rooms = new FakeRoomResolver { Map = { ["r1"] = "11" } };
        var gift = BuildGift(4242, 11, 7, "Dup", 3, "小心心", 1, 1, 1, 1);
        var body = BuildImFetchBody(gift);
        var im = new GiftImFetchClient(new HttpClient(new ScriptedHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) })));

        var deduper = new GiftEventDeduplicator();
        using var collector = new GiftCollectorService(
            ctx.Config, ctx.Douyin, ctx.Gifts, ctx.Log, im, cookies, rooms, deduper,
            new GiftCursorStore(_tempDir));
        collector.StartGiftCollector("r1");
        Assert.True(await WaitUntil(() => (ctx.Users.GetUser("7")?.Points ?? 0) >= 1, TimeSpan.FromSeconds(3)));
        collector.StopGiftCollector();

        Assert.Equal(1, ctx.Users.GetUser("7")?.Points);
        Assert.True(deduper.Contains("4242"));
    }

    private static string? ReadQuery(string query, string key)
    {
        if (query.StartsWith('?'))
        {
            query = query[1..];
        }

        foreach (var part in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var idx = part.IndexOf('=');
            var name = idx < 0 ? part : part[..idx];
            if (!name.Equals(key, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var value = idx < 0 ? "" : part[(idx + 1)..];
            return Uri.UnescapeDataString(value);
        }

        return null;
    }

    private static async Task<bool> WaitUntil(Func<bool> cond, TimeSpan timeout)
    {
        var start = DateTime.UtcNow;
        while (DateTime.UtcNow - start < timeout)
        {
            if (cond())
            {
                return true;
            }

            await Task.Delay(20);
        }

        return cond();
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, true); } catch { /* ignore */ }
    }

    private static byte[] OkEmptyBody()
    {
        // 合法空 Response，避免空字节导致 parse fail 干扰生命周期测试
        return new Response { Cursor = "idle" }.ToByteArray();
    }

    private static HttpResponseMessage OkEmpty()
        => new(HttpStatusCode.OK) { Content = new ByteArrayContent(OkEmptyBody()) };

    private static GiftMessage BuildGift(
        ulong msgId, ulong roomId, ulong userId, string nick,
        ulong giftId, string giftName, uint diamond, ulong repeat, ulong group, uint repeatEnd)
    {
        return new GiftMessage
        {
            Common = new Common { MsgId = msgId, RoomId = roomId, CreateTime = 1_700_000_000UL },
            GiftId = giftId,
            RepeatCount = repeat,
            GroupId = group,
            RepeatEnd = repeatEnd,
            User = new User { Id = userId, NickName = nick, IdStr = userId.ToString() },
            Gift = new GiftStruct { Id = giftId, Name = giftName, DiamondCount = diamond }
        };
    }

    private static byte[] BuildImFetchBody(GiftMessage gift)
    {
        var response = new Response { Cursor = "c1" };
        response.MessagesList.Add(new Message
        {
            Method = GiftNormalizer.WebcastGiftMethod,
            Payload = gift.ToByteString()
        });
        return new PushFrame
        {
            PayloadType = "msg",
            Payload = ByteString.CopyFrom(response.ToByteArray())
        }.ToByteArray();
    }

    private sealed class ScriptedHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _h;
        public ScriptedHandler(Func<HttpRequestMessage, HttpResponseMessage> h) => _h = h;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(_h(request));
    }

    private sealed class FakeCookieProvider : ICookieProvider
    {
        private readonly string _cookie;
        private int _failTimes;
        public int Calls;

        public FakeCookieProvider(string cookie, int failTimes = 0)
        {
            _cookie = cookie;
            _failTimes = failTimes;
        }

        public Task<string> GetActiveCookieAsync(CancellationToken ct = default)
        {
            Interlocked.Increment(ref Calls);
            if (Volatile.Read(ref _failTimes) > 0)
            {
                Interlocked.Decrement(ref _failTimes);
                throw new CookieInvalidException("fake cookie invalid");
            }

            return Task.FromResult(_cookie);
        }
    }

    private sealed class FakeRoomResolver : IGiftRoomResolver
    {
        public Dictionary<string, string> Map { get; } = new(StringComparer.Ordinal);
        public List<string> Resolved { get; } = new();
        public string? ResolvedCookie => null;

        public Task<string> ResolveRoomIdAsync(string webRid, CancellationToken ct = default)
        {
            Resolved.Add(webRid);
            if (!Map.TryGetValue(webRid, out var id))
            {
                throw new InvalidOperationException("unknown room");
            }

            return Task.FromResult(id);
        }

        public void ClearResolvedCookie()
        {
        }
    }

    private sealed class GiftTestContext : IDisposable
    {
        public ConfigManager Config { get; }
        public DouyinService Douyin { get; }
        public GiftService Gifts { get; }
        public UserRepository Users { get; }
        public LogService Log { get; }
        private readonly AppDatabase _db;

        public GiftTestContext(string root)
        {
            var data = Path.Combine(root, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(data);
            Config = new ConfigManager();
            Config.Load();
            Config.Settings.Gift.PointsPerValue = 1;
            Config.Settings.Gift.IdleImFetchIntervalMs = 40;
            Config.Settings.Gift.ActiveImFetchIntervalMs = 40;
            Config.Settings.Gift.ReconnectDelayMs = 40;
            Config.Settings.Gift.ComboTimeoutSeconds = 10;
            Log = new LogService(data);
            Douyin = new DouyinService(Config.Settings.Douyin, Log);
            _db = new AppDatabase(data);
            Users = new UserRepository(_db);
            Gifts = new GiftService(
                Config, Douyin, new GiftRepository(_db), Users,
                new UserLevelService(Config, Users), new GiftRuleRepository(_db),
                Log, new SystemMessageService(20));
            Gifts.BindRoom("test");
        }

        public void Dispose() => _db.Dispose();
    }
}
