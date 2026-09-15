using System.Net;
using System.Text;
using System.Text.Json;
using LiveAssistant.Config;
using LiveAssistant.Database;
using LiveAssistant.Models;
using LiveAssistant.Services;
using Xunit;

namespace LiveAssistant.Tests;

/// <summary>点歌入队/扣分事务一致性 + pending 按直播间隔离。</summary>
public sealed class AfterFixTransactionConsistencyTests : IDisposable
{
    private readonly string _dir;

    public AfterFixTransactionConsistencyTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "la-tx-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    [Fact]
    public async Task QueueAddOk_PointsDeductThrows_RollsBackQueue_PointsUnchanged()
    {
        var ctx = CreateCtx(pointsCost: 10, maxQueue: 50);
        ctx.Users.EnsureUser("u1", "观众");
        ctx.Users.TryChangePoints("u1", "观众", 100, PointsTransactionType.AdminAdjust, "seed", null, out _);

        Assert.True(await ctx.Song.HandleDanmakuAsync(
            new DanmakuItem { UserId = "u1", Nickname = "观众", Content = "点歌 泡沫" }, "room1"));

        using (var conn = ctx.Db.Open())
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "DROP TABLE points_ledger";
            cmd.ExecuteNonQuery();
        }

        Exception? boom = null;
        try
        {
            await ctx.Song.HandleDanmakuAsync(
                new DanmakuItem { UserId = "u1", Nickname = "观众", Content = "确定" }, "room1");
        }
        catch (Exception ex)
        {
            boom = ex;
        }

