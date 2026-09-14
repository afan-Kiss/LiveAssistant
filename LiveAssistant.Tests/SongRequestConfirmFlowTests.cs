using System.Net;
using System.Text;
using System.Text.Json;
using LiveAssistant.Config;
using LiveAssistant.Database;
using LiveAssistant.Models;
using LiveAssistant.Services;
using Xunit;

namespace LiveAssistant.Tests;

public sealed class SongRequestConfirmFlowTests : IDisposable
{
    private readonly string _dir;

    public SongRequestConfirmFlowTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "la-sr-confirm-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    [Fact]
    public async Task FreeMode_DoesNotEnqueueUntilUserReplies确定()
    {
        var ctx = CreateContext(SongRequestPolicyMode.Free);
        var user = new DanmakuItem { UserId = "u1", Nickname = "观众A", Content = "点歌 泡沫" };

        await ctx.SongRequest.HandleDanmakuAsync(user, "room1");
        Assert.Equal(0, ctx.Queue.WaitingCount);

        await ctx.SongRequest.HandleDanmakuAsync(
            new DanmakuItem { UserId = "u1", Nickname = "观众A", Content = "确定" }, "room1");
        Assert.Equal(1, ctx.Queue.WaitingCount);
    }

    [Fact]
    public async Task PointsMode_DoesNotEnqueueUntilUserReplies确定()
    {
        var ctx = CreateContext(SongRequestPolicyMode.Points, userPoints: 100, pointsCost: 10);
        var user = new DanmakuItem { UserId = "u1", Nickname = "观众B", Content = "点歌 泡沫" };

        await ctx.SongRequest.HandleDanmakuAsync(user, "room1");
        Assert.Equal(0, ctx.Queue.WaitingCount);

        await ctx.SongRequest.HandleDanmakuAsync(
            new DanmakuItem { UserId = "u1", Nickname = "观众B", Content = "确定" }, "room1");
        Assert.Equal(1, ctx.Queue.WaitingCount);
        Assert.Equal(90, ctx.Users.GetUser("u1")!.Points);
    }

    private TestContext CreateContext(
        SongRequestPolicyMode mode,
        int userPoints = 0,
        int pointsCost = 10)
    {
        var db = new AppDatabase(_dir);
        var config = new ConfigManager();
        config.Load();
        config.Settings.SongRequestPolicy.Mode = mode;
        config.Settings.SongRequestPolicy.PointsCost = pointsCost;
        config.ReplyTemplates["songRequestConfirm"] = "是否确定点歌《{song}》- {artist}？回复 确定 开始点歌";
        config.ReplyTemplates["songRequestConfirmPoints"] =
            "是否确定点歌《{song}》- {artist}？需要 {cost} 积分，回复 确定 开始点歌";

        var log = new LogService(_dir);
        var handler = new KugouStubHandler();
        var kugou = new KugouService(config.Settings.Kugou, log, new HttpClient(handler)
        {
            BaseAddress = new Uri("http://127.0.0.1:17888/")
        });
        var users = new UserRepository(db);
        users.EnsureUser("u1", "测试");
        if (userPoints > 0)
        {
            users.AddPoints("u1", "测试", userPoints);
        }

        var queue = new QueueService(db);
        var permission = new SongRequestPermissionService(
            config, users, queue,
            new SongBlacklistService(new SongBlacklistRepository(db)),
            new LevelPermissionRepository(db),
            new UserLevelService(config, users));

        var replyQueue = new ReplyQueue(
            new DouyinService(config.Settings.Douyin, log),
            log,
            config.Settings.Reply,
            sendMention: (_, _, _, _) => Task.FromResult(true));

        var songRequest = new SongRequestService(
            config, kugou, queue, permission, new ReplyService(config),
            replyQueue, new SystemMessageService(20), log);

        return new TestContext(queue, users, songRequest);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, true);
        }
        catch
        {
            // ignore
        }
    }

    private sealed record TestContext(QueueService Queue, UserRepository Users, SongRequestService SongRequest);

    private sealed class KugouStubHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? "";
            if (path.Contains("search", StringComparison.Ordinal))
            {
                return Task.FromResult(Json(new
                {
                    code = 0,
                    data = new
                    {
                        歌单 = new[]
                        {
                            new
                            {
                                hash = "hash1",
                                歌曲名称 = "泡沫",
                                歌手名称 = "G.E.M.邓紫棋",
                                id = "1"
                            }
                        }
                    }
                }));
            }

            if (path.Contains("song/url", StringComparison.Ordinal))
            {
                return Task.FromResult(Json(new
                {
                    code = 0,
                    data = new { url = "http://fs/yp/f_paomo.mp3", is_preview = false }
                }));
            }

            if (path.Contains("login/status", StringComparison.Ordinal))
            {
                return Task.FromResult(Json(new { code = 0, data = new { logged_in = false } }));
            }

            return Task.FromResult(Json(new { code = 1, msg = "unknown" }));
        }

        private static HttpResponseMessage Json(object body)
        {
            var json = JsonSerializer.Serialize(body);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
        }
    }
}
