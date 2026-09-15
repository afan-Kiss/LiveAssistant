using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using LiveAssistant.Config;
using LiveAssistant.Database;
using LiveAssistant.Models;
using LiveAssistant.Services;
using LiveAssistant.Services.AiSpeech;
using LiveAssistant.Utils;
using Xunit;

namespace LiveAssistant.Tests;

/// <summary>本轮稳定性 BUG 修复的回归测试。</summary>
public sealed class StabilityBugFixTests : IDisposable
{
    private readonly string _dir;

    public StabilityBugFixTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "la-bugfix-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    [Fact]
    public async Task SongRequest_DoesNotTriggerKeyword_AndReturnsConsumed()
    {
        var db = new AppDatabase(Path.Combine(_dir, "kw-song"));
        var config = new ConfigManager();
        config.Load();
        config.Settings.KeywordReply.Enabled = true;
        var log = new LogService(_dir);
        var kwRepo = new KeywordReplyRepository(db);
        kwRepo.Add(new KeywordReplyRule { Keyword = "点歌", ReplyContent = "关键词抢答了", Enabled = true });
        var keyword = new KeywordReplyService(config, kwRepo, new NullAIReplyService(), log);
        var users = new UserRepository(db);
        users.EnsureUser("u1", "观众");

        var sent = new ConcurrentBag<string>();
        using var replyQueue = new ReplyQueue(
            new DouyinService(config.Settings.Douyin, log),
            log,
            new ReplySettings { MaxPerSecond = 50, MaxRetries = 0 },
            sendMention: (_, _, content, _) =>
            {
                sent.Add(content);
                return Task.FromResult(true);
            });

        var song = CreateSongRequest(db, config, log, replyQueue, users);
        var item = new DanmakuItem { UserId = "u1", Nickname = "观众", Content = "点歌 泡沫", MsgType = "chat" };

        // 模拟 LiveAppHost 串行路由：点歌消费后不再走关键词
        var songConsumed = await song.HandleDanmakuAsync(item, "room1");
        Assert.True(songConsumed);
        var keywordConsumed = false;
        if (!songConsumed)
        {
            keywordConsumed = await keyword.TryHandleAsync(item, replyQueue, new ReplyService(config), users, "room1");
        }

        await Task.Delay(200);
        Assert.False(keywordConsumed);
        Assert.DoesNotContain(sent, s => s.Contains("关键词抢答"));
    }

    [Fact]
    public async Task Confirm_IsConsumedOnce_IdleChatNotConsumed()
    {
        var db = new AppDatabase(Path.Combine(_dir, "confirm"));
        var config = new ConfigManager();
        config.Load();
        config.Settings.SongRequestPolicy.Mode = SongRequestPolicyMode.Free;
        config.Settings.SongRequestPolicy.RequireConfirm = true;
        var log = new LogService(_dir);
        var users = new UserRepository(db);
        users.EnsureUser("u1", "观众");
        using var replyQueue = new ReplyQueue(
            new DouyinService(config.Settings.Douyin, log),
            log,
            new ReplySettings { MaxPerSecond = 50, MaxRetries = 0, SongRequestBatchWindowMs = 50 },
            sendMention: (_, _, _, _) => Task.FromResult(true));
        var (song, queue) = CreateSongRequestWithQueue(db, config, log, replyQueue, users);

        Assert.True(await song.HandleDanmakuAsync(
            new DanmakuItem { UserId = "u1", Nickname = "观众", Content = "点歌 泡沫" }, "room1"));
        Assert.False(await song.HandleDanmakuAsync(
            new DanmakuItem { UserId = "u1", Nickname = "观众", Content = "主播你好" }, "room1"));
        Assert.True(await song.HandleDanmakuAsync(
            new DanmakuItem { UserId = "u1", Nickname = "观众", Content = "确定" }, "room1"));
        Assert.True(await WaitUntil(() => queue.WaitingCount == 1, TimeSpan.FromSeconds(3)));

        var before = queue.WaitingCount;
        Assert.True(await song.HandleDanmakuAsync(
            new DanmakuItem { UserId = "u1", Nickname = "观众", Content = "确定" }, "room1"));
        await Task.Delay(200);
        Assert.Equal(before, queue.WaitingCount);
    }

