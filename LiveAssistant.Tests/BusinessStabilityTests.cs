using LiveAssistant.Config;
using LiveAssistant.Database;
using LiveAssistant.Models;
using LiveAssistant.Services;
using Xunit;

namespace LiveAssistant.Tests;

public sealed class BusinessStabilityTests : IDisposable
{
    private readonly string _dir;

    public BusinessStabilityTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "la-biz-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    [Fact]
    public void OutboundReplyTracker_FiltersRecentReplyIdAndContent()
    {
        var tracker = new OutboundReplyTracker(TimeSpan.FromMinutes(1));
        tracker.Track("reply-42", "点歌成功《泡沫》\n前面还有0首");

        // 仅 msg_id / reply_id 命中才算出站回显；正文相同不再单独过滤
        Assert.True(tracker.IsRecentOutbound("reply-42", "任意内容"));
        Assert.False(tracker.IsRecentOutbound("other", "点歌成功《泡沫》\n前面还有0首"));
        Assert.False(tracker.IsRecentOutbound("other", "点歌成功《泡沫》前面还有0首"));
        Assert.False(tracker.IsRecentOutbound(null, "点歌成功《泡沫》前面还有0首"));
        Assert.True(tracker.HasRecentOutboundContent("点歌成功《泡沫》前面还有0首"));
        Assert.False(tracker.IsRecentOutbound("other", "点歌 晴天"));
        Assert.False(tracker.IsRecentOutbound("other", "点歌成功《泡沫》前面还有1首"));
    }

    [Fact]
    public void GiftService_RecordsGiftAndPointsAtomically()
    {
        var db = new AppDatabase(_dir);
        var config = new ConfigManager();
        config.Load();
        config.Settings.Gift.PointsPerValue = 2;
        var log = new LogService(_dir);
        var users = new UserRepository(db);
        var gifts = new GiftService(
            config,
            new DouyinService(config.Settings.Douyin, log),
            new GiftRepository(db),
            users,
            new UserLevelService(config, users),
            new GiftRuleRepository(db),
            log,
            new SystemMessageService(20));
        gifts.BindRoom("room");

        Assert.True(gifts.HandleGiftEvent(new GiftEvent
        {
            EventId = "atomic-1",
            UserId = "u1",
            Nickname = "粉丝",
            GiftName = "玫瑰",
            GiftId = "g1",
            Count = 1,
            Value = 5,
            Time = DateTime.Now
        }));

        Assert.Equal(10, users.GetUser("u1")?.Points);
        Assert.True(new GiftRepository(db).ExistsByEventId("atomic-1"));
    }

    [Fact]
    public void GiftService_ThanksThrottledForBurstSameGift()
    {
        var db = new AppDatabase(Path.Combine(_dir, "thanks"));
        Directory.CreateDirectory(Path.Combine(_dir, "thanks"));
        var config = new ConfigManager();
        config.Load();
        config.Settings.Gift.PointsPerValue = 1;
        // 本测验证旧版礼物感谢限流；电影评分引导开启时会跳过纯感谢文案
        config.Settings.MovieInteraction.Notification.Enabled = false;
        var log = new LogService(_dir);
        var sent = new List<string>();
        var replyQueue = new ReplyQueue(
            new DouyinService(config.Settings.Douyin, log),
            log,
            config.Settings.Reply,
            sendMention: (_, _, content, _, _) =>
            {
                sent.Add(content);
                return Task.FromResult(new MentionSendResult { Ok = true });
            });

        var gifts = new GiftService(
            config,
            new DouyinService(config.Settings.Douyin, log),
            new GiftRepository(db),
            new UserRepository(db),
            new UserLevelService(config, new UserRepository(db)),
            new GiftRuleRepository(db),
            log,
            new SystemMessageService(20),
            new ReplyService(config),
            replyQueue);
        gifts.Start("room");

        for (var i = 0; i < 6; i++)
        {
            gifts.HandleGiftEvent(new GiftEvent
            {
                EventId = $"thanks-{i}",
                UserId = "u1",
                Nickname = "粉丝",
                GiftName = "小心心",
                GiftId = "g1",
                Count = 1,
                Value = 1,
                Time = DateTime.Now
            });
        }

        for (var i = 0; i < 20 && sent.Count < 3; i++)
        {
            Thread.Sleep(50);
        }

        Assert.InRange(sent.Count, 1, 3);
    }

