using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace LiveAssistant.Services.AiSpeech;

/// <summary>
/// 独立于点歌 PlaybackService 的 WAV 播放器，可指定输出设备与软件增益。
/// 临时 wav：播放完成 / 失败 / 取消 / Dispose / 超时 均删除。
/// 任意异常或挂起：调用方最终应回到 Idle（本类保证 await 不会永久卡住）。
/// </summary>
public sealed class AiSpeechPlayer : IDisposable
{
    /// <summary>单次播放硬超时，防止 PlaybackStopped 丢失导致永久 Playing。</summary>
    public static TimeSpan DefaultPlayTimeout { get; set; } = TimeSpan.FromMinutes(3);

    private readonly object _lock = new();
    private WaveOutEvent? _output;
    private WaveStream? _reader;
    private IDisposable? _providerChain;
    private string? _tempFile;
    private TaskCompletionSource<bool>? _playTcs;
    private int _deviceNumber = -1;
    private float _volumeGain = 1.5f;
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

    /// <summary>最近一次分析的 peak / rms / gain（诊断）。</summary>
    public float LastPeak { get; private set; }
    public float LastRms { get; private set; }
    public float LastGain { get; private set; }

    public void SetDeviceNumber(int deviceNumber)
    {
        lock (_lock)
        {
            _deviceNumber = deviceNumber;
        }
    }

    /// <summary>软件增益；允许 &gt;1.0。100%=1.0，150%=1.5，200%=2.0。</summary>
    public void SetVolumeGain(float gain)
    {
        if (float.IsNaN(gain) || float.IsInfinity(gain) || gain <= 0)
        {
            gain = 1f;
        }

        lock (_lock)
        {
            _volumeGain = Math.Clamp(gain, 0.5f, 2.0f);
        }
    }

    /// <summary>按 VolumePercent（50～200）设置增益。</summary>
    public void SetVolumePercent(int volumePercent)
    {
        var pct = Math.Clamp(volumePercent, 50, 200);
        SetVolumeGain(pct / 100f);
    }

    public Task PlayWavAsync(byte[] wavBytes, string tempDirectory, CancellationToken ct)
        => PlayWavAsync(wavBytes, tempDirectory, DefaultPlayTimeout, ct);

