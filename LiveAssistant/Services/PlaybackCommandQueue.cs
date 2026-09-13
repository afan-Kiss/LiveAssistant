using System.Threading.Channels;
using LiveAssistant.Config;
using LiveAssistant.Models;

namespace LiveAssistant.Services;

/// <summary>
/// 播放相关操作的唯一入口，所有命令串行执行。
/// </summary>
public sealed class PlaybackCommandQueue : IDisposable
{
    private readonly Channel<PlaybackCommandKind> _channel =
        Channel.CreateUnbounded<PlaybackCommandKind>(new UnboundedChannelOptions { SingleReader = true });

    private readonly ConfigManager _config;
    private readonly QueueService _queue;
    private readonly KugouService _kugou;
    private readonly RandomPlaylistService _random;
    private readonly PlaybackService _playback;
    private readonly ReplyService _reply;
    private readonly SystemMessageService _system;
    private readonly LogService _log;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _worker;
    private int _pendingVolume = -1;
    private bool _advanceScheduled;
    private readonly object _enqueueLock = new();
    private bool _skipAdvancePending;

    public PlaybackCommandQueue(
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

        _playback.TrackFinished += OnTrackFinished;
        _worker = Task.Run(() => WorkerLoopAsync(_cts.Token));
    }

    public PlaybackService Playback => _playback;

    public void Enqueue(PlaybackCommandKind kind)
    {
        if (IsSkipOrAdvance(kind))
        {
            lock (_enqueueLock)
            {
                if (_skipAdvancePending)
                {
                    _log.PlaybackInfo($"合并重复命令: {kind}");
                    return;
                }
                _skipAdvancePending = true;
            }
        }

        if (!_channel.Writer.TryWrite(kind))
        {
            if (IsSkipOrAdvance(kind))
            {
                lock (_enqueueLock)
                {
                    _skipAdvancePending = false;
                }
            }
            _log.PlaybackWarn($"播放命令入队失败: {kind}");
        }
    }

    private static bool IsSkipOrAdvance(PlaybackCommandKind kind) =>
        kind is PlaybackCommandKind.Skip or PlaybackCommandKind.Advance;

    private void ReleaseSkipAdvancePending()
    {
        lock (_enqueueLock)
        {
            _skipAdvancePending = false;
        }
    }

    public Task EnqueueEnsurePlayingAsync()
    {
        Enqueue(PlaybackCommandKind.EnsurePlaying);
        return Task.CompletedTask;
    }

    public Task EnqueueSkipAsync()
    {
        Enqueue(PlaybackCommandKind.Skip);
        return Task.CompletedTask;
    }

    public void EnqueuePause() => Enqueue(PlaybackCommandKind.Pause);
    public void EnqueueResume() => Enqueue(PlaybackCommandKind.Resume);
    public void EnqueueStop() => Enqueue(PlaybackCommandKind.Stop);
    public void EnqueueAdvance() => Enqueue(PlaybackCommandKind.Advance);

    public void EnqueueSetVolume(int volume)
    {
        _pendingVolume = volume;
        Enqueue(PlaybackCommandKind.SetVolume);
    }

    private void OnTrackFinished()
    {
        if (_advanceScheduled)
        {
            return;
        }
        _advanceScheduled = true;
        EnqueueAdvance();
    }

