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
        var msg = reply.Render("pointsQuery", new Dictionary<string, string>
        {
            ["name"] = item.Nickname,
            ["score"] = user.Points.ToString(),
            ["level"] = user.Level.ToString(),
            // 兼容旧模板里的 {details}，不再展示礼物/点歌明细
            ["details"] = ""
        });

        if (string.IsNullOrWhiteSpace(msg))
        {
            msg = $"你当前有 {user.Points} 积分，等级 Lv{user.Level}";
        }

        replyQueue.EnqueueMention(webRid, item.UserId, msg, nickname: item.Nickname);
        return true;
    }
}
