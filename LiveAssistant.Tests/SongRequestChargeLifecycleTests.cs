using LiveAssistant.Config;
using LiveAssistant.Database;
using LiveAssistant.Models;
using LiveAssistant.Services;
using Xunit;

namespace LiveAssistant.Tests;

public sealed class SongRequestChargeLifecycleTests : IDisposable
{
    private readonly string _dir;
    private readonly AppDatabase _db;
    private readonly PointsLedgerRepository _ledger;
    private readonly UserRepository _users;
    private readonly SongRequestChargeRepository _charges;
    private readonly ConfigManager _config;
    private readonly LogService _log;
    private readonly QueueService _queue;
    private readonly SongRequestPermissionService _permission;

    public SongRequestChargeLifecycleTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "la_charge_" + Guid.NewGuid().ToString("N"));
        _db = new AppDatabase(_dir);
        _ledger = new PointsLedgerRepository(_db);
        _users = new UserRepository(_db, _ledger);
        _charges = new SongRequestChargeRepository(_db, _ledger);
        _config = new ConfigManager();
        _config.Load();
        _config.Settings.SongRequestPolicy.Mode = SongRequestPolicyMode.Points;
        _config.Settings.SongRequestPolicy.PointsCost = 10;
        _config.Settings.SongRequestPolicy.MinLevel = 0;
        _log = new LogService(_dir);
        _queue = new QueueService(_db);
        _permission = new SongRequestPermissionService(
            _config, _users, _queue,
            new SongBlacklistService(new SongBlacklistRepository(_db)),
            new LevelPermissionRepository(_db),
            new UserLevelService(_config, _users),
            log: _log,
            charges: _charges);
        _queue.OnWaitingItemRemoved = (id, reason) =>
            _permission.RefundQueueItemIfNeeded(id, reason);
    }

    [Fact]
    public void PointsCharge_PlayFail_RefundsOnce()
    {
        _users.EnsureUser("u1", "观众");
        _users.AddPoints("u1", "观众", 50);
        var item = Danmaku("u1", "观众");
        Assert.True(_queue.TryAddWithPriority(MakeQueue("u1", "观众", "晴天"), 0, 100, false, out var added) && added != null);
        var charge = _permission.TryCommitSuccessfulRequest(item, added!.Id);
        Assert.True(charge.Success);
        Assert.Equal(10, charge.PointsDeducted);
        Assert.Equal(40, _users.GetUser("u1")!.Points);

        var r1 = _permission.RefundSongRequestCharge(added.Id, "player_start_failed");
        Assert.Equal("success", r1.Result);
        Assert.Equal(10, r1.PointsRestored);
        Assert.Equal(50, _users.GetUser("u1")!.Points);

        var r2 = _permission.RefundSongRequestCharge(added.Id, "player_start_failed");
        Assert.Equal("already_refunded", r2.Result);
        Assert.Equal(50, _users.GetUser("u1")!.Points);

        var ledger = _ledger.ListByUser("u1", 20);
        Assert.Contains(ledger, e => e.Type == PointsTransactionType.Refund && e.Delta == 10);
    }

    [Fact]
    public void CreditCharge_PlayFail_RestoresCreditOnce()
    {
        _users.EnsureUser("u2", "次卡用户");
        _users.AddSongPermissionCredits("u2", 1);
        Assert.Equal(1, _users.GetUser("u2")!.SongPermissionCredits);

        Assert.True(_queue.TryAddWithPriority(MakeQueue("u2", "次卡用户", "海阔天空"), 0, 100, false, out var added) && added != null);
        var charge = _permission.TryCommitSuccessfulRequest(Danmaku("u2", "次卡用户"), added!.Id);
        Assert.True(charge.Success);
        Assert.True(charge.CreditConsumed);
        Assert.Equal(0, _users.GetUser("u2")!.SongPermissionCredits);

        var r1 = _permission.RefundSongRequestCharge(added.Id, "resolve_fresh_failed");
        Assert.Equal("success", r1.Result);
        Assert.Equal(1, r1.CreditRestored);
        Assert.Equal(1, _users.GetUser("u2")!.SongPermissionCredits);

        var r2 = _permission.RefundSongRequestCharge(added.Id, "resolve_fresh_failed");
        Assert.Equal("already_refunded", r2.Result);
        Assert.Equal(1, _users.GetUser("u2")!.SongPermissionCredits);
    }

    [Fact]
    public void FreeCharge_Fail_NoPointsRefund()
    {
        _config.Settings.SongRequestPolicy.Mode = SongRequestPolicyMode.Free;
        _users.EnsureUser("u3", "免费");
        _users.AddPoints("u3", "免费", 20);
        Assert.True(_queue.TryAddWithPriority(MakeQueue("u3", "免费", "七里香"), 0, 100, false, out var added) && added != null);
        var charge = _permission.TryCommitSuccessfulRequest(Danmaku("u3", "免费"), added!.Id);
        Assert.True(charge.Success);
        Assert.Equal(0, charge.PointsDeducted);
        Assert.Equal(SongRequestChargeType.Free, charge.ChargeType);

        var r = _permission.RefundSongRequestCharge(added.Id, "admin_delete_waiting");
        Assert.Equal("success", r.Result);
        Assert.Equal(0, r.PointsRestored);
        Assert.Equal(20, _users.GetUser("u3")!.Points);
        Assert.DoesNotContain(_ledger.ListByUser("u3", 20), e => e.Type == PointsTransactionType.Refund);
    }

    [Fact]
    public void Fulfilled_Skip_DoesNotRefund()
    {
        _users.EnsureUser("u4", "已播");
        _users.AddPoints("u4", "已播", 30);
        Assert.True(_queue.TryAddWithPriority(MakeQueue("u4", "已播", "稻香"), 0, 100, false, out var added) && added != null);
        Assert.True(_permission.TryCommitSuccessfulRequest(Danmaku("u4", "已播"), added!.Id).Success);
        Assert.True(_permission.MarkSongRequestFulfilled(added.Id));

        var r = _permission.RefundSongRequestCharge(added.Id, "user_skip");
        Assert.Equal("already_fulfilled", r.Result);
        Assert.Equal(20, _users.GetUser("u4")!.Points);
    }

    [Fact]
    public void StartupAbandon_RefundsChargedWaiting()
    {
        _users.EnsureUser("u5", "重启");
        _users.AddPoints("u5", "重启", 40);
        Assert.True(_queue.TryAddWithPriority(MakeQueue("u5", "重启", "告白气球"), 0, 100, false, out var added) && added != null);
        Assert.True(_permission.TryCommitSuccessfulRequest(Danmaku("u5", "重启"), added!.Id).Success);
        Assert.Equal(30, _users.GetUser("u5")!.Points);

        foreach (var pending in _db.ListPendingQueueItemsForAbandon())
        {
            if (!pending.IsRandom)
            {
                _permission.RefundSongRequestCharge(pending.Id, "startup_abandon");
            }
        }

        var abandoned = _db.AbandonPendingQueueOnStartup();
        Assert.True(abandoned >= 1);
        Assert.Equal(40, _users.GetUser("u5")!.Points);
        Assert.Equal(SongRequestChargeStatus.Refunded, _charges.GetByQueueItemId(added.Id)!.Status);
    }

    [Fact]
    public void AdminDeleteWaiting_Refunds()
    {
        _users.EnsureUser("u6", "删队");
        _users.AddPoints("u6", "删队", 25);
        Assert.True(_queue.TryAddWithPriority(MakeQueue("u6", "删队", "夜曲"), 0, 100, false, out var added) && added != null);
        Assert.True(_permission.TryCommitSuccessfulRequest(Danmaku("u6", "删队"), added!.Id).Success);
        Assert.True(_queue.Remove(added.Id));
        Assert.Equal(25, _users.GetUser("u6")!.Points);
        Assert.Equal(SongRequestChargeStatus.Refunded, _charges.GetByQueueItemId(added.Id)!.Status);
    }

    [Fact]
    public void ClearQueue_RefundsEach()
    {
        _users.EnsureUser("u7", "清空A");
        _users.EnsureUser("u8", "清空B");
        _users.AddPoints("u7", "清空A", 50);
        _users.AddPoints("u8", "清空B", 50);
        Assert.True(_queue.TryAddWithPriority(MakeQueue("u7", "清空A", "A歌"), 0, 100, false, out var a) && a != null);
        Assert.True(_queue.TryAddWithPriority(MakeQueue("u8", "清空B", "B歌"), 0, 100, false, out var b) && b != null);
        Assert.True(_permission.TryCommitSuccessfulRequest(Danmaku("u7", "清空A"), a!.Id).Success);
        Assert.True(_permission.TryCommitSuccessfulRequest(Danmaku("u8", "清空B"), b!.Id).Success);

        _queue.ClearWaiting();
        Assert.Equal(50, _users.GetUser("u7")!.Points);
        Assert.Equal(50, _users.GetUser("u8")!.Points);
        Assert.Equal(SongRequestChargeStatus.Refunded, _charges.GetByQueueItemId(a.Id)!.Status);
        Assert.Equal(SongRequestChargeStatus.Refunded, _charges.GetByQueueItemId(b.Id)!.Status);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* ignore */ }
    }

    private static DanmakuItem Danmaku(string userId, string nick) => new()
    {
        UserId = userId,
        Nickname = nick,
        Content = "点歌 晴天",
        MsgType = "chat",
        MsgId = Guid.NewGuid().ToString("N")
    };

    private static QueueItem MakeQueue(string userId, string nick, string song) => new()
    {
        UserId = userId,
        Nickname = nick,
        SongName = song,
        Artist = "周杰伦",
        Hash = Guid.NewGuid().ToString("N"),
        IsRandom = false,
        Status = QueueItemStatus.Waiting
    };
}
