using System.Net;
using System.Text;
using System.Text.Json;
using LiveAssistant.Config;
using LiveAssistant.Database;
using LiveAssistant.Models;
using LiveAssistant.Services;
using LiveAssistant.Services.AiSpeech;
using Xunit;

namespace LiveAssistant.Tests;

/// <summary>
/// 出站过滤 / 扣费补偿 / 队列事务 / 跨房并发的加固一致性测试。
/// </summary>
public sealed class FinalConsistencyHardeningTests : IDisposable
{
    private readonly string _dir;

    public FinalConsistencyHardeningTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "la-harden-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    [Fact]
    public void BotTrack_ThenRealAudienceSameContent_MustNotFilter()
    {
        var tracker = new OutboundReplyTracker(TimeSpan.FromMinutes(2));
        var log = new LogService(Path.Combine(_dir, "bot-audience"));
        var danmaku = new DanmakuService(
            new DouyinService(new DouyinSettings(), log),
            log,
            new SystemMessageService(20),
            outboundTracker: tracker);

        tracker.Track("reply-x", "好的");
        danmaku.SetDouyinLoginNicknameForTests("机器人账号");

        Assert.False(danmaku.ShouldIgnoreBotMessage("audience-msg-1", "真实观众", "好的"));
    }

    [Fact]
    public void BotTrack_ActualBotEcho_MustFilter()
    {
        var tracker = new OutboundReplyTracker(TimeSpan.FromMinutes(2));
        var log = new LogService(Path.Combine(_dir, "bot-echo"));
        var danmaku = new DanmakuService(
            new DouyinService(new DouyinSettings(), log),
            log,
            new SystemMessageService(20),
            outboundTracker: tracker);

        tracker.Track("reply-x", "好的");
        danmaku.SetDouyinLoginNicknameForTests("机器人账号");

        Assert.True(danmaku.ShouldIgnoreBotMessage("any-new-id", "机器人账号", "好的"));
    }

    [Fact]
    public void DifferentMsgId_SameContent_RealUser_MustNotFilter()
    {
        var tracker = new OutboundReplyTracker(TimeSpan.FromMinutes(2));
        var log = new LogService(Path.Combine(_dir, "diff-msgid"));
        var danmaku = new DanmakuService(
            new DouyinService(new DouyinSettings(), log),
            log,
            new SystemMessageService(20),
            outboundTracker: tracker);

        tracker.Track("reply-1", "好的");
        danmaku.SetDouyinLoginNicknameForTests("机器人账号");

        Assert.False(danmaku.ShouldIgnoreBotMessage("different-msg", "路人甲", "好的"));
    }

    [Fact]
    public void PlatformMessageIdMatched_WhenAvailable_MustFilter()
    {
        var tracker = new OutboundReplyTracker(TimeSpan.FromMinutes(2));
        var log = new LogService(Path.Combine(_dir, "plat-msgid"));
        var danmaku = new DanmakuService(
            new DouyinService(new DouyinSettings(), log),
            log,
            new SystemMessageService(20),
            outboundTracker: tracker);

        tracker.Track("local-reply", "内容", platformMessageId: "plat-99");

        Assert.True(tracker.MatchesTrackedMessageId("plat-99"));
        Assert.True(danmaku.ShouldIgnoreBotMessage("plat-99", "路人", "内容"));
    }

    [Fact]
    public async Task ChargeCommit_ThenForcedAbort_RefundSuccess_QueueEmpty()
    {
        using var ctx = CreateCtx(SongRequestPolicyMode.Points, pointsCost: 10, maxQueue: 50);
        ctx.Users.EnsureUser("u1", "观众");
        ctx.Users.TryChangePoints("u1", "观众", 100, PointsTransactionType.AdminAdjust, "seed", null, out _);

        Assert.True(await ctx.Song.HandleDanmakuAsync(
            new DanmakuItem { UserId = "u1", Nickname = "观众", Content = "点歌 泡沫" }, "room1"));

        ctx.Song.TestAfterChargeBeforeFinalize = () => throw new Exception("abort");

        Assert.True(await ctx.Song.HandleDanmakuAsync(
            new DanmakuItem { UserId = "u1", Nickname = "观众", Content = "确定" }, "room1"));

        Assert.Equal(0, ctx.Queue.WaitingCount);
        Assert.Equal(100, ctx.Users.GetUser("u1")!.Points);
    }

