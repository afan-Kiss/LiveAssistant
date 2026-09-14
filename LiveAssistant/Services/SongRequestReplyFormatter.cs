namespace LiveAssistant.Services;

public sealed class SongRequestReplyEntry
{
    public required string UserId { get; init; }
    public required string Nickname { get; init; }
    public required string SongName { get; init; }
    public int AheadCount { get; init; }
}

public static class SongRequestReplyFormatter
{
    /// <summary>文案不含 @，由 mention 接口负责 @ 对方。</summary>
    public static string FormatForUser(IReadOnlyList<SongRequestReplyEntry> entries)
    {
        if (entries.Count == 0)
        {
            return "";
        }

        var ahead = Math.Max(0, entries[^1].AheadCount);
        if (entries.Count == 1)
        {
            return $"点歌成功《{entries[0].SongName}》\n前面还有{ahead}首";
        }

        var songs = string.Join("、", entries.Select(e => $"《{e.SongName}》"));
        return $"点歌成功 {songs}\n前面还有{ahead}首";
    }
}
