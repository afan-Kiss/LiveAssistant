using System.Diagnostics;
using LiveAssistant.Config;
using LiveAssistant.Models;

namespace LiveAssistant.Services.AiSpeech;

/// <summary>
/// AI 语音互动协调器 V2：优先级调度、提示词热加载、上下文、礼物/欢迎/点赞缓冲。
/// 与点歌/礼物积分完全隔离，任何失败不影响直播主链路。
/// </summary>
public sealed class AiSpeechCoordinator : IDisposable
{
    /// <summary>内置回退人格；运行时优先读 Config/AiSpeech/personality.txt。</summary>
    public static string DefaultSystemPrompt => AiPersonalityLoader.DefaultPersonalityText.Trim();

    private readonly ConfigManager _config;
    private readonly LogService _log;
    private readonly OutboundReplyTracker _outboundTracker;
    private readonly OllamaClient _ollama;
    private readonly GptSovitsClient _tts;
    private readonly AiSpeechPlayer _player;
    private readonly string _tempDir;

    private readonly AiSpeechScheduler _scheduler = new();
    private readonly AiSpeechMetrics _metrics = new();
    private readonly AiReplyDuplicateGuard _replyDupGuard = new(20, TimeSpan.FromMinutes(5));
    private readonly AiPromptStore _prompts;
    private readonly AiSpeechHealthChecker _health;
    private readonly UserConversationContext _userContext;
    private readonly RoomConversationContext _roomContext;
    private readonly RoomContextSummary _roomSummary;
    private readonly GiftMergeBuffer _giftBuffer = new();
    private readonly WelcomeBatchBuffer _welcomeBuffer = new();
    private readonly LikeAccumulateBuffer _likeBuffer = new();

    private readonly object _recentContentLock = new();
    private readonly Dictionary<string, DateTime> _recentContents = new(StringComparer.Ordinal);

    private CancellationTokenSource _cts = new();
    private Task? _worker;
    private Task? _metricsLoop;
    private int _disposed;
    private volatile AiSpeechPhase _phase = AiSpeechPhase.Idle;
    private volatile AiSpeechEventKind _latestKind = AiSpeechEventKind.Danmaku;
    private volatile string _latestNickname = "";
    private volatile string _latestContent = "";
    private volatile string _latestReply = "";
    private volatile string _latestEmotion = "";
    private double _latestSpeed = 1.0;
    private readonly object _speedLock = new();
    private volatile string _serviceHint = "";
    private volatile bool _ollamaOk;
    private volatile bool _ttsOk;
    private volatile bool _voiceReady = true;
    private volatile bool _modelAvailable;
    private volatile string _healthSummary = "正在检测 AI 服务…";
    private volatile string _configuredModelName = "";
    private string[] _installedModels = Array.Empty<string>();
    private readonly object _installedModelsLock = new();
    private readonly SemaphoreSlim _executionGate = new(1, 1);
    private CancellationTokenSource? _activePlayCts;
    private long _lastOllamaMs;
    private long _lastTtsMs;
    private long _lastTotalMs;
    private DateTime _lastPlayCompletedUtc = DateTime.MinValue;
    private DateTime _lastSummaryEnqueueUtc = DateTime.MinValue;

    public event Action? StatusChanged;

    public AiSpeechCoordinator(
        ConfigManager config,
        LogService log,
        OutboundReplyTracker outboundTracker)
    {
        _config = config;
        _log = log;
        _outboundTracker = outboundTracker;
        var s = config.Settings.AiSpeech;
        _ollama = new OllamaClient(s.OllamaUrl, TimeSpan.FromSeconds(Math.Clamp(s.OllamaTimeoutSeconds, 10, 120)));
        _tts = new GptSovitsClient(s.TtsUrl, TimeSpan.FromSeconds(Math.Clamp(s.TtsTimeoutSeconds, 10, 120)));
        _player = new AiSpeechPlayer();
        _tempDir = Path.Combine(config.DataDirectory, "ai-speech", "temp");
        Directory.CreateDirectory(_tempDir);

        _prompts = new AiPromptStore(
            Path.Combine(AppPaths.ConfigDirectory, "AiSpeech"),
            Path.Combine(config.DataDirectory, "AiSpeech"),
            msg =>
            {
                try { _log.AiInfo(msg); } catch { /* ignore */ }
            });

        _userContext = new UserConversationContext(
            s.UserContextCount,
            s.HostReplyContextCount,
            s.ContextTtlMinutes,
            s.MaxTrackedUsers > 0 ? s.MaxTrackedUsers : 2000);
        _roomContext = new RoomConversationContext(s.RoomContextCount, s.RoomWindowSeconds);
        _roomSummary = new RoomContextSummary();

        _health = new AiSpeechHealthChecker(
            _ollama,
            _tts,
            () => Settings.OllamaUrl,
            () => Settings.OllamaTimeoutSeconds,
            () => Settings.TtsUrl,
            () => Settings.TtsTimeoutSeconds,
            () => string.IsNullOrWhiteSpace(Settings.Model) ? AiSpeechModelsCatalog.DefaultModel : Settings.Model,
            () => string.IsNullOrWhiteSpace(Settings.Voice) ? "my_voice" : Settings.Voice,
            msg =>
            {
                try { _log.AiInfo(msg); } catch { /* ignore */ }
            });
        _health.Updated += OnHealthUpdated;

        ApplySchedulerLimits();
        ApplyBufferIntervals();
        _giftBuffer.Flushed += OnGiftFlushed;
        _welcomeBuffer.Flushed += OnWelcomeFlushed;
        _likeBuffer.Flushed += OnLikeFlushed;

        ApplyDeviceFromSettings();
        _worker = Task.Run(() => WorkerLoopAsync(_cts.Token));
        _metricsLoop = Task.Run(() => MetricsSnapshotLoopAsync(_cts.Token));
        // 非阻塞：启动探测 + 运行中自动恢复（绝不拉起外部进程）
        _health.StartBackgroundStartupChecks(_cts.Token);
        _health.StartBackgroundRuntimeRecovery(_cts.Token);
        ApplyHealthReport(_health.Latest);
    }

    public AiSpeechSettings Settings => _config.Settings.AiSpeech;

    /// <summary>提示词仓库（UI 编辑器用）。</summary>
    public AiPromptStore Prompts => _prompts;

    /// <summary>运行指标（测试/诊断）。</summary>
    public AiSpeechMetrics Metrics => _metrics;

