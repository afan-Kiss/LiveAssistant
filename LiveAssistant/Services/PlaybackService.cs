using System.Diagnostics;
using LiveAssistant.Models;
using NAudio.Wave;

namespace LiveAssistant.Services;

/// <summary>
/// 底层播放器，仅允许 PlaybackCommandQueue 调用控制方法。
/// </summary>
public sealed class PlaybackService : IDisposable
{
    private static readonly TimeSpan DisposeWaitNormal = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan DisposeWaitAbnormal = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan PendingDisposeSafetyWait = TimeSpan.FromMilliseconds(100);

    private readonly LogService _log;
    private readonly object _lock = new();
    private readonly object _disposeSync = new();
    private readonly SemaphoreSlim _stopGate = new(1, 1);
    private WaveOutEvent? _output;
    private MediaFoundationReader? _reader;
    private CancellationTokenSource? _progressCts;
    private CancellationTokenSource? _heartbeatCts;
    private TrackInfo? _currentTrack;
    private Models.PlaybackState _state = Models.PlaybackState.Idle;
    private bool _isRandomFillActive;
    private int _progressSec;
    private int _volume = 80;
    private volatile bool _trackFinishedSignaled;
    private Task? _pendingDisposeTask;
    private long _lastReaderPositionMs;
    private DateTime _lastReaderChangeUtc = DateTime.MinValue;
    private long _lastCallbackIntervalMs;

    public event Action? StateChanged;

    public PlaybackService(LogService log)
    {
        _log = log;
    }

    public TrackInfo? CurrentTrack
    {
        get { lock (_lock) return _currentTrack; }
    }

    public Models.PlaybackState State
    {
        get { lock (_lock) return _state; }
    }

    public bool IsRandomFillActive
    {
        get { lock (_lock) return _isRandomFillActive; }
    }

    public int ProgressSec
    {
        get { lock (_lock) return _progressSec; }
    }

    public int DurationSec
    {
        get
        {
            lock (_lock)
            {
                if (_reader == null)
                {
                    return _currentTrack?.DurationSec ?? 0;
                }

                return (int)_reader.TotalTime.TotalSeconds;
            }
        }
    }

    public void SetVolume(int volume)
    {
        _volume = Math.Clamp(volume, 0, 100);
        WaveOutEvent? output;
        lock (_lock)
        {
            output = _output;
        }

        if (output != null)
        {
            try
            {
                output.Volume = _volume / 100f;
            }
            catch (Exception ex)
            {
                _log.Error("playback", "设置音量失败", ex);
            }
        }
    }

    public async Task<bool> PlayAsync(TrackInfo track, bool isRandomFill = false, CancellationToken ct = default)
    {
        await WaitPendingDisposeAsync(PendingDisposeSafetyWait, "play_start", ct);

        var totalSw = Stopwatch.StartNew();
        var stopSw = Stopwatch.StartNew();
        await StopInternalAsync(fireEvent: false, abnormal: false, ct);
        var stopMs = stopSw.ElapsedMilliseconds;

        if (string.IsNullOrWhiteSpace(track.PlayUrl))
        {
            _log.PlaybackWarn($"无播放地址: {track.SongName}");
            return false;
        }

        try
        {
            var readerSw = Stopwatch.StartNew();
            var reader = await Task.Run(() => new MediaFoundationReader(track.PlayUrl), ct);
            var readerMs = readerSw.ElapsedMilliseconds;

            var (output, initMs) = await Task.Run(() =>
            {
                var localSw = Stopwatch.StartNew();
                var waveOut = new WaveOutEvent { Volume = _volume / 100f };
                waveOut.Init(reader);
                return (waveOut, localSw.ElapsedMilliseconds);
            }, ct);

            output.PlaybackStopped += OnPlaybackStopped;
            _trackFinishedSignaled = false;
            _lastReaderPositionMs = 0;
            _lastReaderChangeUtc = DateTime.MinValue;
            _lastCallbackIntervalMs = 0;

            lock (_lock)
            {
                _reader = reader;
                _output = output;
                _currentTrack = track;
                _state = isRandomFill ? Models.PlaybackState.RandomFill : Models.PlaybackState.Playing;
                _isRandomFillActive = isRandomFill;
                _progressSec = 0;
            }

            var playSw = Stopwatch.StartNew();
            output.Play();
            var playMs = playSw.ElapsedMilliseconds;

            StartProgressLoop();
            StartHeartbeatLoop();
            StateChanged?.Invoke();
            _log.PlaybackInfo(
                $"PLAYBACK_TIMING song={track.SongName} stop_ms={stopMs} reader_ms={readerMs} init_ms={initMs} " +
                $"play_ms={playMs} total_ms={totalSw.ElapsedMilliseconds} thread={Environment.CurrentManagedThreadId}");
            _log.PlaybackInfo($"开始播放: {track.SongName} - {track.Artist}");
            return true;
        }
        catch (Exception ex)
        {
            _log.Error("playback", $"播放失败: {track.SongName}", ex);
            await StopInternalAsync(fireEvent: true, abnormal: true, ct);
            return false;
        }
    }

