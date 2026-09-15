using System.Collections.Concurrent;
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
/// 第二轮审查前的针对性一致性测试（只加测试，不改业务代码）。
/// </summary>
public sealed class AfterFixConsistencyTests : IDisposable
{
    private readonly string _dir;

    public AfterFixConsistencyTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "la-after-fix-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    [Fact]
    public async Task ConcurrentConfirm_SameUser_EnqueuesOnce_DeductsOnce()
    {
        var ctx = CreateSongCtx(SongRequestPolicyMode.Points, pointsCost: 10, maxQueue: 50);
        ctx.Users.EnsureUser("u1", "同用户");
        ctx.Users.TryChangePoints("u1", "同用户", 100, PointsTransactionType.AdminAdjust, "seed", null, out _);

        Assert.True(await ctx.Song.HandleDanmakuAsync(
            new DanmakuItem { UserId = "u1", Nickname = "同用户", Content = "点歌 泡沫" }, "room1"));

        var confirm = new DanmakuItem { UserId = "u1", Nickname = "同用户", Content = "确定" };
        await Task.WhenAll(
            ctx.Song.HandleDanmakuAsync(confirm, "room1"),
            ctx.Song.HandleDanmakuAsync(confirm, "room1"));

        Assert.True(await WaitUntil(() => ctx.Queue.WaitingCount >= 1, TimeSpan.FromSeconds(4)));
        Assert.Equal(1, ctx.Queue.WaitingCount);
        Assert.Equal(90, ctx.Users.GetUser("u1")!.Points);
    }

    [Fact]
    public async Task LastQueueSlot_TwoUsersConfirm_OnlyOneSucceeds_LoserNotCharged()
    {
        var ctx = CreateSongCtx(SongRequestPolicyMode.Points, pointsCost: 10, maxQueue: 1);
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

        Assert.True(await WaitUntil(() => ctx.Queue.WaitingCount == 1, TimeSpan.FromSeconds(4)));
        Assert.Equal(1, ctx.Queue.WaitingCount);

        var pa = ctx.Users.GetUser("a")!.Points;
        var pb = ctx.Users.GetUser("b")!.Points;
        // 恰好一人扣 10，另一人仍 100
        Assert.True((pa == 90 && pb == 100) || (pa == 100 && pb == 90),
            $"期望仅一人扣积分，实际 a={pa} b={pb}");
        Assert.Equal(190, pa + pb);
    }

    [Fact]
    public async Task EnqueueThenPointsLedgerThrow_MustNotLeaveSongWithoutCharge()
    {
        var ctx = CreateSongCtx(SongRequestPolicyMode.Points, pointsCost: 10, maxQueue: 50);
        ctx.Users.EnsureUser("u1", "观众");
        ctx.Users.TryChangePoints("u1", "观众", 100, PointsTransactionType.AdminAdjust, "seed", null, out _);

        Assert.True(await ctx.Song.HandleDanmakuAsync(
            new DanmakuItem { UserId = "u1", Nickname = "观众", Content = "点歌 泡沫" }, "room1"));

        // 破坏流水表：入队成功后扣积分写流水会抛异常
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

        var points = ctx.Users.GetUser("u1")!.Points;
        var waiting = ctx.Queue.WaitingCount;

        Assert.Equal(0, waiting);
        Assert.Equal(100, points);
        Assert.Null(boom);
    }

    [Fact]
    public async Task Skip_DeductOkButCommandRejected_RefundsPoints()
    {
        var db = new AppDatabase(Path.Combine(_dir, "skip-refund"));
        var config = new ConfigManager();
        config.Load();
        config.Settings.SongRequestPolicy.Mode = SongRequestPolicyMode.Points;
        config.Settings.SongRequestPolicy.SkipPointsCost = 20;
        var log = new LogService(Path.Combine(_dir, "skip-log"));
        var users = new UserRepository(db);
        users.EnsureUser("u1", "切歌侠");
        users.TryChangePoints("u1", "切歌侠", 100, PointsTransactionType.AdminAdjust, "seed", null, out _);

        var queue = new QueueService(db);
        queue.BeginPlaying(new QueueItem
        {
            UserId = "owner",
            Nickname = "主播",
            SongName = "在播",
            PlayUrl = "http://x/a.mp3"
        });

        var playback = new PlaybackService(log);
        var system = new SystemMessageService(20);
        var commands = new PlaybackCommandQueue(
            config, queue, new KugouService(config.Settings.Kugou, log),
            new RandomPlaylistService(config, db), playback, new ReplyService(config), system, log);
        var engine = new PlaybackEngine(config, commands, system);
        using var replyQueue = new ReplyQueue(
            new DouyinService(config.Settings.Douyin, log),
            log,
            new ReplySettings { MaxPerSecond = 50, MaxRetries = 0 },
            sendMention: (_, _, _, _) => Task.FromResult(true));
        var skip = new SkipSongService(
            config, users, playback, queue, engine, new ReplyService(config), replyQueue, system, log);

        // 关闭命令队列，使 SkipAsync / EnqueueSkipAsync 返回 false
        commands.Dispose();

        Assert.True(await skip.TryHandleAsync(
            new DanmakuItem { UserId = "u1", Nickname = "切歌侠", Content = "切歌" }, "room1"));

        Assert.Equal(100, users.GetUser("u1")!.Points);
    }