    public AiSpeechStatusSnapshot GetStatus()
    {
        var maxQ = Math.Clamp(Settings.MaxQueueSize, 1, 20);
        var q = _scheduler.Count;
        _metrics.NoteQueueSize(q);
        var m = _metrics.Snapshot(q, maxQ);
        string[] installed;
        lock (_installedModelsLock) installed = _installedModels;
        return new AiSpeechStatusSnapshot
        {
            Enabled = Settings.Enabled || Settings.TestMode,
            TestMode = Settings.TestMode,
            Phase = _phase,
            PhaseText = AiSpeechPhaseText.ToText(_phase),
            RuntimeStatusText = AiSpeechPhaseText.ToRuntimeStatus(_phase),
            QueueCount = q,
            MaxQueueSize = maxQ,
            LatestNickname = _latestNickname,
            LatestContent = _latestContent,
            LatestReply = _latestReply,
            ServiceHint = _serviceHint,
            OllamaOk = _ollamaOk,
            TtsOk = _ttsOk,
            VoiceReady = _voiceReady,
            ModelAvailable = _modelAvailable,
            ModelName = string.IsNullOrWhiteSpace(_configuredModelName)
                ? (string.IsNullOrWhiteSpace(Settings.Model) ? AiSpeechModelsCatalog.DefaultModel : Settings.Model)
                : _configuredModelName,
            InstalledModels = installed,
            HealthSummary = _healthSummary,
            AiReady = _ollamaOk && _modelAvailable && _ttsOk && _voiceReady,
            VoiceName = string.IsNullOrWhiteSpace(Settings.Voice) ? "my_voice" : Settings.Voice,
            LastOllamaMs = Interlocked.Read(ref _lastOllamaMs),
            LastTtsMs = Interlocked.Read(ref _lastTtsMs),
            LastTotalMs = Interlocked.Read(ref _lastTotalMs),
            TaskKind = _latestKind,
            Emotion = _latestEmotion,
            Speed = GetLatestSpeed(),
            PromptLoadedAt = _prompts.GetLoadedAt(),
            PromptVersion = _prompts.GetVersion(),
            TodayReplyCount = m.TodayReplyCount,
            SuccessRatePercent = m.SuccessRatePercent,
            AverageTotalMs = m.AverageTotalMs,
            LastError = m.LastError,
            MaxQueueSizeSeen = m.MaxQueueSizeSeen
        };
    }

    private double GetLatestSpeed()
    {
        lock (_speedLock) return _latestSpeed;
    }

    private void SetLatestSpeed(double speed)
    {
        lock (_speedLock) _latestSpeed = speed;
    }

    public void ApplyDeviceFromSettings()
    {
        var device = AudioOutputDevices.ResolveDeviceNumber(
            Settings.OutputDeviceName,
            Settings.OutputDeviceNumber);
        _player.SetDeviceNumber(device);
    }

    public void SaveSettingsFromUi(Action<AiSpeechSettings> mutate)
    {
        mutate(Settings);
        ClampSettings(Settings);
        SyncReplyInterval(Settings);
        ApplySchedulerLimits();
        ApplyBufferIntervals();
        _userContext.Configure(
            Settings.UserContextCount,
            Settings.HostReplyContextCount,
            Settings.ContextTtlMinutes,
            Settings.MaxTrackedUsers > 0 ? Settings.MaxTrackedUsers : 2000);
        _roomContext.Configure(Settings.RoomContextCount, Settings.RoomWindowSeconds);

        AiPersonalityLoader.EnsureDefaultFileExists();
        _prompts.EnsureDefaultFiles();
        _ollama.Configure(Settings.OllamaUrl, TimeSpan.FromSeconds(Settings.OllamaTimeoutSeconds));
        _tts.Configure(Settings.TtsUrl, TimeSpan.FromSeconds(Settings.TtsTimeoutSeconds));
        ApplyDeviceFromSettings();
        _config.Save();
        NotifyStatus();
    }

    public async Task RefreshHealthAsync(CancellationToken ct = default)
    {
        try
        {
            var report = await _health.RefreshAsync(ct);
            ApplyHealthReport(report);
        }
        catch (Exception ex)
        {
            _ollamaOk = false;
            _ttsOk = false;
            _voiceReady = false;
            _modelAvailable = false;
            _serviceHint = "健康检查失败";
            _healthSummary = "❌ AI服务异常";
            _log.AiWarn($"health_fail {ex.GetType().Name}: {ex.Message}");
            NotifyStatus();
        }
    }

    private void OnHealthUpdated(AiSpeechHealthReport report)
    {
        try
        {
            ApplyHealthReport(report);
        }
        catch (Exception ex)
        {
            try { _log.AiWarn($"health_apply_fail {ex.GetType().Name}: {ex.Message}"); } catch { /* ignore */ }
        }
    }

    private void ApplyHealthReport(AiSpeechHealthReport report)
    {
        _ollamaOk = report.OllamaAvailable;
        _modelAvailable = report.ModelAvailable;
        _ttsOk = report.TtsAvailable && report.TtsReady;
        _voiceReady = report.VoiceReady;
        _configuredModelName = report.ModelConfigured;
        _serviceHint = report.ServiceHint ?? "";
        _healthSummary = report.SummaryLines.Count == 0
            ? (report.FullyReady ? "✅ 可以发言" : "❌ 暂不可发言")
            : string.Join("\n", report.SummaryLines);
        lock (_installedModelsLock)
        {
            _installedModels = report.InstalledModels?.ToArray() ?? Array.Empty<string>();
        }

        NotifyStatus();
    }

    /// <summary>单元测试可验证的 TTS/Voice 健康语义。</summary>
    internal static void ApplyTtsHealth(
        GptSovitsHealth health,
        string configuredVoice,
        out bool ttsOk,
        out bool voiceReady)
    {
        ttsOk = health.Ok && health.TtsReady;
        if (!health.Ok)
        {
            voiceReady = false;
            return;
        }

        if (health.VoiceReady)
        {
            voiceReady = true;
            return;
        }

        if (string.IsNullOrWhiteSpace(health.Voice))
        {
            voiceReady = true;
            return;
        }

        voiceReady = string.Equals(health.Voice, configuredVoice, StringComparison.OrdinalIgnoreCase);
    }

    public async Task<IReadOnlyList<string>> ListOllamaModelsAsync(CancellationToken ct = default)
    {
        try
        {
            var report = await _health.RefreshAsync(ct);
            ApplyHealthReport(report);
            return report.InstalledModels;
        }
        catch (Exception ex)
        {
            _ollamaOk = false;
            _modelAvailable = false;
            _serviceHint = "Ollama未启动";
            _healthSummary = "❌ Ollama未连接";
            _log.AiWarn($"list_models_fail {ex.GetType().Name}: {ex.Message}");
            NotifyStatus();
            return Array.Empty<string>();
        }
    }