    [Fact]
    public async Task ChargeCommit_ThenForcedAbort_RefundFails_ReportsCompensationFailed()
    {
        using var ctx = CreateCtx(SongRequestPolicyMode.Points, pointsCost: 10, maxQueue: 50);
        ctx.Users.EnsureUser("u1", "观众");
        ctx.Users.TryChangePoints("u1", "观众", 100, PointsTransactionType.AdminAdjust, "seed", null, out _);

        Assert.True(await ctx.Song.HandleDanmakuAsync(
            new DanmakuItem { UserId = "u1", Nickname = "观众", Content = "点歌 泡沫" }, "room1"));

        ctx.Permission.TestForceRefundFailure = () => true;
        ctx.Song.TestAfterChargeBeforeFinalize = () => throw new Exception("abort");

        Assert.True(await ctx.Song.HandleDanmakuAsync(
            new DanmakuItem { UserId = "u1", Nickname = "观众", Content = "确定" }, "room1"));

        Assert.Equal(0, ctx.Queue.WaitingCount);
        Assert.Contains(ctx.System.Messages, m => m.Contains("人工检查积分", StringComparison.Ordinal));
        // 退款强制失败时积分可能仍被扣；禁止断言已退回
        Assert.True(ctx.Users.GetUser("u1")!.Points <= 100);
    }

    [Fact]
    public async Task CreditConsume_ThenAbort_CreditRestoreFail_Reports()
    {
        using var ctx = CreateCtx(SongRequestPolicyMode.Free, pointsCost: 0, maxQueue: 50);
        ctx.Users.EnsureUser("u1", "次卡用户");
        Assert.True(ctx.Users.AddSongPermissionCredits("u1", 1));
        Assert.Equal(1, ctx.Users.GetUser("u1")!.SongPermissionCredits);

        Assert.True(await ctx.Song.HandleDanmakuAsync(
            new DanmakuItem { UserId = "u1", Nickname = "次卡用户", Content = "点歌 泡沫" }, "room1"));

        ctx.Permission.TestForceCreditRestoreFailure = () => true;
        ctx.Song.TestAfterChargeBeforeFinalize = () => throw new Exception("abort");

        Assert.True(await ctx.Song.HandleDanmakuAsync(
            new DanmakuItem { UserId = "u1", Nickname = "次卡用户", Content = "确定" }, "room1"));

        Assert.Equal(0, ctx.Queue.WaitingCount);
        Assert.Equal(0, ctx.Users.GetUser("u1")!.SongPermissionCredits);
        Assert.Contains(ctx.System.Messages, m => m.Contains("人工检查积分", StringComparison.Ordinal));
    }

    [Fact]
    public async Task LevelRefreshThrows_AfterCommit_DoesNotRollbackChargeOrQueue()
    {
        using var ctx = CreateCtx(SongRequestPolicyMode.Points, pointsCost: 10, maxQueue: 50);
        ctx.Users.EnsureUser("u1", "观众");
        ctx.Users.TryChangePoints("u1", "观众", 100, PointsTransactionType.AdminAdjust, "seed", null, out _);
        var requestBefore = ctx.Users.GetUser("u1")!.RequestCount;

        Assert.True(await ctx.Song.HandleDanmakuAsync(
            new DanmakuItem { UserId = "u1", Nickname = "观众", Content = "点歌 泡沫" }, "room1"));

        ctx.Permission.TestAfterCommitBeforeLevelRefresh = () => throw new Exception("level");

        Assert.True(await ctx.Song.HandleDanmakuAsync(
            new DanmakuItem { UserId = "u1", Nickname = "观众", Content = "确定" }, "room1"));

        Assert.True(await WaitUntil(() => ctx.Queue.WaitingCount == 1, TimeSpan.FromSeconds(3)));
        Assert.Equal(1, ctx.Queue.WaitingCount);
        Assert.Equal(90, ctx.Users.GetUser("u1")!.Points);
        Assert.Equal(requestBefore + 1, ctx.Users.GetUser("u1")!.RequestCount);
    }

