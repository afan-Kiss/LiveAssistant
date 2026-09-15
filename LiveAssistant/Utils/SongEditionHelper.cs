namespace LiveAssistant.Utils;

/// <summary>
/// 识别 DJ/Remix 等衍生版，避免点歌取链失败时误落到改版音频。
/// </summary>
public static class SongEditionHelper
{
    private static readonly string[] DerivativeMarkers =
    [
        "dj",
        "remix",
        "混音",
        "串烧",
        "夜店",
        "加长",
        "伴奏",
        "纯音乐",
        "钢琴版",
        "吉他版",
        "翻唱",
        "cover",
        "live",
        "现场",
        "抖音热播",
        "加速",
        "喊麦"
    ];

    public static bool LooksLikeDerivativeEdition(string? songName, string? artist)
    {
        return ContainsDerivativeMarker(songName) || ContainsDerivativeMarker(artist);
    }

    public static bool IsCompatibleAlternate(
        string requestedName,
        string? requestedArtist,
        string? candidateName,
        string? candidateArtist)
    {
        requestedName = requestedName?.Trim() ?? "";
        candidateName = candidateName?.Trim() ?? "";
        if (requestedName.Length == 0 || candidateName.Length == 0)
        {
            return false;
        }

        var requestIsDerivative = LooksLikeDerivativeEdition(requestedName, requestedArtist);
        var candidateIsDerivative = LooksLikeDerivativeEdition(candidateName, candidateArtist);
        if (candidateIsDerivative && !requestIsDerivative)
        {
            return false;
        }

        if (!TitlesMatchForAlternate(requestedName, candidateName))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(requestedArtist))
        {
            return !HasExtraDjCollaborator(requestedArtist, candidateArtist);
        }

        return ArtistNameMatcher.IsSameArtist(requestedArtist, candidateArtist)
               && !HasExtraDjCollaborator(requestedArtist, candidateArtist);
    }

    private static bool TitlesMatchForAlternate(string requestedName, string candidateName)
    {
        if (requestedName.Equals(candidateName, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var normalizedRequest = NormalizeTitle(requestedName);
        var normalizedCandidate = NormalizeTitle(candidateName);
        return normalizedRequest.Length > 0
               && normalizedRequest.Equals(normalizedCandidate, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 去掉括号注释后比较，但若候选标题比点歌名多出实质后缀则拒绝（避免「死了都要爱」匹配到「死了都要爱DJ版」）。
    /// </summary>
    private static string NormalizeTitle(string title)
    {
        Span<char> buffer = stackalloc char[title.Length];
        var index = 0;
        var depth = 0;
        foreach (var ch in title)
        {
            if (ch is '(' or '（' or '[' or '【')
            {
                depth++;
                continue;
            }

            if (ch is ')' or '）' or ']' or '】')
            {
                if (depth > 0)
                {
                    depth--;
                }

                continue;
            }

            if (depth > 0 || char.IsWhiteSpace(ch) || ch is '-' or '_' or '/' or '·' or '.')
            {
                continue;
            }

            buffer[index++] = char.ToLowerInvariant(ch);
        }

        return index == 0 ? "" : new string(buffer[..index]);
    }

    private static bool ContainsDerivativeMarker(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var normalized = text.Trim();
        foreach (var marker in DerivativeMarkers)
        {
            if (marker.Equals("dj", StringComparison.Ordinal))
            {
                if (ContainsDjToken(normalized))
                {
                    return true;
                }

                continue;
            }

            if (normalized.Contains(marker, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool ContainsDjToken(string text)
    {
        for (var i = 0; i < text.Length - 1; i++)
        {
            if (char.ToLowerInvariant(text[i]) != 'd'
                || char.ToLowerInvariant(text[i + 1]) != 'j')
            {
                continue;
            }

            // 仅把 ASCII 字母数字当词边界，允许「爱DJ版」这类中文粘连
            var prevOk = i == 0 || !IsAsciiLetterOrDigit(text[i - 1]);
            var nextOk = i + 2 >= text.Length || !IsAsciiLetterOrDigit(text[i + 2]);
            if (prevOk && nextOk)
            {
                return true;
            }
        }

        return text.Contains("打碟", StringComparison.Ordinal);
    }

    private static bool IsAsciiLetterOrDigit(char ch)
        => ch is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9');

    private static bool HasExtraDjCollaborator(string? requestedArtist, string? candidateArtist)
    {
        if (string.IsNullOrWhiteSpace(candidateArtist))
        {
            return false;
        }

        if (LooksLikeDerivativeEdition(null, requestedArtist))
        {
            return false;
        }

        return ContainsDjToken(candidateArtist);
    }
}