    [Fact]
    public async Task KeywordHit_ReturnsTrue_EmptyKeywordIgnored()
    {
        var db = new AppDatabase(Path.Combine(_dir, "kw-empty"));
        var config = new ConfigManager();
        config.Load();
        config.Settings.KeywordReply.Enabled = true;
        var log = new LogService(_dir);
        var kwRepo = new KeywordReplyRepository(db);
        // 直接写库绕过校验，模拟历史脏数据
        using (var conn = db.Open())
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText =
                "INSERT INTO keyword_replies (keyword, template_key, reply_content, enabled, created_at) VALUES ('', '', '空关键词不该命中', 1, $now)";
            cmd.Parameters.AddWithValue("$now", DateTime.Now.ToString("O"));
            cmd.ExecuteNonQuery();
        }

        kwRepo.Add(new KeywordReplyRule { Keyword = "你好", ReplyContent = "你好呀", Enabled = true });
        var keyword = new KeywordReplyService(config, kwRepo, new NullAIReplyService(), log);
        var users = new UserRepository(db);
        users.EnsureUser("u1", "观众");
        var sent = new ConcurrentBag<string>();
        using var replyQueue = new ReplyQueue(
            new DouyinService(config.Settings.Douyin, log),
            log,
            new ReplySettings { MaxPerSecond = 50 },
            sendMention: (_, _, content, _) =>
            {
                sent.Add(content);
                return Task.FromResult(true);
            });

