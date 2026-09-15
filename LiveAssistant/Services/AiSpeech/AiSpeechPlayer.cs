using NAudio.Wave;

namespace LiveAssistant.Services.AiSpeech;

/// <summary>
/// 独立于点歌 PlaybackService 的 WAV 播放器，可指定输出设备。
/// 临时 wav：播放完成 / 失败 / 取消 / Dispose 均删除。
/// </summary>
public sealed class AiSpeechPlayer : IDisposable
{
    private readonly object _lock = new();
    private WaveOutEvent? _output;
    private WaveStream? _reader;
    private string? _tempFile;
    private TaskCompletionSource<bool>? _playTcs;
    private int _deviceNumber = -1;
    private volatile bool _disposed;

    public bool IsPlaying
    {
        get
        {
            lock (_lock)
            {
                return _output?.PlaybackState == PlaybackState.Playing;
            }
        }
    }

    /// <summary>当前临时文件路径（诊断用；播放结束后应为 null）。</summary>
    public string? CurrentTempFile
    {
        get { lock (_lock) return _tempFile; }
    }

    public void SetDeviceNumber(int deviceNumber)
    {
        lock (_lock)
        {
            _deviceNumber = deviceNumber;
        }
    }

    public async Task PlayWavAsync(byte[] wavBytes, string tempDirectory, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Directory.CreateDirectory(tempDirectory);

        StopInternal(cancel: true);

        var path = Path.Combine(tempDirectory, $"{Guid.NewGuid():N}.wav");
        try
        {
            await File.WriteAllBytesAsync(path, wavBytes, ct);
        }
        catch
        {
            CleanupTemp(path);
            throw;
        }

        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        WaveOutEvent? output = null;
        WaveStream? reader = null;

        try
        {
            reader = new WaveFileReader(path);
            int device;
            lock (_lock)
            {
                device = _deviceNumber;
            }

            output = new WaveOutEvent();
            if (device >= 0)
            {
                output.DeviceNumber = device;
            }

            output.PlaybackStopped += (_, e) =>
            {
                if (e.Exception != null)
                {
                    tcs.TrySetException(e.Exception);
                }
                else
                {
                    tcs.TrySetResult(true);
                }
            };

            lock (_lock)
            {
                _tempFile = path;
                _reader = reader;
                _output = output;
                _playTcs = tcs;
            }

            output.Init(reader);
            output.Play();

            using var reg = ct.Register(() =>
            {
                try { StopInternal(cancel: true); } catch { /* ignore */ }
                tcs.TrySetCanceled(ct);
            });

            await tcs.Task;
        }
        catch (OperationCanceledException)
        {
            CleanupTemp(path);
            throw;
        }
        catch
        {
            CleanupTemp(path);
            throw;
        }
        finally
        {
            StopInternal(cancel: false);
            CleanupTemp(path);
        }
    }

    public void Stop()
    {
        StopInternal(cancel: true);
    }

    private void StopInternal(bool cancel)
    {
        WaveOutEvent? output;
        WaveStream? reader;
        string? temp;
        TaskCompletionSource<bool>? tcs;

        lock (_lock)
        {
            output = _output;
            reader = _reader;
            temp = _tempFile;
            tcs = _playTcs;
            _output = null;
            _reader = null;
            _tempFile = null;
            _playTcs = null;
        }

        try { output?.Stop(); } catch { /* ignore */ }
        try { output?.Dispose(); } catch { /* ignore */ }
        try { reader?.Dispose(); } catch { /* ignore */ }
        CleanupTemp(temp);

        if (cancel)
        {
            tcs?.TrySetCanceled();
        }
        else
        {
            tcs?.TrySetResult(true);
        }
    }

    private static void CleanupTemp(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        for (var i = 0; i < 3; i++)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }

                return;
            }
            catch
            {
                Thread.Sleep(20);
            }
        }
    }

    /// <summary>清理目录下全部临时 wav（协调器退出时调用）。</summary>
    public static void CleanupTempDirectory(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            return;
        }

        foreach (var f in Directory.EnumerateFiles(directory, "*.wav"))
        {
            CleanupTemp(f);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        StopInternal(cancel: true);
    }
}