        Assert.Null(boom);
        Assert.Equal(0, ctx.Queue.WaitingCount);
        Assert.Equal(100, ctx.Users.GetUser("u1")!.Points);
    }

    [Fact]
    public async Task PointsDeductOk_RecordRequestFails_RollsBackQueue_RestoresPoints()
    {
        var ctx = CreateCtx(pointsCost: 10, maxQueue: 50);
        ctx.Users.EnsureUser("u1", "观众");
        ctx.Users.TryChangePoints("u1", "观众", 100, PointsTransactionType.AdminAdjust, "seed", null, out _);

        ctx.Permission.TestBeforeRecordRequest = (_, _) =>
            throw new InvalidOperationException("simulated record failure");

        Assert.True(await ctx.Song.HandleDanmakuAsync(
            new DanmakuItem { UserId = "u1", Nickname = "观众", Content = "点歌 泡沫" }, "room1"));

        Exception? boom = null;
        try
        {
            await ctx.Song.HandleDanmakuAsync(
                new DanmakuItem { UserId = "u1", Nickname = "观众", Content = "确定" }, "room1");
        }
        catch (Exception ex)
        {
            boom = ex;
        }

        Assert.Null(boom);
        Assert.Equal(0, ctx.Queue.WaitingCount);
        Assert.Equal(100, ctx.Users.GetUser("u1")!.Points);
    }

    [Fact]
    public async Task LastSlot_TwoUsers_OnlyOneSucceeds_LoserPointsUnchanged()
    {
        var ctx = CreateCtx(pointsCost: 10, maxQueue: 1);
        ctx.Users.EnsureUser("a", "甲");
        ctx.Users.EnsureUser("b", "乙");
        ctx.Users.TryChangePoints("a", "甲", 100, PointsTransactionType.AdminAdjust, "seed", null, out _);
        ctx.Users.TryChangePoints("b", "乙", 100, PointsTransactionType.AdminAdjust, "seed", null, out _);

        Assert.True(await ctx.Song.HandleDanmakuAsync(
            new DanmakuItem { UserId = "a", Nickname = "甲", Content = "点歌 泡沫" }, "room1"));
        Assert.True(await ctx.Song.HandleDanmakuAsync(
            new DanmakuItem { UserId = "b", Nickname = "乙", Content = "点歌 泡沫" }, "room1"));

        await Task.WhenAll(
            ctx.Song.HandleDanmakuAsync(new DanmakuItem { UserId = "a", Nickname = "甲", Content = "确定" }, "room1"),
            ctx.Song.HandleDanmakuAsync(new DanmakuItem { UserId = "b", Nickname = "乙", Content = "确定" }, "room1"));

        Assert.Equal(1, ctx.Queue.WaitingCount);
        var pa = ctx.Users.GetUser("a")!.Points;
        var pb = ctx.Users.GetUser("b")!.Points;
        Assert.True((pa == 90 && pb == 100) || (pa == 100 && pb == 90),
            $"期望仅一人扣分，实际 a={pa} b={pb}");
        Assert.Equal(190, pa + pb);
    }

    [Fact]
    public async Task SameUser_DoubleConfirm_EnqueuesOnce_DeductsOnce()
    {
        var ctx = CreateCtx(pointsCost: 10, maxQueue: 50);
        ctx.Users.EnsureUser("u1", "同用户");
        ctx.Users.TryChangePoints("u1", "同用户", 100, PointsTransactionType.AdminAdjust, "seed", null, out _);

        Assert.True(await ctx.Song.HandleDanmakuAsync(
            new DanmakuItem { UserId = "u1", Nickname = "同用户", Content = "点歌 泡沫" }, "room1"));

        var confirm = new DanmakuItem { UserId = "u1", Nickname = "同用户", Content = "确定" };
        await Task.WhenAll(
            ctx.Song.HandleDanmakuAsync(confirm, "room1"),
            ctx.Song.HandleDanmakuAsync(confirm, "room1"));

        Assert.Equal(1, ctx.Queue.WaitingCount);
        Assert.Equal(90, ctx.Users.GetUser("u1")!.Points);
    }

    [Fact]
    public async Task Pending_IsIsolatedByWebRid_ConfirmInOtherRoomDoesNotConsume()
    {
        var ctx = CreateCtx(pointsCost: 10, maxQueue: 50);
        ctx.Users.EnsureUser("u1", "跨房");
        ctx.Users.TryChangePoints("u1", "跨房", 100, PointsTransactionType.AdminAdjust, "seed", null, out _);

        Assert.True(await ctx.Song.HandleDanmakuAsync(
            new DanmakuItem { UserId = "u1", Nickname = "跨房", Content = "点歌 泡沫" }, "roomA"));

        // 直播间 B 的「确定」不能消费 A 的 pending
        Assert.True(await ctx.Song.HandleDanmakuAsync(
            new DanmakuItem { UserId = "u1", Nickname = "跨房", Content = "确定" }, "roomB"));
        Assert.Equal(0, ctx.Queue.WaitingCount);
        Assert.Equal(100, ctx.Users.GetUser("u1")!.Points);

        // A 仍可确认入队
        Assert.True(await ctx.Song.HandleDanmakuAsync(
            new DanmakuItem { UserId = "u1", Nickname = "跨房", Content = "确定" }, "roomA"));
        Assert.True(await WaitUntil(() => ctx.Queue.WaitingCount == 1, TimeSpan.FromSeconds(3)));
        Assert.Equal(90, ctx.Users.GetUser("u1")!.Points);
    }

    [Fact]
    public void SessionStore_SameUserDifferentRooms_Independent()
    {
        var store = new SongRequestSessionStore(TimeSpan.FromMinutes(5));
        store.Set(new SongRequestSession
        {
            WebRid = "roomA",
            UserId = "u1",
            Nickname = "N",
            Keyword = "泡沫",
            Step = SongRequestSessionStep.Confirm,
            Selected = new SongSearchCandidate { SongName = "泡沫", Artist = "邓紫棋" }
        });
        store.Set(new SongRequestSession
        {
            WebRid = "roomB",
            UserId = "u1",
            Nickname = "N",
            Keyword = "晴天",
            Step = SongRequestSessionStep.Confirm,
            Selected = new SongSearchCandidate { SongName = "晴天", Artist = "周杰伦" }
        });

        Assert.Equal("泡沫", store.Get("roomA", "u1")!.Keyword);
        Assert.Equal("晴天", store.Get("roomB", "u1")!.Keyword);

        store.Clear("roomA", "u1");
        Assert.Null(store.Get("roomA", "u1"));
        Assert.NotNull(store.Get("roomB", "u1"));
    }

    private SongCtx CreateCtx(int pointsCost, int maxQueue)
    {
        var db = new AppDatabase(Path.Combine(_dir, Guid.NewGuid().ToString("N")));
        var config = new ConfigManager();
        config.Load();
        config.Settings.SongRequestPolicy.Mode = SongRequestPolicyMode.Points;
        config.Settings.SongRequestPolicy.RequireConfirm = true;
        config.Settings.SongRequestPolicy.PointsCost = pointsCost;
        config.Settings.Queue.MaxSize = maxQueue;
        var log = new LogService(Path.Combine(_dir, "log-" + Guid.NewGuid().ToString("N")));
        var users = new UserRepository(db);
        var replyQueue = new ReplyQueue(
            new DouyinService(config.Settings.Douyin, log),
            log,
            new ReplySettings { MaxPerSecond = 50, MaxRetries = 0, SongRequestBatchWindowMs = 50 },
            sendMention: (_, _, _, _) => Task.FromResult(true));
        var kugou = new KugouService(config.Settings.Kugou, log, new HttpClient(new KugouOkHandler())
        {
            BaseAddress = new Uri("http://127.0.0.1:17888/")
        });
        var queue = new QueueService(db);
        var permission = new SongRequestPermissionService(
            config, users, queue,
            new SongBlacklistService(new SongBlacklistRepository(db)),
            new LevelPermissionRepository(db),
            new UserLevelService(config, users));
        var song = new SongRequestService(
            config, kugou, queue, permission, new ReplyService(config),
            replyQueue, new SystemMessageService(20), log);
        return new SongCtx(db, users, queue, song, permission, replyQueue);
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

            await Task.Delay(40);
        }

        return cond();
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* ignore */ }
    }

    private sealed record SongCtx(
        AppDatabase Db,
        UserRepository Users,
        QueueService Queue,
        SongRequestService Song,
        SongRequestPermissionService Permission,
        ReplyQueue ReplyQueue) : IDisposable
    {
        public void Dispose() => ReplyQueue.Dispose();
    }

    private sealed class KugouOkHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? "";
            if (path.Contains("search", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(Json(new
                {
                    code = 0,
                    data = new
                    {
                        歌单 = new object[]
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

            if (path.Contains("song/url", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(Json(new
                {
                    code = 0,
                    data = new { url = "http://fs/yp/f_paomo.mp3", is_preview = false }
                }));
            }

            if (path.Contains("login/status", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(Json(new { code = 0, data = new { logged_in = true, nickname = "test" } }));
            }

            return Task.FromResult(Json(new { code = 1, msg = "unknown" }));
        }

        private static HttpResponseMessage Json(object body)
            => new(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
            };
    }
}
