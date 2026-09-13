using LiveAssistant.Config;
using LiveAssistant.Models;
using LiveAssistant.Utils;

namespace LiveAssistant.Services;

public sealed class SongRequestService
{
    private readonly ConfigManager _config;
    private readonly KugouService _kugou;
    private readonly QueueService _queue;
    private readonly SongRequestPermissionService _permission;
    private readonly ReplyService _reply;
    private readonly ReplyQueue _replyQueue;
    private readonly SystemMessageService _system;
    private readonly LogService _log;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public SongRequestService(
        ConfigManager config,
        KugouService kugou,
        QueueService queue,
        SongRequestPermissionService permission,
        ReplyService reply,
        ReplyQueue replyQueue,
        SystemMessageService system,
        LogService log)
    {
        _config = config;
        _kugou = kugou;
        _queue = queue;
        _permission = permission;
        _reply = reply;
        _replyQueue = replyQueue;
        _system = system;
        _log = log;
    }

    public event Action? RequestHandled;

    public async Task HandleDanmakuAsync(DanmakuItem item, string webRid, CancellationToken ct = default)
    {
        if (!SongNameParser.TryParse(item.Content, out var songName))
        {
            return;
        }

        var displayUser = string.IsNullOrWhiteSpace(item.Nickname) ? item.UserId : item.Nickname;

        await _gate.WaitAsync(ct);
        try
        {
            var permission = _permission.Evaluate(item);
            if (!permission.Allowed)
            {
                Reject(item, webRid, songName, displayUser, permission);
                return;
            }

            var songPermission = _permission.EvaluateSong(songName, permission.User!, item);
            if (!songPermission.Allowed)
            {
                Reject(item, webRid, songName, displayUser, songPermission);
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
                SendReply(webRid, item.UserId, notFound);
                _system.Add($"未找到歌曲《{songName}》");
                _log.LogSongRequest(displayUser, songName, false, "未找到歌曲");
                return;
            }

            var priority = _permission.GetQueuePriority(permission.User!);
            _queue.AddWithPriority(new QueueItem
            {
                UserId = item.UserId,
                Nickname = item.Nickname,
                SongName = track.SongName,
                Artist = track.Artist,
                SongId = track.SongId,
                Hash = track.Hash,
                PlayUrl = track.PlayUrl,
                IsRandom = false
            }, priority);

            _permission.RecordSuccessfulRequest(item);

            var ahead = Math.Max(0, _queue.WaitingCount - 1);
            var reply = _reply.Render("songRequestAccepted", new Dictionary<string, string>
            {
                ["name"] = item.Nickname,
                ["song"] = track.SongName,
                ["queue"] = ahead.ToString()
            });
            SendReply(webRid, item.UserId, reply);

            _system.Add($"已加入队列: {item.Nickname} - {track.SongName}（前面 {ahead} 首）");
            _log.LogSongRequest(displayUser, track.SongName, true);
            RequestHandled?.Invoke();
        }
        finally
        {
            _gate.Release();
        }

        await Task.CompletedTask;
    }

    private void Reject(DanmakuItem item, string webRid, string songName, string displayUser, SongRequestPermissionResult permission)
    {
        var templateKey = permission.TemplateKey ?? "songRequestRejected";
        var variables = permission.TemplateVariables;
        if (!variables.ContainsKey("name"))
        {
            variables["name"] = item.Nickname;
        }

        var msg = _reply.Render(templateKey, variables);
        if (string.IsNullOrWhiteSpace(msg) && templateKey == "songRequestCooldown")
        {
            msg = _reply.Render("songRequestRejected", new Dictionary<string, string>
            {
                ["name"] = item.Nickname,
                ["reason"] = permission.RejectReason ?? "暂时无法点歌"
            });
        }

        SendReply(webRid, item.UserId, msg);
        _system.Add($"{item.Nickname} 点歌被拒绝：{permission.RejectReason}");
        _log.LogSongRequest(displayUser, songName, false, permission.RejectReason);
    }

    private void SendReply(string webRid, string userId, string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return;
        }
        _replyQueue.EnqueueMention(webRid, userId, content);
    }
}
