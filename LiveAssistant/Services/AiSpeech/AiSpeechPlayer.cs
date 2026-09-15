using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace LiveAssistant.Services.AiSpeech;

/// <summary>
/// 独立于点歌 PlaybackService 的 WAV 播放器：自动响度归一化 + 用户音量 + 软限幅。
/// 临时 wav：播放完成 / 失败 / 取消 / Dispose / 超时 均删除。
/// </summary>
public sealed class AiSpeechPlayer : IDisposable
{
    /// <summary>单次播放硬超时，防止 PlaybackStopped 丢失导致永久 Playing。</summary>
    public static TimeSpan DefaultPlayTimeout { get; set; } = TimeSpan.FromMinutes(3);

    /// <summary>归一化目标峰值（约 -1 dBFS）。</summary>
    public const float TargetPeakLinear = 0.89125094f; // 10^(-1/20)

    /// <summary>归一化增益上限（约 +26 dB），避免把近乎静音的噪声拉爆。</summary>
    public const float MaxNormalizeGain = 20f;

    /// <summary>归一化增益下限，避免把已经很响的素材压得过狠。</summary>
    public const float MinNormalizeGain = 0.25f;

    private readonly object _lock = new();
    private WaveOutEvent? _output;
    private WaveStream? _reader;
    private IDisposable? _providerChain;
    private string? _tempFile;
    private TaskCompletionSource<bool>? _playTcs;
    private int _deviceNumber = -1;
    private float _userGain = 1.8f;
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

    public float LastPeakBefore { get; private set; }
    public float LastRmsBefore { get; private set; }
    public float LastNormalizeGain { get; private set; } = 1f;
    public float LastUserGain { get; private set; } = 1f;
    public float LastPeakAfter { get; private set; }

    /// <summary>兼容旧诊断字段：归一化前 peak。</summary>
    public float LastPeak => LastPeakBefore;

    /// <summary>兼容旧诊断字段：归一化前 rms。</summary>
    public float LastRms => LastRmsBefore;

    /// <summary>兼容旧诊断字段：总增益 = normalize * user。</summary>
    public float LastGain => LastNormalizeGain * LastUserGain;

    public void SetDeviceNumber(int deviceNumber)
    {
        lock (_lock)
        {
            _deviceNumber = deviceNumber;
        }
    }

    /// <summary>用户软件增益；允许 &gt;1.0。100%=1.0，180%=1.8，200%=2.0。</summary>
    public void SetVolumeGain(float gain)
    {
        if (float.IsNaN(gain) || float.IsInfinity(gain) || gain <= 0)
        {
            gain = 1f;
        }

        lock (_lock)
        {
            _userGain = Math.Clamp(gain, 0.5f, 2.0f);
        }
    }

    /// <summary>按 VolumePercent（50～200）设置用户增益。</summary>
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
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (playTimeout > TimeSpan.Zero && playTimeout < Timeout.InfiniteTimeSpan)
        {
            timeoutCts.CancelAfter(playTimeout);
        }