    [Fact]
    public void QueueAdd_ReindexThrows_MemoryAndDbConsistent()
    {
        var db = new AppDatabase(Path.Combine(_dir, "q-add"));
        var queue = new QueueService(db);
        queue.TestBeforeAddTransactionCommit = () => throw new Exception("reindex");

        Assert.ThrowsAny<Exception>(() =>
        {
            queue.TryAddWithPriority(
                new QueueItem
                {
                    UserId = "u1",
                    Nickname = "N",
                    SongName = "泡沫",
                    PlayUrl = "http://x/a.mp3"
                },
                0,
                50,
                bypassCapacity: false,
                out _);
        });

        Assert.Equal(0, queue.WaitingCount);
        Assert.Equal(0, CountDbWaiting(db));
    }

    [Fact]
    public void QueueRemove_StatusUpdateThrows_MemoryKeepsItem()
    {
        var db = new AppDatabase(Path.Combine(_dir, "q-rm"));
        var queue = new QueueService(db);
        Assert.True(queue.TryAddWithPriority(
            new QueueItem
            {
                UserId = "u1",
                Nickname = "N",
                SongName = "泡沫",
                PlayUrl = "http://x/a.mp3"
            },
            0,
            50,
            bypassCapacity: false,
            out var added));
        Assert.NotNull(added);
        Assert.Equal(1, queue.WaitingCount);

        queue.TestBeforeRemoveStatusUpdate = () => throw new Exception("upd");

        Assert.ThrowsAny<Exception>(() => queue.Remove(added!.Id));
        Assert.Equal(1, queue.WaitingCount);
    }

    [Fact]
    public async Task QueueReserveThrows_ConfirmCanRetry()
    {
        using var ctx = CreateCtx(SongRequestPolicyMode.Points, pointsCost: 10, maxQueue: 50);
        ctx.Users.EnsureUser("u1", "观众");
        ctx.Users.TryChangePoints("u1", "观众", 100, PointsTransactionType.AdminAdjust, "seed", null, out _);

        Assert.True(await ctx.Song.HandleDanmakuAsync(
            new DanmakuItem { UserId = "u1", Nickname = "观众", Content = "点歌 泡沫" }, "room1"));

        ctx.Queue.TestBeforeAddTransactionCommit = () => throw new Exception("reindex");

        Exception? firstBoom = null;
        try
        {
            await ctx.Song.HandleDanmakuAsync(
                new DanmakuItem { UserId = "u1", Nickname = "观众", Content = "确定" }, "room1");
        }
        catch (Exception ex)
        {
            firstBoom = ex;
        }

        Assert.NotNull(firstBoom);
        Assert.Equal(0, ctx.Queue.WaitingCount);
        Assert.Equal(100, ctx.Users.GetUser("u1")!.Points);

        ctx.Queue.TestBeforeAddTransactionCommit = null;

        Assert.True(await ctx.Song.HandleDanmakuAsync(
            new DanmakuItem { UserId = "u1", Nickname = "观众", Content = "确定" }, "room1"));
        Assert.True(await WaitUntil(() => ctx.Queue.WaitingCount == 1, TimeSpan.FromSeconds(3)));
        Assert.Equal(90, ctx.Users.GetUser("u1")!.Points);
    }

    [Fact]
    public async Task SameUser_RoomA_RoomB_ConcurrentConfirm_NoOvercharge()
    {
        using var ctx = CreateCtx(SongRequestPolicyMode.Points, pointsCost: 10, maxQueue: 50);
        ctx.Users.EnsureUser("u1", "跨房");
        const int startPoints = 100;
        const int cost = 10;
        ctx.Users.TryChangePoints("u1", "跨房", startPoints, PointsTransactionType.AdminAdjust, "seed", null, out _);

        Assert.True(await ctx.Song.HandleDanmakuAsync(
            new DanmakuItem { UserId = "u1", Nickname = "跨房", Content = "点歌 泡沫" }, "roomA"));
        Assert.True(await ctx.Song.HandleDanmakuAsync(
            new DanmakuItem { UserId = "u1", Nickname = "跨房", Content = "点歌 泡沫" }, "roomB"));

        await Task.WhenAll(
            ctx.Song.HandleDanmakuAsync(new DanmakuItem { UserId = "u1", Nickname = "跨房", Content = "确定" }, "roomA"),
            ctx.Song.HandleDanmakuAsync(new DanmakuItem { UserId = "u1", Nickname = "跨房", Content = "确定" }, "roomB"));

        Assert.True(await WaitUntil(() => ctx.Queue.WaitingCount >= 1, TimeSpan.FromSeconds(4)));

        var points = ctx.Users.GetUser("u1")!.Points;
        var waiting = ctx.Queue.WaitingCount;
        var deducted = startPoints - points;

        Assert.True(points >= 0, $"积分为负：{points}");
        Assert.Equal(0, deducted % cost);
        Assert.Equal(deducted / cost, waiting);
        Assert.True(deducted <= waiting * cost, $"超额扣费 deducted={deducted} waiting={waiting}");
        Assert.Equal(startPoints - waiting * cost, points);
        // 容量足够时两房均可成功
        Assert.True(waiting is 1 or 2, $"waiting={waiting}");
        Assert.True(points is 90 or 80, $"points={points}");
    }

