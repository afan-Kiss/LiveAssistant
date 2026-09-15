using LiveAssistant.Config;
using LiveAssistant.Database;
using LiveAssistant.Models;

namespace LiveAssistant.Services;

public sealed class SongRequestPermissionService
{
    private readonly ConfigManager _config;
    private readonly UserRepository _users;
    private readonly QueueService _queue;
    private readonly SongBlacklistService _blacklist;
    private readonly LevelPermissionRepository _levelPerms;
    private readonly UserLevelService _levels;
    private readonly GiftRepository? _gifts;
    private readonly SongRequestControlService? _control;
    private readonly LogService? _log;

    public SongRequestPermissionService(
        ConfigManager config,
        UserRepository users,
        QueueService queue,
        SongBlacklistService blacklist,
        LevelPermissionRepository levelPerms,
        UserLevelService levels,
        GiftRepository? gifts = null,
        SongRequestControlService? control = null,
        LogService? log = null)
    {
        _config = config;
        _users = users;
        _queue = queue;
        _blacklist = blacklist;
        _levelPerms = levelPerms;
        _levels = levels;
        _gifts = gifts;
        _control = control;
        _log = log;
    }

    /// <summary>测试：强制退款失败。</summary>
    internal Func<bool>? TestForceRefundFailure { get; set; }

    /// <summary>测试：强制次卡恢复失败。</summary>
    internal Func<bool>? TestForceCreditRestoreFailure { get; set; }

    /// <summary>测试：扣费事务成功后、等级刷新前抛异常（派生失败，不应回滚核心事务）。</summary>
    internal Action? TestAfterCommitBeforeLevelRefresh { get; set; }

    public SongRequestPermissionResult Evaluate(DanmakuItem item)
    {
        var gate = _control?.EvaluateGate(item);
        if (gate != null)
        {
            return Deny(null, item.Nickname, gate.Reason, gate.Reason);
        }

        var user = _users.EnsureUser(item.UserId, item.Nickname);
        _levels.RefreshUserLevel(item.UserId);
        user = _users.GetUser(item.UserId) ?? user;

        if (user.Role == UserRole.Blacklist || user.Status == UserStatus.Banned)
        {
            return Deny(user, item.Nickname, "黑名单用户", "黑名单用户");
        }

        if (user.Status == UserStatus.Muted)
        {
            return Deny(user, item.Nickname, "已被禁言", "已被禁言");
        }

        if (IsPrivileged(user.Role))
        {
            return SongRequestPermissionResult.Permit(user);
        }

        var levelPerm = _levelPerms.GetForLevel(user.Level);
        var policy = _config.Settings.SongRequestPolicy;
        var hasGiftPermission = user.SongPermissionUnlimited || user.SongPermissionCredits > 0;

        if (user.Level < policy.MinLevel)
        {
            return Deny(user, item.Nickname, "等级不足", $"需要等级 {policy.MinLevel}");
        }

        if (levelPerm != null && !levelPerm.CanRequest)
        {
            return Deny(user, item.Nickname, "等级不足", $"等级 {user.Level} 不可点歌");
        }

        if (levelPerm != null && user.Points < levelPerm.MinPoints)
        {
            return Deny(user, item.Nickname, "积分不足", $"需要至少 {levelPerm.MinPoints} 积分");
        }

        if (!hasGiftPermission)
        {
            switch (policy.Mode)
            {
                case SongRequestPolicyMode.Points:
                    var cost = levelPerm?.PointsCostOverride >= 0
                        ? levelPerm.PointsCostOverride
                        : policy.PointsCost;
                    if (user.Points < cost)
                    {
                        return Deny(user, item.Nickname, "积分不足", $"点歌需要 {cost} 积分");
                    }
                    break;
                case SongRequestPolicyMode.GiftUnlock:
                    if (user.Points < policy.GiftUnlockMinPoints)
                    {
                        return Deny(user, item.Nickname, "未解锁点歌", $"需送礼物累计 {policy.GiftUnlockMinPoints} 积分");
                    }
                    if (!string.IsNullOrWhiteSpace(policy.RequiredGiftName)
                        && _gifts != null
                        && !_gifts.HasReceivedGift(item.UserId, policy.RequiredGiftName))
                    {
                        return Deny(user, item.Nickname, "需要指定礼物", $"需送出 {policy.RequiredGiftName}");
                    }
                    break;
            }
        }

        if (ShouldApplyCooldown(user))
        {
            var cooldownSec = GetCooldownSeconds(user, levelPerm);
            var (allowed, remaining) = _users.CheckCooldown(item.UserId, cooldownSec);
            if (!allowed)
            {
                return SongRequestPermissionResult.Deny(user, $"冷却中 剩余{remaining}秒", "songRequestCooldown",
                    new Dictionary<string, string>
                    {
                        ["name"] = item.Nickname,
                        ["seconds"] = remaining.ToString()
                    });
            }
        }

        if (_queue.WaitingCount >= _config.Settings.Queue.MaxSize)
        {
            return SongRequestPermissionResult.Deny(user, "队列已满", "queueFull",
                new Dictionary<string, string> { ["name"] = item.Nickname });
        }

        return SongRequestPermissionResult.Permit(user);
    }

