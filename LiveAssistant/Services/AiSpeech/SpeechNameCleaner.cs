using System.Globalization;
using System.Text;

namespace LiveAssistant.Services.AiSpeech;

/// <summary>
/// 清洗昵称供 TTS：去 emoji/符号，保留中日韩/字母/数字，约 8 字。
/// </summary>
public static class SpeechNameCleaner
{
    public const int DefaultMaxChars = 8;

    public static string Clean(string? nickname, int maxChars = DefaultMaxChars)
    {
        if (string.IsNullOrWhiteSpace(nickname))
        {
            return "";
        }

        var sb = new StringBuilder(nickname.Length);
        for (var i = 0; i < nickname.Length; i++)
        {
            var ch = nickname[i];
            if (char.IsSurrogate(ch))
            {
                if (char.IsHighSurrogate(ch) && i + 1 < nickname.Length && char.IsLowSurrogate(nickname[i + 1]))
                {
                    i++;
                }

                continue;
            }

            if (IsAllowed(ch))
            {
                sb.Append(ch);
            }
        }

        var s = sb.ToString().Trim();
        if (s.Length == 0)
        {
            return "";
        }

        if (maxChars > 0 && CountChars(s) > maxChars)
        {
            s = Truncate(s, maxChars);
        }

        return s;
    }

    private static bool IsAllowed(char ch)
    {
        if (char.IsLetterOrDigit(ch))
        {
            return true;
        }

        // CJK 统一汉字及扩展常见区
        if (ch is >= '\u4E00' and <= '\u9FFF'
            or >= '\u3400' and <= '\u4DBF'
            or >= '\uF900' and <= '\uFAFF')
        {
            return true;
        }

        // 常见中日韩标点不保留；下划线/间隔允许少量
        if (ch is '_' or '·' or '・')
        {
            return true;
        }

        var cat = char.GetUnicodeCategory(ch);
        if (cat is UnicodeCategory.OtherLetter)
        {
            return true;
        }

        return false;
    }

    private static int CountChars(string text)
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

    private static string Truncate(string text, int maxChars)
    {
        var sb = new StringBuilder();
        var n = 0;
        foreach (var ch in text)
        {
            if (char.IsWhiteSpace(ch))
            {
                continue;
            }

            sb.Append(ch);
            n++;
            if (n >= maxChars)
            {
                break;
            }
        }

        return sb.ToString();
    }
}
