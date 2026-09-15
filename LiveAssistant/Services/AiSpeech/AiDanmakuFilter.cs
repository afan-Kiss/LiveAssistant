using LiveAssistant.Models;
using LiveAssistant.Utils;

namespace LiveAssistant.Services.AiSpeech;

/// <summary>
/// 弹幕评分：决定是否进入 AI 语音队列。不修改点歌/礼物等原逻辑。
/// </summary>
public static class AiDanmakuFilter
{
    public const int DefaultScoreThreshold = 0;

    private static readonly string[] DefaultHostKeywords =
    {
        "主播", "主啵", "老板", "姐姐", "哥哥", "宝子"
    };

    private static readonly string[] DefaultProductKeywords =
    {
        "软件", "点歌", "系统", "助手", "商品", "链接", "功能", "怎么用", "插件", "程序", "工具"
    };

    private static readonly string[] QuestionMarkers =
    {
        "?", "？", "吗", "呢", "么", "嘛", "啥", "怎么", "怎样", "如何", "为啥", "为什么",
        "啥意思", "可不可以", "能不能", "是不是", "有没有", "多少"
    };

    private static readonly HashSet<string> PureNoise = new(StringComparer.OrdinalIgnoreCase)
    {
        "哈哈", "哈哈哈", "哈哈哈哈", "哈哈哈哈哈", "666", "6666", "66666", "233", "2333",
        "awsl", "yyds", "xswl", "hhh", "hhhh", "lol",
        "👍", "🔥", "❤️", "😂", "🤣", "😊", "👏", "💪", "🎁", "✨"
    };

    public sealed class ScoreResult
    {
        public int Score { get; init; }
        public int Threshold { get; init; }
        public bool EnterAi { get; init; }
        public string Reason { get; init; } = "";
        public string Detail { get; init; } = "";
        public int ContentLength { get; init; }
        public bool HardSkip { get; init; }
    }

    /// <summary>兼容旧调用：硬过滤或分数不足则跳过。</summary>
    public static bool ShouldSkip(DanmakuItem item, out string reason)
        => ShouldSkip(item, DefaultScoreThreshold, out reason);

    public static bool ShouldSkip(DanmakuItem item, int scoreThreshold, out string reason)
    {
        var result = Evaluate(item, scoreThreshold);
        reason = result.HardSkip
            ? result.Reason
            : $"score={result.Score}<{result.Threshold} {result.Detail}".Trim();
        return !result.EnterAi;
    }

