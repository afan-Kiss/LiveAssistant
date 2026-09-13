using LiveAssistant.Config;
using LiveAssistant.Models;
using LiveAssistant.Utils;

namespace LiveAssistant.Services;

public sealed class SongRequestService
{
    private readonly ConfigManager _config;
    private readonly KugouService _kugou;
    private readonly DouyinService _douyin;
    private readonly QueueService _queue;
    private readonly ReplyService _reply;
    private readonly SystemMessageService _system;
    private readonly LogService _log;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public SongRequestService(
        ConfigManager config,
        KugouService kugou,
        DouyinService douyin,
        QueueService queue,
        ReplyService reply,
        SystemMessageService system,
        LogService log)
    {
        _config = config;
        _kugou = kugou;
        _douyin = douyin;
        _queue = queue;
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

        await _gate.WaitAsync(ct);
        try
        {
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
                return;
            }

            var queueItem = _queue.Add(new QueueItem
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
            RequestHandled?.Invoke();
        }
        finally
        {
            _gate.Release();
        }
    }
}
