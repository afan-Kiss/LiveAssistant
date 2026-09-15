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

    public SongRequestPermissionService(
        ConfigManager config,
        UserRepository users,
        QueueService queue,
        SongBlacklistService blacklist,
        LevelPermissionRepository levelPerms,
        UserLevelService levels,
        GiftRepository? gifts = null,
        SongRequestControlService? control = null)
    {
        _config = config;
        _users = users;
        _queue = queue;
        _blacklist = blacklist;
        _levelPerms = levelPerms;
        _levels = levels;
        _gifts = gifts;
        _control = control;
    }

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

    /// <summary>
    /// 测试钩子：积分/点歌次卡已扣减之后、写请求记录之前触发。用于模拟「扣分成功但记请求失败」。
    /// </summary>
    internal Action<DanmakuItem, long?>? TestBeforeRecordRequest { get; set; }

    public bool RecordSuccessfulRequest(DanmakuItem item, long? queueItemId = null)
        => TryCommitSuccessfulRequest(item, queueItemId).Success;

    /// <summary>
    /// 扣积分/次卡并写请求记录。任一步失败则尽量回滚已扣资源后返回失败或抛出（已回滚）。
    /// </summary>
    public SongRequestChargeResult TryCommitSuccessfulRequest(DanmakuItem item, long? queueItemId = null)
    {
        var user = _users.GetUser(item.UserId);
        var pointsBefore = user?.Points ?? 0;
        var deducted = 0;
        var creditConsumed = false;

        try
        {
            if (user != null && !IsPrivileged(user.Role))
            {
                if (user.SongPermissionUnlimited)
                {
                    // 仅记请求
                }
                else if (user.SongPermissionCredits > 0)
                {
                    if (!_users.ConsumeSongPermissionCredit(item.UserId))
                    {
                        return SongRequestChargeResult.Fail(pointsBefore, pointsBefore, 0, false, "credit_consume_failed");
                    }

                    creditConsumed = true;
                }
                else
                {
                    var policy = _config.Settings.SongRequestPolicy;
                    if (policy.Mode == SongRequestPolicyMode.Points)
                    {
                        var levelPerm = _levelPerms.GetForLevel(user.Level);
                        var cost = levelPerm?.PointsCostOverride >= 0
                            ? levelPerm.PointsCostOverride
                            : policy.PointsCost;
                        if (cost > 0)
                        {
                            if (!_users.TryDeductPoints(
                                    item.UserId,
                                    item.Nickname,
                                    cost,
                                    PointsTransactionType.SongRequest,
                                    "点歌扣积分",
                                    queueItemId?.ToString(),
                                    out _))
                            {
                                return SongRequestChargeResult.Fail(
                                    pointsBefore, pointsBefore, 0, false, "insufficient_points");
                            }

                            deducted = cost;
                        }
                    }
                }
            }

            TestBeforeRecordRequest?.Invoke(item, queueItemId);

            _users.RecordSuccessfulRequest(item.UserId, item.Nickname);
            _levels.RefreshUserLevel(item.UserId);

            var pointsAfter = _users.GetUser(item.UserId)?.Points ?? (pointsBefore - deducted);
            return SongRequestChargeResult.Ok(pointsBefore, pointsAfter, deducted, creditConsumed);
        }
        catch
        {
            RestoreCharge(item.UserId, item.Nickname, deducted, creditConsumed);
            throw;
        }
    }

    private void RestoreCharge(string userId, string nickname, int deducted, bool creditConsumed)
    {
        if (deducted > 0)
        {
            _users.TryChangePoints(
                userId,
                nickname,
                deducted,
                PointsTransactionType.Refund,
                "点歌扣积分失败回滚",
                null,
                out _);
        }

        if (creditConsumed)
        {
            _users.AddSongPermissionCredits(userId, 1);
        }
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
