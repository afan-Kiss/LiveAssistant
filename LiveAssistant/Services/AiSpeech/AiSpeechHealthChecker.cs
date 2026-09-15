namespace LiveAssistant.Services.AiSpeech;

/// <summary>
/// AI 依赖健康检查：仅探测状态，绝不启动 ollama.exe / GPT-SoVITS。
/// 失败不抛到宿主；结果供 UI 与协调器使用。
/// </summary>
public sealed class AiSpeechHealthChecker
{
    public const int MaxStartupRetries = 3;
    public static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan RuntimeRecoverInterval = TimeSpan.FromSeconds(30);

    private readonly OllamaClient _ollama;
    private readonly GptSovitsClient _tts;
    private readonly Func<string> _getOllamaUrl;
    private readonly Func<int> _getOllamaTimeoutSeconds;
    private readonly Func<string> _getTtsUrl;
    private readonly Func<int> _getTtsTimeoutSeconds;
    private readonly Func<string> _getModel;
    private readonly Func<string> _getVoice;
    private readonly Action<string>? _log;

    private readonly object _gate = new();
    private AiSpeechHealthReport _latest = AiSpeechHealthReport.Unknown();
    private int _startupAttempts;
    private bool _startupSucceeded;
    private DateTime _lastCheckUtc = DateTime.MinValue;
    private DateTime _nextRetryUtc = DateTime.MinValue;
    private int _checkInFlight;

    public event Action<AiSpeechHealthReport>? Updated;

    public AiSpeechHealthChecker(
        OllamaClient ollama,
        GptSovitsClient tts,
        Func<string> getOllamaUrl,
        Func<int> getOllamaTimeoutSeconds,
        Func<string> getTtsUrl,
        Func<int> getTtsTimeoutSeconds,
        Func<string> getModel,
        Func<string> getVoice,
        Action<string>? log = null)
    {
        _ollama = ollama;
        _tts = tts;
        _getOllamaUrl = getOllamaUrl;
        _getOllamaTimeoutSeconds = getOllamaTimeoutSeconds;
        _getTtsUrl = getTtsUrl;
        _getTtsTimeoutSeconds = getTtsTimeoutSeconds;
        _getModel = getModel;
        _getVoice = getVoice;
        _log = log;
    }

    public AiSpeechHealthReport Latest
    {
        get { lock (_gate) return _latest; }
    }

    public int StartupAttempts
    {
        get { lock (_gate) return _startupAttempts; }
    }

    public bool StartupSucceeded
    {
        get { lock (_gate) return _startupSucceeded; }
    }

    /// <summary>
    /// 后台启动探测：立即 1 次，失败则最多再重试 2 次（间隔 30s），成功即停。
    /// 不阻塞调用方。
    /// </summary>
    public void StartBackgroundStartupChecks(CancellationToken ct)
    {
        _ = Task.Run(() => StartupLoopAsync(ct), ct);
    }

    /// <summary>
    /// 运行中：服务异常时每 30 秒自动恢复探测一次（不限次数）。
    /// 健康时空转等待。
    /// </summary>
    public void StartBackgroundRuntimeRecovery(CancellationToken ct)
    {
        _ = Task.Run(() => RuntimeRecoveryLoopAsync(ct), ct);
    }

    public async Task<AiSpeechHealthReport> CheckOnceAsync(
        string reason,
        CancellationToken ct = default,
        bool countAsStartupAttempt = false)
    {
        if (Interlocked.CompareExchange(ref _checkInFlight, 1, 0) != 0)
        {
            // 并发检查时返回最近结果，避免打爆本机服务
            return Latest;
        }

        try
        {
            var report = await ProbeAsync(reason, ct);
            lock (_gate)
            {
                _latest = report;
                _lastCheckUtc = DateTime.UtcNow;
                if (countAsStartupAttempt)
                {
                    _startupAttempts++;
                    if (report.FullyReady)
                    {
                        _startupSucceeded = true;
                        _nextRetryUtc = DateTime.MinValue;
                    }
                    else if (_startupAttempts < MaxStartupRetries)
                    {
                        _nextRetryUtc = DateTime.UtcNow.Add(RetryDelay);
                    }
                    else
                    {
                        _nextRetryUtc = DateTime.MinValue;
                    }
                }
                else if (report.FullyReady)
                {
                    _startupSucceeded = true;
                    _nextRetryUtc = DateTime.MinValue;
                }
            }

            try { Updated?.Invoke(report); } catch { /* ignore */ }
            return report;
        }
        finally
        {
            Interlocked.Exchange(ref _checkInFlight, 0);
        }
    }

