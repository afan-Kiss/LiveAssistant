using LiveAssistant.Models;
using NAudio.Wave;

namespace LiveAssistant.Services;

public sealed class PlaybackService : IDisposable
{
    private readonly LogService _log;
    private readonly object _lock = new();
    private WaveOutEvent? _output;
    private MediaFoundationReader? _reader;
    private CancellationTokenSource? _progressCts;
    private TrackInfo? _currentTrack;
    private Models.PlaybackState _state = Models.PlaybackState.Idle;
    private bool _isRandomFillActive;
    private int _progressSec;
    private int _volume = 80;

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
        lock (_lock)
        {
            if (_output != null)
            {
                _output.Volume = _volume / 100f;
            }
        }
    }

    public async Task<bool> PlayAsync(TrackInfo track, bool isRandomFill = false, CancellationToken ct = default)
    {
        StopInternal(fireEvent: false);

        if (string.IsNullOrWhiteSpace(track.PlayUrl))
        {
            _log.Warn($"无播放地址: {track.SongName}");
            return false;
        }

        try
        {
            var reader = await Task.Run(() => new MediaFoundationReader(track.PlayUrl), ct);
            var output = new WaveOutEvent { Volume = _volume / 100f };
            output.Init(reader);
            output.PlaybackStopped += OnPlaybackStopped;

            lock (_lock)
            {
                _reader = reader;
                _output = output;
                _currentTrack = track;
                _state = isRandomFill ? Models.PlaybackState.RandomFill : Models.PlaybackState.Playing;
                _isRandomFillActive = isRandomFill;
                _progressSec = 0;
            }

            output.Play();
            StartProgressLoop();
            StateChanged?.Invoke();
            _log.Info($"开始播放: {track.SongName} - {track.Artist}");
            return true;
        }
        catch (Exception ex)
        {
            _log.Error($"播放失败: {track.SongName}", ex);
            StopInternal(fireEvent: true);
            return false;
        }
    }

    public void Pause()
    {
        lock (_lock)
        {
            if (_output?.PlaybackState == NAudio.Wave.PlaybackState.Playing &&
                (_state == Models.PlaybackState.Playing || _state == Models.PlaybackState.RandomFill))
            {
                _output.Pause();
                _state = Models.PlaybackState.Paused;
                StateChanged?.Invoke();
            }
        }
    }

    public void Resume()
    {
        lock (_lock)
        {
            if (_output != null && _state == Models.PlaybackState.Paused)
            {
                _output.Play();
                _state = _isRandomFillActive ? Models.PlaybackState.RandomFill : Models.PlaybackState.Playing;
                StateChanged?.Invoke();
            }
        }
    }

    public void Stop()
    {
        StopInternal(fireEvent: true);
    }

    public event Action? TrackFinished;

    private void OnPlaybackStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception != null)
        {
            _log.Error("播放异常结束", e.Exception);
        }

        var finished = false;
        lock (_lock)
        {
            if (_output == null)
            {
                return;
            }
            finished = true;
        }

        if (finished)
        {
            StopInternal(fireEvent: true);
            TrackFinished?.Invoke();
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
                lock (_lock)
                {
                    if (_reader != null && _output?.PlaybackState == NAudio.Wave.PlaybackState.Playing)
                    {
                        _progressSec = (int)_reader.CurrentTime.TotalSeconds;
                    }
                }
                StateChanged?.Invoke();
                try
                {
                    await Task.Delay(500, token);
                }
                catch (TaskCanceledException)
                {
                    break;
                }
            }
        }, token);
    }

    private void StopInternal(bool fireEvent)
    {
        _progressCts?.Cancel();
        _progressCts = null;

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
            try
            {
                output.Stop();
            }
            catch
            {
                // ignore
            }
            output.Dispose();
        }

        reader?.Dispose();

        if (fireEvent)
        {
            StateChanged?.Invoke();
        }
    }

    public void Dispose()
    {
        StopInternal(fireEvent: false);
    }
}
