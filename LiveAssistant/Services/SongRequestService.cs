using LiveAssistant.Config;
using LiveAssistant.Database;
using LiveAssistant.Models;
using LiveAssistant.Utils;

namespace LiveAssistant.Services;

public sealed class SongRequestService
{
    private readonly ConfigManager _config;
    private readonly KugouService _kugou;
    private readonly DouyinService _douyin;
    private readonly QueueService _queue;
    private readonly UserRepository _users;
    private readonly ReplyService _reply;
    private readonly SystemMessageService _system;
    private readonly LogService _log;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public SongRequestService(
        ConfigManager config,
        KugouService kugou,
        DouyinService douyin,
        QueueService queue,
        UserRepository users,
        ReplyService reply,
        SystemMessageService system,
        LogService log)
    {
        _config = config;
        _kugou = kugou;
        _douyin = douyin;
        _queue = queue;
        _users = users;
        _reply = reply;
        _system = system;
        _log = log;
    }

    public event Action? RequestHandled;

    public async Task HandleDanmakuAsync(DanmakuItem item, string webRid, CancellationToken ct = default)
    {
        if (_config.Settings.Emergency.PauseSongRequest)
        {
            return;
        }

        if (!SongNameParser.TryParse(item.Content, out var songName))
        {
            return;
        }

        var displayUser = string.IsNullOrWhiteSpace(item.Nickname) ? item.UserId : item.Nickname;

        await _gate.WaitAsync(ct);
        try
        {
            var user = _users.EnsureUser(item.UserId, item.Nickname);

            if (user.Role == UserRole.Blacklist)
            {
                await RejectAsync(webRid, item, songName, "黑名单用户", ct);
                return;
            }

            var cooldownSec = _config.Settings.Queue.RequestCooldownSeconds;
            var (allowed, remaining) = _users.CheckCooldown(item.UserId, cooldownSec);
            if (!allowed)
            {
                var cooldownMsg = _reply.Render("songRequestCooldown", new Dictionary<string, string>
                {
                    ["name"] = item.Nickname,
                    ["seconds"] = remaining.ToString()
                });
                if (string.IsNullOrWhiteSpace(cooldownMsg))
                {
                    cooldownMsg = _reply.Render("songRequestRejected", new Dictionary<string, string>
                    {
                        ["name"] = item.Nickname,
                        ["reason"] = $"请{remaining}秒后再试"
                    });
                }
                if (!string.IsNullOrWhiteSpace(cooldownMsg))
                {
                    await _douyin.SendMentionAsync(webRid, item.UserId, cooldownMsg, ct);
                }
                _system.Add($"{item.Nickname} 点歌冷却中（剩余 {remaining} 秒）");
                _log.LogSongRequest(displayUser, songName, false, $"冷却中 剩余{remaining}秒");
                return;
            }

            if (_queue.WaitingCount >= _config.Settings.Queue.MaxSize)
            {
                var fullMsg = _reply.Render("queueFull", new Dictionary<string, string>
                {
                    ["name"] = item.Nickname
                });
                if (!string.IsNullOrWhiteSpace(fullMsg))
                {
                    await _douyin.SendMentionAsync(webRid, item.UserId, fullMsg, ct);
                }
                _system.Add($"{item.Nickname} 点歌失败：队列已满");
                _log.LogSongRequest(displayUser, songName, false, "队列已满");
                return;
            }

            _system.Add($"{item.Nickname} 点歌《{songName}》，正在搜索...");
            var track = await _kugou.ResolveTrackAsync(songName, ct);
            if (track == null)
            {
                var notFound = _reply.Render("songNotFound", new Dictionary<string, string>
                {
                    ["name"] = item.Nickname,
                    ["song"] = songName
                });
                if (!string.IsNullOrWhiteSpace(notFound))
                {
                    await _douyin.SendMentionAsync(webRid, item.UserId, notFound, ct);
                }
                _system.Add($"未找到歌曲《{songName}》");
                _log.LogSongRequest(displayUser, songName, false, "未找到歌曲");
                return;
            }

            _queue.Add(new QueueItem
            {
                UserId = item.UserId,
                Nickname = item.Nickname,
                SongName = track.SongName,
                Artist = track.Artist,
                SongId = track.SongId,
                Hash = track.Hash,
                PlayUrl = track.PlayUrl,
                IsRandom = false
            });

            _users.RecordSuccessfulRequest(item.UserId, item.Nickname);

            var ahead = Math.Max(0, _queue.WaitingCount - 1);
            var reply = _reply.Render("songRequestAccepted", new Dictionary<string, string>
            {
                ["name"] = item.Nickname,
                ["song"] = track.SongName,
                ["queue"] = ahead.ToString()
            });
            if (!string.IsNullOrWhiteSpace(reply))
            {
                await _douyin.SendMentionAsync(webRid, item.UserId, reply, ct);
            }

            _system.Add($"已加入队列: {item.Nickname} - {track.SongName}（前面 {ahead} 首）");
            _log.LogSongRequest(displayUser, track.SongName, true);
            RequestHandled?.Invoke();
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task RejectAsync(string webRid, DanmakuItem item, string songName, string reason, CancellationToken ct)
    {
        var displayUser = string.IsNullOrWhiteSpace(item.Nickname) ? item.UserId : item.Nickname;
        var msg = _reply.Render("songRequestRejected", new Dictionary<string, string>
        {
            ["name"] = item.Nickname,
            ["reason"] = reason
        });
        if (!string.IsNullOrWhiteSpace(msg))
        {
            await _douyin.SendMentionAsync(webRid, item.UserId, msg, ct);
        }
        _system.Add($"{item.Nickname} 点歌被拒绝：{reason}");
        _log.LogSongRequest(displayUser, songName, false, reason);
    }
}
