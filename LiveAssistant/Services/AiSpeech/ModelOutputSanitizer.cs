using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace LiveAssistant.Services.AiSpeech;

/// <summary>
/// 模型输出消毒：剥离思考/分析块与角色前缀，检测空答案与提示词泄露。
/// Ollama 输出进入 TTS 前必须经过；思考内容绝不能作为朗读兜底。
/// </summary>
public static partial class ModelOutputSanitizer
{
    public sealed class SanitizeResult
    {
        public bool Ok { get; init; }
        public string Text { get; init; } = "";
        public string? RejectReason { get; init; }

        public static SanitizeResult Accept(string text)
            => new() { Ok = true, Text = text };

        public static SanitizeResult Reject(string reason)
            => new() { Ok = false, RejectReason = reason };
    }

    [GeneratedRegex(@"<think>[\s\S]*?</think>", RegexOptions.IgnoreCase)]
    private static partial Regex ThinkBlock();

    [GeneratedRegex(@"<analysis>[\s\S]*?</analysis>", RegexOptions.IgnoreCase)]
    private static partial Regex AnalysisBlock();

    /// <summary>未闭合的 think/analysis 块（模型截断时）。</summary>
    [GeneratedRegex(@"<(think|analysis)\b[^>]*>[\s\S]*$", RegexOptions.IgnoreCase)]
    private static partial Regex UnclosedThink();

    [GeneratedRegex(@"```[\s\S]*?```")]
    private static partial Regex CodeFence();

    [GeneratedRegex(@"(?im)^\s*(Reasoning|Thinking|Analysis|思考|分析|推理)\s*[:：].*$")]
    private static partial Regex ReasoningLine();

    [GeneratedRegex(@"(?is)(?:Reasoning|Thinking|Analysis|思考|分析|推理)\s*[:：]\s*[\s\S]*?(?=(?:Final\s*Answer|最终回答|回复)\s*[:：]|$)")]
    private static partial Regex ReasoningSection();

    [GeneratedRegex(@"(?im)^\s*(System|Assistant|User|Human|AI)\s*[:：]\s*.*$")]
    private static partial Regex RoleLabelLine();

    [GeneratedRegex(@"(?is)\b(System|Assistant)\s*[:：]\s*")]
    private static partial Regex RoleLabelInline();

    [GeneratedRegex("`[^`]*`")]
    private static partial Regex InlineCode();

    [GeneratedRegex(@"[*_~#>\|]+")]
    private static partial Regex MdNoise();

    [GeneratedRegex(@"\s{2,}")]
    private static partial Regex MultiSpace();

    public static SanitizeResult Sanitize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return SanitizeResult.Reject("EMPTY");
        }

        var original = raw.Trim();
        var s = original;
        s = ThinkBlock().Replace(s, " ");
        s = AnalysisBlock().Replace(s, " ");
        s = UnclosedThink().Replace(s, " ");
        s = ReasoningSection().Replace(s, " ");
        s = ReasoningLine().Replace(s, " ");
        s = RoleLabelLine().Replace(s, " ");
        s = RoleLabelInline().Replace(s, " ");
        s = CodeFence().Replace(s, " ");
        s = InlineCode().Replace(s, " ");
        s = MdNoise().Replace(s, "");
        s = StripCommonPrefixes(s);
        s = MultiSpace().Replace(s.Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' '), " ").Trim();

        if (DetectPromptLeak(original) || DetectPromptLeak(s))
        {
            return SanitizeResult.Reject("LEAK");
        }

        if (string.IsNullOrWhiteSpace(s))
        {
            if (LooksLikeThinkOnly(original))
            {
                return SanitizeResult.Reject("THINK_ONLY");
            }

            if (HasFinalAnswerMarker(original))
            {
                return SanitizeResult.Reject("NO_FINAL_ANSWER");
            }

            return SanitizeResult.Reject("EMPTY");
        }

        s = StripFinalAnswerLabel(s);
        s = MultiSpace().Replace(s, " ").Trim();
        if (string.IsNullOrWhiteSpace(s))
        {
            return SanitizeResult.Reject("NO_FINAL_ANSWER");
        }

        return SanitizeResult.Accept(s);
    }

    /// <summary>
    /// 供单测/管线校验：消毒后得到可送 TTS 的文本；拒绝则返回 null。
    /// </summary>
    public static string? PrepareSpeakableOrNull(string? raw)
    {
        var r = Sanitize(raw);
        return r.Ok ? r.Text : null;
    }

    public static bool DetectPromptLeak(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var t = text;
        if (t.Contains("系统提示", StringComparison.Ordinal)
            && t.Length > 40)
        {
            return true;
        }

        if (t.Contains("USER_CONTENT_ONLY", StringComparison.OrdinalIgnoreCase)
            && t.Contains("输出硬规则", StringComparison.Ordinal)
            && t.Length > 80)
        {
            return true;
        }

        var hits = 0;
        foreach (var marker in new[]
                 {
                     "【弹幕回复任务】", "【礼物感谢任务】", "【进房欢迎任务】",
                     "【直播间短总结任务】", "【点赞感谢任务】",
                     "不要输出Markdown", "不要提「系统提示」",
                     "你是一名真实直播主播", "【输出硬规则】"
                 })
        {
            if (t.Contains(marker, StringComparison.Ordinal))
            {
                hits++;
            }
        }

        return hits >= 2 || (hits >= 1 && t.Length > 120 && t.Contains("规则：", StringComparison.Ordinal));
    }

    private static bool LooksLikeThinkOnly(string text)
    {
        var withoutThink = ThinkBlock().Replace(text, " ");
        withoutThink = AnalysisBlock().Replace(withoutThink, " ");
        withoutThink = UnclosedThink().Replace(withoutThink, " ");
        withoutThink = ReasoningSection().Replace(withoutThink, " ");
        withoutThink = MultiSpace().Replace(withoutThink, " ").Trim();
        return string.IsNullOrWhiteSpace(withoutThink)
               || text.Contains("<think>", StringComparison.OrdinalIgnoreCase)
               || ReasoningLine().IsMatch(text);
    }

    private static bool HasFinalAnswerMarker(string text)
        => text.Contains("Final Answer", StringComparison.OrdinalIgnoreCase)
           || text.Contains("最终回答", StringComparison.Ordinal)
           || text.Contains("回复：", StringComparison.Ordinal)
           || text.Contains("回复:", StringComparison.Ordinal);

    private static string StripFinalAnswerLabel(string s)
    {
        foreach (var prefix in new[]
                 {
                     "Final Answer:", "Final Answer：", "最终回答：", "最终回答:",
                     "回复：", "回复:", "答：", "答:"
                 })
        {
            if (s.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                s = s[prefix.Length..].Trim();
            }
        }

        return s;
    }

    private static string StripCommonPrefixes(string s)
    {
        foreach (var prefix in new[]
                 {
                     "System:", "System：", "Assistant:", "Assistant：",
                     "主播：", "主播:", "回复：", "回复:", "答："
                 })
        {
            if (s.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                s = s[prefix.Length..].Trim();
            }
        }

        return s;
    }

    public static string ShortHash(string text)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(text ?? ""));
        return Convert.ToHexString(bytes.AsSpan(0, 4)).ToLowerInvariant();
    }
}