    [Fact]
    public async Task Outbound_EchoRightAfterSend_IsFiltered()
    {
        var tracker = new OutboundReplyTracker(TimeSpan.FromMinutes(2));
        var log = new LogService(Path.Combine(_dir, "echo"));
        using var queue = new ReplyQueue(
            new DouyinService(new DouyinSettings(), log),
            log,
            new ReplySettings { MaxPerSecond = 50, MaxRetries = 0 },
            outboundTracker: tracker,
            sendMention: (_, _, _, _) => Task.FromResult(true));

        const string text = "点歌成功《泡沫》前面还有0首";
        queue.EnqueueMention("rid", "bot-user", text);
        Assert.True(await WaitUntil(() => tracker.HasRecentOutboundContent(text), TimeSpan.FromSeconds(2)));

        // Track 使用 replyId：仅靠正文不能再判定为出站回显
        Assert.False(tracker.IsRecentOutbound(null, text));
        Assert.False(tracker.IsRecentOutbound(null, "@某人 " + text));

        var danmaku = new DanmakuService(
            new DouyinService(new DouyinSettings(), log),
            log,
            new SystemMessageService(20),
            outboundTracker: tracker);
        danmaku.SetDouyinLoginNicknameForTests("机器人账号");

        // 登录号回显应过滤；真人同文案必须放行
        Assert.True(danmaku.ShouldIgnoreBotMessage("bot-echo-1", "机器人账号", text));
        Assert.False(danmaku.ShouldIgnoreBotMessage("audience-1", "真实观众", text));
        Assert.False(danmaku.ShouldIgnoreBotMessage("audience-2", "真实观众", "@某人 " + text));
    }

    [Fact]
    public void Outbound_RealAudienceSameText_IsNotFiltered_WithoutTrack()
    {
        var tracker = new OutboundReplyTracker(TimeSpan.FromMinutes(2));
        const string text = "点歌成功《泡沫》前面还有0首";

        // 机器人尚未成功发送 / 未 Track：真人同文案不得被当回显
        Assert.False(tracker.IsRecentOutbound(null, text));
        Assert.False(tracker.IsRecentOutbound("audience-msg-1", text));
    }

    [Fact]
    public async Task SongRequestBatch_ContinuousEnqueue_DoesNotExtendFirstDeadline()
    {
        var sends = new ConcurrentBag<(DateTime At, string Content)>();
        const int windowMs = 500;
        var log = new LogService(Path.Combine(_dir, "batch-deadline"));
        using var queue = new ReplyQueue(
            new DouyinService(new DouyinSettings(), log),
            log,
            new ReplySettings
            {
                MaxPerSecond = 50,
                MaxRetries = 0,
                SongRequestBatchWindowMs = windowMs
            },
            sendMention: (_, _, content, _) =>
            {
                sends.Add((DateTime.UtcNow, content));
                return Task.FromResult(true);
            });

        var started = DateTime.UtcNow;
        queue.EnqueueSongRequestReply("rid", "u0", "N0", "歌0", 0);
        for (var i = 1; i < 10; i++)
        {
            await Task.Delay(80);
            queue.EnqueueSongRequestReply("rid", "u" + i, "N" + i, "歌" + i, i);
        }

        Assert.True(await WaitUntil(() => sends.Count > 0, TimeSpan.FromSeconds(3)));
        var firstAt = sends.Min(s => s.At);
        var elapsed = firstAt - started;

        // 若每次入队重置窗口，10*80ms 会把 deadline 不断推迟；固定窗口应约在 windowMs 附近发出
        Assert.True(elapsed < TimeSpan.FromMilliseconds(windowMs + 450),
            $"第一批 deadline 被延长了：elapsed={elapsed.TotalMilliseconds:F0}ms window={windowMs}");
        Assert.True(elapsed >= TimeSpan.FromMilliseconds(windowMs - 120),
            $"第一批过早发出：elapsed={elapsed.TotalMilliseconds:F0}ms");
    }

