using LiveAssistant.Config;
using LiveAssistant.Models;
using LiveAssistant.Utils;

namespace LiveAssistant.Services;

public sealed class SongRequestService
{
    private readonly ConfigManager _config;
    private readonly KugouService _kugou;
    private readonly QueueService _queue;
    private readonly SongRequestPermissionService _permission;
    private readonly ReplyService _reply;
    private readonly ReplyQueue _replyQueue;
    private readonly SystemMessageService _system;
    private readonly LogService _log;
    private readonly SongRequestUserGateRegistry _userGates = new();
    private readonly SongRequestDeduper _deduper = new();
    private readonly SongRequestSessionStore _sessions = new();

    public SongRequestService(
        ConfigManager config,
        KugouService kugou,
        QueueService queue,
        SongRequestPermissionService permission,
        ReplyService reply,
        ReplyQueue replyQueue,
        SystemMessageService system,
        LogService log)
    {
        _config = config;
        _kugou = kugou;
        _queue = queue;
        _permission = permission;
        _reply = reply;
        _replyQueue = replyQueue;
        _system = system;
        _log = log;
    }

    public event Action? RequestHandled;

    public async Task HandleDanmakuAsync(DanmakuItem item, string webRid, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(item.UserId))
        {
            return;
        }

        var userGate = await _userGates.AcquireAsync(item.UserId, ct);
        using (userGate)
        {
            var session = _sessions.Get(item.UserId);
            if (session != null)
            {
                if (await HandleSessionAsync(item, webRid, session, ct))
                {
                    return;
                }
            }

            if (!SongNameParser.TryParse(item.Content, out var songName))
            {
                return;
            }

            if (!_deduper.TryAdmit(item.UserId, item.Content))
            {
                _log.DouyinInfo($"点歌去重: {item.Nickname} {item.Content}");
                return;
            }

            _sessions.Clear(item.UserId);
            await StartNewRequestAsync(item, webRid, songName, ct);
        }
    }

    private async Task<bool> HandleSessionAsync(
        DanmakuItem item,
        string webRid,
        SongRequestSession session,
        CancellationToken ct)
    {
        if (SongRequestConfirmParser.IsCancel(item.Content))
        {
            _sessions.Clear(item.UserId);
            SendReply(webRid, item.UserId, RenderOrFallback("songRequestCancelled", item.Nickname, "已取消点歌"));
            return true;
        }

        if (SongNameParser.TryParse(item.Content, out _))
        {
            _sessions.Clear(item.UserId);
            return false;
        }

        if (session.Step == SongRequestSessionStep.ChooseArtist)
        {
            MigrateLegacyChooseArtistSession(session, item.Content);
            _sessions.Set(session);
        }

        return session.Step switch
        {
            SongRequestSessionStep.Confirm => await HandleConfirmAsync(item, webRid, session, ct),
            _ => false
        };
    }

    private static void MigrateLegacyChooseArtistSession(SongRequestSession session, string? userInput = null)
    {
        if (session.Step != SongRequestSessionStep.ChooseArtist)
        {
            return;
        }

        SongSearchCandidate? picked = null;
        if (!string.IsNullOrWhiteSpace(userInput) && session.Candidates.Count > 0)
        {
            picked = ArtistNameMatcher.Match(userInput, session.Candidates);
        }

        session.Selected = picked
            ?? (session.Candidates.Count > 0 ? session.Candidates[0] : session.Selected);
        session.Step = SongRequestSessionStep.Confirm;
    }

    private async Task<bool> HandleConfirmAsync(
        DanmakuItem item,
        string webRid,
        SongRequestSession session,
        CancellationToken ct)
    {
        if (!SongRequestConfirmParser.IsConfirm(item.Content))
        {
            await SendConfirmPromptAsync(item, webRid, session, ct, remind: true);
            return true;
        }

        if (session.ConfirmConsumed)
        {
            _log.DouyinInfo($"点歌确认去重: {item.Nickname} user={item.UserId}");
            return true;
        }

        var selected = session.Selected;
        if (selected == null)
        {
            _sessions.Clear(item.UserId);
            return true;
        }

        var permission = _permission.Evaluate(item);
        if (!permission.Allowed)
        {
            _sessions.Clear(item.UserId);
            Reject(item, webRid, selected.SongName, item.Nickname, permission);
            return true;
        }

        var songPermission = _permission.EvaluateSong(selected.SongName, permission.User!, item);
        if (!songPermission.Allowed)
        {
            _sessions.Clear(item.UserId);
            Reject(item, webRid, selected.SongName, item.Nickname, songPermission);
            return true;
        }

        session.ConfirmConsumed = true;
        _sessions.Set(session);

        _system.Add($"{item.Nickname} 确认点歌《{selected.SongName}》- {selected.Artist}，正在解析...");
        TrackInfo? track;
        try
        {
            track = await _kugou.ResolveCandidateAsync(selected, item.Nickname, ct);
        }
        catch (Exception ex)
        {
            _log.Error("kugou", $"取链异常: {selected.SongName}", ex);
            track = null;
        }

        if (!IsTrackPlayable(track))
        {
            FailResolve(item, webRid, selected.SongName, selected.Artist, track);
            _sessions.Clear(item.UserId);
            return true;
        }

        await EnqueueTrackAsync(item, webRid, track!, permission.User!, ct);
        _sessions.Clear(item.UserId);
        return true;
    }

    private async Task StartNewRequestAsync(DanmakuItem item, string webRid, string songName, CancellationToken ct)
    {
        var displayUser = string.IsNullOrWhiteSpace(item.Nickname) ? item.UserId : item.Nickname;
        var permission = _permission.Evaluate(item);
        if (!permission.Allowed)
        {
            Reject(item, webRid, songName, displayUser, permission);
            return;
        }

        var songPermission = _permission.EvaluateSong(songName, permission.User!, item);
        if (!songPermission.Allowed)
        {
            Reject(item, webRid, songName, displayUser, songPermission);
            return;
        }

        _system.Add($"{item.Nickname} 点歌《{songName}》，正在搜索...");
        var candidates = await _kugou.SearchCandidatesAsync(songName, displayLimit: 3, ct);
        if (candidates.Count == 0)
        {
            var notFound = RenderOrFallback("songNotFound", item.Nickname,
                $"没找到《{songName}》，请换个歌名", ("song", songName));
            SendReply(webRid, item.UserId, notFound);
            _system.Add($"未找到歌曲《{songName}》");
            _log.LogSongRequest(displayUser, songName, false, "未找到歌曲");
            return;
        }

        var selected = candidates[0];
        if (candidates.Count > 1)
        {
            var artistList = ArtistNameMatcher.FormatArtistList(candidates);
            _system.Add($"{item.Nickname} 点歌《{songName}》自动选用 {selected.Artist}（候选：{artistList}）");
            _log.DouyinInfo($"点歌自动选用: keyword={songName} artist={selected.Artist} candidates={artistList}");
        }

        var session = new SongRequestSession
        {
            UserId = item.UserId,
            Nickname = item.Nickname,
            Keyword = songName,
            Step = SongRequestSessionStep.Confirm,
            Selected = selected
        };
        _sessions.Set(session);
        await SendConfirmPromptAsync(item, webRid, session, ct);
    }

    private Task SendConfirmPromptAsync(
        DanmakuItem item,
        string webRid,
        SongRequestSession session,
        CancellationToken ct,
        bool remind = false)
    {
        _ = ct;
        var selected = session.Selected;
        if (selected == null)
        {
            return Task.CompletedTask;
        }

        var user = _permission.Evaluate(item).User ?? new UserProfile { UserId = item.UserId, Nickname = item.Nickname };
        var cost = _permission.GetPointsCost(user);
        string msg;
        if (cost > 0)
        {
            msg = RenderOrFallback("songRequestConfirmPoints", item.Nickname,
                $"是否确定点歌《{selected.SongName}》- {selected.Artist}？需要 {cost} 积分，回复 确定 开始点歌",
                ("song", selected.SongName), ("artist", selected.Artist), ("cost", cost.ToString()));
        }
        else
        {
            msg = RenderOrFallback("songRequestConfirm", item.Nickname,
                $"是否确定点歌《{selected.SongName}》- {selected.Artist}？回复 确定 开始点歌",
                ("song", selected.SongName), ("artist", selected.Artist));
        }

        if (remind)
        {
            msg = "请回复 确定 开始点歌，或发送 取消 放弃。" + msg;
        }

        SendReply(webRid, item.UserId, msg);
        return Task.CompletedTask;
    }

    private async Task EnqueueTrackAsync(
        DanmakuItem item,
        string webRid,
        TrackInfo track,
        UserProfile user,
        CancellationToken ct)
    {
        _ = ct;
        if (!IsTrackPlayable(track))
        {
            FailResolve(item, webRid, track.SongName, track.Artist, track);
            return;
        }

        var displayUser = string.IsNullOrWhiteSpace(item.Nickname) ? item.UserId : item.Nickname;
        var priority = _permission.GetQueuePriority(user);
        if (track.IsPreview && _kugou.LoginSnapshot.LoggedIn)
        {
            _system.Add($"《{track.SongName}》只能试听：请点「酷狗登录」扫码，系统会自动领取试用会员");
        }

        var added = _queue.AddWithPriority(new QueueItem
        {
            UserId = item.UserId,
            Nickname = item.Nickname,
            SongName = track.SongName,
            Artist = track.Artist,
            SongId = track.SongId,
            Hash = track.Hash,
            AlbumId = track.AlbumId,
            AlbumAudioId = track.AlbumAudioId,
            PlayUrl = track.PlayUrl,
            IsRandom = false
        }, priority);

        if (!_permission.RecordSuccessfulRequest(item, added.Id))
        {
            _queue.Remove(added.Id);
            var cost = _permission.GetPointsCost(user);
            var msg = _reply.Render("songRequestInsufficientPoints", new Dictionary<string, string>
            {
                ["name"] = displayUser,
                ["cost"] = cost.ToString(),
                ["score"] = user.Points.ToString()
            });
            if (string.IsNullOrWhiteSpace(msg))
            {
                msg = $"点歌需要 {cost} 积分，当前余额不足，已取消入队";
            }

            _replyQueue.EnqueueMention(webRid, item.UserId, msg);
            _system.Add($"{displayUser} 点歌入队失败：积分不足（需要 {cost}）");
            _log.LogSongRequest(displayUser, track.SongName, false);
            return;
        }

        var ahead = _queue.GetAheadCount(added.Id);
        _replyQueue.EnqueueSongRequestReply(webRid, item.UserId, item.Nickname, track.SongName, ahead);

        _system.Add($"已加入队列: {item.Nickname} - {track.SongName}（前面 {ahead} 首）");
        _log.LogSongRequest(displayUser, track.SongName, true);
        RequestHandled?.Invoke();
        await Task.CompletedTask;
    }

    private bool IsTrackPlayable(TrackInfo? track) => _kugou.IsAcceptableForPlayback(track);

    private void FailResolve(
        DanmakuItem item,
        string webRid,
        string songName,
        string artist,
        TrackInfo? track)
    {
        var displayUser = string.IsNullOrWhiteSpace(item.Nickname) ? item.UserId : item.Nickname;
        string reason;
        string templateKey;
        if (track == null || string.IsNullOrWhiteSpace(track.PlayUrl))
        {
            reason = "播放地址获取失败";
            templateKey = "songResolveFailed";
        }
        else if (_config.Settings.Kugou.RequireFullPlayback && track.IsPreview)
        {
            reason = "仅试听版，需完整版";
            templateKey = "songPreviewOnly";
        }
        else
        {
            reason = "播放地址无效";
            templateKey = "songResolveFailed";
        }

        var msg = RenderOrFallback(templateKey, item.Nickname,
            $"《{songName}》{reason}，请稍后重试或换一首歌",
            ("song", songName), ("artist", artist));
        SendReply(webRid, item.UserId, msg);
        _system.Add($"{item.Nickname} 点歌失败《{songName}》: {reason}");
        _log.LogSongRequest(displayUser, songName, false, reason);
    }

    private void Reject(DanmakuItem item, string webRid, string songName, string displayUser, SongRequestPermissionResult permission)
    {
        var templateKey = permission.TemplateKey ?? "songRequestRejected";
        var variables = permission.TemplateVariables;
        if (!variables.ContainsKey("name"))
        {
            variables["name"] = item.Nickname;
        }

        var msg = _reply.Render(templateKey, variables);
        if (string.IsNullOrWhiteSpace(msg))
        {
            msg = templateKey == "songRequestCooldown"
                ? "点歌太频繁，请稍后再试"
                : $"点歌失败：{permission.RejectReason}";
        }

        SendReply(webRid, item.UserId, msg);
        _system.Add($"{item.Nickname} 点歌被拒绝：{permission.RejectReason}");
        _log.LogSongRequest(displayUser, songName, false, permission.RejectReason);
    }

    private string RenderOrFallback(
        string templateKey,
        string nickname,
        string fallback,
        params (string key, string value)[] extra)
    {
        var rendered = Render(templateKey, nickname, extra);
        return string.IsNullOrWhiteSpace(rendered) ? fallback : rendered;
    }

    private string Render(string templateKey, string nickname, params (string key, string value)[] extra)
    {
        var vars = new Dictionary<string, string> { ["name"] = nickname };
        foreach (var (key, value) in extra)
        {
            vars[key] = value;
        }

        return _reply.Render(templateKey, vars);
    }

    private void SendReply(string webRid, string userId, string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return;
        }

        _replyQueue.EnqueueMention(webRid, userId, content);
    }
}