    /// <summary>由 LiveAppHost 在弹幕事件中调用；绝不抛出到宿主。</summary>
    public void TryEnqueueDanmaku(DanmakuItem item, string? roomOwnerNickname, string? loginNickname)
    {
        try
        {
            if (!IsInteractionEnabled() || !Settings.ReplyDanmaku)
            {
                return;
            }

            var contentLen = item?.Content?.Length ?? 0;
            var threshold = Math.Clamp(Settings.ScoreThreshold, 0, 20);
            var scored = AiDanmakuFilter.Evaluate(item!, threshold);
            _log.AiInfo(
                $"AI_DANMAKU_SCORE len={scored.ContentLength} score={scored.Score} threshold={scored.Threshold} enter={(scored.EnterAi ? 1 : 0)} reason={scored.Reason} detail={scored.Detail} nick={item?.Nickname}");

            if (!scored.EnterAi)
            {
                _metrics.NoteFiltered();
                _log.AiInfo(
                    $"AI_DANMAKU_FILTERED reason={scored.Reason} score={scored.Score} threshold={scored.Threshold} user={MaskId(item?.UserId)} nick={item?.Nickname} len={contentLen} enter_ai=0");
                return;
            }

            if (IsSelfHostMessage(item!, roomOwnerNickname, loginNickname))
            {
                _metrics.NoteFiltered();
                _log.AiInfo(
                    $"AI_SELF_MESSAGE_SKIP reason=host_or_login nick={item!.Nickname} user={MaskId(item.UserId)} len={contentLen} score={scored.Score} enter_ai=0");
                return;
            }

            if (_outboundTracker.MatchesTrackedMessageId(item!.MsgId))
            {
                _metrics.NoteFiltered();
                _log.AiInfo(
                    $"AI_SELF_MESSAGE_SKIP reason=outbound_msg_id nick={item.Nickname} len={item.Content.Length} score={scored.Score} enter_ai=0");
                return;
            }

            if (IsDuplicateRecent(item))
            {
                _metrics.NoteFiltered();
                _log.AiInfo(
                    $"AI_DANMAKU_FILTERED reason=duplicate user={MaskId(item.UserId)} len={item.Content.Length} score={scored.Score} enter_ai=0");
                return;
            }

            var content = item.Content.Trim();
            if (!RoomConversationContext.IsNoise(content))
            {
                _roomContext.Add(item.UserId, item.Nickname, content);
            }

            _userContext.AddUserMessage(item.UserId, content);

            var task = new AiSpeechTask
            {
                MsgId = item.MsgId,
                UserId = item.UserId,
                Nickname = item.Nickname,
                Content = content,
                Score = scored.Score,
                ScoreDetail = scored.Detail,
                ReceivedAt = item.Timestamp == default ? DateTime.Now : item.Timestamp,
                EnqueuedAt = DateTime.UtcNow,
                Kind = AiSpeechEventKind.Danmaku,
                Priority = AiSpeechPriority.DanmakuImportant,
                EmotionRequested = Settings.Emotion,
                Speed = Settings.Speed,
                PromptVersion = _prompts.GetVersion()
            };

            _metrics.NoteReceived();
            _log.AiInfo(
                $"AI_DANMAKU_RECEIVED task={task.TaskId} room={_config.Settings.Douyin.WebRid} user={MaskId(task.UserId)} nick={task.Nickname} len={task.Content.Length} score={task.Score} enter_ai=1 detail={task.ScoreDetail}");

            Enqueue(task);
        }
        catch (Exception ex)
        {
            _log.Error("ai_speech", "TryEnqueueDanmaku 异常（已隔离）", ex);
        }
    }

    public void TryEnqueueGift(GiftEvent gift)
    {
        try
        {
            if (!IsInteractionEnabled() || !Settings.ThankGift || gift == null)
            {
                return;
            }

            var count = gift.Count > 0 ? gift.Count : Math.Max(1, gift.RepeatCount);
            _giftBuffer.Add(gift.UserId, gift.Nickname, gift.GiftName, count);
            _log.AiInfo(
                $"AI_GIFT_BUFFER user={MaskId(gift.UserId)} nick={gift.Nickname} gift={gift.GiftName} count={count}");
        }
        catch (Exception ex)
        {
            _log.Error("ai_speech", "TryEnqueueGift 异常（已隔离）", ex);
        }
    }

    public void TryEnqueueMemberJoin(DanmakuItem item)
    {
        try
        {
            if (!IsInteractionEnabled() || !Settings.WelcomeUser || item == null)
            {
                return;
            }

            _welcomeBuffer.Add(item.UserId, item.Nickname);
            _log.AiInfo($"AI_WELCOME_BUFFER user={MaskId(item.UserId)} nick={item.Nickname}");
        }
        catch (Exception ex)
        {
            _log.Error("ai_speech", "TryEnqueueMemberJoin 异常（已隔离）", ex);
        }
    }

    public void TryEnqueueLike(DanmakuItem item)
    {
        try
        {
            if (!IsInteractionEnabled() || !Settings.ThankLike || item == null)
            {
                return;
            }

            var count = 1;
            if (!string.IsNullOrWhiteSpace(item.Content)
                && int.TryParse(item.Content.Trim(), out var parsed)
                && parsed > 0)
            {
                count = parsed;
            }

            _likeBuffer.Add(count);
            _log.AiInfo($"AI_LIKE_BUFFER count={count} user={MaskId(item.UserId)}");
        }
        catch (Exception ex)
        {
            _log.Error("ai_speech", "TryEnqueueLike 异常（已隔离）", ex);
        }
    }

    public void StopCurrentPlayback()
    {
        try
        {
            var cts = Volatile.Read(ref _activePlayCts);
            try { cts?.Cancel(); } catch { /* ignore */ }
            _player.Stop();
            _log.AiInfo("AI_AUDIO_PLAY_CANCEL reason=user_stop");
            if (_phase is AiSpeechPhase.Playing or AiSpeechPhase.Thinking or AiSpeechPhase.Synthesizing
                or AiSpeechPhase.GeneratingGift or AiSpeechPhase.GeneratingWelcome or AiSpeechPhase.GeneratingSummary)
            {
                SetPhase(AiSpeechPhase.Idle);
            }
        }
        catch (Exception ex)
        {
            _log.AiWarn($"stop_play_fail {ex.GetType().Name}: {ex.Message}");
        }
    }

    public async Task<AiSpeechTestResult> TestVoiceAsync(CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        await _executionGate.WaitAsync(ct);
        var playCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Volatile.Write(ref _activePlayCts, playCts);
        try
        {
            ApplyDeviceFromSettings();
            SetPhase(AiSpeechPhase.Synthesizing);
            var text = "你好，现在测试一下我的人工智能语音。";
            var emotion = EmotionPresets.Resolve(Settings.Emotion, AiSpeechEventKind.Danmaku, Settings.Voice);
            _latestEmotion = emotion.EmotionUsed;
            SetLatestSpeed(Settings.Speed);
            _log.AiInfo(
                $"AI_TTS_START task=test_voice emotion={emotion.EmotionUsed} speed={Settings.Speed} refer={(emotion.Configured ? 1 : 0)}");
            var ttsSw = Stopwatch.StartNew();
            var synth = await _tts.SynthesizeAsync(
                text,
                Settings.Voice,
                Settings.Speed,
                emotion.ReferWavPath,
                playCts.Token);
            ttsSw.Stop();
            Interlocked.Exchange(ref _lastTtsMs, ttsSw.ElapsedMilliseconds);
            if (!synth.Success)
            {
                _ttsOk = false;
                _serviceHint = "语音服务不可用";
                _log.AiWarn($"AI_TTS_FAIL task=test_voice status={synth.StatusCode} err={synth.Error}");
                SetPhase(AiSpeechPhase.Idle);
                NotifyStatus();
                return new AiSpeechTestResult
                {
                    Success = false,
                    Error = synth.Error ?? "语音服务不可用",
                    TtsMs = ttsSw.ElapsedMilliseconds,
                    TotalMs = sw.ElapsedMilliseconds
                };
            }

            _log.AiInfo($"AI_TTS_OK task=test_voice tts_ms={ttsSw.ElapsedMilliseconds} bytes={synth.AudioWav.Length}");
            SetPhase(AiSpeechPhase.Playing);
            _log.AiInfo("AI_AUDIO_PLAY_START task=test_voice");
            var playSw = Stopwatch.StartNew();
            await _player.PlayWavAsync(synth.AudioWav, _tempDir, playCts.Token);
            playSw.Stop();
            _log.AiInfo($"AI_AUDIO_PLAY_END task=test_voice play_ms={playSw.ElapsedMilliseconds}");
            _ttsOk = true;
            _serviceHint = "";
            SetPhase(AiSpeechPhase.Idle);
            Interlocked.Exchange(ref _lastTotalMs, sw.ElapsedMilliseconds);
            NotifyStatus();
            return new AiSpeechTestResult
            {
                Success = true,
                Reply = text,
                TtsMs = ttsSw.ElapsedMilliseconds,
                TotalMs = sw.ElapsedMilliseconds
            };
        }
        catch (OperationCanceledException)
        {
            _log.AiInfo("AI_AUDIO_PLAY_CANCEL task=test_voice");
            SetPhase(AiSpeechPhase.Idle);
            return new AiSpeechTestResult { Success = false, Error = "已取消", TotalMs = sw.ElapsedMilliseconds };
        }
        catch (Exception ex)
        {
            _log.Error("ai_speech", "TestVoice 异常", ex);
            _serviceHint = "语音服务不可用";
            SetPhase(AiSpeechPhase.Idle);
            NotifyStatus();
            return new AiSpeechTestResult
            {
                Success = false,
                Error = ex.Message,
                TotalMs = sw.ElapsedMilliseconds
            };
        }
        finally
        {
            Interlocked.CompareExchange(ref _activePlayCts, null, playCts);
            try { playCts.Dispose(); } catch { /* ignore */ }
            SetPhase(AiSpeechPhase.Idle);
            try
            {
                _executionGate.Release();
            }
            catch (ObjectDisposedException)
            {
                // Dispose 与活动任务并发时闸门可能已释放
            }
        }
    }

