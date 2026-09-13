using System.Text.RegularExpressions;

namespace LiveAssistant.Utils;

public static partial class SongNameParser
{
    [GeneratedRegex(@"^点歌\s*[:：]?\s*(.+)$", RegexOptions.IgnoreCase)]
    private static partial Regex RequestPattern();

    public static bool TryParse(string content, out string songName)
    {
        songName = "";
        content = content.Trim();
        if (string.IsNullOrEmpty(content))
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
