namespace LiveAssistant.Services.AiSpeech;

public enum AiSpeechPhase
{
    Idle,
    Thinking,
    Synthesizing,
    Playing
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
    public int QueueCount { get; init; }
    public int MaxQueueSize { get; init; } = 5;
    public string LatestNickname { get; init; } = "";
    public string LatestContent { get; init; } = "";
    public string LatestReply { get; init; } = "";
    public string ServiceHint { get; init; } = "";
    public bool OllamaOk { get; init; }
    public bool TtsOk { get; init; }
    public bool VoiceReady { get; init; }
    public string VoiceName { get; init; } = "my_voice";
    public long LastOllamaMs { get; init; }
    public long LastTtsMs { get; init; }
    public long LastTotalMs { get; init; }
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
