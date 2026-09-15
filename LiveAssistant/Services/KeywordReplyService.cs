using LiveAssistant.Config;
using LiveAssistant.Database;
using LiveAssistant.Models;

namespace LiveAssistant.Services;

public sealed class KeywordReplyService
{
    private readonly ConfigManager _config;
    private readonly KeywordReplyRepository _repo;
    private readonly IAIReplyService _ai;
    private readonly LogService? _log;
    private readonly object _cacheLock = new();
    private List<KeywordReplyRule>? _cachedEnabled;
    private int _cachedGeneration = -1;
    private int _emptyKeywordWarned;

    public KeywordReplyService(
        ConfigManager config,
        KeywordReplyRepository repo,
        IAIReplyService ai,
        LogService? log = null)
    {
        _config = config;
        _repo = repo;
        _ai = ai;
        _log = log;
    }

    public void InvalidateCache()
    {
        lock (_cacheLock)
        {
            _cachedEnabled = null;
            _cachedGeneration = -1;
        }
    }

    /// <summary>真正命中关键词并成功入回复队列时返回 true。</summary>
    public async Task<bool> TryHandleAsync(
        DanmakuItem item,
        ReplyQueue replyQueue,
        ReplyService reply,
        UserRepository users,
        string webRid)
    {
        if (!_config.Settings.KeywordReply.Enabled || string.IsNullOrWhiteSpace(item.Content))
        {
            return false;
        }

        foreach (var rule in GetEnabledRulesCached())
        {
            var keyword = rule.Keyword.Trim();
            if (string.IsNullOrEmpty(keyword))
            {
                continue;
            }

            if (!item.Content.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var user = users.GetUser(item.UserId);
            var msg = RenderRule(rule, item, user, reply);
            if (string.IsNullOrWhiteSpace(msg))
            {
                _log?.DouyinInfo(
                    $"KEYWORD_MATCH ruleId={rule.Id} keyword={keyword} userId={item.UserId} matched=true replyQueued=false");
                continue;
            }

            replyQueue.EnqueueMention(webRid, item.UserId, msg);
            _log?.DouyinInfo(
                $"KEYWORD_MATCH ruleId={rule.Id} keyword={keyword} userId={item.UserId} matched=true replyQueued=true");
            return true;
        }

        var aiReply = await _ai.TryReplyAsync(item);
        if (!string.IsNullOrWhiteSpace(aiReply))
        {
            replyQueue.EnqueueMention(webRid, item.UserId, aiReply);
            return true;
        }

        return false;
    }

    private List<KeywordReplyRule> GetEnabledRulesCached()
    {
        var generation = _repo.Generation;
        lock (_cacheLock)
        {
            if (_cachedEnabled != null && _cachedGeneration == generation)
            {
                return _cachedEnabled;
            }

            var list = new List<KeywordReplyRule>();
            foreach (var rule in _repo.ListEnabled())
            {
                if (string.IsNullOrWhiteSpace(rule.Keyword))
                {
                    if (Interlocked.Exchange(ref _emptyKeywordWarned, 1) == 0)
                    {
                        _log?.DouyinWarn(
                            $"KEYWORD_MATCH ruleId={rule.Id} keyword= empty_ignored=true matched=false " +
                            "（历史空关键词规则已忽略，避免全弹幕命中）");
                    }

                    continue;
                }

                if (rule.Enabled
                    && string.IsNullOrWhiteSpace(rule.ReplyContent)
                    && string.IsNullOrWhiteSpace(rule.TemplateKey))
                {
                    continue;
                }

                list.Add(rule);
            }

            _cachedEnabled = list;
            _cachedGeneration = generation;
            return list;
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
