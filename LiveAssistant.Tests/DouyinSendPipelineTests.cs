using System.Net;
using System.Text;
using System.Text.Json;
using LiveAssistant.Config;
using LiveAssistant.Database;
using LiveAssistant.Models;
using LiveAssistant.Services;
using Xunit;

namespace LiveAssistant.Tests;

/// <summary>CDP 发送/禁言/权限链路回归。</summary>
public sealed class DouyinSendPipelineTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "la_send_" + Guid.NewGuid().ToString("N"));

    public DouyinSendPipelineTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* ignore */ }
    }

    [Fact]
    public async Task SendMessage_Success_RequiresOkTrueAndMsgId()
    {
        string? body = null;
        var douyin = CreateDouyin(req =>
        {
            body = req.Content?.ReadAsStringAsync().GetAwaiter().GetResult();
            return Json("""{"ok":true,"message":"ok","data":{"ok":true,"msg_id":"m-100"}}""");
        });

        var result = await douyin.SendDanmakuAsync("100", "测试");
        Assert.True(result.Ok);
        Assert.Equal("m-100", result.PlatformMessageId);
        using var sent = JsonDocument.Parse(body!);
        Assert.Equal("测试", sent.RootElement.GetProperty("content").GetString());
        Assert.Contains("api/live/danmaku/send", GetLastPath());
    }

    [Fact]
    public async Task SendMessage_Http200_OkFalse_IsFailure()
    {
        var douyin = CreateDouyin(_ =>
            Json("""{"ok":false,"message":"发送被拒绝"}"""));

        var result = await douyin.SendDanmakuAsync("100", "测试");
        Assert.False(result.Ok);
        Assert.Equal("发送被拒绝", result.ErrorReason);
        Assert.Contains("DOUYIN_SEND_MESSAGE", ReadLog());
        Assert.Contains("success=False", ReadLog());
    }

    [Fact]
    public async Task Mention_Success_SendsNickname()
    {
        string? body = null;
        var douyin = CreateDouyin(req =>
        {
            body = req.Content?.ReadAsStringAsync().GetAwaiter().GetResult();
            return Json("""{"ok":true,"data":{"msg_id":"at-1"}}""");
        });

        var result = await douyin.SendMentionDetailedAsync("100", "42", "你好", "观众A");
        Assert.True(result.Ok);
        Assert.Equal("at-1", result.PlatformMessageId);
        using var doc = JsonDocument.Parse(body!);
        Assert.Equal("42", doc.RootElement.GetProperty("user_id").GetString());
        Assert.Equal("观众A", doc.RootElement.GetProperty("nickname").GetString());
        Assert.Equal("你好", doc.RootElement.GetProperty("content").GetString());
    }

    [Fact]
    public async Task Silence_Success_SendsDuration()
    {
        string? body = null;
        var douyin = CreateDouyin(req =>
        {
            body = req.Content?.ReadAsStringAsync().GetAwaiter().GetResult();
            return Json("""{"ok":true,"message":"禁言成功"}""");
        });

        Assert.True(await douyin.ModSilenceAsync("100", "42", "silence", 10));
        using var doc = JsonDocument.Parse(body!);
        Assert.Equal("silence", doc.RootElement.GetProperty("action").GetString());
        Assert.Equal(10, doc.RootElement.GetProperty("duration").GetInt32());
        Assert.Contains("/api/live/mod/silence", GetLastPath());
    }

    [Fact]
    public async Task Unsilence_Success_UsesDedicatedEndpoint()
    {
        var paths = new List<string>();
        var douyin = CreateDouyin(req =>
        {
            paths.Add(req.RequestUri!.AbsolutePath);
            return Json("""{"ok":true,"message":"解除禁言成功"}""");
        });

        Assert.True(await douyin.ModUnsilenceAsync("100", "42"));
        Assert.Contains(paths, p => p.EndsWith("/api/live/mod/unsilence", StringComparison.Ordinal));
        Assert.DoesNotContain(paths, p => p.EndsWith("/api/live/mod/silence", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RoomMismatch_BlocksSend()
    {
        var douyin = CreateDouyin(_ =>
            Json("""{"ok":false,"message":"room_mismatch active_web_rid=100 send_web_rid=999 active_room_id=r1"}"""));

        var result = await douyin.SendMentionDetailedAsync("999", "42", "hi", "观众");
        Assert.False(result.Ok);
        Assert.True(DouyinService.IsRoomMismatch(result.ErrorReason));
    }

    [Fact]
    public async Task NotLoggedIn_BlocksSend()
    {
        var douyin = CreateDouyin(_ =>
            Json("""{"ok":false,"message":"not_logged_in"}"""));

        var result = await douyin.SendDanmakuAsync("100", "测试");
        Assert.False(result.Ok);
        Assert.True(DouyinService.IsNotLoggedIn(result.ErrorReason));
        Assert.Equal("not_logged_in", result.ErrorReason);
    }

    [Fact]
    public async Task PermissionDenied_LoggedOnHealth()
    {
        var douyin = CreateDouyin(_ =>
            Json("""{"ok":true,"data":{"login_ok":true,"nickname":"路人","can_send":false,"can_moderate":false}}"""));

        var health = await douyin.GetHealthAsync();
        Assert.True(health!.LoginOk);
        Assert.False(health.CanSend);
        Assert.Contains("DOUYIN_PERMISSION_DENIED", ReadLog());
    }

    [Fact]
    public async Task SongRequest_SendFail_DoesNotAffectOrderOrPoints()
    {
        var db = new AppDatabase(_dir);
        var config = new ConfigManager();
        config.Load();
        config.Settings.SongRequestPolicy.Mode = SongRequestPolicyMode.Points;
        config.Settings.SongRequestPolicy.RequireConfirm = true;
        config.Settings.SongRequestPolicy.PointsCost = 10;
        config.ReplyTemplates["songRequestConfirm"] = "是否确定点歌《{song}》？";
        config.ReplyTemplates["songRequestConfirmPoints"] = "是否确定点歌《{song}》？需要 {cost} 积分";

        var log = new LogService(_dir);
        var users = new UserRepository(db);
        users.EnsureUser("u1", "观众");
        users.AddPoints("u1", "观众", 100);
        var queue = new QueueService(db);
        var permission = new SongRequestPermissionService(
            config, users, queue,
            new SongBlacklistService(new SongBlacklistRepository(db)),
            new LevelPermissionRepository(db),
            new UserLevelService(config, users));

        var kugou = new KugouService(config.Settings.Kugou, log, new HttpClient(new KugouOkHandler())
        {
            BaseAddress = new Uri("http://127.0.0.1:17888/")
        });

        using var replyQueue = new ReplyQueue(
            new DouyinService(config.Settings.Douyin, log),
            log,
            new ReplySettings { MaxPerSecond = 50, MaxRetries = 0 },
            sendMention: (_, _, _, _, _) => Task.FromResult(new MentionSendResult
            {
                Ok = false,
                ErrorReason = "send_failed",
                HttpStatus = 200
            }));

        var song = new SongRequestService(
            config, kugou, queue, permission, new ReplyService(config),
            replyQueue, new SystemMessageService(20), log);

        Assert.True(await song.HandleDanmakuAsync(
            new DanmakuItem { UserId = "u1", Nickname = "观众", Content = "点歌 泡沫" }, "room1"));
        Assert.True(await song.HandleDanmakuAsync(
            new DanmakuItem { UserId = "u1", Nickname = "观众", Content = "确定" }, "room1"));

        await Task.Delay(400);
        Assert.Equal(1, queue.WaitingCount);
        Assert.Equal(90, users.GetUser("u1")!.Points);
    }

    [Fact]
    public async Task CdpException_DoesNotAffectPoints()
    {
        var db = new AppDatabase(_dir);
        var config = new ConfigManager();
        config.Load();
        config.Settings.SongRequestPolicy.Mode = SongRequestPolicyMode.Points;
        config.Settings.SongRequestPolicy.RequireConfirm = true;
        config.Settings.SongRequestPolicy.PointsCost = 5;
        config.ReplyTemplates["songRequestConfirm"] = "是否确定点歌《{song}》？";
        config.ReplyTemplates["songRequestConfirmPoints"] = "是否确定点歌《{song}》？需要 {cost} 积分";

        var log = new LogService(_dir);
        var users = new UserRepository(db);
        users.EnsureUser("u1", "观众");
        users.AddPoints("u1", "观众", 50);
        var queue = new QueueService(db);
        var permission = new SongRequestPermissionService(
            config, users, queue,
            new SongBlacklistService(new SongBlacklistRepository(db)),
            new LevelPermissionRepository(db),
            new UserLevelService(config, users));
        var kugou = new KugouService(config.Settings.Kugou, log, new HttpClient(new KugouOkHandler())
        {
            BaseAddress = new Uri("http://127.0.0.1:17888/")
        });

        using var replyQueue = new ReplyQueue(
            new DouyinService(config.Settings.Douyin, log),
            log,
            new ReplySettings { MaxPerSecond = 50, MaxRetries = 0 },
            sendMention: (_, _, _, _, _) => throw new HttpRequestException("cdp down"));

        var song = new SongRequestService(
            config, kugou, queue, permission, new ReplyService(config),
            replyQueue, new SystemMessageService(20), log);

        await song.HandleDanmakuAsync(
            new DanmakuItem { UserId = "u1", Nickname = "观众", Content = "点歌 泡沫" }, "room1");
        await song.HandleDanmakuAsync(
            new DanmakuItem { UserId = "u1", Nickname = "观众", Content = "确定" }, "room1");
        await Task.Delay(400);

        Assert.Equal(1, queue.WaitingCount);
        Assert.Equal(45, users.GetUser("u1")!.Points);
    }

    [Fact]
    public async Task DirectBanCommand_CallsSilenceWithDuration()
    {
        var bodies = new List<string>();
        var douyin = CreateDouyin(req =>
        {
            bodies.Add(req.Content?.ReadAsStringAsync().GetAwaiter().GetResult() ?? "");
            if (req.RequestUri!.AbsolutePath.EndsWith("/lookup"))
            {
                return Json("""{"ok":true,"data":[{"user_id":"42","nickname":"测试用户"}]}""");
            }

            return Json("""{"ok":true}""");
        });

        var db = new AppDatabase(_dir);
        var config = new ConfigManager();
        config.Settings.BanVote.Enabled = true;
        var log = new LogService(_dir);
        using var replyQueue = new ReplyQueue(
            douyin, log, new ReplySettings { MaxPerSecond = 50 },
            sendMention: (_, _, _, _, _) => Task.FromResult(new MentionSendResult { Ok = true }));
        using var ban = new BanVoteService(
            config, new BanVoteRepository(db), new UserRepository(db), douyin,
            replyQueue, new ReplyService(config), new SystemMessageService(20), log);

        Assert.True(await ban.TryHandleAsync(new DanmakuItem
        {
            UserId = "admin",
            Nickname = "主播",
            Content = "禁言 测试用户 10"
        }, "100"));

        Assert.Contains(bodies, b => b.Contains("\"duration\":10", StringComparison.Ordinal)
                                      && b.Contains("\"action\":\"silence\"", StringComparison.Ordinal));
    }

    private string _lastPath = "";
    private DouyinService CreateDouyin(Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        var handler = new RecordingHandler(req =>
        {
            _lastPath = req.RequestUri?.AbsolutePath ?? "";
            return respond(req);
        });
        return new DouyinService(new DouyinSettings { BaseUrl = "http://127.0.0.1:17891" }, new LogService(_dir),
            new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:17891/") });
    }

    private string GetLastPath() => _lastPath;

    private string ReadLog()
    {
        var path = Path.Combine(Path.GetFullPath(Path.Combine(_dir, "..", "logs")), "douyin.log");
        if (!File.Exists(path))
        {
            path = Path.Combine(_dir, "logs", "douyin.log");
        }

        return File.Exists(path) ? File.ReadAllText(path) : "";
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

    private sealed class KugouOkHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? "";
            if (path.Contains("search", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(JsonObj(new
                {
                    code = 0,
                    data = new
                    {
                        歌单 = new object[]
                        {
                            new { hash = "hash1", 歌曲名称 = "泡沫", 歌手名称 = "G.E.M.邓紫棋", id = "1" }
                        }
                    }
                }));
            }

            if (path.Contains("song/url", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(JsonObj(new
                {
                    code = 0,
                    data = new { url = "http://fs/yp/f.mp3", is_preview = false }
                }));
            }

            if (path.Contains("login/status", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(JsonObj(new { code = 0, data = new { logged_in = true, nickname = "t" } }));
            }

            return Task.FromResult(JsonObj(new { code = 1 }));
        }

        private static HttpResponseMessage JsonObj(object body)
        {
            var json = JsonSerializer.Serialize(body);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
        }
    }
}
