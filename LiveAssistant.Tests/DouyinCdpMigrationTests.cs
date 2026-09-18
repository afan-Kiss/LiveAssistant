using System.Net;
using System.Text;
using LiveAssistant.Config;
using LiveAssistant.Database;
using LiveAssistant.Models;
using LiveAssistant.Services;
using Xunit;

namespace LiveAssistant.Tests;

public sealed class DouyinCdpMigrationTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "la_cdp_" + Guid.NewGuid().ToString("N"));

    public DouyinCdpMigrationTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* ignore */ }
    }

    [Fact]
    public void DefaultBaseUrl_IsCdpPort()
    {
        Assert.Equal("http://127.0.0.1:17891", new DouyinSettings().BaseUrl);
    }

    [Fact]
    public async Task HealthNotLoggedIn_DoesNotPretendConnected()
    {
        var paths = new List<string>();
        var danmaku = CreateDanmaku(req =>
        {
            paths.Add(req.RequestUri!.AbsolutePath);
            return Json("""{"ok":true,"data":{"login_ok":false,"login_hint":"未登录","nickname":""}}""");
        });

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => danmaku.StartAsync("100"));
        Assert.Contains("未登录", ex.Message);
        Assert.False(danmaku.IsRunning);
        Assert.Equal("抖音 CDP 未登录，请先完成扫码登录", danmaku.ConnectionStatus);
        Assert.DoesNotContain(paths, p => p.Contains("cookie/import", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task CollectStartOkFalse_FailsStart()
    {
        var danmaku = CreateDanmaku(req =>
        {
            var path = req.RequestUri!.AbsolutePath;
            if (path.EndsWith("/api/health"))
            {
                return Json("""{"ok":true,"data":{"login_ok":true,"nickname":"助手"}}""");
            }

            if (path.EndsWith("/api/live/room/resolve"))
            {
                return Json("""{"ok":true,"data":{"web_rid":"100","room_id":"9","title":"房","owner":{"user_id":"1","nickname":"主播"}}}""");
            }

            if (path.EndsWith("/api/live/collect/sessions"))
            {
                return Json("""{"ok":true,"data":[]}""");
            }

            if (path.EndsWith("/api/live/room/reconnect"))
            {
                return Json("""{"ok":true,"message":"reconnected"}""");
            }

            if (path.EndsWith("/api/live/collect/start"))
            {
                return Json("""{"ok":false,"message":"尚未登录"}""");
            }

            return Json("""{"ok":true,"data":{"items":[],"message_count":0,"mention_count":0,"running":true}}""");
        });

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => danmaku.StartAsync("100"));
        Assert.Contains("尚未登录", ex.Message);
        Assert.False(danmaku.IsRunning);
    }

    [Fact]
    public async Task CollectStartOk_EntersRunning_AndKeepsNickname()
    {
        var paths = new List<string>();
        var danmaku = CreateDanmaku(req =>
        {
            paths.Add(req.RequestUri!.PathAndQuery);
            var path = req.RequestUri!.AbsolutePath;
            if (path.EndsWith("/api/health"))
            {
                return Json("""{"ok":true,"data":{"login_ok":true,"nickname":"登录号"}}""");
            }

            if (path.EndsWith("/api/live/room/resolve"))
            {
                return Json("""{"ok":true,"data":{"web_rid":"100","room_id":"9","title":"房","owner":{"nickname":"房主"}}}""");
            }

            if (path.EndsWith("/api/live/collect/sessions"))
            {
                return Json("""{"ok":true,"data":[{"session_id":"room_old","web_rid":"old","running":true}]}""");
            }

            if (path.EndsWith("/api/live/collect/stop"))
            {
                return Json("""{"ok":true}""");
            }

            if (path.EndsWith("/api/live/room/reconnect") || path.EndsWith("/api/live/collect/start"))
            {
                return Json("""{"ok":true}""");
            }

            return Json("""{"ok":true,"data":{"items":[],"message_count":0,"mention_count":0,"running":true}}""");
        });

        await danmaku.StartAsync("100");
        Assert.True(danmaku.IsRunning);
        Assert.Equal("登录号", danmaku.DouyinLoginNickname);
        Assert.Equal("房主", danmaku.RoomOwnerNickname);
        Assert.Contains(paths, p => p.Contains("/api/live/collect/stop", StringComparison.Ordinal));
        Assert.DoesNotContain(paths, p => p.Contains("/collect/", StringComparison.Ordinal) && p.Contains("/stop") && !p.EndsWith("/api/live/collect/stop", StringComparison.Ordinal));
        Assert.DoesNotContain(paths, p => p.Contains("cookie/import", StringComparison.OrdinalIgnoreCase));
        danmaku.Stop();
    }

    [Fact]
    public async Task MentionSilenceLookup_StillWork()
    {
        var paths = new List<string>();
        var douyin = CreateDouyin(req =>
        {
            paths.Add(req.RequestUri!.AbsolutePath);
            if (req.RequestUri.AbsolutePath.EndsWith("/api/live/danmaku/mention"))
            {
                return Json("""{"ok":true,"message":"ok","data":{"msg_id":"mid-1","ok":true}}""");
            }

            if (req.RequestUri.AbsolutePath.EndsWith("/api/live/mod/silence"))
            {
                return Json("""{"ok":true}""");
            }

            if (req.RequestUri.AbsolutePath.EndsWith("/api/live/user/lookup"))
            {
                return Json("""{"ok":true,"data":[{"user_id":"42","nickname":"观众"}]}""");
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var mention = await douyin.SendMentionDetailedAsync("100", "42", "你好");
        Assert.True(mention.Ok);
        Assert.Equal("mid-1", mention.PlatformMessageId);
        Assert.Equal("mid-1", DouyinService.TryExtractPlatformMessageId("""{"ok":true,"data":{"msg_id":"mid-1"}}"""));
        Assert.True(await douyin.ModSilenceAsync("100", "42", "silence"));
        var user = await douyin.LookupUserAsync("100", "观众");
        Assert.Equal("42", user?.UserId);
        Assert.Contains("/api/live/danmaku/mention", paths);
        Assert.Contains("/api/live/mod/silence", paths);
        Assert.Contains("/api/live/user/lookup", paths);
    }

    [Fact]
    public void Dispatch_ChatMemberLike()
    {
        var danmaku = CreateDanmaku(_ => Json("""{"ok":true}"""));
        var seen = new List<string>();
        danmaku.DanmakuReceived += item => seen.Add(item.MsgType + ":" + item.Content);
        danmaku.DispatchForTests(
        [
            new DouyinDanmakuMessage { MsgId = "1", MsgType = "chat", Content = "点歌 晴天", User = new DouyinUser { UserId = "9", Nickname = "观众" } },
            new DouyinDanmakuMessage { MsgId = "2", MsgType = "member", Content = "", User = new DouyinUser { UserId = "8", Nickname = "进房" } },
            new DouyinDanmakuMessage { MsgId = "3", MsgType = "like", Content = "", User = new DouyinUser { UserId = "7", Nickname = "点赞" } }
        ]);
        Assert.Contains("chat:点歌 晴天", seen);
        Assert.Contains("member:", seen);
        Assert.Contains("like:", seen);
    }

    [Fact]
    public void Member_ReachesWelcomeService()
    {
        var config = new ConfigManager();
        config.Settings.Welcome.Enabled = true;
        config.Settings.Welcome.Template = "欢迎 {name}";
        var system = new SystemMessageService(20);
        var db = new AppDatabase(_dir);
        var douyin = CreateDouyin(_ => Json("""{"ok":true,"data":{"msg_id":"x"}}"""));
        var log = new LogService(_dir);
        using var queue = new ReplyQueue(douyin, log, config.Settings.Reply, sendMention: (_, _, _, _, _) =>
            Task.FromResult(new MentionSendResult { Ok = true, PlatformMessageId = "m" }));
        var welcome = new WelcomeService(config, new ReplyService(config), queue, system, new WelcomeCooldownRepository(db));
        welcome.HandleMemberJoin(new DanmakuItem { MsgType = "member", UserId = "8", Nickname = "进房" }, "100");
        Assert.Contains(system.Messages, m => m.Contains("进房", StringComparison.Ordinal));
        welcome.HandleMemberJoin(new DanmakuItem { MsgType = "chat", UserId = "8", Nickname = "进房", Content = "点歌 晴天" }, "100");
    }

    private DanmakuService CreateDanmaku(Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        var douyin = CreateDouyin(respond);
        return new DanmakuService(douyin, new LogService(_dir), new SystemMessageService(20), pollIntervalMs: 300);
    }

    private DouyinService CreateDouyin(Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        var handler = new RecordingHandler(respond);
        return new DouyinService(new DouyinSettings { BaseUrl = "http://127.0.0.1:17891" }, new LogService(_dir),
            new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:17891/") });
    }

    private static HttpResponseMessage Json(string body)
        => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;
        public RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) => _respond = respond;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(_respond(request));
    }
}
