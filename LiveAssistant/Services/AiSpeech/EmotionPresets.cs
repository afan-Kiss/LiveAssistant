namespace LiveAssistant.Services.AiSpeech;

/// <summary>
/// 情感预设 → 可选参考 wav（voices/my_voice/reference 或 Config/AiSpeech/emotions）。
/// </summary>
public static class EmotionPresets
{
    public const string Auto = "auto";
    public const string Neutral = "neutral";
    public const string Happy = "happy";
    public const string Warm = "warm";
    public const string Excited = "excited";
    public const string Calm = "calm";

    public readonly record struct ResolveResult(string EmotionUsed, bool Configured, string? ReferWavPath);

    public static IReadOnlyList<string> KnownIds { get; } =
        new[] { Auto, Neutral, Happy, Warm, Excited, Calm };

    public static string AutoPick(AiSpeechEventKind kind) => kind switch
    {
        AiSpeechEventKind.Gift => Excited,
        AiSpeechEventKind.SongRequest => Happy,
        AiSpeechEventKind.Welcome => Warm,
        AiSpeechEventKind.Like => Happy,
        AiSpeechEventKind.Summary => Calm,
        AiSpeechEventKind.Danmaku => Neutral,
        _ => Neutral
    };

    public static ResolveResult Resolve(string? emotion, AiSpeechEventKind kind, string? voice = "my_voice")
    {
        var requested = (emotion ?? Auto).Trim();
        if (string.IsNullOrWhiteSpace(requested) || requested.Equals(Auto, StringComparison.OrdinalIgnoreCase))
        {
            requested = AutoPick(kind);
        }

        var id = NormalizeId(requested);
        var path = FindReferWav(id, voice);
        return new ResolveResult(id, path != null, path);
    }

    public static bool IsConfigured(string emotionId, string? voice = "my_voice")
        => FindReferWav(NormalizeId(emotionId), voice) != null;

    public static string? FindReferWav(string emotionId, string? voice = "my_voice")
    {
        emotionId = NormalizeId(emotionId);
        voice = string.IsNullOrWhiteSpace(voice) ? "my_voice" : voice.Trim();

        foreach (var dir in CandidateDirs(voice))
        {
            if (!Directory.Exists(dir))
            {
                continue;
            }

            foreach (var name in new[]
                     {
                         $"{emotionId}.wav",
                         $"{emotionId}.mp3",
                         $"{voice}_{emotionId}.wav"
                     })
            {
                var path = Path.Combine(dir, name);
                if (File.Exists(path))
                {
                    return path;
                }
            }
        }

        return null;
    }

    private static IEnumerable<string> CandidateDirs(string voice)
    {
        var exe = AppPaths.ExeDirectory;
        yield return Path.Combine(exe, "voices", voice, "reference");
        yield return Path.Combine(exe, "Config", "AiSpeech", "emotions");
        yield return Path.Combine(AppPaths.ConfigDirectory, "AiSpeech", "emotions");
        yield return Path.Combine(AppPaths.ResolveDataDirectory(), "AiSpeech", "emotions");
    }

    private static string NormalizeId(string id)
    {
        id = (id ?? Neutral).Trim().ToLowerInvariant();
        return id switch
        {
            "auto" => Neutral,
            "开心" or "happy" => Happy,
            "热情" or "warm" => Warm,
            "激动" or "excited" => Excited,
            "平静" or "calm" => Calm,
            "中性" or "neutral" => Neutral,
            _ => string.IsNullOrWhiteSpace(id) ? Neutral : id
        };
    }
}
