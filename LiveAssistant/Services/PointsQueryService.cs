using LiveAssistant.Database;
using LiveAssistant.Models;
using LiveAssistant.Utils;

namespace LiveAssistant.Services;

public sealed class PointsQueryService
{
    private readonly UserRepository _users;

    public PointsQueryService(UserRepository users)
    {
        _users = users;
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
        var msg = reply.Render("pointsQuery", new Dictionary<string, string>
        {
            ["name"] = item.Nickname,
            ["score"] = user.Points.ToString(),
            ["level"] = user.Level.ToString()
        });

        if (string.IsNullOrWhiteSpace(msg))
        {
            msg = $"@{item.Nickname} 你当前有 {user.Points} 积分，等级 Lv{user.Level}";
        }

        replyQueue.EnqueueMention(webRid, item.UserId, msg);
        return true;
    }
}
