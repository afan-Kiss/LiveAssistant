using LiveAssistant.Config;
using LiveAssistant.Database;
using LiveAssistant.Models;

namespace LiveAssistant.Services;

public sealed class KeywordReplyService
{
    private readonly ConfigManager _config;
    private readonly KeywordReplyRepository _repo;
    private readonly IAIReplyService _ai;

    public KeywordReplyService(ConfigManager config, KeywordReplyRepository repo, IAIReplyService ai)
    {
        _config = config;
        _repo = repo;
        _ai = ai;
    }

    public async Task TryHandleAsync(
        DanmakuItem item,
        ReplyQueue replyQueue,
        ReplyService reply,
        UserRepository users,
        string webRid)
    {
        if (!_config.Settings.KeywordReply.Enabled || string.IsNullOrWhiteSpace(item.Content))
        {
            return;
        }

        foreach (var rule in _repo.ListEnabled())
        {
            if (!item.Content.Contains(rule.Keyword, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var user = users.GetUser(item.UserId);
            var msg = RenderRule(rule, item, user, reply);
            if (!string.IsNullOrWhiteSpace(msg))
            {
                replyQueue.EnqueueMention(webRid, item.UserId, msg);
                return;
            }
        }

        var aiReply = await _ai.TryReplyAsync(item);
        if (!string.IsNullOrWhiteSpace(aiReply))
        {
            replyQueue.EnqueueMention(webRid, item.UserId, aiReply);
        }
    }

    private static string RenderRule(KeywordReplyRule rule, DanmakuItem item, UserProfile? user, ReplyService reply)
    {
        if (!string.IsNullOrWhiteSpace(rule.ReplyContent))
        {
            return rule.ReplyContent
                .Replace("{name}", item.Nickname, StringComparison.OrdinalIgnoreCase)
                .Replace("{song}", item.Content, StringComparison.OrdinalIgnoreCase)
                .Replace("{score}", (user?.Points ?? 0).ToString(), StringComparison.OrdinalIgnoreCase);
        }

        if (!string.IsNullOrWhiteSpace(rule.TemplateKey))
        {
            return reply.Render(rule.TemplateKey, new Dictionary<string, string>
            {
                ["name"] = item.Nickname,
                ["song"] = item.Content,
                ["queue"] = "0",
                ["gift"] = "",
                ["score"] = (user?.Points ?? 0).ToString()
            });
        }

        return "";
    }
}
