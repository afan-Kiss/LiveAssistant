using System.Diagnostics;
using LiveAssistant.Config;
using LiveAssistant.Models;

namespace LiveAssistant.Services.AiSpeech;

/// <summary>
/// AI 语音互动协调器：单队列、节流、过滤、Ollama→TTS→播放。
/// 与点歌/礼物完全隔离，任何失败不影响直播主链路。
/// </summary>
public sealed class AiSpeechCoordinator : IDisposable
{
    /// <summary>内置回退人格；运行时优先读 Config/ai_personality.txt。</summary>
    public static string DefaultSystemPrompt => AiPersonalityLoader.DefaultPersonalityText.Trim();

    private readonly ConfigManager _config;
    private readonly LogService _log;
    private readonly OutboundReplyTracker _outboundTracker;
    private readonly OllamaClient _ollama;
    private readonly GptSovitsClient _tts;
    private readonly AiSpeechPlayer _player;
    private readonly string _tempDir;
    private readonly object _queueLock = new();
    private readonly LinkedList<AiSpeechTask> _queue = new();
    private readonly object _recentContentLock = new();
    private readonly Dictionary<string, DateTime> _recentContents = new(StringComparer.Ordinal);
    private readonly object _aiReplyLock = new();
    private readonly Dictionary<string, DateTime> _recentAiReplies = new(StringComparer.Ordinal);

