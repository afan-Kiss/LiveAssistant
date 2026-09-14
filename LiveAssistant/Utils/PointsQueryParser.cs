using System.Text.RegularExpressions;

namespace LiveAssistant.Utils;

public static partial class PointsQueryParser
{
    [GeneratedRegex(@"^(查积分|我的积分|积分查询)\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex QueryPattern();

    public static bool TryParse(string content)
    {
        content = content.Trim();
        return !string.IsNullOrEmpty(content) && QueryPattern().IsMatch(content);
    }
}
