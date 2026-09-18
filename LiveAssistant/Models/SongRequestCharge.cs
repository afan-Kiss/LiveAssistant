namespace LiveAssistant.Models;

public sealed class SongRequestCharge
{
    public long Id { get; set; }
    public long QueueItemId { get; set; }
    public string UserId { get; set; } = "";
    public string Nickname { get; set; } = "";
    public string ChargeType { get; set; } = SongRequestChargeType.Free;
    public int PointsDeducted { get; set; }
    public int CreditConsumed { get; set; }
    public string Status { get; set; } = SongRequestChargeStatus.Charged;
    public string? RefundReason { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public DateTime? FulfilledAt { get; set; }
    public DateTime? RefundedAt { get; set; }
}

public static class SongRequestChargeType
{
    public const string Free = "free";
    public const string Points = "points";
    public const string Credit = "credit";
}

public static class SongRequestChargeStatus
{
    public const string Charged = "charged";
    public const string Fulfilled = "fulfilled";
    public const string Refunded = "refunded";
}

public sealed class SongRequestRefundResult
{
    public bool Success { get; init; }
    public string Result { get; init; } = "failed";
    public int PointsRestored { get; init; }
    public int CreditRestored { get; init; }
    public string? FailureReason { get; init; }

    public static SongRequestRefundResult Ok(int pointsRestored, int creditRestored)
        => new()
        {
            Success = true,
            Result = "success",
            PointsRestored = pointsRestored,
            CreditRestored = creditRestored
        };

    public static SongRequestRefundResult AlreadyRefunded()
        => new()
        {
            Success = true,
            Result = "already_refunded"
        };

    public static SongRequestRefundResult NotRefundable(string reason)
        => new()
        {
            Success = true,
            Result = reason
        };

    public static SongRequestRefundResult Fail(string reason)
        => new()
        {
            Success = false,
            Result = "failed",
            FailureReason = reason
        };
}
