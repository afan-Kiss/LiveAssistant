using LiveAssistant.Config;
using LiveAssistant.Database;
using LiveAssistant.Models;

namespace LiveAssistant.Services;

/// <summary>
/// 统一点歌权限判断（角色、冷却、队列容量）。
/// </summary>
public sealed class SongRequestPermissionService
{
    private readonly ConfigManager _config;
    private readonly UserRepository _users;
    private readonly QueueService _queue;
    private readonly SongBlacklistService _blacklist;

    public SongRequestPermissionService(
        ConfigManager config,
        UserRepository users,
        QueueService queue,
        SongBlacklistService blacklist)
    {
        _config = config;
        _users = users;
        _queue = queue;
        _blacklist = blacklist;
    }

    public SongRequestPermissionResult Evaluate(DanmakuItem item)
    {
        if (_config.Settings.Emergency.PauseSongRequest)
        {
            return SongRequestPermissionResult.Deny(null, "点歌已暂停", "songRequestRejected",
                new Dictionary<string, string>
                {
                    ["name"] = item.Nickname,
                    ["reason"] = "主播已暂停点歌"
                });
        }

        var user = _users.EnsureUser(item.UserId, item.Nickname);

        if (user.Role == UserRole.Blacklist || user.Status == UserStatus.Banned)
        {
            return SongRequestPermissionResult.Deny(user, "黑名单用户", "songRequestRejected",
                new Dictionary<string, string>
                {
                    ["name"] = item.Nickname,
                    ["reason"] = "黑名单用户"
                });
        }

        if (user.Status == UserStatus.Muted)
        {
            return SongRequestPermissionResult.Deny(user, "已被禁言", "songRequestRejected",
                new Dictionary<string, string>
                {
                    ["name"] = item.Nickname,
                    ["reason"] = "已被禁言"
                });
        }

        if (ShouldApplyCooldown(user.Role))
        {
            var cooldownSec = _config.Settings.Queue.RequestCooldownSeconds;
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
                new Dictionary<string, string>
                {
                    ["name"] = item.Nickname
                });
        }

        return SongRequestPermissionResult.Permit(user);
    }

    public SongRequestPermissionResult EvaluateSong(string songName, UserProfile user, DanmakuItem item)
    {
        if (_blacklist.IsBlocked(songName))
        {
            return SongRequestPermissionResult.Deny(user, "歌曲在黑名单", "songRequestRejected",
                new Dictionary<string, string>
                {
                    ["name"] = item.Nickname,
                    ["reason"] = "该歌曲不可点"
                });
        }
        return SongRequestPermissionResult.Permit(user);
    }

    public void RecordSuccessfulRequest(DanmakuItem item)
    {
        _users.RecordSuccessfulRequest(item.UserId, item.Nickname);
    }

    private static bool ShouldApplyCooldown(UserRole role) =>
        role is UserRole.Normal;
}
