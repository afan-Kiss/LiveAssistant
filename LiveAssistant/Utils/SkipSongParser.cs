using System.Text.RegularExpressions;

namespace LiveAssistant.Utils;

public static partial class SkipSongParser
{
    [GeneratedRegex(@"^(切歌|下一首|跳过|skip)\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex SkipPattern();

    public static bool TryParse(string content)
    {
        content = content.Trim();
        return !string.IsNullOrEmpty(content) && SkipPattern().IsMatch(content);
    }
}
