using LiveAssistant.Config;
using LiveAssistant.Models;

namespace LiveAssistant.Services;

public sealed class WelcomeService
{
    private readonly ConfigManager _config;
    private readonly ReplyService _reply;
    private readonly ReplyQueue _replyQueue;
    private readonly SystemMessageService _system;

    public WelcomeService(
        ConfigManager config,
        ReplyService reply,
        ReplyQueue replyQueue,
        SystemMessageService system)
    {
        _config = config;
        _reply = reply;
        _replyQueue = replyQueue;
        _system = system;
    }

    public void HandleMemberJoin(DanmakuItem item, string webRid)
    {
        if (!_config.Settings.Welcome.Enabled || item.MsgType != "member")
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(item.UserId))
        {
            return;
        }

        var msg = _reply.Render("welcome", new Dictionary<string, string>
        {
            ["name"] = item.Nickname
        });
        if (string.IsNullOrWhiteSpace(msg))
        {
            msg = $"欢迎 {item.Nickname} 进入直播间";
        }

        _replyQueue.EnqueueMention(webRid, item.UserId, msg);
        _system.Add($"欢迎: {item.Nickname}");
    }
}