    public static ScoreResult Evaluate(DanmakuItem item, int scoreThreshold = DefaultScoreThreshold)
    {
        scoreThreshold = Math.Clamp(scoreThreshold, 0, 20);
        if (item == null)
        {
            return Hard("null", 0);
        }

        var type = (item.MsgType ?? "chat").Trim().ToLowerInvariant();
        if (type is "gift" or "member" or "like" or "follow" or "social" or "room" or "system")
        {
            return Hard($"msg_type={type}", 0);
        }

        if (!string.Equals(type, "chat", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(type)
            && type is not "text" and not "danmaku")
        {
            return Hard($"msg_type={type}", 0);
        }

        var content = (item.Content ?? "").Trim();
        var len = content.Length;
        if (string.IsNullOrEmpty(content))
        {
            return Hard("empty", 0);
        }

        if (SongNameParser.IsBotReply(content))
        {
            return Hard("bot_reply", len);
        }

        if (SongNameParser.TryParse(content, out _))
        {
            return Hard("song_request", len);
        }

        if (SongRequestConfirmParser.IsConfirm(content) || SongRequestConfirmParser.IsCancel(content))
        {
            return Hard("song_confirm", len);
        }

        if (SkipSongParser.TryParse(content))
        {
            return Hard("skip_song", len);
        }

        if (PointsQueryParser.TryParse(content))
        {
            return Hard("points_query", len);
        }

        if (content.StartsWith("禁言", StringComparison.OrdinalIgnoreCase))
        {
            return Hard("ban_vote", len);
        }

        if (IsPureDigits(content))
        {
            return Hard("digits", len);
        }

        var compact = string.Concat(content.Where(c => !char.IsWhiteSpace(c)));
        if (compact.Length <= 1)
        {
            return Hard("short", len);
        }

        if (PureNoise.Contains(compact))
        {
            return Hard("noise", len);
        }

        if (IsMostlyEmojiOrNoise(content))
        {
            return Hard("emoji", len);
        }

        var score = 0;
        var parts = new List<string>();

        var isQuestion = LooksLikeQuestion(content);
        if (isQuestion)
        {
            score += 3;
            parts.Add("+提问/疑问+3");
        }

        if (ContainsAny(content, DefaultHostKeywords))
        {
            score += 2;
            parts.Add("+主播称呼+2");
        }

        if (ContainsAny(content, DefaultProductKeywords))
        {
            score += 2;
            parts.Add("+软件/商品+2");
        }

        // 降低分：夹杂刷屏语气（纯噪声仍由上方 Hard 拦截）
        if (ContainsNoiseToken(content))
        {
            score -= 2;
            parts.Add("-刷屏语气-2");
        }

        // 短弹幕（如「你好」）允许进入：阈值=0 时 score=0 即可回复，不再额外扣「过短」分

        var detail = parts.Count == 0 ? "无加分项" : string.Join(" ", parts);
        var enter = score >= scoreThreshold;
        return new ScoreResult
        {
            Score = score,
            Threshold = scoreThreshold,
            EnterAi = enter,
            Reason = enter ? "ok" : "low_score",
            Detail = detail,
            ContentLength = len,
            HardSkip = false
        };
    }

    private static ScoreResult Hard(string reason, int len) => new()
    {
        Score = int.MinValue,
        Threshold = 0,
        EnterAi = false,
        Reason = reason,
        Detail = "",
        ContentLength = len,
        HardSkip = true
    };

    private static bool LooksLikeQuestion(string content)
    {
        foreach (var marker in QuestionMarkers)
        {
            if (content.Contains(marker, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool ContainsAny(string content, IEnumerable<string> keywords)
    {
        foreach (var kw in keywords)
        {
            if (string.IsNullOrWhiteSpace(kw))
            {
                continue;
            }

            if (content.Contains(kw, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool ContainsNoiseToken(string content)
    {
        var lower = content.ToLowerInvariant();
        if (lower.Contains("666", StringComparison.Ordinal)
            || lower.Contains("233", StringComparison.Ordinal))
        {
            return true;
        }

        // 「哈哈」单独刷屏已硬过滤；夹在长句中降分
        var haCount = 0;
        for (var i = 0; i + 1 < content.Length; i++)
        {
            if (content[i] == '哈' && content[i + 1] == '哈')
            {
                haCount++;
                i++;
            }
        }

        return haCount >= 1 && content.Length <= 12;
    }

    private static bool IsPureDigits(string content)
    {
        foreach (var ch in content)
        {
            if (!char.IsDigit(ch) && !char.IsWhiteSpace(ch))
            {
                return false;
            }
        }

        return content.Any(char.IsDigit);
    }

    private static bool IsMostlyEmojiOrNoise(string content)
    {
        var letters = 0;
        var others = 0;
        foreach (var ch in content)
        {
            if (char.IsWhiteSpace(ch))
            {
                continue;
            }

            if (char.IsLetterOrDigit(ch) || IsCjk(ch) || IsCnPunct(ch))
            {
                letters++;
            }
            else
            {
                others++;
            }
        }

        if (letters == 0)
        {
            return true;
        }

        return others >= letters * 2;
    }

    private static bool IsCjk(char ch)
        => ch is >= '\u4E00' and <= '\u9FFF'
            or >= '\u3400' and <= '\u4DBF'
            or >= '\uF900' and <= '\uFAFF';

    private static bool IsCnPunct(char ch)
        => ch is '，' or '。' or '！' or '？' or '；' or '：' or '、' or '…' or '—' or '～';
}
