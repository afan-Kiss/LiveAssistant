using LiveAssistant.Config;
using LiveAssistant.Database;
using LiveAssistant.Models;

namespace LiveAssistant.Services;

public sealed class WelcomeService
{
    private readonly ConfigManager _config;
    private readonly ReplyService _reply;
    private readonly ReplyQueue _replyQueue;
    private readonly SystemMessageService _system;
    private readonly WelcomeCooldownRepository _cooldown;

    public WelcomeService(
        ConfigManager config,
        ReplyService reply,
        ReplyQueue replyQueue,
        SystemMessageService system,
        WelcomeCooldownRepository cooldown)
    {
        _config = config;
        _reply = reply;
        _replyQueue = replyQueue;
        _system = system;
        _cooldown = cooldown;
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

        if (!_cooldown.ShouldWelcome(item.UserId, _config.Settings.Welcome.CooldownSeconds))
        {
            return;
        }

        // 进房仅走 AI 语音欢迎（AiSpeechCoordinator），不再发送 @ 欢迎弹幕，避免出站回显污染词云/AI。
        _cooldown.RecordWelcome(item.UserId);
        _system.Add($"进房: {item.Nickname}（AI语音欢迎）");
    }
}
