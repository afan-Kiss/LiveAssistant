using LiveAssistant.Config;
using LiveAssistant.Database;
using LiveAssistant.Models;

namespace LiveAssistant.Services;

public sealed class BanVoteService : IDisposable
{
    private readonly ConfigManager _config;
    private readonly BanVoteRepository _votes;
    private readonly UserRepository _users;
    private readonly DouyinService _douyin;
    private readonly ReplyQueue _replyQueue;
    private readonly ReplyService _reply;
    private readonly SystemMessageService _system;
    private readonly LogService _log;
    private readonly CancellationTokenSource _lifetimeCts = new();
    private int _disposed;

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

    /// <summary>命中「禁言」业务命令时返回 true（无论投票是否成功）。</summary>
    public async Task<bool> TryHandleAsync(DanmakuItem item, string webRid, CancellationToken ct = default)
    {
        if (!_config.Settings.BanVote.Enabled)
        {
            return false;
        }

        var content = item.Content.Trim();
        if (!content.StartsWith("禁言", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var targetName = content.Length > 2 ? content[2..].Trim() : "";
        if (string.IsNullOrWhiteSpace(targetName))
        {
            return true;
        }

        var target = _users.FindByNickname(targetName);
        if (target == null)
        {
            var lookup = await _douyin.LookupUserAsync(webRid, targetName, ct);
            if (lookup == null || string.IsNullOrWhiteSpace(lookup.UserId))
            {
                _system.Add($"禁言投票: 未找到用户 {targetName}");
                return true;
            }

            target = _users.EnsureUser(lookup.UserId, lookup.Nickname ?? targetName);
        }

        if (string.IsNullOrWhiteSpace(item.UserId))
        {
            return true;
        }

        if (item.UserId == target.UserId)
        {
            _system.Add($"禁言投票: {item.Nickname} 不能投票禁言自己");
            return true;
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
            SendVoteProgressReply(webRid, item, target, session, session.VoteCount);
            return true;
        }

        var count = _votes.AddVote(session.Id, target.UserId, target.Nickname, item.UserId, item.Nickname);
        _system.Add($"禁言投票: {item.Nickname} 投票禁言 {target.Nickname} ({count}/{session.RequiredVotes})");
        _log.BanInfo($"投票 user={item.Nickname} target={target.Nickname} count={count}/{session.RequiredVotes}");
        SendVoteProgressReply(webRid, item, target, session, count);

        if (count >= session.RequiredVotes)
        {
            await ExecuteBanAsync(target, webRid, session);
        }

        return true;
    }

    /// <summary>兼容旧调用名。</summary>
    public Task HandleDanmakuAsync(DanmakuItem item, string webRid, CancellationToken ct = default)
        => TryHandleAsync(item, webRid, ct);

    private async Task ExecuteBanAsync(UserProfile target, string webRid, BanVoteSession session)
    {
        var ok = await _douyin.ModSilenceAsync(webRid, target.UserId, "silence", _lifetimeCts.Token);
        var duration = _config.Settings.BanVote.BanDurationSeconds;

        if (!ok)
        {
            _votes.CompleteSession(session.Id, "api_failed");
            var failMsg = $"禁言投票通过，但平台禁言 API 失败: {target.Nickname}（本地未设为禁言）";
            _system.Add(failMsg);
            _log.BanInfo(failMsg);
            return;
        }

        _users.SetStatus(target.UserId, UserStatus.Muted);
        var msg = $"禁言投票通过，已禁言 {target.Nickname} {duration}秒 (发起人 {session.InitiatorNickname})";
        _system.Add(msg);
        _log.BanInfo(msg);
        _votes.CompleteSession(session.Id, "banned");

        if (duration > 0)
        {
            var lifetime = _lifetimeCts.Token;
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(duration), lifetime);
                    await _douyin.ModSilenceAsync(webRid, target.UserId, "unsilence", lifetime);
                    _users.SetStatus(target.UserId, UserStatus.Active);
                    _log.BanInfo($"自动解除禁言 target={target.Nickname} duration={duration}s");
                }
                catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
                {
                    // service disposing
                }
                catch (Exception ex)
                {
                    _log.BanWarn($"自动解除禁言失败: {ex.Message}");
                }
            }, lifetime);
        }

        var reply = _reply.Render("banVotePassed", new Dictionary<string, string>
        {
            ["name"] = target.Nickname
        });
        if (string.IsNullOrWhiteSpace(reply))
        {
            reply = "已被投票禁言";
        }

        _replyQueue.EnqueueMention(webRid, target.UserId, reply);
    }

    private void SendVoteProgressReply(
        string webRid,
        DanmakuItem voter,
        UserProfile target,
        BanVoteSession session,
        int count)
    {
        if (string.IsNullOrWhiteSpace(voter.UserId))
        {
            return;
        }

        var msg = _reply.Render("banVoteProgress", new Dictionary<string, string>
        {
            ["name"] = voter.Nickname,
            ["target"] = target.Nickname,
            ["count"] = count.ToString(),
            ["required"] = session.RequiredVotes.ToString()
        });
        if (string.IsNullOrWhiteSpace(msg))
        {
            msg = $"已投票禁言 {target.Nickname}，当前 {count}/{session.RequiredVotes} 票";
        }

        _replyQueue.EnqueueMention(webRid, voter.UserId, msg);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try { _lifetimeCts.Cancel(); } catch { /* ignore */ }
        try { _lifetimeCts.Dispose(); } catch { /* ignore */ }
    }
}
