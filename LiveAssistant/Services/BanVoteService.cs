using LiveAssistant.Config;
using LiveAssistant.Database;
using LiveAssistant.Models;

namespace LiveAssistant.Services;

public sealed class BanVoteService
{
    private readonly ConfigManager _config;
    private readonly BanVoteRepository _votes;
    private readonly UserRepository _users;
    private readonly DouyinService _douyin;
    private readonly ReplyQueue _replyQueue;
    private readonly ReplyService _reply;
    private readonly SystemMessageService _system;
    private readonly LogService _log;

    public BanVoteService(
        ConfigManager config,
        BanVoteRepository votes,
        UserRepository users,
        DouyinService douyin,
        ReplyQueue replyQueue,
        ReplyService reply,
        SystemMessageService system,
        LogService log)
    {
        _config = config;
        _votes = votes;
        _users = users;
        _douyin = douyin;
        _replyQueue = replyQueue;
        _reply = reply;
        _system = system;
        _log = log;
    }

    public async Task HandleDanmakuAsync(DanmakuItem item, string webRid, CancellationToken ct = default)
    {
        if (!_config.Settings.BanVote.Enabled)
        {
            return;
        }

        var content = item.Content.Trim();
        if (!content.StartsWith("禁言", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var targetName = content.Length > 2 ? content[2..].Trim() : "";
        if (string.IsNullOrWhiteSpace(targetName))
        {
            return;
        }

        var target = _users.FindByNickname(targetName);
        if (target == null)
        {
            var lookup = await _douyin.LookupUserAsync(webRid, targetName, ct);
            if (lookup == null || string.IsNullOrWhiteSpace(lookup.UserId))
            {
                _system.Add($"禁言投票: 未找到用户 {targetName}");
                return;
            }

            target = _users.EnsureUser(lookup.UserId, lookup.Nickname ?? targetName);
        }

        if (string.IsNullOrWhiteSpace(item.UserId))
        {
            return;
        }

        var session = _votes.GetActiveSession(target.UserId);
        if (session != null && _votes.IsExpired(session))
        {
            _votes.ExpireSession(session.Id);
            _log.BanInfo($"投票过期 session={session.Id} target={target.Nickname}");
            session = null;
        }

        var isNew = session == null;
        if (session == null)
        {
            session = _votes.CreateSession(
                target.UserId,
                target.Nickname,
                _config.Settings.BanVote.RequiredVotes,
                _config.Settings.BanVote.WindowSeconds,
                item.UserId,
                item.Nickname);
            _system.Add($"禁言投票已创建: {target.Nickname} (发起人 {item.Nickname})");
            _log.BanInfo($"投票创建 initiator={item.Nickname} target={target.Nickname}");
        }

        if (!isNew && _votes.HasVoted(session.Id, item.UserId))
        {
            return;
        }

        var count = _votes.AddVote(session.Id, target.UserId, target.Nickname, item.UserId, item.Nickname);
        _system.Add($"禁言投票: {item.Nickname} 投票禁言 {target.Nickname} ({count}/{session.RequiredVotes})");
        _log.BanInfo($"投票 user={item.Nickname} target={target.Nickname} count={count}/{session.RequiredVotes}");

        if (count >= session.RequiredVotes)
        {
            await ExecuteBanAsync(target, webRid, session, ct);
        }
    }

    private async Task ExecuteBanAsync(UserProfile target, string webRid, BanVoteSession session, CancellationToken ct)
    {
        var ok = await _douyin.ModSilenceAsync(webRid, target.UserId, "silence", ct);
        _users.SetStatus(target.UserId, UserStatus.Muted);
        var duration = _config.Settings.BanVote.BanDurationSeconds;
        var msg = ok
            ? $"禁言投票通过，已禁言 {target.Nickname} {duration}秒 (发起人 {session.InitiatorNickname})"
            : $"禁言投票通过，但平台禁言 API 失败: {target.Nickname}";
        _system.Add(msg);
        _log.BanInfo(msg);
        _votes.CompleteSession(session.Id, ok ? "banned" : "api_failed");

        if (ok && duration > 0)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(duration), ct);
                    await _douyin.ModSilenceAsync(webRid, target.UserId, "unsilence", ct);
                    _users.SetStatus(target.UserId, UserStatus.Active);
                    _log.BanInfo($"自动解除禁言 target={target.Nickname} duration={duration}s");
                }
                catch (Exception ex)
                {
                    _log.BanWarn($"自动解除禁言失败: {ex.Message}");
                }
            });
        }

        var reply = _reply.Render("banVotePassed", new Dictionary<string, string>
        {
            ["name"] = target.Nickname
        });
        if (!string.IsNullOrWhiteSpace(reply))
        {
            _replyQueue.EnqueueMention(webRid, target.UserId, reply);
        }
    }
}