    public async Task<AiSpeechTestResult> TestAiAsync(CancellationToken ct = default)
    {
        var nick = "测试用户";
        var danmaku = "主播这个功能是你自己做的吗？";
        var task = new AiSpeechTask
        {
            Nickname = nick,
            Content = danmaku,
            UserId = "test-user",
            MsgId = "test-" + Guid.NewGuid().ToString("N"),
            Kind = AiSpeechEventKind.Danmaku,
            Priority = AiSpeechPriority.DanmakuImportant,
            EmotionRequested = Settings.Emotion,
            Speed = Settings.Speed,
            PromptVersion = _prompts.GetVersion()
        };
        _latestNickname = nick;
        _latestContent = danmaku;
        _latestReply = "";
        _latestKind = AiSpeechEventKind.Danmaku;
        NotifyStatus();
        return await ProcessTaskAsync(task, isTest: true, ct);
    }

    private void OnGiftFlushed(GiftMergeBuffer.GiftMergeItem item)
    {
        try
        {
            if (!IsInteractionEnabled() || !Settings.ThankGift)
            {
                return;
            }

            var nick = SpeechNameCleaner.Clean(item.Nickname);
            var giftLabel = item.Count > 1 ? $"{item.GiftName}x{item.Count}" : item.GiftName;
            string? prebuilt = null;
            if (IsTemplateGiftMode())
            {
                var template = string.IsNullOrWhiteSpace(Settings.GiftThankTemplate)
                    ? "感谢 {nickname} 送的 {giftName}，谢谢支持。"
                    : Settings.GiftThankTemplate;
                prebuilt = template
                    .Replace("{nickname}", string.IsNullOrWhiteSpace(nick) ? "朋友" : nick, StringComparison.OrdinalIgnoreCase)
                    .Replace("{giftName}", giftLabel, StringComparison.OrdinalIgnoreCase)
                    .Replace("{count}", item.Count.ToString(), StringComparison.OrdinalIgnoreCase);
            }

            var content = string.IsNullOrWhiteSpace(prebuilt)
                ? $"观众昵称：{(string.IsNullOrWhiteSpace(nick) ? item.Nickname : nick)}\n礼物：{item.GiftName}\n数量：{item.Count}"
                : prebuilt;

            var task = new AiSpeechTask
            {
                UserId = item.UserId,
                Nickname = item.Nickname,
                Content = content,
                Kind = AiSpeechEventKind.Gift,
                Priority = AiSpeechPriority.Gift,
                EmotionRequested = Settings.Emotion,
                Speed = Settings.Speed,
                PrebuiltText = prebuilt,
                PromptVersion = _prompts.GetVersion(),
                ReceivedAt = item.FirstAtUtc.ToLocalTime(),
                EnqueuedAt = DateTime.UtcNow
            };
            _log.AiInfo(
                $"AI_GIFT_FLUSH task={task.TaskId} user={MaskId(item.UserId)} gift={item.GiftName} count={item.Count} mode={(prebuilt != null ? "template" : "ai")}");
            _metrics.NoteReceived();
            Enqueue(task);
        }
        catch (Exception ex)
        {
            _log.Error("ai_speech", "OnGiftFlushed 异常（已隔离）", ex);
        }
    }

    private void OnWelcomeFlushed(WelcomeBatchBuffer.WelcomeBatch batch)
    {
        try
        {
            if (!IsInteractionEnabled() || !Settings.WelcomeUser || batch.Nicknames.Count == 0)
            {
                return;
            }

            var names = string.Join("、", batch.Nicknames);
            var task = new AiSpeechTask
            {
                Nickname = batch.Nicknames[0],
                Content = $"请欢迎这些进房观众：{names}",
                Kind = AiSpeechEventKind.Welcome,
                Priority = AiSpeechPriority.Welcome,
                EmotionRequested = Settings.Emotion,
                Speed = Settings.Speed,
                PromptVersion = _prompts.GetVersion(),
                EnqueuedAt = DateTime.UtcNow
            };
            _log.AiInfo($"AI_WELCOME_FLUSH task={task.TaskId} names={names}");
            _metrics.NoteReceived();
            Enqueue(task);
        }
        catch (Exception ex)
        {
            _log.Error("ai_speech", "OnWelcomeFlushed 异常（已隔离）", ex);
        }
    }

    private void OnLikeFlushed(LikeAccumulateBuffer.LikeBatch batch)
    {
        try
        {
            if (!IsInteractionEnabled() || !Settings.ThankLike || batch.Count <= 0)
            {
                return;
            }

            var task = new AiSpeechTask
            {
                Nickname = "观众",
                Content = $"刚才累计约 {batch.Count} 个点赞，请口头感谢一下。",
                Kind = AiSpeechEventKind.Like,
                Priority = AiSpeechPriority.Like,
                EmotionRequested = Settings.Emotion,
                Speed = Settings.Speed,
                PromptVersion = _prompts.GetVersion(),
                EnqueuedAt = DateTime.UtcNow
            };
            _log.AiInfo($"AI_LIKE_FLUSH task={task.TaskId} count={batch.Count}");
            _metrics.NoteReceived();
            Enqueue(task);
        }
        catch (Exception ex)
        {
            _log.Error("ai_speech", "OnLikeFlushed 异常（已隔离）", ex);
        }
    }

    private void Enqueue(AiSpeechTask task)
    {
        ApplySchedulerLimits();
        _scheduler.Enqueue(task);
        var q = _scheduler.Count;
        _metrics.NoteQueueSize(q);
        _log.AiInfo(
            $"AI_QUEUE_ADD task={task.TaskId} kind={task.Kind} priority={task.Priority} queue={q}/{_scheduler.MaxSize} nick={task.Nickname}");
        _latestNickname = task.Nickname;
        _latestContent = task.Content;
        _latestKind = task.Kind;
        NotifyStatus();
    }

