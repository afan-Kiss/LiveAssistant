using System.Net;
using System.Text.Json;
using LiveAssistant.Config;
using LiveAssistant.Database;
using LiveAssistant.Models;
using LiveAssistant.Services;
using LiveAssistant.Utils;
using Xunit;

namespace LiveAssistant.Tests;

public sealed class MovieInteractionTests : IDisposable
{
    private readonly string _dataDir;
    private readonly AppDatabase _db;
    private readonly ConfigManager _config;
    private readonly LogService _log;
    private readonly MovieInteractionService _svc;

    public MovieInteractionTests()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), "la_movie_" + Guid.NewGuid().ToString("N"));
        _db = new AppDatabase(_dataDir);
        _config = new ConfigManager();
        _config.Load();
        _config.Settings.MovieInteraction.Enabled = true;
        _config.Settings.MovieInteraction.PointsPerDiamond = 10;
        _config.Settings.MovieInteraction.CreditExpireSeconds = 900;
        _config.Settings.MovieInteraction.SyncEnabled = false;
        _config.Settings.MovieInteraction.ServerBaseUrl = "";
        _log = new LogService(_dataDir);
        _svc = new MovieInteractionService(_config, _db, _log);
        SeedDefaultCatalog();
    }

    [Fact]
    public void OneDiamond_BecomesTenPoints()
    {
        Assert.True(_svc.OnGiftReceived(Gift("g1", "u1", value: 1, diamond: 1, count: 1)));
        var credit = _svc.Repository.GetCreditByGiftEventId("g1");
        Assert.NotNull(credit);
        Assert.Equal(10, credit!.Points);
        Assert.Equal(1, credit.Value);
    }

    [Fact]
    public void ThirtyThousandDiamonds_BecomesThreeHundredThousandPoints()
    {
        Assert.True(_svc.OnGiftReceived(Gift("carnival", "u1", value: 30000, diamond: 30000, count: 1, name: "嘉年华")));
        Assert.Equal(300000, _svc.Repository.GetCreditByGiftEventId("carnival")!.Points);
    }

    [Fact]
    public void MultipleGifts_AccumulateAndKeepIndependentExpiry()
    {
        var t0 = new DateTime(2026, 9, 18, 12, 0, 0);
        var t1 = t0.AddMinutes(1);
        _svc.NowProvider = () => t0;
        Assert.True(_svc.OnGiftReceived(Gift("a", "u1", 1, t0.AddMinutes(-10))));
        _svc.NowProvider = () => t1;
        Assert.True(_svc.OnGiftReceived(Gift("b", "u1", 10, t1.AddMinutes(-10))));

        var credits = _svc.Repository.GetCreditsByUser("u1");
        Assert.Equal(2, credits.Count);
        Assert.Equal(10, credits[0].Points);
        Assert.Equal(100, credits[1].Points);
        Assert.Equal(t0.AddSeconds(900), credits[0].ExpiresAt);
        Assert.Equal(t1.AddSeconds(900), credits[1].ExpiresAt);
        Assert.NotEqual(credits[0].ExpiresAt, credits[1].ExpiresAt);

        var now = t1.AddSeconds(30);
        Assert.True(Apply("u1", "哪吒 好评", now, "m-acc"));
        Assert.Equal(110, _svc.Repository.GetTotalScore("1462628"));
        Assert.All(credits.Select(c => _svc.Repository.GetCreditByGiftEventId(c.GiftEventId)!), c =>
            Assert.Equal("consumed", c.Status));
    }

    [Fact]
    public void CreditExpiresAfterFifteenMinutes_AndIsNotDeleted()
    {
        var created = new DateTime(2026, 9, 18, 12, 0, 0);
        _svc.NowProvider = () => created;
        Assert.True(_svc.OnGiftReceived(Gift("exp", "u1", 1, created.AddMinutes(-5))));
        var credit = _svc.Repository.GetCreditByGiftEventId("exp")!;
        Assert.Equal(created.AddMinutes(15), credit.ExpiresAt);

        Assert.False(Apply("u1", "哪吒好评", created.AddMinutes(15), "m-exp"));
        var expired = _svc.Repository.GetCreditByGiftEventId("exp")!;
        Assert.Equal("expired", expired.Status);
        Assert.Equal(0, _svc.Repository.GetTotalScore("1462628"));

        _svc.NowProvider = () => created;
        Assert.True(_svc.OnGiftReceived(Gift("live", "u2", 1, created.AddMinutes(-5))));
        Assert.True(Apply("u2", "哪吒好评", created.AddMinutes(15).AddSeconds(-1), "m-live"));
        Assert.Equal(10, _svc.Repository.GetTotalScore("1462628"));
    }

    [Fact]
    public void OldPlatformGiftTime_StillGetsFullExpireWindow()
    {
        var receivedAt = new DateTime(2026, 9, 18, 15, 0, 0);
        var platformTime = receivedAt.AddMinutes(-2);
        _svc.NowProvider = () => receivedAt;
        Assert.True(_svc.OnGiftReceived(Gift("late", "u1", 1, platformTime)));
        var credit = _svc.Repository.GetCreditByGiftEventId("late")!;
        var remaining = (credit.ExpiresAt - receivedAt).TotalSeconds;
        Assert.InRange(remaining, 899, 901);
        Assert.Equal(receivedAt.AddSeconds(900), credit.ExpiresAt);
        // 若误用平台时间，只剩约 780 秒
        Assert.NotEqual(platformTime.AddSeconds(900), credit.ExpiresAt);
    }

    [Fact]
    public void GetEvents_WhenClientCursorAhead_ReturnsReset()
    {
        _svc.OnDanmaku(Chat("u1", "普通弹幕", "dm-1"));
        _svc.OnDanmaku(Chat("u1", "又一条", "dm-2"));
        var max = _svc.Repository.GetStreamMaxSeq();
        Assert.True(max >= 2);

        var json = System.Text.Json.JsonSerializer.Serialize(_svc.GetEvents(50000));
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        Assert.True(doc.RootElement.GetProperty("reset").GetBoolean());
        Assert.Equal(0, doc.RootElement.GetProperty("cursor").GetInt64());
        Assert.True(doc.RootElement.GetProperty("serverMaxSeq").GetInt64() <= max);
        Assert.False(string.IsNullOrWhiteSpace(doc.RootElement.GetProperty("streamEpoch").GetString()));

        var epoch1 = doc.RootElement.GetProperty("streamEpoch").GetString();
        var again = System.Text.Json.JsonSerializer.Serialize(_svc.GetEvents(0));
        using var doc2 = System.Text.Json.JsonDocument.Parse(again);
        Assert.False(doc2.RootElement.GetProperty("reset").GetBoolean());
        Assert.Equal(epoch1, doc2.RootElement.GetProperty("streamEpoch").GetString());
        Assert.True(doc2.RootElement.GetProperty("events").GetArrayLength() >= 2);
    }

    [Fact]
    public void InvalidDanmaku_DoesNotConsumeCredits()
    {
        Assert.True(_svc.OnGiftReceived(Gift("g", "u1", 1)));
        foreach (var text in new[] { "你好", "哪吒加油", "点歌 晴天", "哪吒 好 评", "今天哪吒不错" })
        {
            Assert.False(Apply("u1", text, DateTime.Now, "m-" + text.GetHashCode()));
        }

        Assert.Equal("pending", _svc.Repository.GetCreditByGiftEventId("g")!.Status);
        Assert.Equal(0, _svc.Repository.GetTotalScore("1462628"));
    }

    [Fact]
    public void StandaloneGoodReview_WithoutCredit_DoesNotConsume()
    {
        Assert.False(Apply("u-no-credit", "好看", DateTime.Now, "standalone-no-credit"));
        Assert.False(Apply("u-no-credit", "好评", DateTime.Now, "standalone-no-credit-2"));
    }

    [Fact]
    public void StandaloneGoodReview_WithCredit_AppliesScore()
    {
        var now = DateTime.Now;
        Assert.True(_svc.OnGiftReceived(Gift("g-standalone", "u1", 1, now)));
        Assert.True(Apply("u1", "好看", now.AddSeconds(1), "standalone-good"));
        Assert.Equal(10, _svc.Repository.GetTotalScore("1462628"));
    }

    [Fact]
    public void MovieName_WithHaoKanAndBuHaoKan_Work()
    {
        var now = DateTime.Now;
        Assert.True(_svc.OnGiftReceived(Gift("hk1", "u1", 1, now)));
        Assert.True(Apply("u1", "哪吒 好看", now.AddSeconds(1), "m-haokan"));
        Assert.Equal(10, _svc.Repository.GetTotalScore("1462628"));

        Assert.True(_svc.OnGiftReceived(Gift("hk2", "u2", 2, now.AddSeconds(2))));
        Assert.True(Apply("u2", "流浪地球 不好看", now.AddSeconds(3), "m-buhaokan"));
        Assert.Equal(-20, _svc.Repository.GetTotalScore("2"));
    }

    [Fact]
    public void GoodReview_Adds_BadReview_Subtracts_TotalMayBeNegative()
    {
        var now = DateTime.Now;
        Assert.True(_svc.OnGiftReceived(Gift("g", "u1", 1, now)));
        Assert.True(Apply("u1", "哪吒   好评", now.AddSeconds(1), "m-good"));
        Assert.Equal(10, _svc.Repository.GetTotalScore("1462628"));

        Assert.True(_svc.OnGiftReceived(Gift("g2", "u1", 3, now.AddSeconds(2))));
        Assert.True(Apply("u1", "哪吒差评", now.AddSeconds(3), "m-bad"));
        Assert.Equal(-20, _svc.Repository.GetTotalScore("1462628"));
    }

    [Fact]
    public void VoterCounts_PerGiftEvent_IncludingRepeatUsers()
    {
        var now = DateTime.Now;
        // 1) A 好评一次 → good=1 bad=0
        Assert.True(_svc.OnGiftReceived(Gift("vc1", "A", 1, now)));
        Assert.True(Apply("A", "哪吒 好评", now.AddSeconds(1), "vc-m1"));
        AssertVoterCounts("1462628", score: 10, good: 1, bad: 0);

        // 2) A 再连续好评 2 次（共 3 次）→ good=3
        Assert.True(_svc.OnGiftReceived(Gift("vc2", "A", 1, now.AddSeconds(2))));
        Assert.True(Apply("A", "哪吒 好评", now.AddSeconds(3), "vc-m2"));
        Assert.True(_svc.OnGiftReceived(Gift("vc3", "A", 1, now.AddSeconds(4))));
        Assert.True(Apply("A", "哪吒 好评", now.AddSeconds(5), "vc-m3"));
        AssertVoterCounts("1462628", score: 30, good: 3, bad: 0);

        // 3) A 再差评 → good=3 bad=1（不互相覆盖）
        Assert.True(_svc.OnGiftReceived(Gift("vc4", "A", 1, now.AddSeconds(6))));
        Assert.True(Apply("A", "哪吒 差评", now.AddSeconds(7), "vc-m4"));
        AssertVoterCounts("1462628", score: 20, good: 3, bad: 1);

        // 4) B/C 好评 → good=5
        Assert.True(_svc.OnGiftReceived(Gift("vc5", "B", 1, now.AddSeconds(8))));
        Assert.True(Apply("B", "哪吒 好评", now.AddSeconds(9), "vc-m5"));
        Assert.True(_svc.OnGiftReceived(Gift("vc6", "C", 1, now.AddSeconds(10))));
        Assert.True(Apply("C", "哪吒 好评", now.AddSeconds(11), "vc-m6"));
        AssertVoterCounts("1462628", score: 40, good: 5, bad: 1);
    }

    [Fact]
    public void VoterCounts_ArePerMovie_AndSurviveRestart()
    {
        var now = DateTime.Now;
        Assert.True(_svc.OnGiftReceived(Gift("pm1", "u1", 1, now)));
        Assert.True(Apply("u1", "哪吒 好评", now.AddSeconds(1), "pm-nezha"));
        Assert.True(_svc.OnGiftReceived(Gift("pm2", "u2", 2, now.AddSeconds(2))));
        Assert.True(Apply("u2", "流浪地球 差评", now.AddSeconds(3), "pm-earth"));

        AssertVoterCounts("1462628", score: 10, good: 1, bad: 0);
        AssertVoterCounts("2", score: -20, good: 0, bad: 1);

        using var db2 = new AppDatabase(_dataDir);
        var svc2 = new MovieInteractionService(_config, db2, _log);
        var payload = JsonSerializer.Serialize(svc2.GetScores());
        using var doc = JsonDocument.Parse(payload);
        var movies = doc.RootElement.GetProperty("movies");
        Assert.Equal(2, movies.GetArrayLength());

        var nezha = movies.EnumerateArray().First(m => m.GetProperty("movieId").GetString() == "1462628");
        Assert.Equal(10, nezha.GetProperty("score").GetInt64());
        Assert.Equal(1, nezha.GetProperty("goodUserCount").GetInt64());
        Assert.Equal(0, nezha.GetProperty("badUserCount").GetInt64());

        var earth = movies.EnumerateArray().First(m => m.GetProperty("movieId").GetString() == "2");
        Assert.Equal(-20, earth.GetProperty("score").GetInt64());
        Assert.Equal(0, earth.GetProperty("goodUserCount").GetInt64());
        Assert.Equal(1, earth.GetProperty("badUserCount").GetInt64());
        svc2.Dispose();
    }

    [Fact]
    public void GetScores_EmptyMovieHasZeroVoterCounts()
    {
        // 无任何评分时 movies 为空列表
        var payload = JsonSerializer.Serialize(_svc.GetScores());
        using var doc = JsonDocument.Parse(payload);
        Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal(0, doc.RootElement.GetProperty("movies").GetArrayLength());
    }

    [Fact]
    public void ConsumedCredits_CannotBeUsedAgain()
    {
        var now = DateTime.Now;
        Assert.True(_svc.OnGiftReceived(Gift("g", "u1", 1, now)));
        Assert.True(Apply("u1", "哪吒好评", now.AddSeconds(1), "m1"));
        Assert.False(Apply("u1", "哪吒好评", now.AddSeconds(2), "m2"));
        Assert.Equal(10, _svc.Repository.GetTotalScore("1462628"));
        Assert.Equal("consumed", _svc.Repository.GetCreditByGiftEventId("g")!.Status);
    }

    [Fact]
    public void SameGiftEventId_DoesNotCreateCreditTwice()
    {
        var gift = Gift("same", "u1", 5);
        Assert.True(_svc.OnGiftReceived(gift));
        Assert.False(_svc.OnGiftReceived(gift));
        Assert.Single(_svc.Repository.GetCreditsByUser("u1"));
        Assert.Equal(50, _svc.Repository.GetCreditByGiftEventId("same")!.Points);
    }

    [Fact]
    public void ComboGift_UsesSettledValueOnce()
    {
        // 连击结算后 Value 已是总钻石，禁止 DiamondCount × Count 再乘一遍。
        var gift = Gift("combo", "u1", value: 10, diamond: 1, count: 10, name: "小心心");
        Assert.True(_svc.OnGiftReceived(gift));
        Assert.False(_svc.OnGiftReceived(gift));
        var credit = _svc.Repository.GetCreditByGiftEventId("combo")!;
        Assert.Equal(100, credit.Points);
        Assert.Equal(10, credit.Value);
        Assert.Equal(1, credit.DiamondCount);
    }

    [Fact]
    public void ConcurrentScoreDanmaku_ConsumesCreditsOnlyOnce()
    {
        var now = DateTime.Now;
        Assert.True(_svc.OnGiftReceived(Gift("g", "u1", 10, now)));
        var successes = 0;
        Parallel.Invoke(
            () => { if (Apply("u1", "哪吒 好评", now.AddSeconds(1), "c1")) Interlocked.Increment(ref successes); },
            () => { if (Apply("u1", "哪吒 好评", now.AddSeconds(1), "c2")) Interlocked.Increment(ref successes); });

        Assert.Equal(1, successes);
        Assert.Equal(100, _svc.Repository.GetTotalScore("1462628"));
        Assert.Equal("consumed", _svc.Repository.GetCreditByGiftEventId("g")!.Status);
        Assert.Equal(1, _svc.Repository.CountPendingUploads());
    }

    [Fact]
    public void Restart_RestoresPendingCredit()
    {
        var now = DateTime.Now;
        Assert.True(_svc.OnGiftReceived(Gift("keep", "u1", 2, now)));

        using var db2 = new AppDatabase(_dataDir);
        var svc2 = new MovieInteractionService(_config, db2, _log);
        var credit = svc2.Repository.GetCreditByGiftEventId("keep");
        Assert.NotNull(credit);
        Assert.Equal("pending", credit!.Status);
        Assert.Equal(20, credit.Points);
        Assert.True(svc2.TryApplyScoreFromDanmaku(Chat("u1", "哪吒好评", "restart"), nowOverride: now.AddSeconds(5)));
        Assert.Equal(20, svc2.Repository.GetTotalScore("1462628"));
        svc2.Dispose();
    }

    [Fact]
    public void Restart_RestoresPendingSync()
    {
        var now = DateTime.Now;
        Assert.True(_svc.OnGiftReceived(Gift("sync", "u1", 1, now)));
        Assert.True(Apply("u1", "哪吒好评", now.AddSeconds(1), "ms"));
        var before = _svc.Repository.GetPendingSyncEvents(DateTime.Now.AddMinutes(1));
        Assert.Single(before);
        Assert.Equal("pending", before[0].UploadStatus);

        using var db2 = new AppDatabase(_dataDir);
        var repo = new MovieInteractionRepository(db2);
        var after = repo.GetPendingSyncEvents(DateTime.Now.AddMinutes(1));
        Assert.Single(after);
        Assert.Equal(before[0].EventId, after[0].EventId);
        Assert.Contains("pending", after[0].UploadStatus);
    }

    [Fact]
    public async Task Server500_SchedulesRetry_ThenSyncs()
    {
        var now = DateTime.Now;
        Assert.True(_svc.OnGiftReceived(Gift("up", "u1", 1, now)));
        Assert.True(Apply("u1", "哪吒好评", now.AddSeconds(1), "up-msg"));
        var eventId = _svc.Repository.GetCreditByGiftEventId("up")!.ScoreEventId!;

        var handler = new ScriptedHandler();
        handler.Enqueue(HttpStatusCode.InternalServerError);
        handler.Enqueue(HttpStatusCode.OK);
        _config.Settings.MovieInteraction.SyncEnabled = true;
        _config.Settings.MovieInteraction.ServerBaseUrl = "http://127.0.0.1:9";
        _config.Settings.MovieInteraction.ApiToken = "secret-token-should-not-matter";

        using var sync = new MovieScoreSyncService(_config, _svc, _log, handler);
        await sync.FlushOnceAsync();
        var failed = _svc.Repository.GetScoreEvent(eventId)!;
        Assert.Equal("failed", failed.UploadStatus);
        Assert.Equal(1, failed.UploadAttempts);
        Assert.NotNull(failed.NextRetryAt);
        Assert.True(failed.NextRetryAt > DateTime.Now.AddSeconds(3));

        ForceRetryNow(eventId);
        await sync.FlushOnceAsync();
        var synced = _svc.Repository.GetScoreEvent(eventId)!;
        Assert.Equal("synced", synced.UploadStatus);
        Assert.NotNull(synced.UploadedAt);
        Assert.Equal(2, handler.Calls);
        Assert.All(handler.Paths, p => Assert.EndsWith("/api/movie-score/events", p));
        Assert.Equal(10, _svc.Repository.GetTotalScore("1462628"));
    }

    [Fact]
    public async Task ServerTimeout_DoesNotRollBackLocalScore()
    {
        var now = DateTime.Now;
        Assert.True(_svc.OnGiftReceived(Gift("to", "u1", 4, now)));
        Assert.True(Apply("u1", "流浪地球 差评", now.AddSeconds(1), "to-msg"));
        Assert.Equal(-40, _svc.Repository.GetTotalScore("2"));

        _config.Settings.MovieInteraction.SyncEnabled = true;
        _config.Settings.MovieInteraction.ServerBaseUrl = "http://127.0.0.1:9";
        using var sync = new MovieScoreSyncService(_config, _svc, _log, new TimeoutHandler());
        await sync.FlushOnceAsync();

        Assert.Equal(-40, _svc.Repository.GetTotalScore("2"));
        Assert.Equal("consumed", _svc.Repository.GetCreditByGiftEventId("to")!.Status);
        var stored = _svc.Repository.GetScoreEvent(_svc.Repository.GetCreditByGiftEventId("to")!.ScoreEventId!)!;
        Assert.Equal("failed", stored.UploadStatus);
        Assert.True(stored.UploadAttempts >= 1);
        Assert.NotNull(stored.NextRetryAt);
    }

    [Fact]
    public void AliasAndFullName_Match_AmbiguousName_DoesNotScore()
    {
        var now = DateTime.Now;
        Assert.True(_svc.OnGiftReceived(Gift("a1", "u1", 1, now)));
        Assert.True(Apply("u1", "哪吒2好评", now.AddSeconds(1), "alias"));
        Assert.Equal(10, _svc.Repository.GetTotalScore("1462628"));

        Assert.True(_svc.OnGiftReceived(Gift("a2", "u1", 1, now.AddSeconds(2))));
        Assert.True(Apply("u1", "哪吒之魔童闹海 好评", now.AddSeconds(3), "full"));
        Assert.Equal(20, _svc.Repository.GetTotalScore("1462628"));

        Assert.True(_svc.OnGiftReceived(Gift("a3", "u1", 5, now.AddSeconds(4))));
        _svc.UpdateCatalog(new MovieCatalogUpdateRequest
        {
            Movies =
            [
                new MovieCatalogUpdateItem { MovieId = "1462628", MovieName = "哪吒之魔童闹海", Aliases = ["哪吒"], Rank = 1 },
                new MovieCatalogUpdateItem { MovieId = "99", MovieName = "哪吒之魔童降世", Aliases = ["哪吒"], Rank = 2 }
            ]
        });
        Assert.False(Apply("u1", "哪吒 好评", now.AddSeconds(5), "amb"));
        Assert.Equal("pending", _svc.Repository.GetCreditByGiftEventId("a3")!.Status);
        Assert.Equal(20, _svc.Repository.GetTotalScore("1462628"));
        Assert.Equal(0, _svc.Repository.GetTotalScore("99"));
    }

    [Fact]
    public void UnknownMovie_DoesNotConsume()
    {
        Assert.True(_svc.OnGiftReceived(Gift("g", "u1", 1)));
        Assert.False(Apply("u1", "不存在的电影 好评", DateTime.Now, "unk"));
        Assert.Equal("pending", _svc.Repository.GetCreditByGiftEventId("g")!.Status);
    }

    [Fact]
    public void OutboundScoreEcho_DoesNotConsumeCredit()
    {
        var tracker = new OutboundReplyTracker(TimeSpan.FromMinutes(2));
        tracker.Track("reply-1", "哪吒 好看", platformMessageId: "plat-score-echo");
        using var svc = new MovieInteractionService(
            _config,
            _db,
            _log,
            audienceFilter: new ChatAudienceFilter(tracker));
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
        Assert.True(svc.OnGiftReceived(Gift("echo-gift", "u1", 1, now)));
        var echo = Chat("u1", "哪吒 好看", "plat-score-echo");
        echo.RoomKey = "room1";
        Assert.False(svc.TryApplyScoreFromDanmaku(echo, nowOverride: now.AddSeconds(1)));
        Assert.Equal("pending", svc.Repository.GetCreditByGiftEventId("echo-gift")!.Status);
        Assert.Equal(0, svc.Repository.GetTotalScore("1462628"));
    }

    [Fact]
    public void MovieName_FuzzyMatch_DoesNotMatchLongerUnrelatedQuery()
    {
        _svc.UpdateCatalog(new MovieCatalogUpdateRequest
        {
            Movies =
            [
                new MovieCatalogUpdateItem { MovieId = "ody", MovieName = "奥德赛", Rank = 1 }
            ]
        });

        var now = DateTime.Now;
        Assert.True(_svc.OnGiftReceived(Gift("ody-gift", "u1", 1, now)));
        Assert.False(Apply("u1", "奥德赛续集 好评", now.AddSeconds(1), "ody-long"));
        Assert.Equal("pending", _svc.Repository.GetCreditByGiftEventId("ody-gift")!.Status);
        Assert.Equal(0, _svc.Repository.GetTotalScore("ody"));
    }

    [Fact]
    public void MovieName_FuzzyMatch_IgnoresPunctuationInCatalog()
    {
        _svc.UpdateCatalog(new MovieCatalogUpdateRequest
        {
            Movies =
            [
                new MovieCatalogUpdateItem { MovieId = "bx", MovieName = "八仙！", Rank = 1 }
            ]
        });

        var now = DateTime.Now;
        Assert.True(_svc.OnGiftReceived(Gift("bx-gift", "u1", 1, now)));

        Assert.True(Apply("u1", "八仙 不好看", now.AddSeconds(1), "bx-bad"));
        Assert.Equal(-10, _svc.Repository.GetTotalScore("bx"));

        Assert.True(_svc.OnGiftReceived(Gift("bx-gift2", "u2", 1, now.AddSeconds(2))));
        Assert.True(Apply("u2", "八仙！ 好看", now.AddSeconds(3), "bx-good"));
        Assert.Equal(0, _svc.Repository.GetTotalScore("bx"));
    }

    [Fact]
    public void CatalogUpdate_DoesNotEraseHistoricalScores()
    {
        var now = DateTime.Now;
        Assert.True(_svc.OnGiftReceived(Gift("g", "u1", 2, now)));
        Assert.True(Apply("u1", "哪吒好评", now.AddSeconds(1), "hist"));
        _svc.UpdateCatalog(new MovieCatalogUpdateRequest
        {
            Movies = [new MovieCatalogUpdateItem { MovieId = "new", MovieName = "新片", Aliases = ["新"], Rank = 1 }]
        });
        Assert.Equal(20, _svc.Repository.GetTotalScore("1462628"));
    }

    [Fact]
    public void StreamApi_EmitsDanmakuAndScore_CursorDoesNotRepeat()
    {
        var now = DateTime.Now;
        var chat = Chat("u9", "大家好呀", "msg-plain");
        chat.Timestamp = now;
        _svc.OnDanmaku(chat);

        Assert.True(_svc.OnGiftReceived(Gift("g", "u9", 1, now)));
        var scoreItem = Chat("u9", "哪吒 好评", "msg-score");
        scoreItem.Timestamp = now.AddSeconds(1);
        Assert.True(_svc.TryApplyScoreFromDanmaku(scoreItem, nowOverride: now.AddSeconds(1)));

        var first = _svc.Repository.GetStreamAfter(0);
        Assert.Contains(first, e => e.Type == "danmaku");
        Assert.Contains(first, e => e.Type == "movie_score");

        var danmaku = first.First(e => e.Type == "danmaku");
        using (var doc = JsonDocument.Parse(danmaku.PayloadJson))
        {
            Assert.Equal("danmaku", doc.RootElement.GetProperty("type").GetString());
            Assert.Equal("msg-plain", doc.RootElement.GetProperty("msgId").GetString());
            Assert.Equal("u9", doc.RootElement.GetProperty("userId").GetString());
            Assert.Equal("大家好呀", doc.RootElement.GetProperty("content").GetString());
        }

        var score = first.First(e => e.Type == "movie_score");
        using (var doc = JsonDocument.Parse(score.PayloadJson))
        {
            Assert.Equal("movie_score", doc.RootElement.GetProperty("type").GetString());
            Assert.Equal("u9", doc.RootElement.GetProperty("userId").GetString());
            Assert.Equal("1462628", doc.RootElement.GetProperty("movieId").GetString());
            Assert.Equal("good", doc.RootElement.GetProperty("action").GetString());
            Assert.Equal(10, doc.RootElement.GetProperty("scoreDelta").GetInt32());
            Assert.Equal(10, doc.RootElement.GetProperty("totalScore").GetInt32());
            Assert.False(string.IsNullOrWhiteSpace(doc.RootElement.GetProperty("eventId").GetString()));
        }

        var cursor = first.Max(e => e.Seq);
        Assert.Empty(_svc.Repository.GetStreamAfter(cursor));
        var again = _svc.Repository.GetStreamAfter(0);
        Assert.Equal(first.Select(e => e.Seq), again.Select(e => e.Seq));
    }

    [Fact]
    public void MovieSideChannel_DoesNotChangeDanmakuOrSongParse()
    {
        var item = Chat("u1", "点歌 晴天", "song-1");
        var before = item.Content;
        _svc.OnDanmaku(item);
        Assert.Equal(before, item.Content);
        Assert.True(SongNameParser.TryParse(item.Content, out var song));
        Assert.Contains("晴天", song);
    }

    [Fact]
    public void GiftPointsLedger_StaysIndependentFromMovieCredits()
    {
        var gifts = new GiftRepository(_db);
        var users = new UserRepository(_db);
        var giftService = new GiftService(
            _config,
            new DouyinService(_config.Settings.Douyin, _log),
            gifts,
            users,
            new UserLevelService(_config, users),
            new GiftRuleRepository(_db),
            _log,
            new SystemMessageService(50));
        giftService.Start("room1");
        giftService.GiftReceived += g => _svc.OnGiftReceived(g);

        var gift = Gift("ind", "u1", value: 1, diamond: 1, count: 1);
        gift.RoomKey = "room1";
        Assert.True(giftService.HandleGiftEvent(gift));
        Assert.False(giftService.HandleGiftEvent(gift));

        Assert.Equal(1, users.GetUser("u1")!.Points);
        Assert.Equal(10, _svc.Repository.GetCreditByGiftEventId("ind")!.Points);
        giftService.Dispose();
    }

    [Fact]
    public void NoCredits_DoesNotCreateScore()
    {
        Assert.False(Apply("u1", "哪吒好评", DateTime.Now, "none"));
        Assert.Equal(0, _svc.Repository.CountPendingUploads());
        Assert.Equal(0, _svc.Repository.GetTotalScore("1462628"));
    }

    [Fact]
    public void GetHealth_ExposesCreditExpireWindow()
    {
        var json = JsonSerializer.Serialize(_svc.GetHealth());
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(900, doc.RootElement.GetProperty("creditExpireSeconds").GetInt32());
        Assert.Equal(15, doc.RootElement.GetProperty("creditExpireMinutes").GetInt32());
    }

    [Fact]
    public void ResolveMovie_SubtitleAndMainTitle_MatchKillBill()
    {
        _svc.UpdateCatalog(new MovieCatalogUpdateRequest
        {
            Movies =
            [
                new MovieCatalogUpdateItem
                {
                    MovieId = "75313",
                    MovieName = "杀死比尔：血色全传",
                    Aliases = [],
                    Rank = 7
                },
                new MovieCatalogUpdateItem
                {
                    MovieId = "1500469",
                    MovieName = "功夫女足",
                    Rank = 1
                }
            ]
        });

        var stored = _svc.Repository.GetCatalog().Single(m => m.MovieId == "75313");
        Assert.Contains("杀死比尔", stored.Aliases);
        Assert.Contains("血色全传", stored.Aliases);

        var bySub = _svc.ResolveMovie("血色全传");
        Assert.False(bySub.Ambiguous);
        Assert.NotNull(bySub.Entry);
        Assert.Equal("75313", bySub.Entry!.MovieId);
        Assert.Equal("alias", bySub.MatchType);

        var byMain = _svc.ResolveMovie("杀死比尔");
        Assert.False(byMain.Ambiguous);
        Assert.NotNull(byMain.Entry);
        Assert.Equal("75313", byMain.Entry!.MovieId);
        Assert.Equal("alias", byMain.MatchType);

        var byFull = _svc.ResolveMovie("杀死比尔：血色全传");
        Assert.False(byFull.Ambiguous);
        Assert.NotNull(byFull.Entry);
        Assert.Equal("75313", byFull.Entry!.MovieId);
        Assert.True(byFull.MatchType is "movieName" or "normalized");

        var missing = _svc.ResolveMovie("不存在的电影名");
        Assert.Null(missing.Entry);
        Assert.False(missing.Ambiguous);
        Assert.Equal("not_found", missing.FailReason);
    }

    [Fact]
    public void ScoreDanmaku_SubtitleAlias_AppliesToKillBill()
    {
        _svc.UpdateCatalog(new MovieCatalogUpdateRequest
        {
            Movies =
            [
                new MovieCatalogUpdateItem
                {
                    MovieId = "75313",
                    MovieName = "杀死比尔：血色全传",
                    Aliases = [],
                    Rank = 7
                }
            ]
        });

        var now = DateTime.Now;
        Assert.True(_svc.OnGiftReceived(Gift("kb1", "u1", 1, now)));
        Assert.True(Apply("u1", "血色全传 好看", now.AddSeconds(1), "kb-sub"));
        Assert.Equal(10, _svc.Repository.GetTotalScore("75313"));
        AssertVoterCounts("75313", score: 10, good: 1, bad: 0);

        Assert.True(_svc.OnGiftReceived(Gift("kb2", "u2", 1, now.AddSeconds(2))));
        Assert.True(Apply("u2", "杀死比尔 好看", now.AddSeconds(3), "kb-main"));
        AssertVoterCounts("75313", score: 20, good: 2, bad: 0);
    }

    [Fact]
    public void ExpandTitleParts_SplitsColonAndStripsBrackets()
    {
        var parts = MovieInteractionService.ExpandTitleParts("杀死比尔：血色全传").ToList();
        Assert.Contains("杀死比尔", parts);
        Assert.Contains("血色全传", parts);

        var merged = MovieInteractionService.MergeAliases("电影名（导演剪辑版）", ["官方别名"]);
        Assert.Contains("官方别名", merged);
        Assert.Contains("电影名", merged);
        Assert.DoesNotContain("电影名（导演剪辑版）", merged);
    }

    [Theory]
    [InlineData("/api/movie-interaction/health")]
    [InlineData("/api/movie-interaction/movies")]
    [InlineData("/api/movie-interaction/scores")]
    [InlineData("/api/movie-interaction/events")]
    public void LocalApiRoutes_AreRegisteredOnAdminHost(string route)
    {
        var src = File.ReadAllText(FindAdminWebHost());
        Assert.Contains(route, src);
        Assert.Contains("UseUrls($\"http://127.0.0.1:{port}\")", src);
    }

    public void Dispose()
    {
        _svc.Dispose();
        _db.Dispose();
        try { Directory.Delete(_dataDir, true); } catch { /* ignore */ }
    }

    private void SeedDefaultCatalog()
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
                },
                new MovieCatalogUpdateItem
                {
                    MovieId = "2",
                    MovieName = "流浪地球2",
                    Aliases = ["流浪地球"],
                    Rank = 2
                }
            ]
        });
    }

    private bool Apply(string userId, string content, DateTime now, string msgId)
        => _svc.TryApplyScoreFromDanmaku(Chat(userId, content, msgId), content, now);

    private void AssertVoterCounts(string movieId, long score, long good, long bad)
    {
        var total = _svc.Repository.GetTotals().Single(t => t.MovieId == movieId);
        Assert.Equal(score, total.Score);
        Assert.Equal(good, total.GoodUserCount);
        Assert.Equal(bad, total.BadUserCount);

        var payload = JsonSerializer.Serialize(_svc.GetScores());
        using var doc = JsonDocument.Parse(payload);
        var movie = doc.RootElement.GetProperty("movies").EnumerateArray()
            .Single(m => m.GetProperty("movieId").GetString() == movieId);
        Assert.Equal(score, movie.GetProperty("score").GetInt64());
        Assert.Equal(good, movie.GetProperty("goodUserCount").GetInt64());
        Assert.Equal(bad, movie.GetProperty("badUserCount").GetInt64());
    }

    private void ForceRetryNow(string eventId)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE movie_score_events SET next_retry_at = $n WHERE event_id = $e";
        cmd.Parameters.AddWithValue("$n", DateTime.Now.AddSeconds(-1).ToString("O"));
        cmd.Parameters.AddWithValue("$e", eventId);
        cmd.ExecuteNonQuery();
    }

    private static GiftEvent Gift(
        string eventId,
        string userId,
        int value,
        DateTime? time = null,
        int diamond = 0,
        int count = 1,
        string name = "礼物")
    {
        return new GiftEvent
        {
            EventId = eventId,
            UserId = userId,
            Nickname = "观众",
            GiftId = "gid",
            GiftName = name,
            Count = count,
            DiamondCount = diamond == 0 ? value : diamond,
            Value = value,
            Time = time ?? DateTime.Now,
            RoomKey = "room1"
        };
    }

    private static DanmakuItem Chat(string userId, string content, string msgId)
    {
        return new DanmakuItem
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

    private static string FindRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 20; i++)
        {
            if (File.Exists(Path.Combine(dir, "LiveAssistant.sln")))
            {
                return dir;
            }

            dir = Directory.GetParent(dir)?.FullName
                  ?? throw new DirectoryNotFoundException("LiveAssistant.sln not found");
        }

        throw new DirectoryNotFoundException("LiveAssistant.sln not found");
    }

    private static string FindAdminWebHost()
    {
        var bundled = Path.Combine(AppContext.BaseDirectory, "repo-snapshots", "AdminWebHost.cs");
        if (File.Exists(bundled))
        {
            return bundled;
        }

        return Path.Combine(FindRepoRoot(), "LiveAssistant", "Admin", "AdminWebHost.cs");
    }

    private sealed class ScriptedHandler : HttpMessageHandler
    {
        private readonly Queue<HttpStatusCode> _codes = new();
        public int Calls { get; private set; }
        public List<string> Paths { get; } = new();

        public void Enqueue(HttpStatusCode code) => _codes.Enqueue(code);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            Paths.Add(request.RequestUri?.AbsolutePath ?? "");
            var code = _codes.Count > 0 ? _codes.Dequeue() : HttpStatusCode.InternalServerError;
            return Task.FromResult(new HttpResponseMessage(code)
            {
                Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json")
            });
        }
    }

    private sealed class TimeoutHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new TaskCanceledException("simulated timeout");
    }
}
