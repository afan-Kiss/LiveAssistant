namespace LiveAssistant.Models;

public sealed class PointsLedgerEntry
{
    public long Id { get; set; }
    public string UserId { get; set; } = "";
    public int Delta { get; set; }
    public int BalanceAfter { get; set; }
    public string Type { get; set; } = "";
    public string Reason { get; set; } = "";
    public string? RefId { get; set; }
    public string? OperatorName { get; set; }
    public DateTime CreatedAt { get; set; }
}

public static class PointsTransactionType
{
    public const string Gift = "gift";
    public const string SongRequest = "song_request";
    public const string SkipSong = "skip_song";
    public const string AdminSet = "admin_set";
    public const string AdminAdjust = "admin_adjust";
    public const string Refund = "refund";
}
