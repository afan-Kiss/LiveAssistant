using System.Text.RegularExpressions;

namespace LiveAssistant.Utils;

public static partial class SongRequestConfirmParser
{
    [GeneratedRegex(@"^(确认|确定|是的|好的|可以|ok|yes)\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex ConfirmPattern();

    [GeneratedRegex(@"^(取消|不要了|算了|不用了)\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex CancelPattern();

    public static bool IsConfirm(string content)
        => !string.IsNullOrWhiteSpace(content) && ConfirmPattern().IsMatch(content.Trim());

    public static bool IsCancel(string content)
        => !string.IsNullOrWhiteSpace(content) && CancelPattern().IsMatch(content.Trim());
}
