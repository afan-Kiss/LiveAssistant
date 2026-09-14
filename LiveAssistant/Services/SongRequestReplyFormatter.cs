namespace LiveAssistant.Services;

public sealed class SongRequestReplyEntry
{
    public required string Nickname { get; init; }
    public required string SongName { get; init; }
}

public static class SongRequestReplyFormatter
{
    public static string FormatMerged(IReadOnlyList<SongRequestReplyEntry> entries, int queueCount)
    {
        if (entries.Count == 0)
        {
            return "";
        }

        if (entries.Count == 1)
        {
            var one = entries[0];
            return $"@{one.Nickname} 点歌成功《{one.SongName}》\n前面还有{Math.Max(0, queueCount - 1)}首";
        }

        var lines = entries.Select(e => $"{e.Nickname}-{e.SongName}");
        return "🎵 点歌:\n" + string.Join("\n", lines) + $"\n\n当前排队{queueCount}首";
    }
}
