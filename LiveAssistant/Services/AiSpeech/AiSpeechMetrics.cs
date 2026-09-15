using System.Text;

namespace LiveAssistant.Services.AiSpeech;

/// <summary>
/// AI 直播运行指标：任务计数、耗时均值、队列峰值、跳过/错误原因。
/// 线程安全；快照不含 Prompt / 用户上下文 / 弹幕正文。
/// </summary>
public sealed class AiSpeechMetrics
{
    private readonly object _gate = new();
    private readonly DateTime _startedUtc = DateTime.UtcNow;
    private DateTime _dayAnchorLocal = DateTime.Today;

    private long _received;
    private long _filtered;
    private long _generated;
    private long _ttsSuccess;
    private long _ttsFailed;
    private long _playSuccess;
    private long _playFailed;

    private long _ollamaMsSum;
    private long _ollamaMsCount;
    private long _ttsMsSum;
    private long _ttsMsCount;
    private long _totalMsSum;
    private long _totalMsCount;
    private long _queueWaitMsSum;
    private long _queueWaitMsCount;

    private long _maxQueueSeen;
    private long _skipThinkOnly;
    private long _skipEmptyReply;
    private long _skipDuplicateReply;
    private long _skipExpired;
    private long _skipFilterBlock;
    private long _skipByModel;
    private long _errOllama;
    private long _errTts;
    private long _errAudio;

    private long _todayReplies;
    private string _lastError = "";
    private DateTime _lastErrorUtc = DateTime.MinValue;

    public void NoteReceived(int count = 1)
    {
        if (count <= 0) return;
        Interlocked.Add(ref _received, count);
    }

    public void NoteFiltered(int count = 1)
    {
        if (count <= 0) return;
        Interlocked.Add(ref _filtered, count);
        Interlocked.Add(ref _skipFilterBlock, count);
    }

    public void NoteGenerated(long ollamaMs = -1, long queueWaitMs = -1)
    {
        Interlocked.Increment(ref _generated);
        NoteTodayReply();
        if (ollamaMs >= 0) AddAvg(ref _ollamaMsSum, ref _ollamaMsCount, ollamaMs);
        if (queueWaitMs >= 0) AddAvg(ref _queueWaitMsSum, ref _queueWaitMsCount, queueWaitMs);
    }

    public void NoteTtsSuccess(long ttsMs)
    {
        Interlocked.Increment(ref _ttsSuccess);
        if (ttsMs >= 0) AddAvg(ref _ttsMsSum, ref _ttsMsCount, ttsMs);
    }

    public void NoteTtsFailed(string? error = null)
    {
        Interlocked.Increment(ref _ttsFailed);
        Interlocked.Increment(ref _errTts);
        SetLastError(error ?? "tts_error");
    }

    public void NotePlaySuccess(long totalMs = -1)
    {
        Interlocked.Increment(ref _playSuccess);
        if (totalMs >= 0) AddAvg(ref _totalMsSum, ref _totalMsCount, totalMs);
    }

    public void NotePlayFailed(string? error = null)
    {
        Interlocked.Increment(ref _playFailed);
        Interlocked.Increment(ref _errAudio);
        SetLastError(error ?? "audio_error");
    }

    public void NoteOllamaError(string? error = null)
    {
        Interlocked.Increment(ref _errOllama);
        SetLastError(error ?? "ollama_error");
    }

    public void NoteSkip(string reason, string? detail = null)
    {
        switch ((reason ?? "").Trim().ToUpperInvariant())
        {
            case "THINK_ONLY":
                Interlocked.Increment(ref _skipThinkOnly);
                break;
            case "EMPTY":
            case "EMPTY_REPLY":
            case "NO_FINAL_ANSWER":
                Interlocked.Increment(ref _skipEmptyReply);
                break;
            case "DUPLICATE_REPLY":
                Interlocked.Increment(ref _skipDuplicateReply);
                break;
            case "EXPIRED":
                Interlocked.Increment(ref _skipExpired);
                break;
            case "FILTER_BLOCK":
            case "FILTER":
                Interlocked.Increment(ref _skipFilterBlock);
                break;
            case "SKIP":
            case "SKIP_BY_MODEL":
                Interlocked.Increment(ref _skipByModel);
                break;
            default:
                break;
        }

        if (!string.IsNullOrWhiteSpace(detail))
        {
            SetLastError($"{reason}:{Trim(detail!, 80)}");
        }
    }