    private async Task WorkerLoopAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var kind in _channel.Reader.ReadAllAsync(ct))
            {
                try
                {
                    await ProcessCommandAsync(kind, ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _log.Error("playback", $"处理命令 {kind} 异常", ex);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // normal shutdown
        }
        catch (Exception ex)
        {
            _log.Error("playback", "播放命令队列异常退出", ex);
        }
    }

    private async Task ProcessCommandAsync(PlaybackCommandKind kind, CancellationToken ct)
    {
        switch (kind)
        {
            case PlaybackCommandKind.SetVolume:
                if (_pendingVolume >= 0)
                {
                    _playback.SetVolume(_pendingVolume);
                    _log.PlaybackInfo($"音量设置为 {_pendingVolume}");
                }
                return;
            case PlaybackCommandKind.Pause:
                _playback.Pause();
                _log.PlaybackInfo("暂停播放");
                return;
            case PlaybackCommandKind.Resume:
                _playback.Resume();
                _log.PlaybackInfo("恢复播放");
                return;
            case PlaybackCommandKind.Stop:
                _playback.Stop();
                _queue.FinishCurrent();
                _log.PlaybackInfo("停止播放");
                return;
            case PlaybackCommandKind.Skip:
                try
                {
                    _playback.Stop();
                    _queue.FinishCurrent();
                    _advanceScheduled = false;
                    await AdvanceInternalAsync(ct);
                }
                finally
                {
                    ReleaseSkipAdvancePending();
                }
                return;
            case PlaybackCommandKind.EnsurePlaying:
                if (_playback.State != Models.PlaybackState.Idle)
                {
                    return;
                }
                await AdvanceInternalAsync(ct);
                return;
            case PlaybackCommandKind.Advance:
                try
                {
                    _advanceScheduled = false;
                    _queue.FinishCurrent();
                    await AdvanceInternalAsync(ct);
                }
                finally
                {
                    ReleaseSkipAdvancePending();
                }
                return;
        }
    }

    private async Task AdvanceInternalAsync(CancellationToken ct)
    {
        var mode = _config.Settings.Playback.Mode;

        if (TryDequeueRequest(out var requestItem))
        {
            await PlayQueueItemAsync(requestItem, isRandomFill: false, ct);
            return;
        }

        switch (mode)
        {
            case PlaybackMode.RequestOnly:
                _system.Add("队列已空，等待点歌");
                _log.PlaybackInfo("队列已空，等待点歌");
                return;
            case PlaybackMode.RandomOnly:
                await PlayRandomAsync(isRandomFill: false, ct);
                return;
            case PlaybackMode.RequestWithRandomFill:
                await PlayRandomAsync(isRandomFill: true, ct);
                return;
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

    private async Task PlayQueueItemAsync(QueueItem item, bool isRandomFill, CancellationToken ct)
    {
        var source = item.IsRandom ? "random" : "request";
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
            track = await _kugou.ResolveTrackAsync(item.SongName, ct);
        }

        if (track == null)
        {
            _log.LogPlayback(item.SongName, source, item.Nickname, item.PlayUrl, false, "解析曲目失败");
            _log.LogPlaybackError(item.SongName, item.Nickname, source, item.PlayUrl, "解析曲目失败");
            await HandlePlayFailureAsync(item.SongName);
            if (_config.Settings.Playback.AutoSkipOnError)
            {
                EnqueueAdvance();
            }
            return;
        }

        item.PlayUrl = track.PlayUrl;
        _queue.SetNowPlaying(item);

        var ok = await _playback.PlayAsync(track, isRandomFill, ct);
        _log.LogPlayback(track.SongName, source, item.Nickname, track.PlayUrl, ok,
            ok ? null : "播放器启动失败");

        if (!ok)
        {
            _log.LogPlaybackError(track.SongName, item.Nickname, source, track.PlayUrl, "播放器启动失败");
            await HandlePlayFailureAsync(track.SongName);
            if (_config.Settings.Playback.AutoSkipOnError)
            {
                EnqueueAdvance();
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

    private async Task PlayRandomAsync(bool isRandomFill, CancellationToken ct)
    {
        var pick = _random.PickNext();
        if (pick == null)
        {
            _system.Add("随机歌单为空，无法补位");
            _log.PlaybackWarn("随机歌单为空");
            return;
        }

        var track = await _kugou.ResolveTrackFromItemAsync(pick, ct);
        if (track == null)
        {
            var songLabel = pick.Keyword ?? pick.Title ?? "?";
            _log.LogPlayback(songLabel, "random", "随机", null, false, "随机曲目解析失败");
            _log.LogPlaybackError(songLabel, "随机", "random", null, "随机曲目解析失败");
            _system.Add($"随机歌曲解析失败: {pick.Keyword ?? pick.Title}");
            if (_config.Settings.Playback.AutoSkipOnError)
            {
                await Task.Delay(500, ct);
                EnqueueAdvance();
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
        _queue.BeginPlaying(item);

        var ok = await _playback.PlayAsync(track, isRandomFill: true, ct);
        _log.LogPlayback(track.SongName, "random", "随机", track.PlayUrl, ok,
            ok ? null : "播放器启动失败");

        if (!ok)
        {
            _log.LogPlaybackError(track.SongName, "随机", "random", track.PlayUrl, "播放器启动失败");
            await HandlePlayFailureAsync(track.SongName);
            if (_config.Settings.Playback.AutoSkipOnError)
            {
                EnqueueAdvance();
            }
            return;
        }

        _random.RecordPlayed(track.SongId, track.Hash);
        _system.Add(isRandomFill
            ? $"随机补位: {track.SongName} - {track.Artist}"
            : $"随机播放: {track.SongName} - {track.Artist}");
    }

    private Task HandlePlayFailureAsync(string songName)
    {
        var msg = _reply.Render("playbackError", new Dictionary<string, string>
        {
            ["song"] = songName
        });
        _system.Add(string.IsNullOrWhiteSpace(msg) ? $"播放失败，已跳过《{songName}》" : msg);
        _log.PlaybackWarn($"播放失败: {songName}");
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _playback.TrackFinished -= OnTrackFinished;
        _cts.Cancel();
        _channel.Writer.TryComplete();
        try
        {
            _worker.Wait(TimeSpan.FromSeconds(2));
        }
        catch
        {
            // ignore
        }
        _cts.Dispose();
    }
}
