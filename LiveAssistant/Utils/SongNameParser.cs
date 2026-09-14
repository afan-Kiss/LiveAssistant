using System.Text.RegularExpressions;

namespace LiveAssistant.Utils;

public static partial class SongNameParser
{
    // 排除机器人回复（点歌成功/失败）；支持「点歌 泡沫」「点歌:泡沫」「点歌泡沫」
    [GeneratedRegex(@"^点歌(?!成功|失败|太频繁)(?:\s+|[:：]\s*|(?=[^:：\s]))(.+)$", RegexOptions.IgnoreCase)]
    private static partial Regex RequestPattern();

    public static bool IsBotReply(string content)
    {
        content = content.Trim();
        return content.StartsWith("点歌成功", StringComparison.Ordinal)
               || content.StartsWith("点歌失败", StringComparison.Ordinal)
               || content.StartsWith("已切歌", StringComparison.Ordinal)
               || content.StartsWith("找到多首", StringComparison.Ordinal)
               || content.StartsWith("是否点歌", StringComparison.Ordinal)
               || content.StartsWith("请回复 确定", StringComparison.Ordinal)
               || content.StartsWith("是否确定点歌", StringComparison.Ordinal);
    }

    public static bool TryParse(string content, out string songName)
    {
        songName = "";
        content = content.Trim();
        if (string.IsNullOrEmpty(content) || IsBotReply(content))
        {
            return false;
        }

        var match = RequestPattern().Match(content);
        if (!match.Success)
        {
            return false;
        }

        songName = match.Groups[1].Value.Trim();
        return !string.IsNullOrEmpty(songName);
    }
}
