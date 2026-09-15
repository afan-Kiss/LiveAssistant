using System.Net;
using Douyin.Live;
using Google.Protobuf;
using LiveAssistant.Config;
using LiveAssistant.Database;
using LiveAssistant.GiftProtocol;
using LiveAssistant.Services;
using Xunit;

namespace LiveAssistant.Tests;

public sealed class GiftCollectorTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _cookiePath;

    public GiftCollectorTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "la_gc_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _cookiePath = Path.Combine(_tempDir, "cookies.json");
        File.WriteAllText(_cookiePath, """
        {
          "active": "默认账号",
          "profiles": {
            "默认账号": {
              "name": "默认账号",
              "cookie": "sessionid=abc123; ttwid=xyz"
            }
          }
        }
        """);
    }

    [Fact]
    public async Task ImFetch_ReceivesGiftMessage_ThroughPipeline()
    {
        var gift = BuildGift(9001, 1, 77, "Tester", 11, "小心心", 1, 1, 1, 1);
        var body = BuildImFetchBody(gift);
        var handler = new ScriptedHandler(req =>
        {
            Assert.Contains("/webcast/im/fetch/", req.RequestUri!.AbsoluteUri);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(body)
            };
        });

        var client = new GiftImFetchClient(new HttpClient(handler));
        var result = await client.FetchAsync("12345", "web1", "sessionid=abc", "uid1", "", "");
        Assert.Single(result.Gifts);

        var pipeline = new GiftProtocolPipeline();
        var events = pipeline.ProcessGiftMessage(result.Gifts[0]);
        Assert.Single(events);
        Assert.Equal("77", events[0].UserId);
        Assert.Equal("小心心", events[0].GiftName);
    }

    [Fact]
    public void ProtobufParse_ImFetchBody_OnlyKeepsGiftMessages()
    {
        var gift = BuildGift(1, 1, 2, "A", 3, "玫瑰", 5, 2, 9, 1);
        var response = new Response();
        response.MessagesList.Add(new Message
        {
            Method = "WebcastChatMessage",
            Payload = ByteString.CopyFromUtf8("not-a-gift")
        });
        response.MessagesList.Add(new Message
        {
            Method = GiftNormalizer.WebcastGiftMethod,
            Payload = gift.ToByteString()
        });
        response.Cursor = "c-1";
        response.InternalExt = "ext-1";

        var parsed = WebcastGiftParser.ParseImFetchBody(response.ToByteArray());
        Assert.True(parsed.Success, parsed.Error);
        Assert.Single(parsed.Gifts);
        Assert.Equal((ulong)3, parsed.Gifts[0].GiftId);
        Assert.Equal("c-1", parsed.Response?.Cursor);
    }

    [Fact]
    public void ComboMerge_ViaCollectorPipeline()
    {
        var pipeline = new GiftProtocolPipeline();
        Assert.Empty(pipeline.ProcessGiftMessage(BuildGift(1, 1, 5, "U", 8, "跑车", 10, 3, 1, 0)));
        Assert.Empty(pipeline.ProcessGiftMessage(BuildGift(2, 1, 5, "U", 8, "跑车", 10, 6, 1, 0)));
        var end = pipeline.ProcessGiftMessage(BuildGift(3, 1, 5, "U", 8, "跑车", 10, 6, 1, 1, totalCount: 6));
        Assert.Single(end);
        Assert.Equal(6, end[0].Count);
        Assert.Equal(60, end[0].Value);
    }

    [Fact]
    public async Task CookieInvalid_Http401_Throws()
    {
        var handler = new ScriptedHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized));
        var client = new GiftImFetchClient(new HttpClient(handler));
        await Assert.ThrowsAsync<CookieInvalidException>(() =>
            client.FetchAsync("1", "w", "sessionid=x", "u", "", ""));
    }

    [Fact]
    public async Task CookieInvalid_MissingSession_ThrowsBeforeRequest()
    {
        var handler = new ScriptedHandler(_ => throw new Exception("should not call"));
        var client = new GiftImFetchClient(new HttpClient(handler));
        await Assert.ThrowsAsync<CookieInvalidException>(() =>
            client.FetchAsync("1", "w", "ttwid=only", "u", "", ""));
    }

    [Fact]
    public void SidecarCookieStore_ReadsActiveCookie()
    {
        Assert.True(SidecarCookieStore.TryReadActiveCookie(_cookiePath, out var cookie, out var name, out var err), err);
        Assert.Equal("默认账号", name);
        Assert.Contains("sessionid=abc123", cookie);
    }

    [Fact]
    public void SidecarCookieStore_DetectsMissingSession()
    {
        var bad = Path.Combine(_tempDir, "bad.json");
        File.WriteAllText(bad, """
        {"active":"a","profiles":{"a":{"cookie":"ttwid=1"}}}
        """);
        Assert.False(SidecarCookieStore.TryReadActiveCookie(bad, out _, out _, out var err));
        Assert.Contains("sessionid", err ?? "", StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task FileCookieProvider_FallsBackToRoomResolve_WhenCookiesJsonMissing()
    {
        var missingPath = Path.Combine(_tempDir, "missing-cookies.json");
        var handler = new ScriptedHandler(req =>
        {
            var path = req.RequestUri!.AbsolutePath;
            if (path.EndsWith("/api/cookie", StringComparison.OrdinalIgnoreCase))
            {
                return JsonResponse("""
                {"ok":true,"data":{"active":"默认账号","login_ok":true,"login_hint":"ok"}}
                """);
            }

            if (path.EndsWith("/api/live/room/resolve", StringComparison.OrdinalIgnoreCase))
            {
                return JsonResponse("""
                {"ok":true,"data":{"web_rid":"49489141797","room_id":"123","raw":{"cookie":"sessionid=fallback123; ttwid=xyz"}}}
                """);
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var config = new ConfigManager();
        config.Settings.Douyin.BaseUrl = "http://127.0.0.1:4723";
        config.Settings.Douyin.CookieStorePath = missingPath;
        config.Settings.Douyin.WebRid = "49489141797";
        var log = new LogService(_tempDir);
        var douyin = new DouyinService(config.Settings.Douyin, log, new HttpClient(handler)
        {
            BaseAddress = new Uri("http://127.0.0.1:4723/")
        });
        var provider = new FileCookieProvider(config, douyin);

        var cookie = await provider.GetActiveCookieAsync();

        Assert.Contains("sessionid=fallback123", cookie);
    }

    [Fact]
    public async Task FileCookieProvider_ReusesResolverCookie_WithoutSecondResolve()
    {
        var missingPath = Path.Combine(_tempDir, "missing-cookies-2.json");
        var resolveCalls = 0;
        var handler = new ScriptedHandler(req =>
        {
            var path = req.RequestUri!.AbsolutePath;
            if (path.EndsWith("/api/cookie", StringComparison.OrdinalIgnoreCase))
            {
                return JsonResponse("""
                {"ok":true,"data":{"active":"默认账号","login_ok":true,"login_hint":"ok"}}
                """);
            }

            if (path.EndsWith("/api/live/room/resolve", StringComparison.OrdinalIgnoreCase))
            {
                Interlocked.Increment(ref resolveCalls);
                return JsonResponse("""
                {"ok":true,"data":{"web_rid":"49489141797","room_id":"123","raw":{"cookie":"sessionid=resolver123; ttwid=xyz"}}}
                """);
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var config = new ConfigManager();
        config.Settings.Douyin.BaseUrl = "http://127.0.0.1:4723";
        config.Settings.Douyin.CookieStorePath = missingPath;
        config.Settings.Douyin.WebRid = "49489141797";
        var log = new LogService(_tempDir);
        var douyin = new DouyinService(config.Settings.Douyin, log, new HttpClient(handler)
        {
            BaseAddress = new Uri("http://127.0.0.1:4723/")
        });
        var rooms = new SidecarGiftRoomResolver(douyin);
        await rooms.ResolveRoomIdAsync("49489141797");
        var resolveCallsAfterWarmup = Volatile.Read(ref resolveCalls);
        var provider = new FileCookieProvider(config, douyin, rooms);
        provider.BindWebRid("49489141797");

        var first = await provider.GetActiveCookieAsync();
        var second = await provider.GetActiveCookieAsync();

        Assert.Contains("sessionid=resolver123", first);
        Assert.Equal(first, second);
        Assert.Equal(resolveCallsAfterWarmup, Volatile.Read(ref resolveCalls));
    }

    [Fact]
    public void GiftCollector_StartStop_ReconnectLifecycle()
    {
        var config = new ConfigManager();
        config.Load();
        config.Settings.Gift.IdleImFetchIntervalMs = 50;
        config.Settings.Gift.ReconnectDelayMs = 50;

        var dataDir = Path.Combine(_tempDir, "db");
        Directory.CreateDirectory(dataDir);
        var db = new AppDatabase(dataDir);
        var users = new UserRepository(db);
        var log = new LogService(dataDir);
        var douyin = new DouyinService(config.Settings.Douyin, log);
        var gifts = new GiftService(
            config, douyin, new GiftRepository(db), users,
            new UserLevelService(config, users), new GiftRuleRepository(db),
            log, new SystemMessageService(20));

        var cookies = new StaticCookieProvider("sessionid=x");
        var rooms = new StaticRoomResolver("999");
        var im = new GiftImFetchClient(new HttpClient(new ScriptedHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.InternalServerError))));

        using var collector = new GiftCollectorService(
            config, douyin, gifts, log, im, cookies, rooms,
            cursorStore: new GiftCursorStore(dataDir));
        collector.StartGiftCollector("123");
        Assert.True(collector.IsRunning);
        collector.StopGiftCollector();
        collector.StartGiftCollector("123");
        Assert.True(collector.IsRunning);
        collector.StopGiftCollector();
        Assert.False(collector.IsRunning);
        db.Dispose();
    }

    private sealed class StaticCookieProvider : ICookieProvider
    {
        private readonly string _cookie;
        public StaticCookieProvider(string cookie) => _cookie = cookie;
        public Task<string> GetActiveCookieAsync(CancellationToken ct = default) => Task.FromResult(_cookie);
    }

    private sealed class StaticRoomResolver : IGiftRoomResolver
    {
        private readonly string _roomId;
        public StaticRoomResolver(string roomId) => _roomId = roomId;
        public string? ResolvedCookie => null;
        public void ClearResolvedCookie() { }
        public Task<string> ResolveRoomIdAsync(string webRid, CancellationToken ct = default) => Task.FromResult(_roomId);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, true); } catch { /* ignore */ }
    }

    private static GiftMessage BuildGift(
        ulong msgId, ulong roomId, ulong userId, string nick,
        ulong giftId, string giftName, uint diamond, ulong repeat, ulong group, uint repeatEnd,
        ulong? totalCount = null)
    {
        return new GiftMessage
        {
            Common = new Common { MsgId = msgId, RoomId = roomId, CreateTime = 1_700_000_000UL },
            GiftId = giftId,
            RepeatCount = repeat,
            GroupId = group,
            RepeatEnd = repeatEnd,
            TotalCount = totalCount ?? 0,
            User = new User { Id = userId, NickName = nick, IdStr = userId.ToString() },
            Gift = new GiftStruct { Id = giftId, Name = giftName, DiamondCount = diamond }
        };
    }

    private static byte[] BuildImFetchBody(GiftMessage gift)
    {
        var response = new Response { Cursor = "t-1", InternalExt = "e-1" };
        response.MessagesList.Add(new Message
        {
            Method = GiftNormalizer.WebcastGiftMethod,
            Payload = gift.ToByteString()
        });
        var frame = new PushFrame
        {
            PayloadType = "msg",
            PayloadEncoding = "",
            Payload = ByteString.CopyFrom(response.ToByteArray())
        };
        return frame.ToByteArray();
    }

    private sealed class ScriptedHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler;
        public ScriptedHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) => _handler = handler;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(_handler(request));
    }

    private static HttpResponseMessage JsonResponse(string json)
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
        };
}