    public void NoteQueueSize(int size)
    {
        if (size < 0) return;
        long cur;
        do
        {
            cur = Interlocked.Read(ref _maxQueueSeen);
            if (size <= cur) return;
        } while (Interlocked.CompareExchange(ref _maxQueueSeen, size, cur) != cur);
    }

    public AiSpeechMetricsSnapshot Snapshot(int currentQueueSize, int maxQueueSize)
    {
        EnsureDayRollover();
        var generated = Interlocked.Read(ref _generated);
        var playOk = Interlocked.Read(ref _playSuccess);
        var playFail = Interlocked.Read(ref _playFailed);
        var ttsOk = Interlocked.Read(ref _ttsSuccess);
        var ttsFail = Interlocked.Read(ref _ttsFailed);
        var attempts = playOk + playFail;
        var successRate = attempts <= 0 ? 0.0 : 100.0 * playOk / attempts;

        string lastErr;
        lock (_gate) lastErr = _lastError;

        return new AiSpeechMetricsSnapshot
        {
            ReceivedCount = Interlocked.Read(ref _received),
            FilteredCount = Interlocked.Read(ref _filtered),
            GeneratedCount = generated,
            TtsSuccessCount = ttsOk,
            TtsFailedCount = ttsFail,
            PlaySuccessCount = playOk,
            PlayFailedCount = playFail,
            AverageOllamaMs = Avg(ref _ollamaMsSum, ref _ollamaMsCount),
            AverageTtsMs = Avg(ref _ttsMsSum, ref _ttsMsCount),
            AverageTotalMs = Avg(ref _totalMsSum, ref _totalMsCount),
            AverageQueueWaitMs = Avg(ref _queueWaitMsSum, ref _queueWaitMsCount),
            CurrentQueueSize = currentQueueSize,
            MaxQueueSize = maxQueueSize,
            MaxQueueSizeSeen = Interlocked.Read(ref _maxQueueSeen),
            SkipThinkOnly = Interlocked.Read(ref _skipThinkOnly),
            SkipEmptyReply = Interlocked.Read(ref _skipEmptyReply),
            SkipDuplicateReply = Interlocked.Read(ref _skipDuplicateReply),
            SkipExpired = Interlocked.Read(ref _skipExpired),
            SkipFilterBlock = Interlocked.Read(ref _skipFilterBlock),
            SkipByModel = Interlocked.Read(ref _skipByModel),
            OllamaError = Interlocked.Read(ref _errOllama),
            TtsError = Interlocked.Read(ref _errTts),
            AudioError = Interlocked.Read(ref _errAudio),
            TodayReplyCount = Interlocked.Read(ref _todayReplies),
            SuccessRatePercent = successRate,
            LastError = lastErr,
            UptimeSeconds = (long)(DateTime.UtcNow - _startedUtc).TotalSeconds
        };
    }