    private CancellationTokenSource _cts = new();
    private Task? _worker;
    private int _disposed;
    private volatile AiSpeechPhase _phase = AiSpeechPhase.Idle;
    private volatile string _latestNickname = "";
    private volatile string _latestContent = "";
    private volatile string _latestReply = "";
    private volatile string _serviceHint = "";
    private volatile bool _ollamaOk;
    private volatile bool _ttsOk;
    private volatile bool _voiceReady = true;
    private readonly SemaphoreSlim _executionGate = new(1, 1);
    private CancellationTokenSource? _activePlayCts;
    private long _lastOllamaMs;
    private long _lastTtsMs;
    private long _lastTotalMs;
    private DateTime _lastPlayCompletedUtc = DateTime.MinValue;

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
        ApplyDeviceFromSettings();
        _worker = Task.Run(() => WorkerLoopAsync(_cts.Token));
        _ = RefreshHealthAsync();
    }

    public AiSpeechSettings Settings => _config.Settings.AiSpeech;

    public AiSpeechStatusSnapshot GetStatus()
    {
        int q;
        lock (_queueLock)
        {
            q = _queue.Count;
        }

        return new AiSpeechStatusSnapshot
        {
            Enabled = Settings.Enabled || Settings.TestMode,
            TestMode = Settings.TestMode,
            Phase = _phase,
            PhaseText = PhaseToText(_phase),
            QueueCount = q,
            MaxQueueSize = Math.Clamp(Settings.MaxQueueSize, 1, 20),
            LatestNickname = _latestNickname,
            LatestContent = _latestContent,
            LatestReply = _latestReply,
            ServiceHint = _serviceHint,
            OllamaOk = _ollamaOk,
            TtsOk = _ttsOk,
            VoiceReady = _voiceReady,
            VoiceName = string.IsNullOrWhiteSpace(Settings.Voice) ? "my_voice" : Settings.Voice,
            LastOllamaMs = Interlocked.Read(ref _lastOllamaMs),
            LastTtsMs = Interlocked.Read(ref _lastTtsMs),
            LastTotalMs = Interlocked.Read(ref _lastTotalMs)
        };
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
        Settings.MinIntervalSeconds = Math.Clamp(Settings.MinIntervalSeconds, 3, 60);
        Settings.MaxQueueSize = Math.Clamp(Settings.MaxQueueSize, 1, 20);
        Settings.MaxReplyLength = Math.Clamp(Settings.MaxReplyLength, 10, 120);
        Settings.OllamaTimeoutSeconds = Math.Clamp(Settings.OllamaTimeoutSeconds, 10, 120);
        Settings.TtsTimeoutSeconds = Math.Clamp(Settings.TtsTimeoutSeconds, 10, 120);
        Settings.ScoreThreshold = Math.Clamp(Settings.ScoreThreshold, 0, 20);
        if (string.IsNullOrWhiteSpace(Settings.Model))
        {
            Settings.Model = AiSpeechModelsCatalog.DefaultModel;
        }

        AiPersonalityLoader.EnsureDefaultFileExists();
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
            _ollama.Configure(Settings.OllamaUrl, TimeSpan.FromSeconds(Settings.OllamaTimeoutSeconds));
            _tts.Configure(Settings.TtsUrl, TimeSpan.FromSeconds(Settings.TtsTimeoutSeconds));
            var ollamaTask = _ollama.HealthAsync(ct);
            var ttsTask = _tts.HealthAsync(ct);
            await Task.WhenAll(ollamaTask, ttsTask);
            _ollamaOk = await ollamaTask;
            var health = await ttsTask;
            ApplyTtsHealth(health, Settings.Voice, out var ttsOk, out var voiceReady);
            _ttsOk = ttsOk;
            _voiceReady = voiceReady;
            if (!_ollamaOk && !_ttsOk)
            {
                _serviceHint = "AI模型不可用 / 语音服务不可用";
            }
            else if (!_ollamaOk)
            {
                _serviceHint = "AI模型不可用";
            }
            else if (!_ttsOk)
            {
                _serviceHint = "语音服务不可用";
            }
            else if (!_voiceReady)
            {
                _serviceHint = "声音未就绪";
            }
            else
            {
                _serviceHint = "";
            }
        }
        catch (Exception ex)
        {
            _ollamaOk = false;
            _ttsOk = false;
            _voiceReady = false;
            _serviceHint = "健康检查失败";
            _log.AiWarn($"health_fail {ex.GetType().Name}: {ex.Message}");
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
            _ollama.Configure(Settings.OllamaUrl, TimeSpan.FromSeconds(Settings.OllamaTimeoutSeconds));
            var models = await _ollama.ListModelsAsync(ct);
            _ollamaOk = true;
            if (string.IsNullOrWhiteSpace(_serviceHint) || _serviceHint.Contains("AI模型", StringComparison.Ordinal))
            {
                await RefreshHealthAsync(ct);
            }

            return models;
        }
        catch (Exception ex)
        {
            _ollamaOk = false;
            _serviceHint = "AI模型不可用";
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
            if (!Settings.Enabled && !Settings.TestMode)
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
                _log.AiInfo(
                    $"AI_DANMAKU_FILTERED reason={scored.Reason} score={scored.Score} threshold={scored.Threshold} user={MaskId(item?.UserId)} nick={item?.Nickname} len={contentLen} enter_ai=0");
                return;
            }

            if (IsSelfHostMessage(item!, roomOwnerNickname, loginNickname))
            {
                _log.AiInfo(
                    $"AI_SELF_MESSAGE_SKIP reason=host_or_login nick={item!.Nickname} user={MaskId(item.UserId)} len={contentLen} score={scored.Score} enter_ai=0");
                return;
            }

            if (_outboundTracker.MatchesTrackedMessageId(item!.MsgId))
            {
                _log.AiInfo(
                    $"AI_SELF_MESSAGE_SKIP reason=outbound_msg_id nick={item.Nickname} len={item.Content.Length} score={scored.Score} enter_ai=0");
                return;
            }

            if (IsDuplicateRecent(item))
            {
                _log.AiInfo(
                    $"AI_DANMAKU_FILTERED reason=duplicate user={MaskId(item.UserId)} len={item.Content.Length} score={scored.Score} enter_ai=0");
                return;
            }

            var task = new AiSpeechTask
            {
                MsgId = item.MsgId,
                UserId = item.UserId,
                Nickname = item.Nickname,
                Content = item.Content.Trim(),
                Score = scored.Score,
                ScoreDetail = scored.Detail,
                ReceivedAt = item.Timestamp == default ? DateTime.Now : item.Timestamp,
                EnqueuedAt = DateTime.UtcNow
            };

            _log.AiInfo(
                $"AI_DANMAKU_RECEIVED task={task.TaskId} room={_config.Settings.Douyin.WebRid} user={MaskId(task.UserId)} nick={task.Nickname} len={task.Content.Length} score={task.Score} enter_ai=1 detail={task.ScoreDetail}");

            Enqueue(task);
        }
        catch (Exception ex)
        {
            _log.Error("ai_speech", "TryEnqueueDanmaku 异常（已隔离）", ex);
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
            if (_phase == AiSpeechPhase.Playing || _phase == AiSpeechPhase.Thinking || _phase == AiSpeechPhase.Synthesizing)
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
            _log.AiInfo("AI_TTS_START task=test_voice");
            var ttsSw = Stopwatch.StartNew();
            var synth = await _tts.SynthesizeAsync(text, Settings.Voice, playCts.Token);
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
            MsgId = "test-" + Guid.NewGuid().ToString("N")
        };
        _latestNickname = nick;
        _latestContent = danmaku;
        _latestReply = "";
        NotifyStatus();
        return await ProcessTaskAsync(task, isTest: true, ct);
    }

    private void Enqueue(AiSpeechTask task)
    {
        var max = Math.Clamp(Settings.MaxQueueSize, 1, 20);
        lock (_queueLock)
        {
            ExpireLocked();
            while (_queue.Count >= max)
            {
                var oldest = _queue.First!;
                _queue.RemoveFirst();
                _log.AiInfo($"AI_QUEUE_DROP task={oldest.Value.TaskId} reason=overflow nick={oldest.Value.Nickname}");
            }

            _queue.AddLast(task);
            _log.AiInfo($"AI_QUEUE_ADD task={task.TaskId} queue={_queue.Count}/{max} nick={task.Nickname}");
        }

        _latestNickname = task.Nickname;
        _latestContent = task.Content;
        NotifyStatus();
    }

    private async Task WorkerLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (!Settings.Enabled && !Settings.TestMode)
                {
                    await Task.Delay(400, ct);
                    continue;
                }

                AiSpeechTask? next = null;
                lock (_queueLock)
                {
                    ExpireLocked();
                    if (_queue.Count > 0)
                    {
                        next = _queue.First!.Value;
                        _queue.RemoveFirst();
                    }
                }

                if (next == null)
                {
                    await Task.Delay(200, ct);
                    continue;
                }

                var interval = Math.Clamp(Settings.MinIntervalSeconds, 3, 60);
                var wait = _lastPlayCompletedUtc == DateTime.MinValue
                    ? TimeSpan.Zero
                    : _lastPlayCompletedUtc.AddSeconds(interval) - DateTime.UtcNow;
                if (wait > TimeSpan.Zero)
                {
                    await Task.Delay(wait, ct);
                }

                // 测试模式下同样走单队列；只是强调「按间隔挑一条」——worker 本来就是这样
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

    private void ExpireLocked()
    {
        var maxAge = TimeSpan.FromSeconds(Math.Clamp(Settings.MaxAgeSeconds, 5, 120));
        var now = DateTime.UtcNow;
        var node = _queue.First;
        while (node != null)
        {
            var next = node.Next;
            if (now - node.Value.EnqueuedAt > maxAge)
            {
                _log.AiInfo($"AI_QUEUE_EXPIRED task={node.Value.TaskId} nick={node.Value.Nickname}");
                _queue.Remove(node);
            }

            node = next;
        }
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
        await _executionGate.WaitAsync(ct);
        var playCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Volatile.Write(ref _activePlayCts, playCts);
        var playCt = playCts.Token;

        try
        {
            SetPhase(AiSpeechPhase.Thinking);
            var model = string.IsNullOrWhiteSpace(Settings.Model)
                ? AiSpeechModelsCatalog.DefaultModel
                : Settings.Model.Trim();
            _log.AiInfo(
                $"AI_GENERATE_START task={task.TaskId} model={model} score={task.Score} len={task.Content.Length}");

            var systemPrompt = AiPersonalityLoader.LoadOrFallback(_config.DataDirectory, Settings.SystemPrompt);
            var userPrompt = $"观众昵称：{task.Nickname}\n观众说：{task.Content}";

            var ollamaSw = Stopwatch.StartNew();
            var gen = await GenerateWithRetryAsync(model, systemPrompt, userPrompt, playCt);
            ollamaSw.Stop();
            Interlocked.Exchange(ref _lastOllamaMs, ollamaSw.ElapsedMilliseconds);

            if (!gen.Success)
            {
                if (gen.ResourceError)
                {
                    _log.AiWarn($"AI_OLLAMA_RESOURCE_ERROR task={task.TaskId} status={gen.StatusCode} err={gen.Error}");
                }
                else
                {
                    _log.AiWarn($"AI_GENERATE_FAIL task={task.TaskId} status={gen.StatusCode} err={gen.Error}");
                }

                // 服务仍在线但模型未装/推理失败时，不要把整站 Ollama 标成挂掉
                var ollamaAlive = await _ollama.HealthAsync(CancellationToken.None);
                _ollamaOk = ollamaAlive;
                var modelMissing = LooksLikeModelMissing(gen.Error, model);
                _serviceHint = !ollamaAlive
                    ? "AI模型不可用"
                    : modelMissing
                        ? $"模型未安装：{model}（请 ollama pull）"
                        : gen.ResourceError
                            ? "AI显存不足，请改用 qwen3:8b"
                            : $"AI生成失败：{TrimHint(gen.Error)}";
                SetPhase(AiSpeechPhase.Idle);
                NotifyStatus();
                return new AiSpeechTestResult
                {
                    Success = false,
                    Nickname = task.Nickname,
                    Danmaku = task.Content,
                    Error = modelMissing
                        ? $"模型未安装：{model}"
                        : (gen.Error ?? "AI模型不可用"),
                    OllamaMs = ollamaSw.ElapsedMilliseconds,
                    TotalMs = totalSw.ElapsedMilliseconds
                };
            }

            _ollamaOk = true;
            var cleaned = SpeechTextCleaner.Clean(gen.Text, Settings.MaxReplyLength);
            if (string.IsNullOrWhiteSpace(cleaned))
            {
                _log.AiWarn($"AI_GENERATE_FAIL task={task.TaskId} err=empty_after_clean");
                SetPhase(AiSpeechPhase.Idle);
                return new AiSpeechTestResult
                {
                    Success = false,
                    Nickname = task.Nickname,
                    Danmaku = task.Content,
                    Error = "AI 回复清洗后为空",
                    OllamaMs = ollamaSw.ElapsedMilliseconds,
                    TotalMs = totalSw.ElapsedMilliseconds
                };
            }

            _latestReply = cleaned;
            // AI 语音不向抖音发送弹幕，不得写入 OutboundReplyTracker
            _log.AiInfo(
                $"AI_GENERATE_OK task={task.TaskId} ollama_ms={ollamaSw.ElapsedMilliseconds} reply_len={cleaned.Length} ai_reply_ms={ollamaSw.ElapsedMilliseconds}");

            SetPhase(AiSpeechPhase.Synthesizing);
            _log.AiInfo($"AI_TTS_START task={task.TaskId}");
            var ttsSw = Stopwatch.StartNew();
            var synth = await SynthesizeWithRetryAsync(cleaned, playCt);
            ttsSw.Stop();
            Interlocked.Exchange(ref _lastTtsMs, ttsSw.ElapsedMilliseconds);

            if (!synth.Success)
            {
                _ttsOk = false;
                _serviceHint = "语音服务不可用";
                _log.AiWarn($"AI_TTS_FAIL task={task.TaskId} status={synth.StatusCode} err={synth.Error}");
                SetPhase(AiSpeechPhase.Idle);
                NotifyStatus();
                return new AiSpeechTestResult
                {
                    Success = false,
                    Nickname = task.Nickname,
                    Danmaku = task.Content,
                    Reply = cleaned,
                    Error = synth.Error ?? "语音服务不可用",
                    OllamaMs = ollamaSw.ElapsedMilliseconds,
                    TtsMs = ttsSw.ElapsedMilliseconds,
                    TotalMs = totalSw.ElapsedMilliseconds
                };
            }

            _ttsOk = true;
            _log.AiInfo(
                $"AI_TTS_OK task={task.TaskId} tts_ms={ttsSw.ElapsedMilliseconds} bytes={synth.AudioWav.Length}");

            SetPhase(AiSpeechPhase.Playing);
            ApplyDeviceFromSettings();
            _log.AiInfo($"AI_AUDIO_PLAY_START task={task.TaskId}");
            var playSw = Stopwatch.StartNew();
            await _player.PlayWavAsync(synth.AudioWav, _tempDir, playCt);
            playSw.Stop();
            _log.AiInfo(
                $"AI_SPEECH taskId={task.TaskId} sourceMsgId={task.MsgId} queueWait={queueWaitMs} " +
                $"generateMs={ollamaSw.ElapsedMilliseconds} ttsMs={ttsSw.ElapsedMilliseconds} " +
                $"playMs={playSw.ElapsedMilliseconds} phase=Idle result=ok");

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
                OllamaMs = ollamaSw.ElapsedMilliseconds,
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

    private async Task<OllamaGenerateResult> GenerateWithRetryAsync(
        string model, string systemPrompt, string userPrompt, CancellationToken ct)
    {
        var first = await _ollama.GenerateAsync(model, systemPrompt, userPrompt, ct);
        if (first.Success || first.ResourceError || ct.IsCancellationRequested)
        {
            return first;
        }

        await Task.Delay(800, ct);
        return await _ollama.GenerateAsync(model, systemPrompt, userPrompt, ct);
    }

    private async Task<GptSovitsSynthesizeResult> SynthesizeWithRetryAsync(string text, CancellationToken ct)
    {
        var first = await _tts.SynthesizeAsync(text, Settings.Voice, ct);
        if (first.Success || ct.IsCancellationRequested)
        {
            return first;
        }

        // 409/5xx 只重试一次
        if (first.StatusCode is 409 or >= 500 and < 600 or null)
        {
            await Task.Delay(800, ct);
            return await _tts.SynthesizeAsync(text, Settings.Voice, ct);
        }

        return first;
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

    private bool IsRecentAiReply(string content)
    {
        var key = OutboundReplyTracker.NormalizeContent(content);
        if (string.IsNullOrEmpty(key))
        {
            return false;
        }

        var now = DateTime.UtcNow;
        lock (_aiReplyLock)
        {
            PurgeMap(_recentAiReplies, now, TimeSpan.FromMinutes(3));
            return _recentAiReplies.ContainsKey(key);
        }
    }

    private void TrackAiReply(string reply)
    {
        var key = OutboundReplyTracker.NormalizeContent(reply);
        if (string.IsNullOrEmpty(key))
        {
            return;
        }

        lock (_aiReplyLock)
        {
            _recentAiReplies[key] = DateTime.UtcNow;
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

    private static string PhaseToText(AiSpeechPhase phase) => phase switch
    {
        AiSpeechPhase.Thinking => "正在思考",
        AiSpeechPhase.Synthesizing => "正在合成声音",
        AiSpeechPhase.Playing => "正在播放",
        _ => "空闲"
    };

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

        try { _player.Dispose(); } catch { /* ignore */ }
        try { _executionGate.Dispose(); } catch { /* ignore */ }
        try { _ollama.Dispose(); } catch { /* ignore */ }
        try { _tts.Dispose(); } catch { /* ignore */ }
        try { _cts.Dispose(); } catch { /* ignore */ }

        try
        {
            if (Directory.Exists(_tempDir))
            {
                foreach (var f in Directory.EnumerateFiles(_tempDir, "*.wav"))
                {
                    try { File.Delete(f); } catch { /* ignore */ }
                }
            }
        }
        catch
        {
            // ignore
        }
    }
}
