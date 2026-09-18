using LiveAssistant.Config;
using LiveAssistant.Database;
using LiveAssistant.Models;
using LiveAssistant.Services;
using Xunit;

namespace LiveAssistant.Tests;

/// <summary>
/// 电影评分主动弹幕：credit 成功才提醒、冷却、评分反馈、发送失败不回滚。
/// </summary>
public sealed class MovieInteractionNotifyTests : IDisposable
{
    private readonly string _dataDir;
    private readonly AppDatabase _db;
    private readonly ConfigManager _config;
    private readonly LogService _log;
    private readonly List<(string UserId, string Content, string? Tag)> _sent = new();
    private readonly ReplyQueue _replyQueue;
    private readonly MovieInteractionService _svc;

    public MovieInteractionNotifyTests()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), "la_movie_notify_" + Guid.NewGuid().ToString("N"));
        _db = new AppDatabase(_dataDir);
        _config = new ConfigManager();
        _config.Load();
        _config.Settings.MovieInteraction.Enabled = true;
        _config.Settings.MovieInteraction.PointsPerDiamond = 10;
        _config.Settings.MovieInteraction.CreditExpireSeconds = 180;
        _config.Settings.MovieInteraction.Notification.Enabled = true;
        _config.Settings.MovieInteraction.Notification.GiftGuideUserCooldownSeconds = 60;
        _config.Settings.MovieInteraction.Notification.GlobalSendIntervalMs = 2500;
        _config.Settings.MovieInteraction.Notification.ScoreSuccessReplyEnabled = true;
        _config.Settings.MovieInteraction.Notification.InvalidScoreHintEnabled = true;
        _config.Settings.Reply.MaxPerSecond = 20;
        _config.Settings.Reply.MinIntervalMs = 0;
        _log = new LogService(_dataDir);
        _replyQueue = new ReplyQueue(
            new DouyinService(_config.Settings.Douyin, _log),
            _log,
            _config.Settings.Reply,
            sendMention: (_, userId, content, _, _) =>
            {
                lock (_sent)
                {
                    _sent.Add((userId, content, null));
                }

                return Task.FromResult(new MentionSendResult
                {
                    Ok = true,
                    PlatformMessageId = $"m-{Guid.NewGuid():N}"
                });
            });
        _svc = new MovieInteractionService(_config, _db, _log, _replyQueue);
        // 测试不强制全局间隔，避免 WaitSent 被 2500ms 卡住
        _config.Settings.Reply.MinIntervalMs = 0;
        SeedCatalog();
    }

    [Fact]
    public void GiftCreditSuccess_EnqueuesScoreGuideOnce()
    {
        Assert.True(_svc.OnGiftReceived(Gift("g1", "u1", 1)));
        Assert.True(WaitSent(1));
        Assert.Contains(_sent, x => x.UserId == "u1" && x.Content.Contains("评分"));
    }

    [Fact]
    public void DuplicateGiftEventId_DoesNotNotifyAgain()
    {
        Assert.True(_svc.OnGiftReceived(Gift("dup", "u1", 1)));
        Assert.True(WaitSent(1));
        Assert.False(_svc.OnGiftReceived(Gift("dup", "u1", 1)));
        Thread.Sleep(200);
        Assert.Single(_sent);
    }

    [Fact]
    public void SameUserCooldown_SkipsSecondGuide_ButCreditStillAccumulates()
    {
        var t0 = new DateTime(2026, 9, 19, 12, 0, 0);
        _svc.NowProvider = () => t0;
        Assert.True(_svc.OnGiftReceived(Gift("a", "u1", 1, t0)));
        Assert.True(WaitSent(1));

        _svc.NowProvider = () => t0.AddSeconds(20);
        Assert.True(_svc.OnGiftReceived(Gift("b", "u1", 2, t0.AddSeconds(20))));
        Thread.Sleep(250);
        Assert.Single(_sent);

        var credits = _svc.Repository.GetCreditsByUser("u1");
        Assert.Equal(2, credits.Count);
        Assert.Equal(10, credits[0].Points);
        Assert.Equal(20, credits[1].Points);
    }

    [Fact]
    public void GoodAndBadScore_ReplyUsesRealDelta()
    {
        var now = new DateTime(2026, 9, 19, 13, 0, 0);
        _svc.NowProvider = () => now;
        Assert.True(_svc.OnGiftReceived(Gift("g-good", "u1", 1, now)));
        Assert.True(WaitSent(1));
        lock (_sent) { _sent.Clear(); }

        Assert.True(_svc.TryApplyScoreFromDanmaku(Chat("u1", "哪吒 好评", "m1"), nowOverride: now));
        Assert.True(WaitSent(1));
        Assert.Contains(_sent, x => x.Content.Contains("好评成功") && x.Content.Contains("+10"));

        lock (_sent) { _sent.Clear(); }
        Assert.True(_svc.OnGiftReceived(Gift("g-bad", "u2", 2, now)));
        Assert.True(WaitSent(1));
        lock (_sent) { _sent.Clear(); }

        Assert.True(_svc.TryApplyScoreFromDanmaku(Chat("u2", "哪吒 差评", "m2"), nowOverride: now));
        Assert.True(WaitSent(1));
        Assert.Contains(_sent, x => x.Content.Contains("差评成功") && x.Content.Contains("-20"));
    }

    [Fact]
    public void ScoreSucceedsEvenWhenReplySendFails()
    {
        using var failQueue = new ReplyQueue(
            new DouyinService(_config.Settings.Douyin, _log),
            _log,
            _config.Settings.Reply,
            sendMention: (_, _, _, _, _) =>
                Task.FromResult(new MentionSendResult { Ok = false, ErrorReason = "not_logged_in" }));
        using var svc = new MovieInteractionService(_config, _db, _log, failQueue);
        svc.UpdateCatalog(new MovieCatalogUpdateRequest
        {
            Movies =
            [
                new MovieCatalogUpdateItem
                {
                    MovieId = "1462628",
                    MovieName = "哪吒之魔童闹海",
                    Aliases = ["哪吒"],
                    Rank = 1
                }
            ]
        });

        var now = DateTime.Now;
        svc.NowProvider = () => now;
        Assert.True(svc.OnGiftReceived(Gift("fail-reply", "u9", 1, now)));
        Assert.True(svc.TryApplyScoreFromDanmaku(Chat("u9", "哪吒 好评", "mf"), nowOverride: now));
        Assert.Equal(10, svc.Repository.GetTotalScore("1462628"));
        Thread.Sleep(400);
    }

    [Fact]
    public void NoCredit_DoesNotConsume_AndHintsOnce()
    {
        var now = DateTime.Now;
        Assert.False(_svc.TryApplyScoreFromDanmaku(Chat("u-empty", "哪吒 好评", "n1"), nowOverride: now));
        Assert.True(WaitSent(1));
        Assert.Contains(_sent, x => x.Content.Contains("暂无评分机会"));
        Assert.Equal(0, _svc.Repository.GetTotalScore("1462628"));

        lock (_sent) { _sent.Clear(); }
        Assert.False(_svc.TryApplyScoreFromDanmaku(Chat("u-empty", "哪吒 好评", "n2"), nowOverride: now));
        Thread.Sleep(200);
        Assert.Empty(_sent);
    }

    [Fact]
    public void UnknownMovie_DoesNotConsume_AndHints()
    {
        var now = DateTime.Now;
        Assert.True(_svc.OnGiftReceived(Gift("g-unk", "u3", 1, now)));
        Assert.True(WaitSent(1));
        lock (_sent) { _sent.Clear(); }

        Assert.False(_svc.TryApplyScoreFromDanmaku(Chat("u3", "不存在的电影xyz 好评", "u1"), nowOverride: now));
        Assert.True(WaitSent(1));
        Assert.Contains(_sent, x => x.Content.Contains("没找到这部电影"));
        Assert.Equal("pending", _svc.Repository.GetCreditByGiftEventId("g-unk")!.Status);
    }

    [Fact]
    public void AmbiguousMovie_DoesNotConsume_AndHints()
    {
        _svc.UpdateCatalog(new MovieCatalogUpdateRequest
        {
            Movies =
            [
                new MovieCatalogUpdateItem { MovieId = "a1", MovieName = "同名电影", Aliases = ["同名"], Rank = 1 },
                new MovieCatalogUpdateItem { MovieId = "a2", MovieName = "同名电影续", Aliases = ["同名"], Rank = 2 }
            ]
        });
        var now = DateTime.Now;
        Assert.True(_svc.OnGiftReceived(Gift("g-amb", "u4", 1, now)));
        Assert.True(WaitSent(1));
        lock (_sent) { _sent.Clear(); }

        Assert.False(_svc.TryApplyScoreFromDanmaku(Chat("u4", "同名 好评", "amb1"), nowOverride: now));
        Assert.True(WaitSent(1));
        Assert.Contains(_sent, x => x.Content.Contains("不够明确"));
        Assert.Equal("pending", _svc.Repository.GetCreditByGiftEventId("g-amb")!.Status);
    }

    [Fact]
    public void RoomMismatchOrNotLoggedIn_DoesNotAffectScoreBusiness()
    {
        using var failQueue = new ReplyQueue(
            new DouyinService(_config.Settings.Douyin, _log),
            _log,
            _config.Settings.Reply,
            sendMention: (_, _, _, _, _) =>
                Task.FromResult(new MentionSendResult { Ok = false, ErrorReason = "room_mismatch" }));
        using var svc = new MovieInteractionService(_config, _db, _log, failQueue);
        svc.UpdateCatalog(new MovieCatalogUpdateRequest
        {
            Movies =
            [
                new MovieCatalogUpdateItem
                {
                    MovieId = "1462628",
                    MovieName = "哪吒之魔童闹海",
                    Aliases = ["哪吒"],
                    Rank = 1
                }
            ]
        });
        var now = DateTime.Now;
        svc.NowProvider = () => now;
        Assert.True(svc.OnGiftReceived(Gift("rm1", "u5", 3, now)));
        Assert.True(svc.TryApplyScoreFromDanmaku(Chat("u5", "哪吒 好评", "rm"), nowOverride: now));
        Assert.Equal(30, svc.Repository.GetTotalScore("1462628"));
    }

    [Fact]
    public void ReplyQueue_MinInterval_IsAppliedFromNotificationConfig()
    {
        var cfg = new ConfigManager();
        cfg.Load();
        cfg.Settings.MovieInteraction.Notification.GlobalSendIntervalMs = 2500;
        cfg.Settings.Reply.MinIntervalMs = 0;
        using var svc = new MovieInteractionService(cfg, _db, _log);
        svc.ApplyGlobalSendInterval();
        Assert.True(cfg.Settings.Reply.MinIntervalMs >= 2500);
    }

    [Fact]
    public void GiftThanks_SkippedWhenMovieGuideEnabled()
    {
        var gifts = new GiftService(
            _config,
            new DouyinService(_config.Settings.Douyin, _log),
            new GiftRepository(_db),
            new UserRepository(_db),
            new UserLevelService(_config, new UserRepository(_db)),
            new GiftRuleRepository(_db),
            _log,
            new SystemMessageService(20),
            new ReplyService(_config),
            _replyQueue);
        gifts.Start("room1");

        lock (_sent) { _sent.Clear(); }
        Assert.True(gifts.HandleGiftEvent(Gift("thanks-skip", "u6", 1)));
        // GiftService 不再发纯感谢；MovieInteraction 由宿主 GiftReceived 触发，这里单独测跳过感谢。
        Thread.Sleep(200);
        Assert.DoesNotContain(_sent, x => x.Content.Contains("感谢送出"));
    }

    public void Dispose()
    {
        _svc.Dispose();
        _replyQueue.Dispose();
        try { Directory.Delete(_dataDir, true); } catch { /* ignore */ }
    }

    private void SeedCatalog()
    {
        _svc.UpdateCatalog(new MovieCatalogUpdateRequest
        {
            Movies =
            [
                new MovieCatalogUpdateItem
                {
                    MovieId = "1462628",
                    MovieName = "哪吒之魔童闹海",
                    Aliases = ["哪吒", "哪吒2"],
                    Rank = 1
                }
            ]
        });
    }

    private bool WaitSent(int minCount, int timeoutMs = 2000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            lock (_sent)
            {
                if (_sent.Count >= minCount)
                {
                    return true;
                }
            }

            Thread.Sleep(40);
        }

        lock (_sent)
        {
            return _sent.Count >= minCount;
        }
    }

    private static GiftEvent Gift(string eventId, string userId, int value, DateTime? time = null)
        => new()
        {
            EventId = eventId,
            UserId = userId,
            Nickname = "观众",
            GiftId = "gid",
            GiftName = "小心心",
            Count = 1,
            DiamondCount = value,
            Value = value,
            Time = time ?? DateTime.Now,
            RoomKey = "room1"
        };

    private static DanmakuItem Chat(string userId, string content, string msgId)
        => new()
        {
            MsgId = msgId,
            UserId = userId,
            Nickname = "观众",
            Content = content,
            MsgType = "chat",
            Platform = "douyin",
            RoomKey = "room1",
            Timestamp = DateTime.Now
        };
}