    /// <summary>结构化快照日志行（无 Prompt/上下文/弹幕正文）。</summary>
    public string FormatSnapshotLog(int currentQueueSize, int maxQueueSize)
    {
        var s = Snapshot(currentQueueSize, maxQueueSize);
        var sb = new StringBuilder(256);
        sb.Append("AI_METRIC_SNAPSHOT");
        sb.Append(" received=").Append(s.ReceivedCount);
        sb.Append(" filtered=").Append(s.FilteredCount);
        sb.Append(" generated=").Append(s.GeneratedCount);
        sb.Append(" tts_ok=").Append(s.TtsSuccessCount);
        sb.Append(" tts_fail=").Append(s.TtsFailedCount);
        sb.Append(" play_ok=").Append(s.PlaySuccessCount);
        sb.Append(" play_fail=").Append(s.PlayFailedCount);
        sb.Append(" success_pct=").Append(s.SuccessRatePercent.ToString("0.0"));
        sb.Append(" avg_ollama_ms=").Append(s.AverageOllamaMs);
        sb.Append(" avg_tts_ms=").Append(s.AverageTtsMs);
        sb.Append(" avg_total_ms=").Append(s.AverageTotalMs);
        sb.Append(" avg_queue_wait_ms=").Append(s.AverageQueueWaitMs);
        sb.Append(" queue=").Append(s.CurrentQueueSize).Append('/').Append(s.MaxQueueSize);
        sb.Append(" max_queue_seen=").Append(s.MaxQueueSizeSeen);
        sb.Append(" skip_think=").Append(s.SkipThinkOnly);
        sb.Append(" skip_empty=").Append(s.SkipEmptyReply);
        sb.Append(" skip_dup=").Append(s.SkipDuplicateReply);
        sb.Append(" skip_expired=").Append(s.SkipExpired);
        sb.Append(" skip_filter=").Append(s.SkipFilterBlock);
        sb.Append(" skip_model=").Append(s.SkipByModel);
        sb.Append(" err_ollama=").Append(s.OllamaError);
        sb.Append(" err_tts=").Append(s.TtsError);
        sb.Append(" err_audio=").Append(s.AudioError);
        sb.Append(" today_replies=").Append(s.TodayReplyCount);
        return sb.ToString();
    }

    private void NoteTodayReply()
    {
        EnsureDayRollover();
        Interlocked.Increment(ref _todayReplies);
    }

    private void EnsureDayRollover()
    {
        var today = DateTime.Today;
        lock (_gate)
        {
            if (today != _dayAnchorLocal)
            {
                _dayAnchorLocal = today;
                Interlocked.Exchange(ref _todayReplies, 0);
            }
        }
    }

    private void SetLastError(string error)
    {
        lock (_gate)
        {
            _lastError = Trim(error, 120);
            _lastErrorUtc = DateTime.UtcNow;
        }
    }

    private static void AddAvg(ref long sum, ref long count, long value)
    {
        Interlocked.Add(ref sum, value);
        Interlocked.Increment(ref count);
    }

    private static long Avg(ref long sum, ref long count)
    {
        var c = Interlocked.Read(ref count);
        if (c <= 0) return 0;
        return Interlocked.Read(ref sum) / c;
    }

    private static string Trim(string s, int max)
        => string.IsNullOrEmpty(s) ? "" : s.Length <= max ? s : s[..max] + "…";
}

public sealed class AiSpeechMetricsSnapshot
{
    public long ReceivedCount { get; init; }
    public long FilteredCount { get; init; }
    public long GeneratedCount { get; init; }
    public long TtsSuccessCount { get; init; }
    public long TtsFailedCount { get; init; }
    public long PlaySuccessCount { get; init; }
    public long PlayFailedCount { get; init; }
    public long AverageOllamaMs { get; init; }
    public long AverageTtsMs { get; init; }
    public long AverageTotalMs { get; init; }
    public long AverageQueueWaitMs { get; init; }
    public int CurrentQueueSize { get; init; }
    public int MaxQueueSize { get; init; }
    public long MaxQueueSizeSeen { get; init; }
    public long SkipThinkOnly { get; init; }
    public long SkipEmptyReply { get; init; }
    public long SkipDuplicateReply { get; init; }
    public long SkipExpired { get; init; }
    public long SkipFilterBlock { get; init; }
    public long SkipByModel { get; init; }
    public long OllamaError { get; init; }
    public long TtsError { get; init; }
    public long AudioError { get; init; }
    public long TodayReplyCount { get; init; }
    public double SuccessRatePercent { get; init; }
    public string LastError { get; init; } = "";
    public long UptimeSeconds { get; init; }
}
