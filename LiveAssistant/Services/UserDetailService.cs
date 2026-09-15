using LiveAssistant.Database;
using LiveAssistant.Models;

namespace LiveAssistant.Services;

/// <summary>
/// 用户详情聚合（积分流水、礼物累计等）。
/// </summary>
public sealed class UserDetailService
{
    private readonly UserRepository _users;
    private readonly GiftRepository _gifts;
    private readonly PointsLedgerRepository _ledger;

    public UserDetailService(UserRepository users, GiftRepository gifts, PointsLedgerRepository ledger)
    {
        _users = users;
        _gifts = gifts;
        _ledger = ledger;
    }

    public UserDetailDto? GetDetail(string userId)
    {
        var user = _users.GetUser(userId);
        if (user == null)
        {
            return null;
        }

        user.TotalGiftPoints = _gifts.SumGiftPointsForUser(userId);
        return new UserDetailDto
        {
            UserId = user.UserId,
            Nickname = user.Nickname,
            Level = user.Level,
            Points = user.Points,
            RequestCount = user.RequestCount,
            TotalGiftPoints = user.TotalGiftPoints,
            LastInteractionAt = user.LastInteractionAt,
            Role = user.Role.ToString(),
            Status = user.Status.ToString(),
            SongPermissionCredits = user.SongPermissionCredits,
            SongPermissionUnlimited = user.SongPermissionUnlimited,
            TodayRequestCount = _users.CountRequestsToday(userId)
        };
    }

    public List<PointsLedgerEntry> GetLedger(string userId, int limit = 50, int offset = 0)
        => _ledger.ListByUser(userId, limit, offset);

    public int CountLedger(string userId) => _ledger.CountByUser(userId);
}

public sealed class UserDetailDto
{
    public string UserId { get; set; } = "";
    public string Nickname { get; set; } = "";
    public int Level { get; set; }
    public int Points { get; set; }
    public int RequestCount { get; set; }
    public int TotalGiftPoints { get; set; }
    public int TodayRequestCount { get; set; }
    public DateTime? LastInteractionAt { get; set; }
    public string Role { get; set; } = "";
    public string Status { get; set; } = "";
    public int SongPermissionCredits { get; set; }
    public bool SongPermissionUnlimited { get; set; }
}
