using LiveAssistant.Models;

namespace LiveAssistant.Utils;

public static class PointsLedgerFormatter
{
    public static string FormatRecentEntries(IReadOnlyList<PointsLedgerEntry> entries, int maxItems = 5)
    {
        if (entries.Count == 0)
        {
            return "暂无积分明细";
        }

        var lines = entries
            .Take(maxItems)
            .Select(FormatEntry)
            .ToList();
        return string.Join("；", lines);
    }

    private static string FormatEntry(PointsLedgerEntry entry)
    {
        var sign = entry.Delta >= 0 ? "+" : "";
        var label = entry.Type switch
        {
            PointsTransactionType.Gift => "礼物",
            PointsTransactionType.SongRequest => "点歌",
            PointsTransactionType.SkipSong => "切歌",
            PointsTransactionType.AdminSet => "管理员设置",
            PointsTransactionType.AdminAdjust => "管理员调整",
            PointsTransactionType.Refund => "退还",
            _ => entry.Type
        };

        var detail = string.IsNullOrWhiteSpace(entry.Reason) ? label : entry.Reason;
        return $"{sign}{entry.Delta} {detail}";
    }
}