    [Fact]
    public async Task UserGateRegistry_PrunesIdleGates()
    {
        var registry = new SongRequestUserGateRegistry(TimeSpan.FromMilliseconds(50));
        var idleHandle = await registry.AcquireAsync("idle-user", CancellationToken.None);
        using (idleHandle)
        {
        }

        Assert.Equal(1, registry.ActiveCount);
        await Task.Delay(80);
        var activeHandle = await registry.AcquireAsync("active-user", CancellationToken.None);
        using (activeHandle)
        {
        }

        Assert.Equal(1, registry.ActiveCount);
    }

    [Fact]
    public async Task Playback_RetriesResolveOnce_AfterPlayFailure()
    {
        var db = new AppDatabase(Path.Combine(_dir, "url-recovery"));
        Directory.CreateDirectory(Path.Combine(_dir, "url-recovery"));
        var queue = new QueueService(db);
        var config = new ConfigManager();
        config.Load();
        config.Settings.Playback.Mode = PlaybackMode.RequestOnly;
        config.Settings.RandomPlaylist.Items.Clear();
        var log = new LogService(_dir);
        var resolveCalls = 0;

        queue.Add(new QueueItem
        {
            UserId = "1",
            Nickname = "A",
            SongName = "恢复歌",
            Hash = "h1",
            PlayUrl = "https://stale.example/old.mp3"
        });

        using var commands = new PlaybackCommandQueue(
            config,
            queue,
            new KugouService(config.Settings.Kugou, log),
            new RandomPlaylistService(config, db),
            new PlaybackService(log),
            new ReplyService(config),
            new SystemMessageService(20),
            log,
            resolveFresh: (_, _) =>
            {
                var n = Interlocked.Increment(ref resolveCalls);
                return Task.FromResult<TrackInfo?>(new TrackInfo
                {
                    SongName = "恢复歌",
                    Hash = "h1",
                    PlayUrl = n == 1
                        ? "https://stale.example/still-bad.mp3"
                        : "https://fresh.example/recovered.mp3"
                });
            },
            playAsync: (track, _, _) =>
                Task.FromResult(track.PlayUrl?.Contains("fresh.example", StringComparison.Ordinal) == true));

        await commands.EnqueueEnsurePlayingAsync();
        Assert.True(await WaitUntil(() => resolveCalls >= 2, TimeSpan.FromSeconds(3)));
        Assert.Equal(2, resolveCalls);
        db.Dispose();
    }

    [Fact]
    public void SettingsStore_ApplyBundle_KeepsStableVersion()
    {
        var db = new AppDatabase(_dir);
        var config = new ConfigManager();
        config.Load();
        var store = new SettingsStore(
            config,
            new ReplyTemplateRepository(db),
            new RandomPoolRepository(db),
            new GiftRuleRepository(db),
            new LevelPermissionRepository(db),
            new KeywordReplyRepository(db),
            new SyncCacheRepository(db));
        store.InitializeFromFilesIfEmpty();

        var bundle = store.BuildBundle();
        store.ApplyBundle(bundle);
        var afterFirst = store.BuildBundle().Version;
        var afterSecond = store.BuildBundle().Version;

        Assert.Equal(bundle.Version, afterFirst);
        Assert.Equal(afterFirst, afterSecond);
        db.Dispose();
    }

    [Fact]
    public void SettingsStore_TryLoadCachedBundle_PreservesPointsModeFromAppsettings()
    {
        var db = new AppDatabase(_dir);
        var cache = new SyncCacheRepository(db);
        var config = new ConfigManager();
        config.Load();
        var store = new SettingsStore(
            config,
            new ReplyTemplateRepository(db),
            new RandomPoolRepository(db),
            new GiftRuleRepository(db),
            new LevelPermissionRepository(db),
            new KeywordReplyRepository(db),
            cache);
        store.InitializeFromFilesIfEmpty();

        config.Settings.SongRequestPolicy.Mode = SongRequestPolicyMode.Free;
        var staleBundle = store.BuildBundle();
        cache.Set("sync_bundle", System.Text.Json.JsonSerializer.Serialize(staleBundle));
        cache.Set("sync_version", staleBundle.Version);

        config.Settings.SongRequestPolicy.Mode = SongRequestPolicyMode.Points;
        config.Settings.SongRequestPolicy.PointsCost = 15;
        config.Save();

        Assert.True(store.TryLoadCachedBundle());
        Assert.Equal(SongRequestPolicyMode.Points, config.Settings.SongRequestPolicy.Mode);
        Assert.Equal(15, config.Settings.SongRequestPolicy.PointsCost);

        var reloaded = store.BuildBundle();
        Assert.Equal(SongRequestPolicyMode.Points, reloaded.SongRequestPolicy.Mode);
        db.Dispose();
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
        try
        {
            Directory.Delete(_dir, true);
        }
        catch
        {
            // ignore
        }
    }

}