    /// <summary>手动刷新：立即检查，不计启动重试次数上限。</summary>
    public Task<AiSpeechHealthReport> RefreshAsync(CancellationToken ct = default)
        => CheckOnceAsync("manual_refresh", ct, countAsStartupAttempt: false);

    private async Task StartupLoopAsync(CancellationToken ct)
    {
        try
        {
            await CheckOnceAsync("startup", ct, countAsStartupAttempt: true);
            while (!ct.IsCancellationRequested)
            {
                bool needRetry;
                DateTime next;
                lock (_gate)
                {
                    needRetry = !_startupSucceeded && _startupAttempts < MaxStartupRetries;
                    next = _nextRetryUtc;
                }

                if (!needRetry)
                {
                    break;
                }

                var delay = next - DateTime.UtcNow;
                if (delay < TimeSpan.Zero)
                {
                    delay = TimeSpan.Zero;
                }

                if (delay > TimeSpan.Zero)
                {
                    await Task.Delay(delay, ct);
                }

                await CheckOnceAsync("startup_retry", ct, countAsStartupAttempt: true);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // shutdown
        }
        catch (Exception ex)
        {
            _log?.Invoke($"AI_HEALTH_CHECK error=startup_loop {ex.GetType().Name}: {ex.Message}");
        }
    }

    private async Task RuntimeRecoveryLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(RuntimeRecoverInterval, ct);
                var latest = Latest;
                if (latest.FullyReady)
                {
                    continue;
                }

                // 启动重试尚未用尽时，交给 startup loop，避免双请求
                lock (_gate)
                {
                    if (!_startupSucceeded && _startupAttempts < MaxStartupRetries && _nextRetryUtc != DateTime.MinValue)
                    {
                        continue;
                    }
                }

                await CheckOnceAsync("runtime_recover", ct, countAsStartupAttempt: false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _log?.Invoke($"AI_HEALTH_CHECK error=runtime_loop {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    private async Task<AiSpeechHealthReport> ProbeAsync(string reason, CancellationToken ct)
    {
        var model = (_getModel() ?? "").Trim();
        if (string.IsNullOrWhiteSpace(model))
        {
            model = AiSpeechModelsCatalog.DefaultModel;
        }

        var voice = (_getVoice() ?? "").Trim();
        if (string.IsNullOrWhiteSpace(voice))
        {
            voice = "my_voice";
        }

        try
        {
            _ollama.Configure(_getOllamaUrl(), TimeSpan.FromSeconds(Math.Clamp(_getOllamaTimeoutSeconds(), 5, 120)));
            _tts.Configure(_getTtsUrl(), TimeSpan.FromSeconds(Math.Clamp(_getTtsTimeoutSeconds(), 5, 120)));
        }
        catch
        {
            // configure 失败仍继续探测默认地址
        }

        var ollamaTask = ProbeOllamaAsync(model, ct);
        var ttsTask = ProbeTtsAsync(voice, ct);
        await Task.WhenAll(ollamaTask, ttsTask);
        var ollama = await ollamaTask;
        var tts = await ttsTask;

        var report = new AiSpeechHealthReport
        {
            CheckedAtUtc = DateTime.UtcNow,
            Reason = reason,
            OllamaAvailable = ollama.Available,
            OllamaError = ollama.Error,
            ModelConfigured = model,
            ModelAvailable = ollama.ModelAvailable,
            InstalledModels = ollama.Models,
            TtsAvailable = tts.Available,
            TtsReady = tts.TtsReady,
            VoiceReady = tts.VoiceReady,
            VoiceName = string.IsNullOrWhiteSpace(tts.VoiceReported) ? voice : tts.VoiceReported,
            TtsError = tts.Error,
            ServiceHint = BuildHint(ollama, tts, model, voice),
            SummaryLines = BuildSummary(ollama, tts, model, voice)
        };

        LogReport(report);
        return report;
    }

    private async Task<(bool Available, bool ModelAvailable, IReadOnlyList<string> Models, string? Error)> ProbeOllamaAsync(
        string model,
        CancellationToken ct)
    {
        try
        {
            var models = await _ollama.ListModelsAsync(ct);
            const bool available = true;
            var modelOk = models.Any(m => ModelNameMatches(m, model));

            return (available, modelOk, models, modelOk ? null : $"模型未安装：{model}");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return (false, false, Array.Empty<string>(), "Ollama请求超时");
        }
        catch (Exception ex)
        {
            return (false, false, Array.Empty<string>(), $"Ollama未启动（{ex.GetType().Name}）");
        }
    }

    private async Task<(bool Available, bool TtsReady, bool VoiceReady, string VoiceReported, string? Error)> ProbeTtsAsync(
        string configuredVoice,
        CancellationToken ct)
    {
        try
        {
            var health = await _tts.HealthAsync(ct);
            if (!health.Ok)
            {
                return (false, false, false, configuredVoice, "语音服务未启动");
            }

            var ttsReady = health.TtsReady;
            var voiceReady = health.VoiceReady;
            if (!ttsReady)
            {
                return (true, false, voiceReady, health.Voice, "TTS未就绪");
            }

            if (!voiceReady)
            {
                // 若服务报告了其它 voice 名且与配置不符，也视为未加载
                if (!string.IsNullOrWhiteSpace(health.Voice)
                    && !string.Equals(health.Voice, configuredVoice, StringComparison.OrdinalIgnoreCase))
                {
                    return (true, true, false, health.Voice, "声音模型未加载");
                }

                return (true, true, false, configuredVoice, "声音模型未加载");
            }

            return (true, true, true, string.IsNullOrWhiteSpace(health.Voice) ? configuredVoice : health.Voice, null);
        }
        catch (Exception ex)
        {
            return (false, false, false, configuredVoice, $"语音服务未启动（{ex.GetType().Name}）");
        }
    }

    private void LogReport(AiSpeechHealthReport report)
    {
        var modelsBrief = report.InstalledModels.Count == 0
            ? "-"
            : string.Join(",", report.InstalledModels.Take(8));
        _log?.Invoke(
            $"AI_HEALTH_CHECK reason={report.Reason} ollama={Bool01(report.OllamaAvailable)} " +
            $"model_ok={Bool01(report.ModelAvailable)} model={report.ModelConfigured} " +
            $"tts={Bool01(report.TtsAvailable)} tts_ready={Bool01(report.TtsReady)} " +
            $"voice_ready={Bool01(report.VoiceReady)} voice={report.VoiceName} " +
            $"ready={Bool01(report.FullyReady)} err={Trim(report.ServiceHint, 120)}");

        if (string.Equals(report.Reason, "startup", StringComparison.OrdinalIgnoreCase)
            || report.Reason.StartsWith("startup", StringComparison.OrdinalIgnoreCase))
        {
            _log?.Invoke(
                $"AI_STARTUP_CHECK ollama={report.OllamaAvailable.ToString().ToLowerInvariant()} " +
                $"model={report.ModelConfigured} model_ok={report.ModelAvailable.ToString().ToLowerInvariant()} " +
                $"tts={report.TtsAvailable.ToString().ToLowerInvariant()} " +
                $"voice={report.VoiceName} voice_ready={report.VoiceReady.ToString().ToLowerInvariant()} " +
                $"installed=[{modelsBrief}] hint={Trim(report.ServiceHint, 160)}");
        }

        if (report.OllamaAvailable)
        {
            _log?.Invoke("OLLAMA_AVAILABLE");
        }
    }

    private static bool ModelNameMatches(string installed, string configured)
    {
        if (string.IsNullOrWhiteSpace(installed) || string.IsNullOrWhiteSpace(configured))
        {
            return false;
        }

        if (string.Equals(installed, configured, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // qwen3:8b 匹配 qwen3:8b-instruct 等变体标签
        if (installed.StartsWith(configured + "-", StringComparison.OrdinalIgnoreCase)
            || installed.StartsWith(configured + ":", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // 配置写成 qwen3:8b，列表只有 qwen3:8b@sha...
        var at = installed.IndexOf('@');
        if (at > 0)
        {
            var bare = installed[..at];
            if (string.Equals(bare, configured, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string BuildHint(
        (bool Available, bool ModelAvailable, IReadOnlyList<string> Models, string? Error) ollama,
        (bool Available, bool TtsReady, bool VoiceReady, string VoiceReported, string? Error) tts,
        string model,
        string voice)
    {
        var parts = new List<string>();
        if (!ollama.Available)
        {
            parts.Add("Ollama未启动");
        }
        else if (!ollama.ModelAvailable)
        {
            parts.Add($"当前AI模型未安装：{model}");
            if (ollama.Models.Count > 0)
            {
                parts.Add("已安装：" + string.Join("、", ollama.Models.Take(6)));
            }
        }

        if (!tts.Available)
        {
            parts.Add("语音服务未启动");
        }
        else if (!tts.TtsReady)
        {
            parts.Add("TTS不可用");
        }
        else if (!tts.VoiceReady)
        {
            parts.Add($"声音模型未加载（{voice}）");
        }

        return parts.Count == 0 ? "" : string.Join("；", parts);
    }

    private static IReadOnlyList<string> BuildSummary(
        (bool Available, bool ModelAvailable, IReadOnlyList<string> Models, string? Error) ollama,
        (bool Available, bool TtsReady, bool VoiceReady, string VoiceReported, string? Error) tts,
        string model,
        string voice)
    {
        var lines = new List<string>();
        if (ollama.Available && ollama.ModelAvailable)
        {
            lines.Add("✅ AI模型正常");
        }
        else if (!ollama.Available)
        {
            lines.Add("❌ Ollama未连接");
        }
        else
        {
            lines.Add($"❌ 模型不存在（{model}）");
        }

        if (tts.Available && tts.TtsReady && tts.VoiceReady)
        {
            lines.Add("✅ 声音正常");
        }
        else if (!tts.Available)
        {
            lines.Add("❌ TTS不可用");
        }
        else if (!tts.VoiceReady)
        {
            lines.Add($"❌ 声音模型未加载（{voice}）");
        }
        else
        {
            lines.Add("❌ TTS不可用");
        }

        if (ollama.Available && ollama.ModelAvailable && tts.Available && tts.TtsReady && tts.VoiceReady)
        {
            lines.Add("✅ 可以发言");
        }
        else
        {
            lines.Add("❌ 暂不可发言");
        }

        return lines;
    }

    private static string Bool01(bool v) => v ? "1" : "0";

    private static string Trim(string? s, int max)
    {
        s ??= "";
        return s.Length <= max ? s : s[..max] + "…";
    }
}

public sealed class AiSpeechHealthReport
{
    public DateTime CheckedAtUtc { get; init; } = DateTime.UtcNow;
    public string Reason { get; init; } = "";
    public bool OllamaAvailable { get; init; }
    public string? OllamaError { get; init; }
    public string ModelConfigured { get; init; } = "";
    public bool ModelAvailable { get; init; }
    public IReadOnlyList<string> InstalledModels { get; init; } = Array.Empty<string>();
    public bool TtsAvailable { get; init; }
    public bool TtsReady { get; init; }
    public bool VoiceReady { get; init; }
    public string VoiceName { get; init; } = "my_voice";
    public string? TtsError { get; init; }
    public string ServiceHint { get; init; } = "";
    public IReadOnlyList<string> SummaryLines { get; init; } = Array.Empty<string>();

    public bool FullyReady =>
        OllamaAvailable && ModelAvailable && TtsAvailable && TtsReady && VoiceReady;

    public static AiSpeechHealthReport Unknown() => new()
    {
        Reason = "unknown",
        ServiceHint = "正在检测 AI 服务…",
        SummaryLines = new[] { "⏳ 正在检测…", "⏳ 正在检测…", "⏳ 暂不可发言" }
    };
}
