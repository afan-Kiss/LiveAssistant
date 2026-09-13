using LiveAssistant.Config;
using LiveAssistant.Models;

namespace LiveAssistant.Services;

/// <summary>
/// 点歌 + 随机补位播放状态机。
/// </summary>
public sealed class PlaybackEngine
{
    private readonly ConfigManager _config;
    private readonly QueueService _queue;
    private readonly KugouService _kugou;
    private readonly RandomPlaylistService _random;
    private readonly PlaybackService _playback;
    private readonly ReplyService _reply;
    private readonly SystemMessageService _system;
    private readonly LogService _log;
    private readonly SemaphoreSlim _advanceLock = new(1, 1);
    private bool _busy;

    public PlaybackEngine(
        ConfigManager config,
        QueueService queue,
        KugouService kugou,
        RandomPlaylistService random,
        PlaybackService playback,
        ReplyService reply,
        SystemMessageService system,
        LogService log)
    {
        _config = config;
        _queue = queue;
        _kugou = kugou;
        _random = random;
        _playback = playback;
        _reply = reply;
        _system = system;
        _log = log;

        _playback.TrackFinished += () => _ = AdvanceAsync();
    }

    public PlaybackMode Mode => _config.Settings.Playback.Mode;

    public void SetMode(PlaybackMode mode)
    {
        _config.Settings.Playback.Mode = mode;
        _config.Save();
        _system.Add($"播放模式已切换: {mode}");
    }

    public async Task EnsurePlayingAsync()
    {
        if (_playback.State != Models.PlaybackState.Idle)
        {
            return;
        }

        await AdvanceAsync();
    }

    public async Task SkipAsync()
    {
        _playback.Stop();
        _queue.FinishCurrent();
        await AdvanceAsync();
    }

    public async Task AdvanceAsync()
    {
        if (_busy)
        {
            return;
        }

        await _advanceLock.WaitAsync();
        try
        {
            _busy = true;
            _queue.FinishCurrent();

            var mode = _config.Settings.Playback.Mode;

            if (TryDequeueRequest(out var requestItem))
            {
                await PlayQueueItemAsync(requestItem, isRandomFill: false);
                return;
            }

            switch (mode)
            {
                case PlaybackMode.RequestOnly:
                    _system.Add("队列已空，等待点歌");
                    return;
                case PlaybackMode.RandomOnly:
                    await PlayRandomAsync();
                    return;
                case PlaybackMode.RequestWithRandomFill:
                    await PlayRandomAsync(isRandomFill: true);
                    return;
            }
        }
        finally
        {
            _busy = false;
            _advanceLock.Release();
        }
    }

    private bool TryDequeueRequest(out QueueItem item)
    {
        var next = _queue.DequeueNext();
        if (next == null)
        {
            item = null!;
            return false;
        }

        item = next;
        return true;
    }

    private async Task PlayQueueItemAsync(QueueItem item, bool isRandomFill)
    {
        TrackInfo? track = null;
        if (!string.IsNullOrWhiteSpace(item.PlayUrl))
        {
            track = new TrackInfo
            {
                SongName = item.SongName,
                Artist = item.Artist,
                SongId = item.SongId,
                Hash = item.Hash,
                PlayUrl = item.PlayUrl,
                IsRandom = item.IsRandom,
                Requester = item.Nickname
            };
        }
        else
        {
            track = await _kugou.ResolveTrackAsync(item.SongName);
        }

        if (track == null)
        {
            await HandlePlayFailureAsync(item.SongName);
            await AdvanceAsync();
            return;
        }

        item.PlayUrl = track.PlayUrl;
        _queue.SetNowPlaying(item);

        var ok = await _playback.PlayAsync(track, isRandomFill);
        if (!ok)
        {
            await HandlePlayFailureAsync(track.SongName);
            if (_config.Settings.Playback.AutoSkipOnError)
            {
                await AdvanceAsync();
            }
            return;
        }

        var msg = _reply.Render("nowPlaying", new Dictionary<string, string>
        {
            ["song"] = track.SongName,
            ["name"] = item.Nickname
        });
        if (!string.IsNullOrWhiteSpace(msg))
        {
            _system.Add(msg);
        }
    }

    private async Task PlayRandomAsync(bool isRandomFill = false)
    {
        var pick = _random.PickNext();
        if (pick == null)
        {
            _system.Add("随机歌单为空，无法补位");
            return;
        }

        var track = await _kugou.ResolveTrackFromItemAsync(pick);
        if (track == null)
        {
            _system.Add($"随机歌曲解析失败: {pick.Keyword ?? pick.Title}");
            if (_config.Settings.Playback.AutoSkipOnError)
            {
                await Task.Delay(500);
                await AdvanceAsync();
            }
            return;
        }

        var item = new QueueItem
        {
            Nickname = "随机",
            SongName = track.SongName,
            Artist = track.Artist,
            SongId = track.SongId,
            Hash = track.Hash,
            PlayUrl = track.PlayUrl,
            IsRandom = true
        };
        _queue.SetNowPlaying(item);

        var ok = await _playback.PlayAsync(track, isRandomFill: true);
        if (!ok)
        {
            await HandlePlayFailureAsync(track.SongName);
            if (_config.Settings.Playback.AutoSkipOnError)
            {
                await AdvanceAsync();
            }
            return;
        }

        _random.RecordPlayed(track.SongId, track.Hash);
        _system.Add(isRandomFill
            ? $"随机补位: {track.SongName} - {track.Artist}"
            : $"随机播放: {track.SongName} - {track.Artist}");
    }

    private async Task HandlePlayFailureAsync(string songName)
    {
        var msg = _reply.Render("playbackError", new Dictionary<string, string>
        {
            ["song"] = songName
        });
        _system.Add(string.IsNullOrWhiteSpace(msg) ? $"播放失败，已跳过《{songName}》" : msg);
        _log.Warn($"播放失败: {songName}");
        await Task.CompletedTask;
    }
}