        Assert.False(await keyword.TryHandleAsync(
            new DanmakuItem { UserId = "u1", Nickname = "观众", Content = "随便聊聊" },
            replyQueue, new ReplyService(config), users, "room1"));
        Assert.True(await keyword.TryHandleAsync(
            new DanmakuItem { UserId = "u1", Nickname = "观众", Content = "主播你好" },
            replyQueue, new ReplyService(config), users, "room1"));
        Assert.True(await WaitUntil(() => sent.Contains("你好呀"), TimeSpan.FromSeconds(2)));
        Assert.DoesNotContain(sent, s => s.Contains("空关键词"));
    }

    [Fact]
    public void KeywordRepository_RejectsEmptyKeyword()
    {
        var rule = new KeywordReplyRule { Keyword = "  ", ReplyContent = "x", Enabled = true };
        Assert.Equal("关键词不能为空", KeywordReplyRepository.ValidateRule(rule, true));
        var emptyReply = new KeywordReplyRule { Keyword = "hi", ReplyContent = "", TemplateKey = "", Enabled = true };
        Assert.Contains("回复", KeywordReplyRepository.ValidateRule(emptyReply, true)!);
    }

    [Fact]
    public async Task SongRequestBatch_FixedWindow_DoesNotResetOnContinuousEnqueue()
    {
        var sends = new ConcurrentBag<(DateTime At, string Content)>();
        var settings = new ReplySettings
        {
            MaxPerSecond = 50,
            MaxRetries = 0,
            SongRequestBatchWindowMs = 400
        };
        var log = new LogService(Path.Combine(_dir, "batch"));
        using var queue = new ReplyQueue(
            new DouyinService(new DouyinSettings(), log),
            log,
            settings,
            sendMention: (_, _, content, _) =>
            {
                sends.Add((DateTime.UtcNow, content));
                return Task.FromResult(true);
            });

        var started = DateTime.UtcNow;
        for (var i = 0; i < 8; i++)
        {
            queue.EnqueueSongRequestReply("rid", "u" + i, "N" + i, "歌" + i, i);
            await Task.Delay(80); // < 400ms，若错误重置窗口则永远发不出第一批
        }

        Assert.True(await WaitUntil(() => sends.Count > 0, TimeSpan.FromSeconds(3)));
        var firstAt = sends.Min(s => s.At);
        var elapsed = firstAt - started;
        Assert.True(elapsed < TimeSpan.FromMilliseconds(900),
            $"第一批应在固定窗口内发出，实际耗时 {elapsed.TotalMilliseconds}ms，批次数={sends.Count}");
        Assert.True(sends.Count >= 1);
    }

    [Fact]
    public async Task ReplySendFail_DoesNotTrackOutbound_AudienceSameTextStillPass()
    {
        var tracker = new OutboundReplyTracker(TimeSpan.FromMinutes(2));
        var log = new LogService(Path.Combine(_dir, "outbound"));
        var settings = new ReplySettings { MaxPerSecond = 50, MaxRetries = 0, RetryDelayMs = 10 };
        using var queue = new ReplyQueue(
            new DouyinService(new DouyinSettings(), log),
            log,
            settings,
            outboundTracker: tracker,
            sendMention: (_, _, _, _) => Task.FromResult(false));

        queue.EnqueueMention("rid", "bot", "点歌成功《泡沫》前面还有0首");
        await Task.Delay(500);

        Assert.False(tracker.IsRecentOutbound(null, "点歌成功《泡沫》前面还有0首"));
        // 观众发同样文字：不应因失败入队而被当成机器人出站
        Assert.False(SongNameParser.IsBotReply("点歌成功《泡沫》前面还有0首") && tracker.IsRecentOutbound(null, "点歌成功《泡沫》前面还有0首"));
    }

    [Fact]
    public async Task ReplySendSuccess_TracksOutbound()
    {
        var tracker = new OutboundReplyTracker(TimeSpan.FromMinutes(2));
        var log = new LogService(Path.Combine(_dir, "outbound-ok"));
        using var queue = new ReplyQueue(
            new DouyinService(new DouyinSettings(), log),
            log,
            new ReplySettings { MaxPerSecond = 50, MaxRetries = 0 },
            outboundTracker: tracker,
            sendMention: (_, _, _, _) => Task.FromResult(true));

        queue.EnqueueMention("rid", "bot", "已切歌，消耗20积分");
        Assert.True(await WaitUntil(
            () => tracker.HasRecentOutboundContent("已切歌，消耗20积分"),
            TimeSpan.FromSeconds(2)));
        // 仅正文不能命中 IsRecentOutbound；需 msg_id
        Assert.False(tracker.IsRecentOutbound(null, "已切歌，消耗20积分"));
    }

    [Fact]
    public void QueueMaxSize_ConcurrentAdd_DoesNotExceed()
    {
        var db = new AppDatabase(Path.Combine(_dir, "maxsize"));
        var queue = new QueueService(db);
        const int maxSize = 5;
        var ok = 0;
        Parallel.For(0, 20, i =>
        {
            if (queue.TryAddWithPriority(
                    new QueueItem
                    {
                        UserId = "u" + i,
                        Nickname = "N" + i,
                        SongName = "S" + i,
                        PlayUrl = "http://x/" + i
                    },
                    0,
                    maxSize,
                    bypassCapacity: false,
                    out _))
            {
                Interlocked.Increment(ref ok);
            }
        });

        Assert.Equal(5, ok);
        Assert.Equal(5, queue.WaitingCount);
    }

    [Fact]
    public void EnsureUser_Concurrent_NoUniqueException_SingleRow()
    {
        var db = new AppDatabase(Path.Combine(_dir, "ensure"));
        var users = new UserRepository(db);
        Exception? boom = null;
        Parallel.For(0, 100, i =>
        {
            try
            {
                users.EnsureUser("same-user", "昵称" + i);
            }
            catch (Exception ex)
            {
                Interlocked.CompareExchange(ref boom, ex, null);
            }
        });

        Assert.Null(boom);
        Assert.NotNull(users.GetUser("same-user"));
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM users WHERE user_id='same-user'";
        Assert.Equal(1L, (long)(cmd.ExecuteScalar() ?? 0L));
    }

    [Fact]
    public async Task UserGate_HighConcurrency_NoDisposeOrFullException()
    {
        var registry = new SongRequestUserGateRegistry(TimeSpan.FromMilliseconds(30));
        Exception? boom = null;
        var tasks = Enumerable.Range(0, 80).Select(async i =>
        {
            try
            {
                for (var r = 0; r < 5; r++)
                {
                    var handle = await registry.AcquireAsync("user-" + (i % 10), CancellationToken.None);
                    await Task.Delay(1);
                    handle.Dispose();
                }
            }
            catch (Exception ex)
            {
                Interlocked.CompareExchange(ref boom, ex, null);
            }
        });
        await Task.WhenAll(tasks);
        await Task.Delay(80);
        // 触发 prune
        using (await registry.AcquireAsync("fresh", CancellationToken.None))
        {
        }

        Assert.Null(boom);
        Assert.True(registry.ActiveCount <= 12);
    }

    [Fact]
    public void AiSpeech_HealthSemantics_CorrectCombinations()
    {
        AiSpeechCoordinator.ApplyTtsHealth(
            new GptSovitsHealth { Ok = true, TtsReady = false, VoiceReady = true, Voice = "v1" },
            "v1", out var ttsOk, out var voiceReady);
        Assert.False(ttsOk);
        Assert.True(voiceReady);

        AiSpeechCoordinator.ApplyTtsHealth(
            new GptSovitsHealth { Ok = false, TtsReady = true, VoiceReady = true, Voice = "v1" },
            "v1", out ttsOk, out voiceReady);
        Assert.False(ttsOk);
        Assert.False(voiceReady);

        AiSpeechCoordinator.ApplyTtsHealth(
            new GptSovitsHealth { Ok = true, TtsReady = true, VoiceReady = false, Voice = "other" },
            "v1", out ttsOk, out voiceReady);
        Assert.True(ttsOk);
        Assert.False(voiceReady);

        AiSpeechCoordinator.ApplyTtsHealth(
            new GptSovitsHealth { Ok = true, TtsReady = true, VoiceReady = true, Voice = "v1" },
            "v1", out ttsOk, out voiceReady);
        Assert.True(ttsOk);
        Assert.True(voiceReady);
    }

    [Fact]
    public async Task AiSpeech_ConcurrentTestAndWorker_EndsIdle_NoObjectDisposed()
    {
        var temp = Path.Combine(_dir, "ai-gate");
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
            config.Settings.AiSpeech.OllamaTimeoutSeconds = 1;
            config.Settings.AiSpeech.TtsTimeoutSeconds = 1;
            config.Settings.AiSpeech.MinIntervalSeconds = 3;
            var log = new LogService(temp);
            var tracker = new OutboundReplyTracker();
            using var coordinator = new AiSpeechCoordinator(config, log, tracker);

            Exception? boom = null;
            var t1 = Task.Run(async () =>
            {
                try { await coordinator.TestAiAsync(); }
                catch (Exception ex) { Interlocked.CompareExchange(ref boom, ex, null); }
            });
            var t2 = Task.Run(async () =>
            {
                try { await coordinator.TestVoiceAsync(); }
                catch (Exception ex) { Interlocked.CompareExchange(ref boom, ex, null); }
            });
            coordinator.TryEnqueueDanmaku(new DanmakuItem
            {
                MsgId = "m1",
                UserId = "u1",
                Nickname = "观众",
                Content = "主播这个功能是你自己做的吗",
                MsgType = "chat"
            }, null, null);

            await Task.WhenAll(t1, t2);
            Assert.Null(boom);
            Assert.True(
                await WaitUntil(() => coordinator.GetStatus().Phase == AiSpeechPhase.Idle, TimeSpan.FromSeconds(12)),
                $"phase={coordinator.GetStatus().Phase}");
            var latest = coordinator.GetStatus().LatestReply;
            if (!string.IsNullOrWhiteSpace(latest))
            {
                Assert.False(tracker.IsRecentOutbound(null, latest));
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("LA_DATA_DIR", null);
        }
    }

    [Fact]
    public async Task SkipSong_DeductsBeforeSkip_InsufficientRejected()
    {
        var (svc, users, queue, _) = CreateSkipService(userPoints: 5, skipCost: 20);
        queue.BeginPlaying(new QueueItem { SongName = "曲", Nickname = "A" });
        Assert.True(await svc.TryHandleAsync(new DanmakuItem
        {
            UserId = "u1",
            Nickname = "观众",
            Content = "切歌"
        }, "room1"));
        Assert.Equal(5, users.GetUser("u1")!.Points);
    }

    [Fact]
    public async Task SkipSong_Concurrent_OnlyAffordableSucceed()
    {
        var (svc, users, queue, _) = CreateSkipService(userPoints: 20, skipCost: 20);
        queue.BeginPlaying(new QueueItem { SongName = "曲", Nickname = "A" });
        var handled = 0;
        await Task.WhenAll(Enumerable.Range(0, 2).Select(async _ =>
        {
            if (await svc.TryHandleAsync(new DanmakuItem
                {
                    UserId = "u1",
                    Nickname = "观众",
                    Content = "切歌"
                }, "room1"))
            {
                Interlocked.Increment(ref handled);
            }
        }));

        Assert.Equal(2, handled); // 两条都消费命令，但只有一次扣分成功
        Assert.Equal(0, users.GetUser("u1")!.Points);
    }

    [Fact]
    public async Task BanVote_ApiFail_DoesNotSetLocalMuted()
    {
        var db = new AppDatabase(Path.Combine(_dir, "ban"));
        var config = new ConfigManager();
        config.Load();
        config.Settings.BanVote.Enabled = true;
        config.Settings.BanVote.RequiredVotes = 1;
        config.Settings.BanVote.BanDurationSeconds = 60;
        var log = new LogService(_dir);
        var users = new UserRepository(db);
        users.EnsureUser("target", "目标用户");
        users.EnsureUser("voter", "投票人");
        Assert.Equal(UserStatus.Active, users.GetUser("target")!.Status);

        var handler = new DouyinSilenceFailHandler();
        var douyin = new DouyinService(config.Settings.Douyin, log, new HttpClient(handler)
        {
            BaseAddress = new Uri("http://127.0.0.1:17999/")
        });
        using var replyQueue = new ReplyQueue(
            douyin, log, new ReplySettings { MaxPerSecond = 50 },
            sendMention: (_, _, _, _) => Task.FromResult(true));
        using var ban = new BanVoteService(
            config,
            new BanVoteRepository(db),
            users,
            douyin,
            replyQueue,
            new ReplyService(config),
            new SystemMessageService(20),
            log);

        Assert.True(await ban.TryHandleAsync(new DanmakuItem
        {
            UserId = "voter",
            Nickname = "投票人",
            Content = "禁言目标用户"
        }, "room1"));

        await Task.Delay(300);
        Assert.Equal(UserStatus.Active, users.GetUser("target")!.Status);
    }

    [Fact]
    public void SongNameParser_IsBotReply_StillDetected_ButNotSoleFilter()
    {
        Assert.True(SongNameParser.IsBotReply("点歌成功《泡沫》前面还有0首"));
        Assert.True(SongNameParser.IsBotReply("你当前有 10 积分"));
        Assert.False(SongNameParser.TryParse("点歌成功《泡沫》", out _));
    }

    private (SkipSongService svc, UserRepository users, QueueService queue, List<string> sent) CreateSkipService(
        int userPoints,
        int skipCost)
    {
        var db = new AppDatabase(Path.Combine(_dir, "skip-" + Guid.NewGuid().ToString("N")[..8]));
        var users = new UserRepository(db);
        users.EnsureUser("u1", "观众");
        users.AddPoints("u1", "观众", userPoints);
        var config = new ConfigManager();
        config.Load();
        config.Settings.SongRequestPolicy.Mode = SongRequestPolicyMode.Points;
        config.Settings.SongRequestPolicy.SkipPointsCost = skipCost;
        config.ReplyTemplates["skipSongSuccess"] = "已切歌";
        config.ReplyTemplates["skipSongSuccessPaid"] = "已切歌，消耗{cost}积分";
        config.ReplyTemplates["skipSongInsufficientPoints"] = "切歌需要 {cost} 积分，你当前只有 {score} 积分";
        config.ReplyTemplates["skipSongNothingPlaying"] = "当前没有可切的歌曲";
        var log = new LogService(_dir);
        var queue = new QueueService(db);
        var playback = new PlaybackService(log);
        var kugou = new KugouService(config.Settings.Kugou, log);
        var random = new RandomPlaylistService(config, db);
        var system = new SystemMessageService(50);
        var commands = new PlaybackCommandQueue(config, queue, kugou, random, playback, new ReplyService(config), system, log);
        var engine = new PlaybackEngine(config, commands, system);
        var sent = new List<string>();
        var replyQueue = new ReplyQueue(
            new DouyinService(config.Settings.Douyin, log),
            log,
            config.Settings.Reply,
            sendMention: (_, _, content, _) =>
            {
                sent.Add(content);
                return Task.FromResult(true);
            });
        var svc = new SkipSongService(
            config, users, playback, queue, engine, new ReplyService(config), replyQueue, system, log);
        return (svc, users, queue, sent);
    }

    private static SongRequestService CreateSongRequest(
        AppDatabase db,
        ConfigManager config,
        LogService log,
        ReplyQueue replyQueue,
        UserRepository users)
        => CreateSongRequestWithQueue(db, config, log, replyQueue, users).Song;

    private static (SongRequestService Song, QueueService Queue) CreateSongRequestWithQueue(
        AppDatabase db,
        ConfigManager config,
        LogService log,
        ReplyQueue replyQueue,
        UserRepository users)
    {
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
        return (song, queue);
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

    private sealed class DouyinSilenceFailHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? "";
            var json = path.Contains("silence", StringComparison.OrdinalIgnoreCase)
                ? """{"ok":false,"error":"api fail"}"""
                : """{"ok":true,"data":{}}""";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
        }
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
        {
            var json = JsonSerializer.Serialize(body);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
        }
    }
}
