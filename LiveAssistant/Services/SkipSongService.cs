using LiveAssistant.Config;
using LiveAssistant.Database;
using LiveAssistant.Models;
using LiveAssistant.Utils;

namespace LiveAssistant.Services;

public sealed class SkipSongService
{
    private readonly ConfigManager _config;
    private readonly UserRepository _users;
    private readonly PlaybackService _playback;
    private readonly QueueService _queue;
    private readonly PlaybackEngine _engine;
    private readonly ReplyService _reply;
    private readonly ReplyQueue _replyQueue;
    private readonly SystemMessageService _system;
    private readonly LogService _log;

    public SkipSongService(
        ConfigManager config,
        UserRepository users,
        PlaybackService playback,
        QueueService queue,
        PlaybackEngine engine,
        ReplyService reply,
        ReplyQueue replyQueue,
        SystemMessageService system,
        LogService log)
    {
        _config = config;
        _users = users;
        _playback = playback;
        _queue = queue;
        _engine = engine;
        _reply = reply;
        _replyQueue = replyQueue;
        _system = system;
        _log = log;
    }

    public async Task<bool> TryHandleAsync(DanmakuItem item, string webRid)
    {
        if (!SkipSongParser.TryParse(item.Content))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(item.UserId))
        {
            return true;
        }

        var user = _users.EnsureUser(item.UserId, item.Nickname);
        if (user.Role == UserRole.Blacklist || user.Status == UserStatus.Banned)
        {
            Reply(webRid, item.UserId, "skipSongRejected", item.Nickname, "你已被限制使用切歌");
            return true;
        }

        if (user.Status == UserStatus.Muted)
        {
            Reply(webRid, item.UserId, "skipSongRejected", item.Nickname, "你已被禁言");
            return true;
        }

        if (!HasSkippablePlayback())
        {
            Reply(webRid, item.UserId, "skipSongNothingPlaying", item.Nickname);
            _system.Add($"{item.Nickname} 切歌失败：当前没有可切的歌曲");
            return true;
        }

        var cost = GetSkipCost(user);
        if (cost > 0)
        {
            user = _users.GetUser(item.UserId) ?? user;
            if (user.Points < cost)
            {
                var msg = _reply.Render("skipSongInsufficientPoints", new Dictionary<string, string>
                {
                    ["name"] = item.Nickname,
                    ["cost"] = cost.ToString(),
                    ["score"] = user.Points.ToString()
                });
                if (string.IsNullOrWhiteSpace(msg))
                {
                    msg = $"切歌需要 {cost} 积分，你当前只有 {user.Points} 积分";
                }

                _replyQueue.EnqueueMention(webRid, item.UserId, msg, nickname: item.Nickname);
                _system.Add($"{item.Nickname} 切歌失败：积分不足（需要 {cost}）");
                return true;
            }

            // 先原子扣积分，再接受切歌命令，避免「已切歌但扣分失败」
            if (!_users.TryDeductPoints(
                    item.UserId,
                    item.Nickname,
                    cost,
                    PointsTransactionType.SkipSong,
                    "切歌扣积分",
                    null,
                    out _))
            {
                _replyQueue.EnqueueMention(webRid, item.UserId,
                    $"切歌需要 {cost} 积分，请送礼物获取积分后再试", nickname: item.Nickname);
                _system.Add($"{item.Nickname} 切歌失败：扣积分失败");
                return true;
            }
        }

        var accepted = await _engine.SkipAsync();
        if (!accepted)
        {
            if (cost > 0)
            {
                _users.TryChangePoints(
                    item.UserId,
                    item.Nickname,
                    cost,
                    PointsTransactionType.Refund,
                    "切歌命令入队失败退回积分",
                    null,
                    out _);
                _log.Info($"切歌命令入队失败，已退回积分 user={item.Nickname} cost={cost}");
            }

            Reply(webRid, item.UserId, "skipSongRejected", item.Nickname, "切歌失败，请稍后再试");
            _system.Add($"{item.Nickname} 切歌失败：命令入队失败");
            return true;
        }

        var successKey = cost > 0 ? "skipSongSuccessPaid" : "skipSongSuccess";
        var success = _reply.Render(successKey, new Dictionary<string, string>
        {
            ["name"] = item.Nickname,
            ["cost"] = cost.ToString()
        });
        if (string.IsNullOrWhiteSpace(success))
        {
            success = cost > 0 ? $"已切歌，消耗{cost}积分" : "已切歌";
        }

        _replyQueue.EnqueueMention(webRid, item.UserId, success, nickname: item.Nickname);
        _system.Add(cost > 0
            ? $"{item.Nickname} 切歌成功（-{cost} 积分）"
            : $"{item.Nickname} 切歌成功");
        _log.Info($"切歌: {item.Nickname} cost={cost}");
        return true;
    }

    public bool TryHandle(DanmakuItem item, string webRid)
        => TryHandleAsync(item, webRid).GetAwaiter().GetResult();

    private bool HasSkippablePlayback()
        => _playback.State != PlaybackState.Idle || _queue.NowPlaying != null;

    private int GetSkipCost(UserProfile user)
    {
        if (user.Role is UserRole.Admin or UserRole.Manager)
        {
            return 0;
        }

        if (_config.Settings.SongRequestPolicy.Mode != SongRequestPolicyMode.Points)
        {
            return 0;
        }

        return Math.Max(0, _config.Settings.SongRequestPolicy.SkipPointsCost);
    }

    private void Reply(string webRid, string userId, string templateKey, string nickname, string? reason = null)
    {
        var variables = new Dictionary<string, string> { ["name"] = nickname };
        if (!string.IsNullOrWhiteSpace(reason))
        {
            variables["reason"] = reason;
        }

        var msg = _reply.Render(templateKey, variables);
        if (!string.IsNullOrWhiteSpace(msg))
        {
            _replyQueue.EnqueueMention(webRid, userId, msg, nickname: nickname);
        }
    }
}