    public void Pause()
    {
        try
        {
            WaveOutEvent? output;
            Models.PlaybackState state;
            lock (_lock)
            {
                output = _output;
                state = _state;
            }

            if (output?.PlaybackState == NAudio.Wave.PlaybackState.Playing
                && (state == Models.PlaybackState.Playing || state == Models.PlaybackState.RandomFill))
            {
                output.Pause();
                lock (_lock)
                {
                    _state = Models.PlaybackState.Paused;
                }
                StateChanged?.Invoke();
            }
        }
        catch (Exception ex)
        {
            _log.Error("playback", "暂停失败", ex);
        }
    }

    public void Resume()
    {
        try
        {
            WaveOutEvent? output;
            Models.PlaybackState state;
            bool isRandom;
            lock (_lock)
            {
                output = _output;
                state = _state;
                isRandom = _isRandomFillActive;
            }

            if (output != null && state == Models.PlaybackState.Paused)
            {
                output.Play();
                lock (_lock)
                {
                    _state = isRandom ? Models.PlaybackState.RandomFill : Models.PlaybackState.Playing;
                }
                StateChanged?.Invoke();
            }
        }
        catch (Exception ex)
        {
            _log.Error("playback", "恢复失败", ex);
        }
    }

    public Task StopAsync(CancellationToken ct = default) =>
        StopInternalAsync(fireEvent: true, abnormal: false, ct);

    public void Stop()
    {
        try
        {
            StopAsync().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            _log.Error("playback", "停止失败", ex);
        }
    }

    public event Action? TrackFinished;

