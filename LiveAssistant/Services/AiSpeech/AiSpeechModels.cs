namespace LiveAssistant.Services.AiSpeech;

public enum AiSpeechPhase
{
    Idle,
    Thinking,
    GeneratingGift,
    GeneratingWelcome,
    GeneratingSongRequest,
    GeneratingSummary,
    Synthesizing,
    Playing
}

public enum AiSpeechEventKind
{
    Danmaku,
    Gift,
    SongRequest,
    Welcome,
    Like,
    Summary,
    System
}

/// <summary>优先级：数值越小越优先。Gift &gt; SongRequest &gt; Danmaku &gt; Welcome/Like。</summary>
public enum AiSpeechPriority
{
    P0 = 0,
    Gift = 1,
    SongRequest = 2,
    DanmakuImportant = 3,
    Summary = 4,
    Welcome = 5,
    Like = 6
}

public enum AiContextMode
{
    Auto,
    None,
    User,
    Room
}

public static class AiContextModeHelper
{
    public static AiContextMode Parse(string? mode)
    {
        var m = (mode ?? "").Trim().ToLowerInvariant();
        return m switch
        {
            "none" => AiContextMode.None,
            "user" => AiContextMode.User,
            "room" => AiContextMode.Room,
            _ => AiContextMode.Auto
        };
    }

    public static string ToConfigString(AiContextMode mode) => mode switch
    {
        AiContextMode.None => "none",
        AiContextMode.User => "user",
        AiContextMode.Room => "room",
        _ => "auto"
    };
}

public sealed class AiSpeechTask
{
    public string TaskId { get; init; } = Guid.NewGuid().ToString("N");
    public string MsgId { get; init; } = "";
    public string UserId { get; init; } = "";
    public string Nickname { get; init; } = "";
    public string Content { get; init; } = "";
    public int Score { get; init; }
    public string ScoreDetail { get; init; } = "";
    public DateTime EnqueuedAt { get; init; } = DateTime.UtcNow;
    public DateTime ReceivedAt { get; init; } = DateTime.Now;

    public AiSpeechEventKind Kind { get; init; } = AiSpeechEventKind.Danmaku;
    public AiSpeechPriority Priority { get; init; } = AiSpeechPriority.DanmakuImportant;
    public string EmotionRequested { get; init; } = "auto";
    public string EmotionUsed { get; set; } = "";
    public double Speed { get; init; } = 1.0;
    /// <summary>已有成稿时跳过 LLM（如礼物模板感谢）。</summary>
    public string? PrebuiltText { get; init; }
    public string PromptVersion { get; init; } = "";
    public IReadOnlyList<(string Role, string Content)>? ContextUserMessages { get; init; }
    public IReadOnlyList<(string Role, string Content)>? ContextRoomMessages { get; init; }
}

/// <summary>模型下拉项：显示推荐标记，保存真实模型名。</summary>
public sealed class AiModelComboItem
{
    public string ModelId { get; }
    public string DisplayName { get; }

    public AiModelComboItem(string modelId)
    {
        ModelId = (modelId ?? "").Trim();
        DisplayName = AiSpeechModelsCatalog.FormatDisplayName(ModelId);
    }

    public override string ToString() => DisplayName;
}

public sealed class AiSpeechStatusSnapshot
{
    public bool Enabled { get; init; }
    public bool TestMode { get; init; }
    public AiSpeechPhase Phase { get; init; } = AiSpeechPhase.Idle;
    public string PhaseText { get; init; } = "空闲";
    /// <summary>面板简化状态：空闲 / 生成回复 / 合成声音 / 播放中。</summary>
    public string RuntimeStatusText { get; init; } = "空闲";
    public int QueueCount { get; init; }
    public int MaxQueueSize { get; init; } = 5;
    public string LatestNickname { get; init; } = "";
    public string LatestContent { get; init; } = "";
    public string LatestReply { get; init; } = "";
    public string ServiceHint { get; init; } = "";
    public bool OllamaOk { get; init; }
    public bool TtsOk { get; init; }
    public bool VoiceReady { get; init; }
    public bool ModelAvailable { get; init; }
    public string ModelName { get; init; } = "";
    public IReadOnlyList<string> InstalledModels { get; init; } = Array.Empty<string>();
    public string HealthSummary { get; init; } = "";
    public bool AiReady { get; init; }
    public string VoiceName { get; init; } = "my_voice";
    public long LastOllamaMs { get; init; }
    public long LastTtsMs { get; init; }
    public long LastTotalMs { get; init; }

    public AiSpeechEventKind TaskKind { get; init; } = AiSpeechEventKind.Danmaku;
    public string Emotion { get; init; } = "";
    public double Speed { get; init; } = 1.0;
    public DateTime? PromptLoadedAt { get; init; }
    public string PromptVersion { get; init; } = "";

    public long TodayReplyCount { get; init; }
    public double SuccessRatePercent { get; init; }
    public long AverageTotalMs { get; init; }
    public string LastError { get; init; } = "";
    public long MaxQueueSizeSeen { get; init; }
}

public sealed class AiSpeechTestResult
{
    public bool Success { get; init; }
    public string Nickname { get; init; } = "";
    public string Danmaku { get; init; } = "";
    public string Reply { get; init; } = "";
    public long OllamaMs { get; init; }
    public long TtsMs { get; init; }
    public long TotalMs { get; init; }
    public string? Error { get; init; }
}

public sealed class AudioOutputDeviceInfo
{
    public int DeviceNumber { get; init; }
    public string Name { get; init; } = "";
    public override string ToString() => DeviceNumber < 0 ? $"系统默认 ({Name})" : $"{DeviceNumber}: {Name}";
}

public static class AiSpeechPhaseText
{
    public static string ToText(AiSpeechPhase phase) => phase switch
    {
        AiSpeechPhase.Thinking => "正在思考",
        AiSpeechPhase.GeneratingGift => "正在生成礼物感谢",
        AiSpeechPhase.GeneratingWelcome => "正在生成欢迎语",
        AiSpeechPhase.GeneratingSongRequest => "正在生成点歌播报",
        AiSpeechPhase.GeneratingSummary => "正在生成总结",
        AiSpeechPhase.Synthesizing => "正在合成声音",
        AiSpeechPhase.Playing => "正在播放",
        _ => "空闲"
    };

    /// <summary>运行监控面板用的四态文案。</summary>
    public static string ToRuntimeStatus(AiSpeechPhase phase) => phase switch
    {
        AiSpeechPhase.Synthesizing => "合成声音",
        AiSpeechPhase.Playing => "播放中",
        AiSpeechPhase.Idle => "空闲",
        _ => "生成回复"
    };
}