    [Fact]
    public async Task Ai_TestVoiceAndTestAi_DuringWorker_NoDeadlockOrPlayerDispose()
    {
        var temp = Path.Combine(_dir, "ai-concurrent");
        Directory.CreateDirectory(temp);
        Environment.SetEnvironmentVariable("LA_DATA_DIR", temp);

        using var ollama = new SlowJsonServer(delayMs: 600, body: """{"message":{"content":"测试回复"}}""");
        using var tts = new SlowWavServer(delayMs: 400);
        var config = new ConfigManager();
        config.Load();
        config.Settings.AiSpeech.Enabled = true;
        config.Settings.AiSpeech.TestMode = false;
        config.Settings.AiSpeech.OllamaUrl = ollama.Url;
        config.Settings.AiSpeech.TtsUrl = tts.Url;
        config.Settings.AiSpeech.OllamaTimeoutSeconds = 10;
        config.Settings.AiSpeech.TtsTimeoutSeconds = 10;
        config.Settings.AiSpeech.MinIntervalSeconds = 3;
        config.Settings.AiSpeech.ScoreThreshold = 0;
        var log = new LogService(temp);
        using var coordinator = new AiSpeechCoordinator(config, log, new OutboundReplyTracker());

        coordinator.TryEnqueueDanmaku(
            new DanmakuItem
            {
                UserId = "live-u",
                Nickname = "直播观众",
                Content = "主播你好啊今天开心吗",
                MsgType = "chat",
                MsgId = "m1",
                Timestamp = DateTime.Now
            },
            roomOwnerNickname: "主播",
            loginNickname: "机器人");

        Exception? boom = null;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var voiceTask = Task.Run(async () =>
        {
            try { await coordinator.TestVoiceAsync(); }
            catch (Exception ex) { Interlocked.CompareExchange(ref boom, ex, null); }
        });
        var aiTask = Task.Run(async () =>
        {
            try { await coordinator.TestAiAsync(); }
            catch (Exception ex) { Interlocked.CompareExchange(ref boom, ex, null); }
        });

        var completed = Task.WhenAll(voiceTask, aiTask);
        var finished = await Task.WhenAny(completed, Task.Delay(20000));
        sw.Stop();

        Assert.Null(boom);
        Assert.True(ReferenceEquals(finished, completed) && voiceTask.IsCompleted && aiTask.IsCompleted,
            $"疑似死锁或长时间阻塞：voice={voiceTask.Status} ai={aiTask.Status} elapsed={sw.ElapsedMilliseconds}ms");
        Assert.False(boom is ObjectDisposedException);

        // 正式 worker 可能被 Test* 串行挡住，但不应无限卡住；完成后应回到 Idle
        Assert.True(await WaitUntil(
            () => coordinator.GetStatus().Phase == AiSpeechPhase.Idle,
            TimeSpan.FromSeconds(15)));
    }