        try
        {
            reader = new WaveFileReader(path);
            float userGain;
            int device;
            lock (_lock)
            {
                device = _deviceNumber;
                userGain = _userGain;
            }

            var levels = AnalyzeLevels(reader);
            reader.Position = 0;
            var plan = BuildGainPlan(levels.Peak, levels.Rms, userGain);

            LastPeakBefore = plan.PeakBefore;
            LastRmsBefore = plan.RmsBefore;
            LastNormalizeGain = plan.NormalizeGain;
            LastUserGain = plan.UserGain;
            LastPeakAfter = plan.EstimatedPeakAfter;

            var sample = reader.ToSampleProvider();
            // WAV → 自动归一化 → 用户 VolumePercent → soft clip
            var normalize = new VolumeSampleProvider(sample) { Volume = plan.NormalizeGain };
            var user = new VolumeSampleProvider(normalize) { Volume = plan.UserGain };
            var limited = new SoftLimitingSampleProvider(user);
            var waveProvider = limited.ToWaveProvider16();

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
                _providerChain = null;
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

    /// <summary>
    /// 根据 peak/rms 计算归一化增益：目标峰值约 -1 dBFS。
    /// 小声素材按峰值拉高；已够响的素材避免再大幅放大。
    /// </summary>
    public static float ComputeNormalizeGain(float peak, float rms)
    {
        if (float.IsNaN(peak) || float.IsInfinity(peak) || peak < 1e-5f)
        {
            return 1f;
        }

        // 主策略：峰值归一到约 -1 dBFS
        var gain = TargetPeakLinear / peak;

        // 人声响度保护：主体已经较响时，限制继续放大（正常 wav 不过度放大）
        if (!float.IsNaN(rms) && !float.IsInfinity(rms))
        {
            if (rms >= 0.20f)
            {
                // 已很响：最多保持/微降，不允许再 boost
                gain = Math.Min(gain, 1.0f);
            }
            else if (rms >= 0.12f)
            {
                // 正常语音主体：最多约 +3 dB
                gain = Math.Min(gain, 1.4f);
            }
            else if (rms >= 0.06f)
            {
                // 偏轻但仍可听：最多约 +9 dB
                gain = Math.Min(gain, 2.8f);
            }
            // rms 更低：允许接近完整峰值归一（很小的 wav 自动提高）
        }

        if (float.IsNaN(gain) || float.IsInfinity(gain) || gain <= 0)
        {
            return 1f;
        }

        return Math.Clamp(gain, MinNormalizeGain, MaxNormalizeGain);
    }

    public readonly record struct GainPlan(
        float PeakBefore,
        float RmsBefore,
        float NormalizeGain,
        float UserGain,
        float CombinedGain,
        float EstimatedPeakAfter,
        float NormalizeGainDb,
        float UserGainDb);

    public static GainPlan BuildGainPlan(float peakBefore, float rmsBefore, float userGain)
    {
        if (float.IsNaN(userGain) || float.IsInfinity(userGain) || userGain <= 0)
        {
            userGain = 1f;
        }

        userGain = Math.Clamp(userGain, 0.5f, 2.0f);
        var normalize = ComputeNormalizeGain(peakBefore, rmsBefore);
        var combined = normalize * userGain;
        // 限幅前估算；实际输出会经 soft-limit 压到 ≤1
        var estimatedRaw = peakBefore * combined;
        var estimatedAfter = SoftLimitingSampleProvider.SoftLimit(estimatedRaw);
        estimatedAfter = Math.Abs(estimatedAfter);

        return new GainPlan(
            peakBefore,
            rmsBefore,
            normalize,
            userGain,
            combined,
            estimatedAfter,
            LinearToDb(normalize),
            LinearToDb(userGain));
    }

    public static float LinearToDb(float linear)
    {
        if (linear <= 1e-8f || float.IsNaN(linear) || float.IsInfinity(linear))
        {
            return -80f;
        }

        return 20f * MathF.Log10(linear);
    }

    /// <summary>离线走完整播放增益链（归一化 → 用户增益 → 软限幅），供测试。</summary>
    public static GainPlan AnalyzeBytesFullChain(byte[] wavBytes, float userGain)
    {
        using var ms = new MemoryStream(wavBytes);
        using var reader = new WaveFileReader(ms);
        var (peak, rms) = AnalyzeLevels(reader);
        reader.Position = 0;
        var plan = BuildGainPlan(peak, rms, userGain);

        var sample = reader.ToSampleProvider();
        var normalize = new VolumeSampleProvider(sample) { Volume = plan.NormalizeGain };
        var user = new VolumeSampleProvider(normalize) { Volume = plan.UserGain };
        var limited = new SoftLimitingSampleProvider(user);
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

        return plan with { EstimatedPeakAfter = peakAfter };
    }

    /// <summary>兼容旧测试 API。</summary>
    public static (float Peak, float Rms, float PeakAfterGain) AnalyzeBytes(byte[] wavBytes, float gain)
    {
        var plan = AnalyzeBytesFullChain(wavBytes, gain);
        return (plan.PeakBefore, plan.RmsBefore, plan.EstimatedPeakAfter);
    }

    public void Stop() => StopInternal(cancel: true);

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