    [Fact]
    public async Task Dispose_DuringActiveAiTask_NoObjectDisposedException()
    {
        var temp = Path.Combine(_dir, "ai-dispose");
        Directory.CreateDirectory(temp);
        Environment.SetEnvironmentVariable("LA_DATA_DIR", temp);
        try
        {
            var config = new ConfigManager();
            config.Load();
            config.Settings.AiSpeech.Enabled = true;
            config.Settings.AiSpeech.TestMode = true;
            config.Settings.AiSpeech.OllamaUrl = "http://127.0.0.1:1";
            config.Settings.AiSpeech.TtsUrl = "http://127.0.0.1:1";
            config.Settings.AiSpeech.OllamaTimeoutSeconds = 2;
            config.Settings.AiSpeech.TtsTimeoutSeconds = 2;
            config.Settings.AiSpeech.MinIntervalSeconds = 3;
            var log = new LogService(temp);
            var coordinator = new AiSpeechCoordinator(config, log, new OutboundReplyTracker());

            ObjectDisposedException? disposedFromWork = null;
            ObjectDisposedException? disposedFromDispose = null;
            var work = Task.Run(async () =>
            {
                try
                {
                    await coordinator.TestAiAsync();
                }
                catch (ObjectDisposedException ex)
                {
                    Interlocked.CompareExchange(ref disposedFromWork, ex, null);
                }
                catch
                {
                    // 不可达 URL / 取消等其它异常可接受
                }
            });

            coordinator.TryEnqueueDanmaku(
                new DanmakuItem
                {
                    MsgId = "m-dispose",
                    UserId = "u1",
                    Nickname = "观众",
                    Content = "主播你好啊今天开心吗",
                    MsgType = "chat",
                    Timestamp = DateTime.Now
                },
                roomOwnerNickname: "主播",
                loginNickname: "机器人");

            await Task.Delay(80);
            try
            {
                coordinator.Dispose();
            }
            catch (ObjectDisposedException ex)
            {
                disposedFromDispose = ex;
            }

            await Task.WhenAny(work, Task.Delay(8000));
            Assert.Null(disposedFromDispose);
            Assert.Null(disposedFromWork);
        }
        finally
        {
            Environment.SetEnvironmentVariable("LA_DATA_DIR", null);
        }
    }

    private SongCtx CreateCtx(SongRequestPolicyMode mode, int pointsCost, int maxQueue)
    {
        var db = new AppDatabase(Path.Combine(_dir, Guid.NewGuid().ToString("N")));
        var config = new ConfigManager();
        config.Load();
        config.Settings.SongRequestPolicy.Mode = mode;
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
        var system = new SystemMessageService(50);
        var permission = new SongRequestPermissionService(
            config, users, queue,
            new SongBlacklistService(new SongBlacklistRepository(db)),
            new LevelPermissionRepository(db),
            new UserLevelService(config, users),
            log: log);
        var song = new SongRequestService(
            config, kugou, queue, permission, new ReplyService(config),
            replyQueue, system, log);
        return new SongCtx(db, users, queue, song, permission, system, replyQueue);
    }

    private static int CountDbWaiting(AppDatabase db)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM queue_items WHERE status = 'waiting'";
        return Convert.ToInt32(cmd.ExecuteScalar());
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
        SystemMessageService System,
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
