using System.Text;
using System.Text.RegularExpressions;

namespace LiveAssistant.Services.AiSpeech;

/// <summary>
/// 清洗 AI 文本，使其适合 TTS 朗读。尽量保留中文标点与口语。
/// </summary>
public static partial class SpeechTextCleaner
{
    [GeneratedRegex(@"```[\s\S]*?```")]
    private static partial Regex CodeBlockPattern();

    [GeneratedRegex(@"<think>[\s\S]*?</think>", RegexOptions.IgnoreCase)]
    private static partial Regex ThinkBlockPattern();

    [GeneratedRegex(@"</?think>", RegexOptions.IgnoreCase)]
    private static partial Regex ThinkTagPattern();

    [GeneratedRegex("`[^`]*`")]
    private static partial Regex InlineCodePattern();

    [GeneratedRegex(@"https?://\S+", RegexOptions.IgnoreCase)]
    private static partial Regex UrlPattern();

    [GeneratedRegex(@"[*_~#>\|]+")]
    private static partial Regex MdNoisePattern();

    [GeneratedRegex(@"[\u200B-\u200D\uFEFF\u00A0]")]
    private static partial Regex InvisiblePattern();

    [GeneratedRegex(@"([!！?？.。,，~～…\-—_])\1{2,}")]
    private static partial Regex LongSymbolRunPattern();

    [GeneratedRegex(@"[\(\（]\s*(笑|思考|叹气|捂脸|鼓掌|哭|害羞|点头|摇头)[^\)\）]*[\)\）]")]
    private static partial Regex StageDirectionPattern();

    [GeneratedRegex(@"\s{2,}")]
    private static partial Regex MultiSpacePattern();

    public static string Clean(string? text, int maxChars = 50)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return "";
        }

        var s = text.Trim();
        s = ThinkBlockPattern().Replace(s, " ");
        s = ThinkTagPattern().Replace(s, " ");
        s = CodeBlockPattern().Replace(s, " ");
        s = InlineCodePattern().Replace(s, " ");
        s = UrlPattern().Replace(s, " ");
        s = StageDirectionPattern().Replace(s, "");
        s = MdNoisePattern().Replace(s, "");
        s = InvisiblePattern().Replace(s, "");
        s = LongSymbolRunPattern().Replace(s, "$1$1");
        s = StripEmoji(s);
        s = s.Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ');
        s = MultiSpacePattern().Replace(s, " ").Trim();

        // 去掉常见 AI 前缀 / 破功话术
        foreach (var prefix in new[] { "主播：", "主播:", "回复：", "回复:", "答：" })
        {
            if (s.StartsWith(prefix, StringComparison.Ordinal))
            {
                s = s[prefix.Length..].Trim();
            }
        }

        foreach (var bad in new[] { "作为AI", "作为人工智能", "作为语言模型" })
        {
            s = s.Replace(bad, "", StringComparison.OrdinalIgnoreCase);
        }
        s = MultiSpacePattern().Replace(s, " ").Trim();

        if (maxChars > 0 && CountSpeechChars(s) > maxChars)
        {
            s = TruncateBySentence(s, maxChars);
        }

        s = EnsureCompleteUtterance(s);
        return s.Trim();
    }

    /// <summary>
    /// 为 GPT-SoVITS 按句切分：在句号/问叹号后插入换行，减轻长句黏嘴与尾音糊。
    /// </summary>
    public static string FormatForTts(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return "";
        }

        var s = MultiSpacePattern().Replace(text.Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' '), " ").Trim();
        var sb = new StringBuilder(s.Length + 8);
        for (var i = 0; i < s.Length; i++)
        {
            var ch = s[i];
            sb.Append(ch);
            if (ch is '。' or '！' or '？' or '!' or '?' or '…')
            {
                // 句末后若还有内容，换行让引擎分句合成
                var j = i + 1;
                while (j < s.Length && char.IsWhiteSpace(s[j]))
                {
                    j++;
                }

                if (j < s.Length)
                {
                    sb.Append('\n');
                    i = j - 1;
                }
            }
        }

        return sb.ToString().Trim();
    }

    /// <summary>硬切后尽量补全句末标点，避免听感「话说一半」。</summary>
    internal static string EnsureCompleteUtterance(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return "";
        }

        var s = text.Trim();
        var last = s[^1];
        if (last is '。' or '！' or '？' or '!' or '?' or '…' or '；' or ';' or '，' or ',')
        {
            return s;
        }

        // 已像完整口语短句时补句号，避免 TTS 拖尾黏糊
        if (CountSpeechChars(s) >= 4)
        {
            return s + "。";
        }

        return s;
    }

    public static int CountSpeechChars(string text)
    {
        var n = 0;
        foreach (var ch in text)
        {
            if (!char.IsWhiteSpace(ch))
            {
                n++;
            }
        }

        return n;
    }

    internal static string TruncateBySentence(string text, int maxChars)
    {
        if (CountSpeechChars(text) <= maxChars)
        {
            return text;
        }

        var ends = new[] { '。', '！', '？', '；', '.', '!', '?', '…' };
        var sb = new StringBuilder();
        var count = 0;
        var lastSentenceEnd = -1;

        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            sb.Append(ch);
            if (!char.IsWhiteSpace(ch))
            {
                count++;
            }

            if (ends.Contains(ch))
            {
                lastSentenceEnd = sb.Length;
                if (count >= Math.Max(8, maxChars / 2) && count <= maxChars)
                {
                    return sb.ToString(0, lastSentenceEnd).Trim();
                }
            }

            if (count >= maxChars)
            {
                break;
            }
        }

        if (lastSentenceEnd > 0 && CountSpeechChars(sb.ToString(0, lastSentenceEnd)) >= 6)
        {
            return sb.ToString(0, lastSentenceEnd).Trim();
        }

        // 没有完整句子时，退到逗号；仍不够则硬切并交给 EnsureCompleteUtterance 补句号
        var hard = sb.ToString().Trim();
        var comma = Math.Max(hard.LastIndexOf('，'), hard.LastIndexOf(','));
        if (comma >= 8)
        {
            return hard[..(comma + 1)].Trim();
        }

        return hard.TrimEnd('，', ',', '、', '；', ';', '：', ':').Trim();
    }

    private static string StripEmoji(string text)
    {
        var sb = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (char.IsSurrogate(ch))
            {
                // 跳过代理对（多数 emoji）
                if (char.IsHighSurrogate(ch) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
                {
                    i++;
                }

                continue;
            }

            var cat = char.GetUnicodeCategory(ch);
            if (cat is System.Globalization.UnicodeCategory.OtherSymbol
                or System.Globalization.UnicodeCategory.Surrogate
                or System.Globalization.UnicodeCategory.PrivateUse)
            {
                continue;
            }

            // 常见 emoji 区块
            if (ch is >= '\u2600' and <= '\u27BF' or >= '\uFE00' and <= '\uFE0F')
            {
                continue;
            }

            sb.Append(ch);
        }

        return sb.ToString();
    }
}
