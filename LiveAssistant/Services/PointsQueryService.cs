using LiveAssistant.Database;
using LiveAssistant.Models;
using LiveAssistant.Utils;

namespace LiveAssistant.Services;

public sealed class PointsQueryService
{
    private readonly UserRepository _users;
    private readonly PointsLedgerRepository _ledger;

    public PointsQueryService(UserRepository users, PointsLedgerRepository ledger)
    {
        _users = users;
        _ledger = ledger;
    }

    public bool TryHandle(DanmakuItem item, string webRid, ReplyService reply, ReplyQueue replyQueue)
    {
        if (!PointsQueryParser.TryParse(item.Content))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(item.UserId))
        {
            return false;
        }

        var user = _users.EnsureUser(item.UserId, item.Nickname);
        user = _users.GetUser(item.UserId) ?? user;
        var recent = _ledger.ListByUser(item.UserId, 5);
        var details = PointsLedgerFormatter.FormatRecentEntries(recent);
        var msg = reply.Render("pointsQuery", new Dictionary<string, string>
        {
            ["name"] = item.Nickname,
            ["score"] = user.Points.ToString(),
            ["level"] = user.Level.ToString(),
            ["details"] = details
        });

        if (string.IsNullOrWhiteSpace(msg))
        {
            msg = $"@{item.Nickname} 你当前有 {user.Points} 积分，等级 Lv{user.Level}。最近：{details}";
        }

        replyQueue.EnqueueMention(webRid, item.UserId, msg);
        return true;
    }
}