    private async Task MetricsSnapshotLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromMinutes(5), ct);
                if (Volatile.Read(ref _disposed) != 0)
                {
                    break;
                }

                var line = _metrics.FormatSnapshotLog(_scheduler.Count, _scheduler.MaxSize);
                _log.AiInfo(line);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                try { _log.AiWarn($"AI_METRIC_SNAPSHOT_FAIL {ex.GetType().Name}: {ex.Message}"); } catch { /* ignore */ }
            }
        }
    }

    private async Task WorkerLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (!IsInteractionEnabled())
                {
                    await Task.Delay(400, ct);
                    continue;
                }

                MaybeEnqueueRoomSummary();

                if (!_scheduler.TryDequeue(out var next, out var expired) || next == null)
                {
                    if (expired > 0)
                    {
                        _metrics.NoteSkip("EXPIRED");
                        for (var i = 1; i < expired; i++) _metrics.NoteSkip("EXPIRED");
                    }

                    await Task.Delay(200, ct);
                    continue;
                }

                if (expired > 0)
                {
                    for (var i = 0; i < expired; i++) _metrics.NoteSkip("EXPIRED");
                }

                var wait = ComputeGapWait(next.Kind);
                if (wait > TimeSpan.Zero)
                {
                    await Task.Delay(wait, ct);
                }

                await ProcessTaskAsync(next, isTest: false, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _log.Error("ai_speech", "WorkerLoop 异常（已隔离）", ex);
                SetPhase(AiSpeechPhase.Idle);
                try { await Task.Delay(1000, ct); } catch { /* ignore */ }
            }
        }
    }

    private TimeSpan ComputeGapWait(AiSpeechEventKind kind)
    {
        if (_lastPlayCompletedUtc == DateTime.MinValue)
        {
            return TimeSpan.Zero;
        }

        var globalGap = Math.Clamp(Settings.GlobalMinGapSeconds, 0, 120);
        var required = globalGap;
        if (kind == AiSpeechEventKind.Danmaku)
        {
            var reply = Settings.ReplyIntervalSeconds > 0
                ? Settings.ReplyIntervalSeconds
                : Settings.MinIntervalSeconds;
            reply = Math.Clamp(reply, 3, 60);
            required = Math.Max(required, reply);
        }

        var due = _lastPlayCompletedUtc.AddSeconds(required);
        var wait = due - DateTime.UtcNow;
        return wait > TimeSpan.Zero ? wait : TimeSpan.Zero;
    }

    private void MaybeEnqueueRoomSummary()
    {
        if (!Settings.AutoRoomSummary || !IsInteractionEnabled())
        {
            return;
        }

        var min = Math.Clamp(Settings.SummaryMinDanmaku, 1, 100);
        if (_roomContext.Count < min)
        {
            return;
        }

        var interval = Math.Clamp(Settings.SummaryIntervalSeconds, 30, 900);
        if (_lastSummaryEnqueueUtc != DateTime.MinValue
            && DateTime.UtcNow - _lastSummaryEnqueueUtc < TimeSpan.FromSeconds(interval))
        {
            return;
        }

        var peek = _scheduler.Peek();
        if (peek?.Kind == AiSpeechEventKind.Summary)
        {
            return;
        }

        _lastSummaryEnqueueUtc = DateTime.UtcNow;
        var task = new AiSpeechTask
        {
            Nickname = "系统",
            Content = "请根据最近直播间弹幕氛围说一句短总结。",
            Kind = AiSpeechEventKind.Summary,
            Priority = AiSpeechPriority.Summary,
            EmotionRequested = Settings.Emotion,
            Speed = Settings.Speed,
            PromptVersion = _prompts.GetVersion(),
            EnqueuedAt = DateTime.UtcNow
        };
        _log.AiInfo($"AI_SUMMARY_ENQUEUE task={task.TaskId} room_msgs={_roomContext.Count}");
        Enqueue(task);
    }

    private async Task<AiSpeechTestResult> ProcessTaskAsync(AiSpeechTask task, bool isTest, CancellationToken ct)
    {
        var totalSw = Stopwatch.StartNew();
        var queueWaitMs = (long)(DateTime.UtcNow - task.EnqueuedAt).TotalMilliseconds;
        if (queueWaitMs < 0)
        {
            queueWaitMs = 0;
        }

        _latestNickname = task.Nickname;
        _latestContent = task.Content;
        _latestKind = task.Kind;
        await _executionGate.WaitAsync(ct);
        var playCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Volatile.Write(ref _activePlayCts, playCts);
        var playCt = playCts.Token;

        long ollamaMs = 0;
        try
        {
            SetPhase(PhaseForKind(task.Kind));
            var model = string.IsNullOrWhiteSpace(Settings.Model)
                ? AiSpeechModelsCatalog.DefaultModel
                : Settings.Model.Trim();

            string cleaned;
            if (!string.IsNullOrWhiteSpace(task.PrebuiltText))
            {
                _log.AiInfo($"AI_GENERATE_SKIP task={task.TaskId} kind={task.Kind} reason=prebuilt");
                var sanitizedPre = ModelOutputSanitizer.Sanitize(task.PrebuiltText);
                if (!sanitizedPre.Ok)
                {
                    NoteSanitizeReject(task, sanitizedPre.RejectReason);
                    SetPhase(AiSpeechPhase.Idle);
                    NotifyStatus();
                    return FailResult(task, $"输出被拒绝：{sanitizedPre.RejectReason}", 0, totalSw.ElapsedMilliseconds);
                }

                cleaned = SpeechTextCleaner.Clean(sanitizedPre.Text, Settings.MaxReplyLength);
                if (string.IsNullOrWhiteSpace(cleaned))
                {
                    _metrics.NoteSkip("EMPTY_REPLY");
                    _log.AiWarn($"AI_GENERATE_FAIL task={task.TaskId} err=empty_after_clean");
                    SetPhase(AiSpeechPhase.Idle);
                    return FailResult(task, "预置文本清洗后为空", 0, totalSw.ElapsedMilliseconds);
                }

                _metrics.NoteGenerated(0, queueWaitMs);
            }
            else
            {
                var userCtx = _userContext.GetMessages(task.UserId);
                var roomCtx = _roomContext.AsChatMessages();
                var summary = _roomSummary.Summary;
                var currentMsg = BuildCurrentUserMessage(task);
                var messages = ContextPromptBuilder.Build(
                    Settings,
                    _prompts,
                    task.Kind,
                    currentMsg,
                    userCtx,
                    roomCtx,
                    string.IsNullOrWhiteSpace(summary) ? null : summary);

                _log.AiInfo(
                    $"AI_GENERATE_START task={task.TaskId} kind={task.Kind} model={model} score={task.Score} len={task.Content.Length} ctx_user={userCtx.Count} ctx_room={roomCtx.Count} prompt_ver={_prompts.GetVersion()}");

                var ollamaSw = Stopwatch.StartNew();
                var gen = await GenerateChatWithRetryAsync(model, messages, playCt);
                ollamaSw.Stop();
                ollamaMs = ollamaSw.ElapsedMilliseconds;
                Interlocked.Exchange(ref _lastOllamaMs, ollamaMs);

                if (!gen.Success)
                {
                    _metrics.NoteOllamaError(gen.Error);
                    if (gen.ResourceError)
                    {
                        _log.AiWarn($"AI_OLLAMA_RESOURCE_ERROR task={task.TaskId} status={gen.StatusCode} err={gen.Error}");
                    }
                    else
                    {
                        _log.AiWarn($"AI_GENERATE_FAIL task={task.TaskId} status={gen.StatusCode} err={gen.Error}");
                    }

                    _ollamaOk = false;
                    _serviceHint = "AI服务异常";
                    _healthSummary = "❌ AI服务异常\n⏳ 将自动重试恢复";
                    // 触发一次非阻塞恢复检查（运行中循环也会继续）
                    _ = SafeRecoverHealthAsync();
                    SetPhase(AiSpeechPhase.Idle);
                    NotifyStatus();
                    return new AiSpeechTestResult
                    {
                        Success = false,
                        Nickname = task.Nickname,
                        Danmaku = task.Content,
                        Error = gen.Error ?? "AI服务异常",
                        OllamaMs = ollamaMs,
                        TotalMs = totalSw.ElapsedMilliseconds
                    };
                }

                _ollamaOk = true;
                var sanitized = ModelOutputSanitizer.Sanitize(gen.Text);
                if (!sanitized.Ok)
                {
                    NoteSanitizeReject(task, sanitized.RejectReason);
                    SetPhase(AiSpeechPhase.Idle);
                    NotifyStatus();
                    return FailResult(task, $"输出被拒绝：{sanitized.RejectReason}", ollamaMs, totalSw.ElapsedMilliseconds);
                }

                cleaned = SpeechTextCleaner.Clean(sanitized.Text, Settings.MaxReplyLength);
                if (string.IsNullOrWhiteSpace(cleaned))
                {
                    _metrics.NoteSkip("EMPTY_REPLY");
                    _log.AiWarn($"AI_GENERATE_FAIL task={task.TaskId} err=empty_after_clean");
                    SetPhase(AiSpeechPhase.Idle);
                    return FailResult(task, "AI 回复清洗后为空", ollamaMs, totalSw.ElapsedMilliseconds);
                }

                _metrics.NoteGenerated(ollamaMs, queueWaitMs);
                _log.AiInfo(
                    $"AI_GENERATE_OK task={task.TaskId} kind={task.Kind} ollama_ms={ollamaMs} reply_len={cleaned.Length} ai_reply_ms={ollamaMs}");
            }

            if (_replyDupGuard.IsDuplicate(cleaned))
            {
                _metrics.NoteSkip("DUPLICATE_REPLY");
                _log.AiInfo($"AI_DUPLICATE_SKIP task={task.TaskId} kind={task.Kind} reply_len={cleaned.Length}");
                SetPhase(AiSpeechPhase.Idle);
                NotifyStatus();
                return FailResult(task, "重复回复已跳过", ollamaMs, totalSw.ElapsedMilliseconds);
            }

            _latestReply = cleaned;

            if (task.Kind == AiSpeechEventKind.Danmaku && !string.IsNullOrWhiteSpace(task.UserId))
            {
                _userContext.AddHostReply(task.UserId, cleaned);
                _roomSummary.UpdateFromMessages(_roomContext.GetRecent());
            }
            else if (task.Kind == AiSpeechEventKind.Summary)
            {
                _roomSummary.SetFromModel(cleaned);
            }

            var emotion = EmotionPresets.Resolve(
                string.IsNullOrWhiteSpace(task.EmotionRequested) ? Settings.Emotion : task.EmotionRequested,
                task.Kind,
                Settings.Voice);
            task.EmotionUsed = emotion.EmotionUsed;
            _latestEmotion = emotion.EmotionUsed;
            var speed = task.Speed > 0 ? task.Speed : Settings.Speed;
            if (speed <= 0)
            {
                speed = 1.0;
            }

            SetLatestSpeed(speed);
            _log.AiInfo(
                $"AI_EMOTION task={task.TaskId} used={emotion.EmotionUsed} configured={(emotion.Configured ? 1 : 0)} speed={speed}");

            SetPhase(AiSpeechPhase.Synthesizing);
            _log.AiInfo($"AI_TTS_START task={task.TaskId} emotion={emotion.EmotionUsed} speed={speed}");
            var ttsSw = Stopwatch.StartNew();
            var synth = await SynthesizeWithRetryAsync(cleaned, speed, emotion.ReferWavPath, playCt);
            ttsSw.Stop();
            Interlocked.Exchange(ref _lastTtsMs, ttsSw.ElapsedMilliseconds);

            if (!synth.Success)
            {
                _ttsOk = false;
                _serviceHint = "AI服务异常";
                _healthSummary = "❌ TTS不可用\n⏳ 将自动重试恢复";
                _metrics.NoteTtsFailed(synth.Error);
                _log.AiWarn($"AI_TTS_FAIL task={task.TaskId} status={synth.StatusCode} err={synth.Error}");
                _ = SafeRecoverHealthAsync();
                SetPhase(AiSpeechPhase.Idle);
                NotifyStatus();
                return new AiSpeechTestResult
                {
                    Success = false,
                    Nickname = task.Nickname,
                    Danmaku = task.Content,
                    Reply = cleaned,
                    Error = synth.Error ?? "语音服务不可用",
                    OllamaMs = ollamaMs,
                    TtsMs = ttsSw.ElapsedMilliseconds,
                    TotalMs = totalSw.ElapsedMilliseconds
                };
            }

            _ttsOk = true;
            _metrics.NoteTtsSuccess(ttsSw.ElapsedMilliseconds);
            _log.AiInfo(
                $"AI_TTS_OK task={task.TaskId} tts_ms={ttsSw.ElapsedMilliseconds} bytes={synth.AudioWav.Length}");

            SetPhase(AiSpeechPhase.Playing);
            ApplyDeviceFromSettings();
            _log.AiInfo($"AI_AUDIO_PLAY_START task={task.TaskId}");
            var playSw = Stopwatch.StartNew();
            try
            {
                await _player.PlayWavAsync(synth.AudioWav, _tempDir, playCt);
            }
            catch (OperationCanceledException)
            {
                _metrics.NotePlayFailed("cancelled");
                throw;
            }
            catch (Exception playEx)
            {
                _metrics.NotePlayFailed(playEx.Message);
                _log.AiWarn($"AI_AUDIO_PLAY_FAIL task={task.TaskId} err={playEx.Message}");
                SetPhase(AiSpeechPhase.Idle);
                NotifyStatus();
                return new AiSpeechTestResult
                {
                    Success = false,
                    Nickname = task.Nickname,
                    Danmaku = task.Content,
                    Reply = cleaned,
                    Error = playEx.Message,
                    OllamaMs = ollamaMs,
                    TtsMs = ttsSw.ElapsedMilliseconds,
                    TotalMs = totalSw.ElapsedMilliseconds
                };
            }

            playSw.Stop();
            _metrics.NotePlaySuccess(totalSw.ElapsedMilliseconds);
            _log.AiInfo(
                $"AI_SPEECH taskId={task.TaskId} kind={task.Kind} sourceMsgId={task.MsgId} queueWait={queueWaitMs} " +
                $"generateMs={ollamaMs} ttsMs={ttsSw.ElapsedMilliseconds} " +
                $"playMs={playSw.ElapsedMilliseconds} emotion={emotion.EmotionUsed} speed={speed} phase=Idle result=ok");

            _lastPlayCompletedUtc = DateTime.UtcNow;
            Interlocked.Exchange(ref _lastTotalMs, totalSw.ElapsedMilliseconds);
            if (!string.IsNullOrWhiteSpace(_serviceHint)
                && (_serviceHint.Contains("不可用", StringComparison.Ordinal)
                    || _serviceHint.Contains("未就绪", StringComparison.Ordinal)))
            {
                _serviceHint = "";
            }

            SetPhase(AiSpeechPhase.Idle);
            NotifyStatus();
            return new AiSpeechTestResult
            {
                Success = true,
                Nickname = task.Nickname,
                Danmaku = task.Content,
                Reply = cleaned,
                OllamaMs = ollamaMs,
                TtsMs = ttsSw.ElapsedMilliseconds,
                TotalMs = totalSw.ElapsedMilliseconds
            };
        }
        catch (OperationCanceledException)
        {
            _log.AiInfo(
                $"AI_SPEECH taskId={task.TaskId} sourceMsgId={task.MsgId} phase=Idle result=cancel cancelReason=token test={isTest}");
            SetPhase(AiSpeechPhase.Idle);
            NotifyStatus();
            return new AiSpeechTestResult
            {
                Success = false,
                Nickname = task.Nickname,
                Danmaku = task.Content,
                Reply = _latestReply,
                Error = "已取消",
                TotalMs = totalSw.ElapsedMilliseconds
            };
        }
        catch (Exception ex)
        {
            _log.Error("ai_speech", $"ProcessTask 异常 task={task.TaskId}", ex);
            SetPhase(AiSpeechPhase.Idle);
            NotifyStatus();
            return new AiSpeechTestResult
            {
                Success = false,
                Nickname = task.Nickname,
                Danmaku = task.Content,
                Error = ex.Message,
                TotalMs = totalSw.ElapsedMilliseconds
            };
        }
        finally
        {
            Interlocked.CompareExchange(ref _activePlayCts, null, playCts);
            try { playCts.Dispose(); } catch { /* ignore */ }
            SetPhase(AiSpeechPhase.Idle);
            try
            {
                _executionGate.Release();
            }
            catch (ObjectDisposedException)
            {
                // Dispose 与活动任务并发时闸门可能已释放
            }
        }
    }

    private void NoteSanitizeReject(AiSpeechTask task, string? reason)
    {
        var r = (reason ?? "EMPTY").Trim().ToUpperInvariant();
        if (r == "SKIP")
        {
            _metrics.NoteSkip("SKIP");
            _log.AiInfo($"AI_SKIP_BY_MODEL task={task.TaskId} kind={task.Kind}");
            return;
        }

        _metrics.NoteSkip(r);
        _log.AiWarn($"AI_OUTPUT_REJECTED reason={reason} task={task.TaskId} kind={task.Kind}");
    }

    private async Task SafeRecoverHealthAsync()
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(2));
            var report = await _health.CheckOnceAsync("task_fail_recover", CancellationToken.None);
            ApplyHealthReport(report);
        }
        catch
        {
            // ignore — 运行恢复循环会继续
        }
    }

    private static string BuildCurrentUserMessage(AiSpeechTask task) => task.Kind switch
    {
        AiSpeechEventKind.Gift => task.Content,
        AiSpeechEventKind.Welcome => task.Content,
        AiSpeechEventKind.Like => task.Content,
        AiSpeechEventKind.Summary => task.Content,
        _ => $"观众昵称：{task.Nickname}\n观众说：{task.Content}"
    };

    private static AiSpeechPhase PhaseForKind(AiSpeechEventKind kind) => kind switch
    {
        AiSpeechEventKind.Gift => AiSpeechPhase.GeneratingGift,
        AiSpeechEventKind.Welcome => AiSpeechPhase.GeneratingWelcome,
        AiSpeechEventKind.Summary => AiSpeechPhase.GeneratingSummary,
        _ => AiSpeechPhase.Thinking
    };

    private static AiSpeechTestResult FailResult(AiSpeechTask task, string error, long ollamaMs, long totalMs)
        => new()
        {
            Success = false,
            Nickname = task.Nickname,
            Danmaku = task.Content,
            Error = error,
            OllamaMs = ollamaMs,
            TotalMs = totalMs
        };

    private async Task<OllamaGenerateResult> GenerateChatWithRetryAsync(
        string model,
        IReadOnlyList<(string Role, string Content)> messages,
        CancellationToken ct)
    {
        var first = await _ollama.GenerateChatAsync(model, messages, ct);
        if (first.Success || first.ResourceError || ct.IsCancellationRequested)
        {
            return first;
        }

        await Task.Delay(800, ct);
        return await _ollama.GenerateChatAsync(model, messages, ct);
    }

    private async Task<GptSovitsSynthesizeResult> SynthesizeWithRetryAsync(
        string text,
        double speedFactor,
        string? referWav,
        CancellationToken ct)
    {
        var first = await _tts.SynthesizeAsync(text, Settings.Voice, speedFactor, referWav, ct);
        if (first.Success || ct.IsCancellationRequested)
        {
            return first;
        }

        if (first.StatusCode is 409 or >= 500 and < 600 or null)
        {
            await Task.Delay(800, ct);
            return await _tts.SynthesizeAsync(text, Settings.Voice, speedFactor, referWav, ct);
        }

        return first;
    }

    private bool IsInteractionEnabled() => Settings.Enabled || Settings.TestMode;

    private bool IsTemplateGiftMode()
        => string.Equals(Settings.GiftThankMode?.Trim(), "template", StringComparison.OrdinalIgnoreCase);

    private void ApplySchedulerLimits()
    {
        _scheduler.MaxSize = Math.Clamp(Settings.MaxQueueSize, 1, 20);
        _scheduler.MaxAgeSeconds = Math.Clamp(Settings.MaxAgeSeconds, 5, 120);
        _scheduler.MaxConsecutiveSameKind = Math.Clamp(
            Settings.SameKindBurstLimit > 0 ? Settings.SameKindBurstLimit : 3, 1, 20);
    }

    private void ApplyBufferIntervals()
    {
        _giftBuffer.MergeSeconds = Settings.GiftMergeSeconds;
        _welcomeBuffer.IntervalSeconds = Settings.WelcomeIntervalSeconds;
        _welcomeBuffer.MaxNames = Settings.WelcomeMaxNames;
        _likeBuffer.IntervalSeconds = Settings.LikeIntervalSeconds;
    }

    private static void ClampSettings(AiSpeechSettings s)
    {
        s.MinIntervalSeconds = Math.Clamp(s.MinIntervalSeconds, 3, 60);
        s.ReplyIntervalSeconds = Math.Clamp(s.ReplyIntervalSeconds <= 0 ? s.MinIntervalSeconds : s.ReplyIntervalSeconds, 3, 60);
        s.MaxQueueSize = Math.Clamp(s.MaxQueueSize, 1, 20);
        s.MaxReplyLength = Math.Clamp(s.MaxReplyLength, 10, 120);
        s.OllamaTimeoutSeconds = Math.Clamp(s.OllamaTimeoutSeconds, 10, 120);
        s.TtsTimeoutSeconds = Math.Clamp(s.TtsTimeoutSeconds, 10, 120);
        s.ScoreThreshold = Math.Clamp(s.ScoreThreshold, 0, 20);
        s.MaxAgeSeconds = Math.Clamp(s.MaxAgeSeconds, 5, 120);
        s.GiftMergeSeconds = Math.Clamp(s.GiftMergeSeconds, 1, 30);
        s.WelcomeIntervalSeconds = Math.Clamp(s.WelcomeIntervalSeconds, 5, 300);
        s.LikeIntervalSeconds = Math.Max(20, Math.Clamp(s.LikeIntervalSeconds, 20, 600));
        s.SummaryIntervalSeconds = Math.Clamp(s.SummaryIntervalSeconds, 30, 900);
        s.GlobalMinGapSeconds = Math.Clamp(s.GlobalMinGapSeconds, 0, 120);
        s.WelcomeMaxNames = Math.Clamp(s.WelcomeMaxNames, 1, 10);
        s.UserContextCount = Math.Clamp(s.UserContextCount, 1, 40);
        s.HostReplyContextCount = Math.Clamp(s.HostReplyContextCount, 1, 20);
        s.RoomContextCount = Math.Clamp(s.RoomContextCount, 5, 100);
        s.ContextTtlMinutes = Math.Clamp(s.ContextTtlMinutes, 1, 180);
        s.RoomWindowSeconds = Math.Clamp(s.RoomWindowSeconds, 10, 600);
        s.MaxTrackedUsers = Math.Clamp(s.MaxTrackedUsers <= 0 ? 2000 : s.MaxTrackedUsers, 16, 50_000);
        s.SameKindBurstLimit = Math.Clamp(s.SameKindBurstLimit <= 0 ? 3 : s.SameKindBurstLimit, 1, 20);
        s.SummaryMinDanmaku = Math.Clamp(s.SummaryMinDanmaku, 1, 100);
        if (s.Speed <= 0 || s.Speed > 3 || double.IsNaN(s.Speed) || double.IsInfinity(s.Speed))
        {
            s.Speed = 1.0;
        }
        else
        {
            s.Speed = Math.Clamp(s.Speed, 0.5, 2.0);
        }

        if (string.IsNullOrWhiteSpace(s.Model))
        {
            s.Model = AiSpeechModelsCatalog.DefaultModel;
        }

        if (string.IsNullOrWhiteSpace(s.ContextMode))
        {
            s.ContextMode = "auto";
        }

        if (string.IsNullOrWhiteSpace(s.Emotion))
        {
            s.Emotion = "auto";
        }

        if (string.IsNullOrWhiteSpace(s.GiftThankMode))
        {
            s.GiftThankMode = "ai";
        }
    }

    private static void SyncReplyInterval(AiSpeechSettings s)
    {
        // 双向同步：以 ReplyIntervalSeconds 为准（已 clamp）
        s.MinIntervalSeconds = s.ReplyIntervalSeconds;
    }

    private bool IsSelfHostMessage(DanmakuItem item, string? roomOwnerNickname, string? loginNickname)
    {
        var nick = item.Nickname?.Trim() ?? "";
        if (nick.Length == 0)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(loginNickname)
            && !loginNickname.Equals("-", StringComparison.Ordinal)
            && nick.Equals(loginNickname.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(roomOwnerNickname)
            && !roomOwnerNickname.Equals("-", StringComparison.Ordinal)
            && nick.Equals(roomOwnerNickname.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    private bool IsDuplicateRecent(DanmakuItem item)
    {
        var key = $"{item.UserId}|{OutboundReplyTracker.NormalizeContent(item.Content)}";
        var now = DateTime.UtcNow;
        lock (_recentContentLock)
        {
            PurgeMap(_recentContents, now, TimeSpan.FromSeconds(25));
            if (_recentContents.ContainsKey(key))
            {
                return true;
            }

            _recentContents[key] = now;
            return false;
        }
    }

    private static bool LooksLikeModelMissing(string? error, string model)
    {
        if (string.IsNullOrWhiteSpace(error))
        {
            return false;
        }

        var e = error.ToLowerInvariant();
        return e.Contains("not found", StringComparison.Ordinal)
               || e.Contains("no such model", StringComparison.Ordinal)
               || e.Contains("model '" + model.ToLowerInvariant() + "' not found", StringComparison.Ordinal)
               || (e.Contains("404") && e.Contains("model", StringComparison.Ordinal));
    }

    private static string TrimHint(string? text)
    {
        text = (text ?? "").Trim();
        if (text.Length <= 48)
        {
            return text;
        }

        return text[..45] + "...";
    }

    private static void PurgeMap(Dictionary<string, DateTime> map, DateTime now, TimeSpan ttl)
    {
        foreach (var key in map.Keys.ToList())
        {
            if (now - map[key] > ttl)
            {
                map.Remove(key);
            }
        }
    }

    private void SetPhase(AiSpeechPhase phase)
    {
        _phase = phase;
        NotifyStatus();
    }

    private void NotifyStatus()
    {
        try { StatusChanged?.Invoke(); } catch { /* ignore */ }
    }

    private static string MaskId(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return "-";
        }

        id = id.Trim();
        return id.Length <= 4 ? id : id[..2] + "***" + id[^2..];
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try { _cts.Cancel(); } catch { /* ignore */ }
        try { Volatile.Read(ref _activePlayCts)?.Cancel(); } catch { /* ignore */ }
        try { _player.Stop(); } catch { /* ignore */ }

        try
        {
            if (_worker != null)
            {
                _ = _worker.Wait(TimeSpan.FromSeconds(5));
            }
        }
        catch
        {
            // ignore
        }

        try
        {
            if (_metricsLoop != null)
            {
                _ = _metricsLoop.Wait(TimeSpan.FromSeconds(2));
            }
        }
        catch
        {
            // ignore
        }

        try
        {
            var line = _metrics.FormatSnapshotLog(_scheduler.Count, _scheduler.MaxSize);
            _log.AiInfo(line + " final=1");
        }
        catch
        {
            // ignore
        }

        try { _health.Updated -= OnHealthUpdated; } catch { /* ignore */ }
        try { _giftBuffer.Flushed -= OnGiftFlushed; } catch { /* ignore */ }
        try { _welcomeBuffer.Flushed -= OnWelcomeFlushed; } catch { /* ignore */ }
        try { _likeBuffer.Flushed -= OnLikeFlushed; } catch { /* ignore */ }
        try { _giftBuffer.Dispose(); } catch { /* ignore */ }
        try { _welcomeBuffer.Dispose(); } catch { /* ignore */ }
        try { _likeBuffer.Dispose(); } catch { /* ignore */ }
        try { _prompts.Dispose(); } catch { /* ignore */ }
        try { _scheduler.Clear(); } catch { /* ignore */ }

        try { _player.Dispose(); } catch { /* ignore */ }
        try { _executionGate.Dispose(); } catch { /* ignore */ }
        try { _ollama.Dispose(); } catch { /* ignore */ }
        try { _tts.Dispose(); } catch { /* ignore */ }
        try { _cts.Dispose(); } catch { /* ignore */ }

        try { AiSpeechPlayer.CleanupTempDirectory(_tempDir); } catch { /* ignore */ }
    }
}
