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

    public SongRequestPermissionService(ConfigManager config, UserRepository users, QueueService queue)
    {
        _config = config;
        _users = users;
        _queue = queue;
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

        if (user.Role == UserRole.Blacklist)
        {
            return SongRequestPermissionResult.Deny(user, "黑名单用户", "songRequestRejected",
                new Dictionary<string, string>
                {
                    ["name"] = item.Nickname,
                    ["reason"] = "黑名单用户"
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

    public void RecordSuccessfulRequest(DanmakuItem item)
    {
        _users.RecordSuccessfulRequest(item.UserId, item.Nickname);
    }

    private static bool ShouldApplyCooldown(UserRole role) =>
        role is UserRole.Normal;
}