    public int GetPointsCost(UserProfile user)
    {
        if (IsPrivileged(user.Role))
        {
            return 0;
        }

        if (user.SongPermissionUnlimited || user.SongPermissionCredits > 0)
        {
            return 0;
        }

        if (_config.Settings.SongRequestPolicy.Mode != SongRequestPolicyMode.Points)
        {
            return 0;
        }

        var levelPerm = _levelPerms.GetForLevel(user.Level);
        return levelPerm?.PointsCostOverride >= 0
            ? levelPerm.PointsCostOverride
            : _config.Settings.SongRequestPolicy.PointsCost;
    }

    public int GetQueuePriority(UserProfile user)
    {
        if (IsPrivileged(user.Role))
        {
            return 100;
        }
        return _levelPerms.GetForLevel(user.Level)?.QueuePriority ?? 0;
    }

    public SongRequestPermissionResult EvaluateSong(string songName, UserProfile user, DanmakuItem item)
    {
        if (_blacklist.IsBlocked(songName))
        {
            return Deny(user, item.Nickname, "歌曲在黑名单", "该歌曲不可点");
        }
        return SongRequestPermissionResult.Permit(user);
    }

    public bool RecordSuccessfulRequest(DanmakuItem item, long? queueItemId = null)
        => TryCommitSuccessfulRequest(item, queueItemId).Success;

    /// <summary>
    /// 核心事务：扣积分/次卡 + request_count 同一 SQLite 提交。
    /// RefreshUserLevel 为派生刷新，失败不回滚核心事务。
    /// </summary>
    public SongRequestChargeResult TryCommitSuccessfulRequest(DanmakuItem item, long? queueItemId = null)
    {
        var user = _users.GetUser(item.UserId);
        var pointsBefore = user?.Points ?? 0;

        if (user != null && IsPrivileged(user.Role))
        {
            // 特权：只记请求次数（cost=0）
            if (!_users.TryCommitSongRequestCharge(
                    item.UserId, item.Nickname, 0, false, queueItemId?.ToString(),
                    out pointsBefore, out var afterPriv, out _, out var failPriv))
            {
                return SongRequestChargeResult.Fail(pointsBefore, pointsBefore, 0, false, failPriv ?? "record_failed");
            }

            TryRefreshLevelBestEffort(item.UserId);
            return SongRequestChargeResult.Ok(pointsBefore, afterPriv, 0, false);
        }

        var consumeCredit = user is { SongPermissionUnlimited: false, SongPermissionCredits: > 0 };
        var unlimited = user?.SongPermissionUnlimited == true;
        var cost = 0;
        if (!unlimited && !consumeCredit && user != null)
        {
            cost = GetPointsCost(user);
        }

        if (!_users.TryCommitSongRequestCharge(
                item.UserId,
                item.Nickname,
                unlimited || consumeCredit ? 0 : cost,
                consumeCredit,
                queueItemId?.ToString(),
                out pointsBefore,
                out var pointsAfter,
                out var creditConsumed,
                out var failureReason))
        {
            return SongRequestChargeResult.Fail(
                pointsBefore, pointsBefore, 0, false, failureReason ?? "charge_failed");
        }

        TryRefreshLevelBestEffort(item.UserId);
        return SongRequestChargeResult.Ok(pointsBefore, pointsAfter, cost, creditConsumed);
    }