    public async Task PlayWavAsync(byte[] wavBytes, string tempDirectory, TimeSpan playTimeout, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (wavBytes == null || wavBytes.Length == 0)
        {
            throw new InvalidOperationException("音频数据为空");
        }

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

        if (!File.Exists(path))
        {
            throw new FileNotFoundException("临时 wav 写入后不存在", path);
        }

        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        WaveOutEvent? output = null;
        WaveFileReader? reader = null;
        IDisposable? providerChain = null;
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (playTimeout > TimeSpan.Zero && playTimeout < Timeout.InfiniteTimeSpan)
        {
            timeoutCts.CancelAfter(playTimeout);
        }

        try
        {
            reader = new WaveFileReader(path);
            float gain;
            int device;
            lock (_lock)
            {
                device = _deviceNumber;
                gain = _volumeGain;
            }

            var levels = AnalyzeLevels(reader);
            reader.Position = 0;
            LastPeak = levels.Peak;
            LastRms = levels.Rms;
            LastGain = gain;

            var sample = reader.ToSampleProvider();
            var volume = new VolumeSampleProvider(sample) { Volume = gain };
            var limited = new SoftLimitingSampleProvider(volume);
            var waveProvider = limited.ToWaveProvider16();
            // 仅用于 Stop 时释放链；WaveFileReader 仍由 _reader 负责
            providerChain = null;

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
                _providerChain = providerChain;
                _output = output;
                _playTcs = tcs;
            }

            using var reg = timeoutCts.Token.Register(() =>
            {
                try { StopInternal(cancel: true); } catch { /* ignore */ }
                if (ct.IsCancellationRequested)
                {
                    tcs.TrySetCanceled(ct);
                }
                else
                {
                    tcs.TrySetException(new TimeoutException("AI 语音播放超时，已强制释放播放器"));
                }
            });

            // Init/Play 也可能因设备异常挂起；注册超时后再启动
            output.Init(waveProvider);
            output.Play();

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

    /// <summary>分析 wav 电平；调用后需把 reader.Position 重置为 0。</summary>
    public static (float Peak, float Rms) AnalyzeLevels(IWaveProvider source)
    {
        var sample = source.ToSampleProvider();
        var buf = new float[Math.Max(1024, sample.WaveFormat.SampleRate / 10)];
        float peak = 0f;
        double sumSq = 0;
        long n = 0;
        int read;
        while ((read = sample.Read(buf, 0, buf.Length)) > 0)
        {
            for (var i = 0; i < read; i++)
            {
                var v = buf[i];
                var a = Math.Abs(v);
                if (a > peak)
                {
                    peak = a;
                }

                sumSq += v * (double)v;
                n++;
            }
        }

        var rms = n > 0 ? (float)Math.Sqrt(sumSq / n) : 0f;
        return (peak, rms);
    }

    /// <summary>离线分析字节流电平与应用增益后的限幅峰值（测试用）。</summary>
    public static (float Peak, float Rms, float PeakAfterGain) AnalyzeBytes(byte[] wavBytes, float gain)
    {
        using var ms = new MemoryStream(wavBytes);
        using var reader = new WaveFileReader(ms);
        var (peak, rms) = AnalyzeLevels(reader);
        reader.Position = 0;
        var sample = reader.ToSampleProvider();
        var volume = new VolumeSampleProvider(sample) { Volume = Math.Clamp(gain, 0.5f, 2.0f) };
        var limited = new SoftLimitingSampleProvider(volume);
        var buf = new float[4096];
        float peakAfter = 0f;
        int read;
        while ((read = limited.Read(buf, 0, buf.Length)) > 0)
        {
            for (var i = 0; i < read; i++)
            {
                var a = Math.Abs(buf[i]);
                if (a > peakAfter)
                {
                    peakAfter = a;
                }
            }
        }

        return (peak, rms, peakAfter);
    }

    public void Stop()
    {
        StopInternal(cancel: true);
    }

    private void StopInternal(bool cancel)
    {
        WaveOutEvent? output;
        WaveStream? reader;
        IDisposable? provider;
        string? temp;
        TaskCompletionSource<bool>? tcs;

        lock (_lock)
        {
            output = _output;
            reader = _reader;
            provider = _providerChain;
            temp = _tempFile;
            tcs = _playTcs;
            _output = null;
            _reader = null;
            _providerChain = null;
            _tempFile = null;
            _playTcs = null;
        }

        try { output?.Stop(); } catch { /* ignore */ }
        try { output?.Dispose(); } catch { /* ignore */ }
        try { provider?.Dispose(); } catch { /* ignore */ }
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

/// <summary>
/// 软件增益后的软限幅：先 soft-knee，再硬夹到 [-1,1]，避免 200% 明显爆音。
/// </summary>
internal sealed class SoftLimitingSampleProvider : ISampleProvider
{
    private readonly ISampleProvider _source;

    public SoftLimitingSampleProvider(ISampleProvider source)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
    }

    public WaveFormat WaveFormat => _source.WaveFormat;

    public int Read(float[] buffer, int offset, int count)
    {
        var read = _source.Read(buffer, offset, count);
        for (var i = 0; i < read; i++)
        {
            buffer[offset + i] = SoftLimit(buffer[offset + i]);
        }

        return read;
    }

    internal static float SoftLimit(float sample)
    {
        var sign = sample < 0 ? -1f : 1f;
        var a = Math.Abs(sample);
        if (a <= 0.9f)
        {
            return sample;
        }

        // soft knee：超过 0.9 后用 tanh 压缩到接近 1.0
        var over = (a - 0.9f) / 0.1f;
        var shaped = 0.9f + 0.1f * MathF.Tanh(over);
        var result = sign * shaped;
        if (result > 1f)
        {
            return 1f;
        }

        if (result < -1f)
        {
            return -1f;
        }

        return result;
    }
}