    private SongCtx CreateSongCtx(SongRequestPolicyMode mode, int pointsCost, int maxQueue)
    {
        var db = new AppDatabase(Path.Combine(_dir, Guid.NewGuid().ToString("N")));
        var config = new ConfigManager();
        config.Load();
        config.Settings.SongRequestPolicy.Mode = mode;
        config.Settings.SongRequestPolicy.RequireConfirm = true;
        config.Settings.SongRequestPolicy.PointsCost = pointsCost;
        config.Settings.Queue.MaxSize = maxQueue;
        var log = new LogService(Path.Combine(_dir, "song-" + Guid.NewGuid().ToString("N")));
        var users = new UserRepository(db);
        var replyQueue = new ReplyQueue(
            new DouyinService(config.Settings.Douyin, log),
            log,
            new ReplySettings { MaxPerSecond = 50, MaxRetries = 0, SongRequestBatchWindowMs = 50 },
            sendMention: (_, _, _, _) => Task.FromResult(true));
        var handler = new KugouOkHandler();
        var kugou = new KugouService(config.Settings.Kugou, log, new HttpClient(handler)
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
        return new SongCtx(db, users, queue, song, replyQueue);
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

    private sealed class SlowJsonServer : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _loop;
        private readonly int _delayMs;
        private readonly string _body;

        public string Url { get; }

        public SlowJsonServer(int delayMs, string body)
        {
            _delayMs = delayMs;
            _body = body;
            var port = GetFreePort();
            Url = $"http://127.0.0.1:{port}/";
            _listener.Prefixes.Add(Url);
            _listener.Start();
            _loop = Task.Run(() => LoopAsync(_cts.Token));
        }

        private async Task LoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                HttpListenerContext ctx;
                try
                {
                    ctx = await _listener.GetContextAsync().WaitAsync(ct);
                }
                catch
                {
                    break;
                }

                _ = Task.Run(async () =>
                {
                    try
                    {
                        await Task.Delay(_delayMs, ct);
                        var bytes = Encoding.UTF8.GetBytes(_body);
                        ctx.Response.StatusCode = 200;
                        ctx.Response.ContentType = "application/json";
                        await ctx.Response.OutputStream.WriteAsync(bytes, ct);
                        ctx.Response.Close();
                    }
                    catch
                    {
                        try { ctx.Response.Abort(); } catch { /* ignore */ }
                    }
                }, ct);
            }
        }

        public void Dispose()
        {
            try { _cts.Cancel(); } catch { /* ignore */ }
            try { _listener.Stop(); } catch { /* ignore */ }
            try { _listener.Close(); } catch { /* ignore */ }
            try { _cts.Dispose(); } catch { /* ignore */ }
        }
    }

    private sealed class SlowWavServer : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _loop;
        private readonly int _delayMs;
        private readonly byte[] _wav;

        public string Url { get; }

        public SlowWavServer(int delayMs)
        {
            _delayMs = delayMs;
            _wav = MinimalWav();
            var port = GetFreePort();
            Url = $"http://127.0.0.1:{port}/";
            _listener.Prefixes.Add(Url);
            _listener.Start();
            _loop = Task.Run(() => LoopAsync(_cts.Token));
        }

        private async Task LoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                HttpListenerContext ctx;
                try
                {
                    ctx = await _listener.GetContextAsync().WaitAsync(ct);
                }
                catch
                {
                    break;
                }

                _ = Task.Run(async () =>
                {
                    try
                    {
                        await Task.Delay(_delayMs, ct);
                        ctx.Response.StatusCode = 200;
                        ctx.Response.ContentType = "audio/wav";
                        await ctx.Response.OutputStream.WriteAsync(_wav, ct);
                        ctx.Response.Close();
                    }
                    catch
                    {
                        try { ctx.Response.Abort(); } catch { /* ignore */ }
                    }
                }, ct);
            }
        }

        private static byte[] MinimalWav()
        {
            // 44-byte header + 1s silence 8-bit mono 8000Hz 不足以真实播放时，用极短 PCM
            var dataSize = 160;
            var bytes = new byte[44 + dataSize];
            Encoding.ASCII.GetBytes("RIFF").CopyTo(bytes, 0);
            BitConverter.GetBytes(36 + dataSize).CopyTo(bytes, 4);
            Encoding.ASCII.GetBytes("WAVE").CopyTo(bytes, 8);
            Encoding.ASCII.GetBytes("fmt ").CopyTo(bytes, 12);
            BitConverter.GetBytes(16).CopyTo(bytes, 16);
            BitConverter.GetBytes((short)1).CopyTo(bytes, 20);
            BitConverter.GetBytes((short)1).CopyTo(bytes, 22);
            BitConverter.GetBytes(8000).CopyTo(bytes, 24);
            BitConverter.GetBytes(8000).CopyTo(bytes, 28);
            BitConverter.GetBytes((short)1).CopyTo(bytes, 32);
            BitConverter.GetBytes((short)8).CopyTo(bytes, 34);
            Encoding.ASCII.GetBytes("data").CopyTo(bytes, 36);
            BitConverter.GetBytes(dataSize).CopyTo(bytes, 40);
            return bytes;
        }

        public void Dispose()
        {
            try { _cts.Cancel(); } catch { /* ignore */ }
            try { _listener.Stop(); } catch { /* ignore */ }
            try { _listener.Close(); } catch { /* ignore */ }
            try { _cts.Dispose(); } catch { /* ignore */ }
        }
    }

    private static int GetFreePort()
    {
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
