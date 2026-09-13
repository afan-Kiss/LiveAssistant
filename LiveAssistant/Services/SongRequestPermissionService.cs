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

    public SongRequestPermissionService(
        ConfigManager config,
        UserRepository users,
        QueueService queue,
        SongBlacklistService blacklist,
        LevelPermissionRepository levelPerms,
        UserLevelService levels)
    {
        _config = config;
        _users = users;
        _queue = queue;
        _blacklist = blacklist;
        _levelPerms = levelPerms;
        _levels = levels;
    }

    public SongRequestPermissionResult Evaluate(DanmakuItem item)
    {
        if (_config.Settings.Emergency.PauseSongRequest)
        {
            return Deny(null, item.Nickname, "点歌已暂停", "主播已暂停点歌");
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
                if (!string.IsNullOrWhiteSpace(policy.RequiredGiftName))
                {
                    return Deny(user, item.Nickname, "需要指定礼物", $"需送出 {policy.RequiredGiftName}");
                }
                break;
        }

        if (ShouldApplyCooldown(user))
        {
            var cooldownSec = levelPerm?.CooldownSeconds ?? _config.Settings.Queue.RequestCooldownSeconds;
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

    public void RecordSuccessfulRequest(DanmakuItem item)
    {
        var user = _users.GetUser(item.UserId);
        if (user != null && !IsPrivileged(user.Role))
        {
            var policy = _config.Settings.SongRequestPolicy;
            if (policy.Mode == SongRequestPolicyMode.Points)
            {
                var levelPerm = _levelPerms.GetForLevel(user.Level);
                var cost = levelPerm?.PointsCostOverride >= 0
                    ? levelPerm.PointsCostOverride
                    : policy.PointsCost;
                _users.DeductPoints(item.UserId, cost);
            }
        }

        _users.RecordSuccessfulRequest(item.UserId, item.Nickname);
        _levels.RefreshUserLevel(item.UserId);
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
