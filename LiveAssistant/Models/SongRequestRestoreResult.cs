namespace LiveAssistant.Models;

public sealed class SongRequestRestoreResult
{
    public bool Success { get; init; }
    public bool PointsRestored { get; init; }
    public bool CreditRestored { get; init; }
    public string? FailureReason { get; init; }
    public Exception? Exception { get; init; }

    public static SongRequestRestoreResult Ok(bool pointsRestored, bool creditRestored)
        => new()
        {
            Success = true,
            PointsRestored = pointsRestored,
            CreditRestored = creditRestored
        };

    public static SongRequestRestoreResult Fail(
        bool pointsRestored,
        bool creditRestored,
        string reason,
        Exception? ex = null)
        => new()
        {
            Success = false,
            PointsRestored = pointsRestored,
            CreditRestored = creditRestored,
            FailureReason = reason,
            Exception = ex
        };
}
