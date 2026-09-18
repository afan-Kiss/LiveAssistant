using LiveAssistant.Config;
using LiveAssistant.Database;
using LiveAssistant.Models;
using LiveAssistant.Utils;

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

        var isKuaishouRoom = KuaishouService.IsKuaishouRoom(webRid);
        var platform = isKuaishouRoom ? "kuaishou" : "douyin";
        var target = _users.FindByNickname(targetName, platform);
        if (target == null)
        {
            // 快手房间无法走抖音查用户 API，仅支持已互动过的本地用户
            if (isKuaishouRoom)
            {
                _system.Add($"禁言投票: 未找到用户 {targetName}（快手仅支持本场已互动用户）");
                return true;
            }

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

        // 禁止跨平台投票：抖音观众不能给快手用户投票，反之亦然
        var voterIsKs = item.UserId.StartsWith("ks:", StringComparison.OrdinalIgnoreCase);
        var targetIsKs = target.UserId.StartsWith("ks:", StringComparison.OrdinalIgnoreCase);
        if (voterIsKs != targetIsKs)
        {
            _system.Add($"禁言投票: 不能跨平台投票（{item.Nickname} → {target.Nickname}）");
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
        var duration = _config.Settings.BanVote.BanDurationSeconds;
        var isKuaishou = KuaishouService.IsKuaishouRoom(webRid);

        // 快手无平台禁言 API：仅本地禁言（点歌权限），并 @ 通知
        var ok = isKuaishou || await _douyin.ModSilenceAsync(
            webRid, PlatformUserIds.RawForApi(target.UserId), "silence", _lifetimeCts.Token);

        if (!ok)
        {
            _votes.CompleteSession(session.Id, "api_failed");
            var failMsg = $"禁言投票通过，但平台禁言 API 失败: {target.Nickname}（本地未设为禁言）";
            _system.Add(failMsg);
            _log.BanInfo(failMsg);
            return;
        }

        _users.SetStatus(target.UserId, UserStatus.Muted);
        var msg = isKuaishou
            ? $"禁言投票通过，已本地禁言 {target.Nickname} {duration}秒（快手无平台禁言，仅限制点歌等） (发起人 {session.InitiatorNickname})"
            : $"禁言投票通过，已禁言 {target.Nickname} {duration}秒 (发起人 {session.InitiatorNickname})";
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
                    if (!isKuaishou)
                    {
                        await _douyin.ModSilenceAsync(
                            webRid, PlatformUserIds.RawForApi(target.UserId), "unsilence", lifetime);
                    }

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

        _replyQueue.EnqueueMention(webRid, target.UserId, reply, nickname: target.Nickname);
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

        _replyQueue.EnqueueMention(webRid, voter.UserId, msg, nickname: voter.Nickname);
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
