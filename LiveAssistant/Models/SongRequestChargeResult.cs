namespace LiveAssistant.Models;

public sealed class SongRequestChargeResult
{
    public bool Success { get; init; }
    public int PointsBefore { get; init; }
    public int PointsAfter { get; init; }
    public int PointsDeducted { get; init; }
    public bool CreditConsumed { get; init; }
    public string ChargeType { get; init; } = SongRequestChargeType.Free;
    public string? FailureReason { get; init; }

    public static SongRequestChargeResult Ok(
        int pointsBefore, int pointsAfter, int pointsDeducted, bool creditConsumed,
        string chargeType = SongRequestChargeType.Free)
        => new()
        {
            Success = true,
            PointsBefore = pointsBefore,
            PointsAfter = pointsAfter,
            PointsDeducted = pointsDeducted,
            CreditConsumed = creditConsumed,
            ChargeType = chargeType
        };

    public static SongRequestChargeResult Fail(
        int pointsBefore, int pointsAfter, int pointsDeducted, bool creditConsumed, string reason)
        => new()
        {
            Success = false,
            PointsBefore = pointsBefore,
            PointsAfter = pointsAfter,
            PointsDeducted = pointsDeducted,
            CreditConsumed = creditConsumed,
            FailureReason = reason
        };
}
