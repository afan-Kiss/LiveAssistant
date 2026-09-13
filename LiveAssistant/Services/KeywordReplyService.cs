using LiveAssistant.Config;
using LiveAssistant.Database;
using LiveAssistant.Models;

namespace LiveAssistant.Services;

/// <summary>
/// 关键词回复预留：匹配关键词后返回模板 key，由调用方渲染发送。
/// </summary>
public sealed class KeywordReplyService
{
    private readonly ConfigManager _config;
    private readonly KeywordReplyRepository _repo;

    public KeywordReplyService(ConfigManager config, KeywordReplyRepository repo)
    {
        _config = config;
        _repo = repo;
    }

    public string? MatchTemplateKey(string content)
    {
        if (!_config.Settings.KeywordReply.Enabled || string.IsNullOrWhiteSpace(content))
        {
            return null;
        }

        foreach (var rule in _repo.ListEnabled())
        {
            if (content.Contains(rule.Keyword, StringComparison.OrdinalIgnoreCase))
            {
                return rule.TemplateKey;
            }
        }
        return null;
    }

    public void TryHandle(DanmakuItem item, ReplyQueue replyQueue, ReplyService reply, string webRid)
    {
        var templateKey = MatchTemplateKey(item.Content);
        if (templateKey == null)
        {
            return;
        }

        var msg = reply.Render(templateKey, new Dictionary<string, string>
        {
            ["name"] = item.Nickname,
            ["song"] = item.Content,
            ["queue"] = "0",
            ["gift"] = ""
        });
        if (!string.IsNullOrWhiteSpace(msg))
        {
            replyQueue.EnqueueMention(webRid, item.UserId, msg);
        }
    }
}