    private void TryRefreshLevelBestEffort(string userId)
    {
        try
        {
            TestAfterCommitBeforeLevelRefresh?.Invoke();
            _levels.RefreshUserLevel(userId);
        }
        catch (Exception ex)
        {
            _log?.Error("song_request", $"RefreshUserLevel 派生失败（核心事务已提交） user={userId}", ex);
        }
    }

    /// <summary>补偿退款；返回值必须检查，禁止忽略。</summary>
    public SongRequestRestoreResult RestoreCharge(string userId, string nickname, int deducted, bool creditConsumed)
    {
        var pointsRestored = true;
        var creditRestored = true;
        Exception? firstEx = null;
        var reasons = new List<string>();

        if (deducted > 0)
        {
            try
            {
                if (TestForceRefundFailure?.Invoke() == true)
                {
                    pointsRestored = false;
                    reasons.Add("refund_forced_fail");
                }
                else if (!_users.TryChangePoints(
                             userId,
                             nickname,
                             deducted,
                             PointsTransactionType.Refund,
                             "点歌扣积分失败回滚",
                             null,
                             out _))
                {
                    pointsRestored = false;
                    reasons.Add("refund_try_change_false");
                    _log?.Error("song_request",
                        $"SONG_REQUEST_COMPENSATION_FAILED userId={userId} pointsDeducted={deducted} pointsRestored=false");
                }
            }
            catch (Exception ex)
            {
                pointsRestored = false;
                firstEx ??= ex;
                reasons.Add("refund_exception");
                _log?.Error("song_request",
                    $"SONG_REQUEST_COMPENSATION_FAILED userId={userId} pointsDeducted={deducted} exception={ex.Message}", ex);
            }
        }

        if (creditConsumed)
        {
            try
            {
                if (TestForceCreditRestoreFailure?.Invoke() == true)
                {
                    creditRestored = false;
                    reasons.Add("credit_restore_forced_fail");
                }
                else if (!_users.AddSongPermissionCredits(userId, 1))
                {
                    creditRestored = false;
                    reasons.Add("credit_restore_false");
                    _log?.Error("song_request",
                        $"SONG_REQUEST_COMPENSATION_FAILED userId={userId} creditRestored=false");
                }
            }
            catch (Exception ex)
            {
                creditRestored = false;
                firstEx ??= ex;
                reasons.Add("credit_restore_exception");
                _log?.Error("song_request",
                    $"SONG_REQUEST_COMPENSATION_FAILED userId={userId} creditRestored=false exception={ex.Message}", ex);
            }
        }

        if (pointsRestored && creditRestored)
        {
            return SongRequestRestoreResult.Ok(deducted > 0, creditConsumed);
        }

        return SongRequestRestoreResult.Fail(
            pointsRestored,
            creditRestored,
            string.Join(",", reasons),
            firstEx);
    }

    private int GetCooldownSeconds(UserProfile user, LevelPermission? levelPerm)
    {
        if (user.Level > 0 && levelPerm != null && levelPerm.CooldownSeconds >= 0)
        {
            return levelPerm.CooldownSeconds;
        }
        return _config.Settings.Queue.SongRequestCooldownSeconds;
    }

    private static bool IsPrivileged(UserRole role) =>
        role is UserRole.Admin or UserRole.Manager;

    private static bool ShouldApplyCooldown(UserProfile user) =>
        user.Role is UserRole.Normal;

    private static SongRequestPermissionResult Deny(UserProfile? user, string nickname, string reason, string display) =>
        SongRequestPermissionResult.Deny(user, reason, "songRequestRejected",
            new Dictionary<string, string>
            {
                ["name"] = nickname,
                ["reason"] = display
            });
}