    private void OnPlaybackStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception != null)
        {
            _log.Error("playback", "播放异常结束", e.Exception);
        }

        var stoppedOutput = sender as WaveOutEvent;
        // 禁止在 NAudio 回调线程里 Stop/Dispose，转后台处理
        _ = Task.Run(() => ProcessTrackFinishedAsync(stoppedOutput));
    }

    private async Task ProcessTrackFinishedAsync(WaveOutEvent? stoppedOutput)
    {
        if (_trackFinishedSignaled)
        {
            return;
        }

        // 手动切歌/立即播放后 output 已换新或清空，忽略旧实例的延迟回调
        lock (_lock)
        {
            if (_output != stoppedOutput)
            {
                _log.PlaybackInfo("忽略过期的 PlaybackStopped 回调");
                return;
            }
        }

        _trackFinishedSignaled = true;
        try
        {
            await StopInternalAsync(fireEvent: true, abnormal: false, CancellationToken.None);
            TrackFinished?.Invoke();
        }
        catch (Exception ex)
        {
            _log.Error("playback", "TrackFinished 回调异常", ex);
        }
    }

    private void StartProgressLoop()
    {
        _progressCts?.Cancel();
        _progressCts = new CancellationTokenSource();
        var token = _progressCts.Token;
        _ = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    WaveOutEvent? output;
                    MediaFoundationReader? reader;
                    lock (_lock)
                    {
                        output = _output;
                        reader = _reader;
                        if (reader != null && output?.PlaybackState == NAudio.Wave.PlaybackState.Playing)
                        {
                            _progressSec = (int)reader.CurrentTime.TotalSeconds;
                            NoteReaderPositionChange(reader);
                        }
                    }

                    StateChanged?.Invoke();
                    await Task.Delay(1000, token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _log.Error("playback", "进度循环异常", ex);
                    try
                    {
                        await Task.Delay(1000, token);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                }
            }
        }, token);
    }

    private void StartHeartbeatLoop()
    {
        _heartbeatCts?.Cancel();
        _heartbeatCts = new CancellationTokenSource();
        var token = _heartbeatCts.Token;
        _ = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(1000, token);
                    LogHeartbeat();
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _log.Error("playback", "心跳循环异常", ex);
                }
            }
        }, token);
    }

    private void NoteReaderPositionChange(MediaFoundationReader reader)
    {
        var posMs = (long)reader.CurrentTime.TotalMilliseconds;
        if (posMs == _lastReaderPositionMs)
        {
            return;
        }

        var now = DateTime.UtcNow;
        if (_lastReaderChangeUtc != DateTime.MinValue)
        {
            _lastCallbackIntervalMs = (long)(now - _lastReaderChangeUtc).TotalMilliseconds;
        }

        _lastReaderChangeUtc = now;
        _lastReaderPositionMs = posMs;
    }

    private void LogHeartbeat()
    {
        string song;
        int position;
        Models.PlaybackState state;
        string waveOutState;
        string bufferState;
        long readerPosMs;
        long readerTotalMs;
        long callbackIntervalMs;
        string lastCallbackUtc;
        lock (_lock)
        {
            song = _currentTrack?.SongName ?? "-";
            position = _progressSec;
            state = _state;
            if (_output != null)
            {
                waveOutState = _output.PlaybackState.ToString();
                bufferState = $"latency={_output.DesiredLatency}ms";
            }
            else
            {
                waveOutState = "none";
                bufferState = "none";
            }

            if (_reader != null)
            {
                readerPosMs = (long)_reader.CurrentTime.TotalMilliseconds;
                readerTotalMs = (long)_reader.TotalTime.TotalMilliseconds;
                NoteReaderPositionChange(_reader);
            }
            else
            {
                readerPosMs = 0;
                readerTotalMs = 0;
            }

            callbackIntervalMs = _lastCallbackIntervalMs;
            lastCallbackUtc = _lastReaderChangeUtc == DateTime.MinValue
                ? "-"
                : _lastReaderChangeUtc.ToString("O");
        }

        _log.PlaybackInfo(
            $"PLAYBACK_HEARTBEAT song={song} position={position}s readerPos={readerPosMs}ms " +
            $"readerTotal={readerTotalMs}ms state={state} waveOut={waveOutState} buffer={bufferState} " +
            $"lastCallback={lastCallbackUtc} callbackInterval={callbackIntervalMs}ms " +
            $"thread={Environment.CurrentManagedThreadId}");
    }

    private async Task StopInternalAsync(bool fireEvent, bool abnormal, CancellationToken ct)
    {
        await _stopGate.WaitAsync(ct);
        try
        {
            _progressCts?.Cancel();
            _progressCts = null;
            _heartbeatCts?.Cancel();
            _heartbeatCts = null;

            WaveOutEvent? output;
            MediaFoundationReader? reader;
            lock (_lock)
            {
                output = _output;
                reader = _reader;
                _output = null;
                _reader = null;
                _currentTrack = null;
                _state = Models.PlaybackState.Idle;
                _isRandomFillActive = false;
                _progressSec = 0;
            }

            if (output != null)
            {
                output.PlaybackStopped -= OnPlaybackStopped;
                var stopSw = Stopwatch.StartNew();
                try
                {
                    await Task.Run(() => output.Stop(), ct);
                }
                catch (Exception ex)
                {
                    _log.Error("playback", "输出设备停止异常", ex);
                }

                var stopMs = stopSw.ElapsedMilliseconds;
                var disposeReason = abnormal ? "abnormal_stop" : "normal_stop";
                await ScheduleDisposeAsync(
                    output,
                    reader,
                    stopMs,
                    abnormal ? DisposeWaitAbnormal : DisposeWaitNormal,
                    disposeReason,
                    ct);
            }
            else if (reader != null)
            {
                await ScheduleDisposeAsync(
                    null,
                    reader,
                    0,
                    abnormal ? DisposeWaitAbnormal : DisposeWaitNormal,
                    abnormal ? "abnormal_reader_only" : "reader_only",
                    ct);
            }

            if (fireEvent)
            {
                StateChanged?.Invoke();
            }
        }
        finally
        {
            _stopGate.Release();
        }
    }

    private async Task ScheduleDisposeAsync(
        WaveOutEvent? output,
        MediaFoundationReader? reader,
        long stopMs,
        TimeSpan waitTimeout,
        string reason,
        CancellationToken ct)
    {
        if (output == null && reader == null)
        {
            return;
        }

        var outputRef = output;
        var readerRef = reader;
        var outputDisposed = 0;
        var readerDisposed = 0;

        var task = Task.Run(() =>
        {
            var sw = Stopwatch.StartNew();
            if (outputRef != null && Interlocked.Exchange(ref outputDisposed, 1) == 0)
            {
                try
                {
                    outputRef.Dispose();
                }
                catch (Exception ex)
                {
                    _log.PlaybackWarn($"PLAYBACK_TIMING output.Dispose异常 reason={reason} err={ex.Message}");
                }
            }

            if (readerRef != null && Interlocked.Exchange(ref readerDisposed, 1) == 0)
            {
                try
                {
                    readerRef.Dispose();
                }
                catch (Exception ex)
                {
                    _log.PlaybackWarn($"PLAYBACK_TIMING reader.Dispose异常 reason={reason} err={ex.Message}");
                }
            }

            _log.PlaybackInfo(
                $"PLAYBACK_TIMING output.Stop_ms={stopMs} dispose_ms={sw.ElapsedMilliseconds} " +
                $"reason={reason} thread={Environment.CurrentManagedThreadId}");
        });

        lock (_disposeSync)
        {
            _pendingDisposeTask = task;
        }

        try
        {
            await task.WaitAsync(waitTimeout, ct);
        }
        catch (TimeoutException)
        {
            _log.PlaybackWarn(
                $"PLAYBACK_TIMING dispose timeout reason={reason} wait_ms={waitTimeout.TotalMilliseconds} " +
                "continuing");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.PlaybackWarn($"PLAYBACK_TIMING dispose wait异常 reason={reason} err={ex.Message}");
        }
    }

    private async Task WaitPendingDisposeAsync(TimeSpan waitTimeout, string reason, CancellationToken ct)
    {
        Task? task;
        lock (_disposeSync)
        {
            task = _pendingDisposeTask;
        }

        if (task == null || task.IsCompleted)
        {
            return;
        }

        try
        {
            await task.WaitAsync(waitTimeout, ct);
        }
        catch (TimeoutException)
        {
            _log.PlaybackWarn(
                $"PLAYBACK_TIMING pending dispose timeout reason={reason} wait_ms={waitTimeout.TotalMilliseconds} " +
                "continuing");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.PlaybackWarn($"PLAYBACK_TIMING pending dispose wait异常 reason={reason} err={ex.Message}");
        }
    }

    public void Dispose()
    {
        try
        {
            StopInternalAsync(fireEvent: false, abnormal: true, CancellationToken.None).GetAwaiter().GetResult();
        }
        catch
        {
            // ignore shutdown errors
        }
        finally
        {
            _stopGate.Dispose();
        }
    }
}
