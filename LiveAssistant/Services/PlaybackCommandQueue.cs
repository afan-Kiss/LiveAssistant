using System.Diagnostics;
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
    private long _pendingPlayNowId;
    private bool _advanceScheduled;
    private readonly object _enqueueLock = new();
    private bool _skipAdvancePending;
    private int _pendingSkipCount;
    private int _pendingAdvanceCount;
    private int _failContinueDepth;
    private volatile bool _playbackStarting;
    private readonly Stack<QueueItem> _playbackHistory = new();
    private readonly Func<QueueItem, CancellationToken, Task<TrackInfo?>>? _resolveFresh;
    private readonly Func<CancellationToken, Task<TrackInfo?>>? _resolveRandom;
    private readonly Func<TrackInfo, bool, CancellationToken, Task<bool>>? _playAsync;
    /// <summary>播放链路 resolve 上限；不阻塞命令队列过久（HTTP 层仍可有更长超时）。</summary>
    private static readonly TimeSpan ResolveFreshTimeout = TimeSpan.FromSeconds(12);

    private readonly object _prefetchLock = new();
    private CancellationTokenSource? _prefetchCts;
    private Task<TrackInfo?>? _prefetchTask;
    private TrackInfo? _prefetchedRandom;

    public PlaybackCommandQueue(
        ConfigManager config,
        QueueService queue,
        KugouService kugou,
        RandomPlaylistService random,
        PlaybackService playback,
        ReplyService reply,
        SystemMessageService system,
        LogService log,
        Func<QueueItem, CancellationToken, Task<TrackInfo?>>? resolveFresh = null,
        Func<TrackInfo, bool, CancellationToken, Task<bool>>? playAsync = null,
        Func<CancellationToken, Task<TrackInfo?>>? resolveRandom = null)
    {
        _config = config;
        _queue = queue;
        _kugou = kugou;
        _random = random;
        _playback = playback;
        _reply = reply;
        _system = system;
        _log = log;
        _resolveFresh = resolveFresh;
        _playAsync = playAsync;
        _resolveRandom = resolveRandom;

        _playback.TrackFinished += OnTrackFinished;
        _worker = Task.Run(() => WorkerLoopAsync(_cts.Token));
    }

    public PlaybackService Playback => _playback;

    public void Enqueue(PlaybackCommandKind kind) => TryEnqueue(kind);

    /// <summary>入队播放命令；skip/advance 合并到进行中的切歌也返回 true。</summary>
    public bool TryEnqueue(PlaybackCommandKind kind)
    {
        if (IsSkipOrAdvance(kind))
        {
            lock (_enqueueLock)
            {
                if (kind == PlaybackCommandKind.Skip)
                {
                    _pendingSkipCount++;
                }
                else
                {
                    _pendingAdvanceCount++;
                }

                if (_skipAdvancePending)
                {
                    _log.PlaybackInfo(
                        $"合并重复命令: {kind} (skip={_pendingSkipCount} advance={_pendingAdvanceCount})");
                    return true;
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
                    if (kind == PlaybackCommandKind.Skip)
                    {
                        _pendingSkipCount = Math.Max(0, _pendingSkipCount - 1);
                    }
                    else
                    {
                        _pendingAdvanceCount = Math.Max(0, _pendingAdvanceCount - 1);
                    }

                    if (_pendingSkipCount == 0 && _pendingAdvanceCount == 0)
                    {
                        _skipAdvancePending = false;
                    }
                }
            }
            _log.PlaybackWarn($"播放命令入队失败: {kind}");
            return false;
        }

        return true;
    }

    private static bool IsSkipOrAdvance(PlaybackCommandKind kind) =>
        kind is PlaybackCommandKind.Skip or PlaybackCommandKind.Advance;

    private void ReleaseSkipAdvancePending()
    {
        lock (_enqueueLock)
        {
            _skipAdvancePending = false;
            if (_pendingSkipCount == 0 && _pendingAdvanceCount == 0)
            {
                return;
            }

            var kind = _pendingSkipCount > 0
                ? PlaybackCommandKind.Skip
                : PlaybackCommandKind.Advance;
            _skipAdvancePending = true;
            if (!_channel.Writer.TryWrite(kind))
            {
                _skipAdvancePending = false;
                _log.PlaybackWarn($"播放命令入队失败: {kind} (续处理合并切歌)");
            }
        }
    }

    private (int Skips, int Advances) TakePendingSkipAdvanceCounts()
    {
        lock (_enqueueLock)
        {
            var skips = _pendingSkipCount;
            var advances = _pendingAdvanceCount;
            _pendingSkipCount = 0;
            _pendingAdvanceCount = 0;
            return (skips, advances);
        }
    }

    private async Task ProcessSkipAdvanceAsync(CancellationToken ct)
    {
        var (skips, advances) = TakePendingSkipAdvanceCounts();
        for (var i = 0; i < advances; i++)
        {
            _advanceScheduled = false;
            _queue.FinishCurrent();
            await AdvanceInternalAsync(ct);
        }

        if (skips <= 0)
        {
            return;
        }

        // 连点「下一首」只停一次；旧逻辑每轮都 Stop 会把刚切到的歌立刻掐掉。
        await _playback.StopAsync(ct);
        _queue.FinishCurrent();
        _advanceScheduled = false;

        for (var i = 1; i < skips; i++)
        {
            _advanceScheduled = false;
            if (_queue.DequeueNext() != null)
            {
                _queue.FinishCurrent();
            }
        }

        await AdvanceInternalAsync(ct);
    }

    public Task EnqueueEnsurePlayingAsync()
    {
        Enqueue(PlaybackCommandKind.EnsurePlaying);
        return Task.CompletedTask;
    }

    /// <summary>返回是否成功接受切歌（含合并到进行中的 skip）。入队失败返回 false。</summary>
    public Task<bool> EnqueueSkipAsync()
    {
        return Task.FromResult(TryEnqueue(PlaybackCommandKind.Skip));
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

    public void EnqueuePlayNow(long queueItemId)
    {
        _pendingPlayNowId = queueItemId;
        Enqueue(PlaybackCommandKind.PlayNow);
    }

    public void EnqueuePlay()
    {
        if (_playback.State == Models.PlaybackState.Paused)
        {
            EnqueueResume();
        }
        else
        {
            Enqueue(PlaybackCommandKind.Play);
        }
    }

    public void EnqueuePrevious() => Enqueue(PlaybackCommandKind.Previous);
    public void EnqueueClearQueue() => Enqueue(PlaybackCommandKind.ClearQueue);

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
                await _playback.StopAsync(ct);
                _queue.FinishCurrent();
                InvalidateRandomPrefetch();
                _log.PlaybackInfo("停止播放");
                return;
            case PlaybackCommandKind.Skip:
                try
                {
                    await ProcessSkipAdvanceAsync(ct);
                }
                finally
                {
                    ReleaseSkipAdvancePending();
                }
                return;
            case PlaybackCommandKind.EnsurePlaying:
                if (ShouldDeferEnsurePlaying())
                {
                    return;
                }
                await AdvanceInternalAsync(ct);
                return;
            case PlaybackCommandKind.Advance:
                try
                {
                    await ProcessSkipAdvanceAsync(ct);
                }
                finally
                {
                    ReleaseSkipAdvancePending();
                }
                return;
            case PlaybackCommandKind.PlayNow:
                await PlayNowInternalAsync(ct);
                return;
            case PlaybackCommandKind.Play:
                if (_playback.State == Models.PlaybackState.Idle)
                {
                    await AdvanceInternalAsync(ct);
                }
                else
                {
                    _playback.Resume();
                }
                _log.PlaybackInfo("播放");
                return;
            case PlaybackCommandKind.Previous:
                await PlayPreviousInternalAsync(ct);
                return;
            case PlaybackCommandKind.ClearQueue:
                _queue.ClearWaiting();
                _log.PlaybackInfo("清空等待队列");
                return;
        }
    }

    private async Task PlayPreviousInternalAsync(CancellationToken ct)
    {
        if (_playbackHistory.Count == 0)
        {
            _system.Add("没有上一首记录");
            return;
        }

        var prev = _playbackHistory.Pop();
        await _playback.StopAsync(ct);
        _queue.FinishCurrent();
        await PlayQueueItemAsync(prev, isRandomFill: prev.IsRandom, ct);
    }

    private void PushHistory(QueueItem item)
    {
        _playbackHistory.Push(item);
        while (_playbackHistory.Count > 20)
        {
            var temp = _playbackHistory.ToArray();
            _playbackHistory.Clear();
            for (var i = 1; i < temp.Length; i++)
            {
                _playbackHistory.Push(temp[i]);
            }
        }
    }

    private async Task PlayNowInternalAsync(CancellationToken ct)
    {
        var id = _pendingPlayNowId;
        _pendingPlayNowId = 0;
        if (id <= 0)
        {
            return;
        }

        var item = _queue.GetItem(id);
        if (item == null)
        {
            _log.PlaybackWarn($"立即播放失败: 队列项 {id} 不存在");
            return;
        }

        await _playback.StopAsync(ct);
        _queue.FinishCurrent();
        _queue.PinToTop(id);
        var next = _queue.DequeueNext();
        if (next != null)
        {
            await PlayQueueItemAsync(next, isRandomFill: false, ct);
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
                if (_config.Settings.Playback.RandomFillEnabled)
                {
                    await PlayRandomAsync(isRandomFill: true, ct);
                }
                else
                {
                    _system.Add("队列已空，等待点歌");
                    _log.PlaybackInfo("队列已空，随机补位已关闭");
                }
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

        var track = await ResolveFreshTrackWithTimeoutAsync(item, ct);

        if (!IsPlayableTrack(track, item.SongName, source, item.Nickname) || track == null)
        {
            await FailCurrentAndContinueAsync(item.SongName, ct);
            return;
        }

        ApplyResolvedTrack(item, track);
        _queue.SyncNowPlayingMetadata(item);

        PushHistory(item);

        var ok = await TryStartPlaybackWithUrlRecoveryAsync(item, track, isRandomFill, ct);
        _log.LogPlayback(track.SongName, source, item.Nickname, track.PlayUrl, ok,
            ok ? null : "播放器启动失败");

        if (!ok)
        {
            _log.LogPlaybackError(track.SongName, item.Nickname, source, track.PlayUrl, "播放器启动失败");
            await FailCurrentAndContinueAsync(track.SongName, ct);
            return;
        }

        // 当前点歌曲在播时，若队列将空则预取下一首随机，缩短「下一首」空白。
        ScheduleRandomPrefetchIfNeeded();

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

    private async Task<TrackInfo?> ResolveFreshTrackWithTimeoutAsync(QueueItem item, CancellationToken ct)
    {
        var cached = _resolveFresh == null ? TryTrackFromCachedUrl(item) : null;
        if (cached != null)
        {
            _log.PlaybackInfo($"PLAYBACK_TIMING ResolveFreshTrack_cached song={item.SongName}");
            return cached;
        }

        var sw = Stopwatch.StartNew();
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(ResolveFreshTimeout);

            var track = _resolveFresh != null
                ? await _resolveFresh(item, timeoutCts.Token)
                : await _kugou.ResolveFreshTrackAsync(
                    item.Hash, item.SongName, item.Artist, item.SongId, item.Nickname, item.IsRandom,
                    item.AlbumId, item.AlbumAudioId, timeoutCts.Token);

            _log.PlaybackInfo(
                $"PLAYBACK_TIMING ResolveFreshTrack_ms={sw.ElapsedMilliseconds} song={item.SongName} ok={(track != null)}");
            return track;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _log.PlaybackWarn(
                $"PLAYBACK_TIMING ResolveFreshTrack TIMEOUT ms={sw.ElapsedMilliseconds} " +
                $"song={item.SongName} hash={item.Hash} reason=resolve_timeout " +
                $"limit_ms={ResolveFreshTimeout.TotalMilliseconds}");
            return null;
        }
        catch (Exception ex)
        {
            _log.PlaybackWarn(
                $"PLAYBACK_TIMING ResolveFreshTrack FAIL ms={sw.ElapsedMilliseconds} " +
                $"song={item.SongName} hash={item.Hash} reason={ex.GetType().Name} err={ex.Message}");
            return null;
        }
    }

    private async Task<TrackInfo?> ResolveRandomTrackAsync(CancellationToken ct)
    {
        if (_resolveRandom != null)
        {
            var swInjected = Stopwatch.StartNew();
            try
            {
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(ResolveFreshTimeout);
                var injected = await _resolveRandom(timeoutCts.Token);
                _log.PlaybackInfo(
                    $"PLAYBACK_TIMING ResolveRandomTrack_ms={swInjected.ElapsedMilliseconds} " +
                    $"song={injected?.SongName ?? "-"} ok={(injected != null)} source=injected");
                return injected;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                _log.PlaybackWarn(
                    $"PLAYBACK_TIMING ResolveRandomTrack TIMEOUT ms={swInjected.ElapsedMilliseconds} " +
                    $"source=injected reason=resolve_timeout");
                return null;
            }
        }

        var sw = Stopwatch.StartNew();
        var source = _config.Settings.RandomPlaylist.Source?.Trim() ?? "kugou";
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(ResolveFreshTimeout);

            TrackInfo? track;
            if (source.Equals("fixed", StringComparison.OrdinalIgnoreCase))
            {
                var pick = _random.PickNext();
                if (pick == null)
                {
                    _system.Add("随机歌单为空，无法补位");
                    _log.PlaybackWarn("随机歌单为空");
                    return null;
                }

                track = await _kugou.ResolveTrackFromItemAsync(pick, timeoutCts.Token);
            }
            else
            {
                track = await _kugou.PickRandomTrackAsync(_random.WasRecentlyPlayed, timeoutCts.Token);
            }

            _log.PlaybackInfo(
                $"PLAYBACK_TIMING ResolveRandomTrack_ms={sw.ElapsedMilliseconds} song={track?.SongName ?? "-"} ok={(track != null)}");
            return track;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _log.PlaybackWarn(
                $"PLAYBACK_TIMING ResolveRandomTrack TIMEOUT ms={sw.ElapsedMilliseconds} " +
                $"source={source} reason=resolve_timeout limit_ms={ResolveFreshTimeout.TotalMilliseconds}");
            if (source.Equals("kugou", StringComparison.OrdinalIgnoreCase))
            {
                var fallback = await _kugou.PickFallbackRandomTrackAsync(_random.WasRecentlyPlayed, ct);
                if (fallback != null)
                {
                    _log.PlaybackInfo(
                        $"PLAYBACK_TIMING ResolveRandomTrack fallback song={fallback.SongName} " +
                        $"after_timeout_ms={sw.ElapsedMilliseconds}");
                    return fallback;
                }
            }

            return null;
        }
        catch (Exception ex)
        {
            _log.PlaybackWarn(
                $"PLAYBACK_TIMING ResolveRandomTrack FAIL ms={sw.ElapsedMilliseconds} " +
                $"source={source} reason={ex.GetType().Name} err={ex.Message}");
            return null;
        }
    }

    private async Task PlayRandomAsync(bool isRandomFill, CancellationToken ct)
    {
        var track = await TakePrefetchedOrResolveRandomAsync(ct);
        if (!IsPlayableTrack(track, track?.SongName ?? "随机", "random", "随机") || track == null)
        {
            if (track != null && !_kugou.IsAcceptableForPlayback(track))
            {
                _system.Add($"《{track.SongName}》仅试听版，已跳过：请扫码登录酷狗并领取试用会员");
            }
            else
            {
                _system.Add("随机补位取歌失败：请确认酷狗 API 在线、已扫码登录并领取试用会员");
            }

            await FailCurrentAndContinueAsync(track?.SongName ?? "随机", ct);
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
            AlbumId = track.AlbumId,
            AlbumAudioId = track.AlbumAudioId,
            IsRandom = true
        };

        var ok = await TryStartPlaybackWithUrlRecoveryAsync(item, track, isRandomFill, ct);
        _log.LogPlayback(track.SongName, "random", "随机", track.PlayUrl, ok,
            ok ? null : "播放器启动失败");

        if (!ok)
        {
            _log.LogPlaybackError(track.SongName, "随机", "random", track.PlayUrl, "播放器启动失败");
            await FailCurrentAndContinueAsync(track.SongName, ct);
            return;
        }

        _queue.SetNowPlaying(item);
        PushHistory(item);
        _random.RecordPlayed(track.SongId, track.Hash);
        ScheduleRandomPrefetchIfNeeded();
        _system.Add(isRandomFill
            ? $"随机补位: {track.SongName} - {track.Artist}"
            : $"随机播放: {track.SongName} - {track.Artist}");
    }

    /// <summary>
    /// 优先用播放中预取好的下一首；没有则等待进行中的预取；再没有才现取。
    /// </summary>
    private async Task<TrackInfo?> TakePrefetchedOrResolveRandomAsync(CancellationToken ct)
    {
        Task<TrackInfo?>? inflight;
        lock (_prefetchLock)
        {
            if (_prefetchedRandom != null)
            {
                var ready = _prefetchedRandom;
                _prefetchedRandom = null;
                _prefetchTask = null;
                _log.PlaybackInfo(
                    $"PLAYBACK_TIMING ResolveRandomTrack_prefetched song={ready.SongName}");
                return ready;
            }

            inflight = _prefetchTask;
            _prefetchTask = null;
            // 摘走进行中的预取任务，避免完成后再次写入缓存导致同一首连播两遍。
        }

        if (inflight != null)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                var track = await inflight.WaitAsync(ct);
                _log.PlaybackInfo(
                    $"PLAYBACK_TIMING ResolveRandomTrack_await_prefetch_ms={sw.ElapsedMilliseconds} " +
                    $"song={track?.SongName ?? "-"} ok={(track != null)}");
                if (track != null)
                {
                    return track;
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                _log.PlaybackWarn(
                    $"PLAYBACK_TIMING ResolveRandomTrack_await_prefetch canceled ms={sw.ElapsedMilliseconds}");
            }
            catch (Exception ex)
            {
                _log.PlaybackWarn(
                    $"PLAYBACK_TIMING ResolveRandomTrack_await_prefetch FAIL ms={sw.ElapsedMilliseconds} " +
                    $"err={ex.Message}");
            }
        }

        return await ResolveRandomTrackAsync(ct);
    }

    private bool ShouldPrefetchRandom()
    {
        var mode = _config.Settings.Playback.Mode;
        if (mode == PlaybackMode.RequestOnly)
        {
            return false;
        }

        if (mode == PlaybackMode.RequestWithRandomFill && !_config.Settings.Playback.RandomFillEnabled)
        {
            return false;
        }

        // 还有点歌排队时，下一首不是随机，不必预取随机。
        return _queue.WaitingCount <= 0;
    }

    private void ScheduleRandomPrefetchIfNeeded()
    {
        if (!ShouldPrefetchRandom())
        {
            return;
        }

        lock (_prefetchLock)
        {
            if (_prefetchedRandom != null)
            {
                return;
            }

            if (_prefetchTask is { IsCompleted: false })
            {
                return;
            }

            var linked = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
            _prefetchCts?.Cancel();
            _prefetchCts?.Dispose();
            _prefetchCts = linked;
            var token = linked.Token;
            Task<TrackInfo?>? self = null;
            self = Task.Run(async () =>
            {
                try
                {
                    var track = await ResolveRandomTrackAsync(token);
                    if (track == null || string.IsNullOrWhiteSpace(track.PlayUrl))
                    {
                        _log.PlaybackWarn("随机预取失败：未拿到可播放曲目");
                        return null;
                    }

                    if (!_kugou.IsAcceptableForPlayback(track))
                    {
                        _log.PlaybackWarn($"随机预取跳过试听版: {track.SongName}");
                        return null;
                    }

                    lock (_prefetchLock)
                    {
                        // 已被 TakePrefetched 摘走或被新预取替换时，勿写入缓存，避免同一首播两遍。
                        if (!token.IsCancellationRequested && ReferenceEquals(_prefetchTask, self))
                        {
                            _prefetchedRandom = track;
                        }
                    }

                    _log.PlaybackInfo($"PLAYBACK_TIMING random_prefetch_ready song={track.SongName}");
                    return track;
                }
                catch (OperationCanceledException)
                {
                    return null;
                }
                catch (Exception ex)
                {
                    _log.PlaybackWarn($"随机预取异常: {ex.Message}");
                    return null;
                }
            }, token);
            _prefetchTask = self;
        }
    }

    private void InvalidateRandomPrefetch()
    {
        CancellationTokenSource? cts;
        lock (_prefetchLock)
        {
            cts = _prefetchCts;
            _prefetchCts = null;
            _prefetchTask = null;
            _prefetchedRandom = null;
        }

        try
        {
            cts?.Cancel();
            cts?.Dispose();
        }
        catch
        {
            // ignore
        }
    }

    private bool ShouldDeferEnsurePlaying()
    {
        if (_playbackStarting)
        {
            return true;
        }

        if (_playback.State != Models.PlaybackState.Idle)
        {
            return true;
        }

        // Idle 却残留 NowPlaying：属于脏状态，清掉才能让新点歌/补位真正开播
        if (_queue.NowPlaying != null)
        {
            _log.PlaybackWarn(
                $"EnsurePlaying 清理 Idle 残留 NowPlaying song={_queue.NowPlaying.SongName}");
            _queue.FinishCurrent();
        }

        return false;
    }

    private async Task<bool> TryStartPlaybackWithUrlRecoveryAsync(
        QueueItem item,
        TrackInfo track,
        bool isRandomFill,
        CancellationToken ct)
    {
        if (await StartPlaybackAsync(track, isRandomFill, ct))
        {
            return true;
        }

        _log.PlaybackWarn($"PLAYBACK_TIMING play_failed retry_resolve_once song={track.SongName}");
        var fresh = await ResolveFreshTrackWithTimeoutAsync(item, ct);
        if (!IsPlayableTrack(fresh, track.SongName, "retry", item.Nickname, logPlayback: false) || fresh == null)
        {
            return false;
        }

        ApplyResolvedTrack(item, fresh);
        _queue.SyncNowPlayingMetadata(item);
        return await StartPlaybackAsync(fresh, isRandomFill, ct);
    }

    private bool IsPlayableTrack(
        TrackInfo? track,
        string songLabel,
        string source,
        string user,
        bool logPlayback = true)
    {
        if (track == null || string.IsNullOrWhiteSpace(track.PlayUrl))
        {
            if (logPlayback)
            {
                _log.LogPlayback(songLabel, source, user, track?.PlayUrl, false, "刷新播放地址失败");
                _log.LogPlaybackError(songLabel, user, source, track?.PlayUrl, "刷新播放地址失败");
            }

            return false;
        }

        if (!_kugou.IsAcceptableForPlayback(track))
        {
            if (logPlayback)
            {
                _log.LogPlayback(songLabel, source, user, track.PlayUrl, false, "仅试听版已拒绝");
                _log.LogPlaybackError(songLabel, user, source, track.PlayUrl, "preview_rejected");
            }

            return false;
        }

        return true;
    }

    private TrackInfo? TryTrackFromCachedUrl(QueueItem item)
    {
        if (string.IsNullOrWhiteSpace(item.PlayUrl) || string.IsNullOrWhiteSpace(item.Hash))
        {
            return null;
        }

        var track = new TrackInfo
        {
            SongName = item.SongName,
            Artist = item.Artist,
            SongId = item.SongId,
            Hash = item.Hash,
            AlbumId = item.AlbumId,
            AlbumAudioId = item.AlbumAudioId,
            PlayUrl = item.PlayUrl,
            IsRandom = item.IsRandom,
            Requester = item.Nickname
        };
        return _kugou.IsAcceptableForPlayback(track) ? track : null;
    }

    private static void ApplyResolvedTrack(QueueItem item, TrackInfo track)
    {
        if (!string.IsNullOrWhiteSpace(track.SongName))
        {
            item.SongName = track.SongName;
        }

        if (!string.IsNullOrWhiteSpace(track.Artist))
        {
            item.Artist = track.Artist;
        }

        if (!string.IsNullOrWhiteSpace(track.SongId))
        {
            item.SongId = track.SongId;
        }

        item.PlayUrl = track.PlayUrl;
        if (!string.IsNullOrWhiteSpace(track.Hash))
        {
            item.Hash = track.Hash;
        }

        if (!string.IsNullOrWhiteSpace(track.AlbumId))
        {
            item.AlbumId = track.AlbumId;
        }

        if (track.AlbumAudioId > 0)
        {
            item.AlbumAudioId = track.AlbumAudioId;
        }
    }

    private async Task<bool> StartPlaybackAsync(TrackInfo track, bool isRandomFill, CancellationToken ct)
    {
        _playbackStarting = true;
        var sw = Stopwatch.StartNew();
        try
        {
            var ok = _playAsync != null
                ? await _playAsync(track, isRandomFill, ct)
                : await _playback.PlayAsync(track, isRandomFill: isRandomFill, ct);
            _log.PlaybackInfo(
                $"PLAYBACK_TIMING StartPlayback_ms={sw.ElapsedMilliseconds} song={track.SongName} ok={ok}");
            return ok;
        }
        finally
        {
            _playbackStarting = false;
        }
    }

    /// <summary>
    /// 播放失败：记录 → FinishCurrent → 继续下一首。禁止 Idle 却卡在 playing。
    /// </summary>
    private async Task FailCurrentAndContinueAsync(string songName, CancellationToken ct)
    {
        await HandlePlayFailureAsync(songName);
        _queue.FinishCurrent();

        if (_failContinueDepth >= 10)
        {
            _log.PlaybackWarn("连续播放失败过多，暂停自动切歌");
            return;
        }

        _failContinueDepth++;
        try
        {
            // 在同一命令内继续，避免 Skip/Advance 合并逻辑吞掉 EnqueueAdvance
            await AdvanceInternalAsync(ct);
        }
        finally
        {
            _failContinueDepth--;
        }
    }

    private Task HandlePlayFailureAsync(string songName)
    {
        var label = string.IsNullOrWhiteSpace(songName) ? "未知歌曲" : songName.Trim();
        var msg = _reply.Render("playbackError", new Dictionary<string, string>
        {
            ["song"] = label
        });
        _system.Add(string.IsNullOrWhiteSpace(msg) ? $"播放失败，已跳过《{label}》" : msg);
        _log.PlaybackWarn($"播放失败: {label}");
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _playback.TrackFinished -= OnTrackFinished;
        InvalidateRandomPrefetch();
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
