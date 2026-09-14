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
    private readonly SemaphoreSlim _gate = new(1, 1);
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

        await _gate.WaitAsync(ct);
        try
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
        finally
        {
            _gate.Release();
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
            SendReply(webRid, item.UserId, Render("songRequestCancelled", item.Nickname) ?? "已取消点歌");
            return true;
        }

        if (SongNameParser.TryParse(item.Content, out _))
        {
            _sessions.Clear(item.UserId);
            return false;
        }

        return session.Step switch
        {
            SongRequestSessionStep.ChooseArtist => await HandleArtistChoiceAsync(item, webRid, session, ct),
            SongRequestSessionStep.Confirm => await HandleConfirmAsync(item, webRid, session, ct),
            _ => false
        };
    }

    private async Task<bool> HandleArtistChoiceAsync(
        DanmakuItem item,
        string webRid,
        SongRequestSession session,
        CancellationToken ct)
    {
        var selected = ArtistNameMatcher.Match(item.Content, session.Candidates);
        if (selected == null)
        {
            var artists = ArtistNameMatcher.FormatArtistList(session.Candidates);
            var msg = Render("songRequestArtistNotFound", item.Nickname,
                ("artists", artists), ("song", session.Keyword))
                ?? $"没有找到该歌手，请从以下歌手中选择：{artists}";
            SendReply(webRid, item.UserId, msg);
            return true;
        }

        session.Selected = selected;
        session.Step = SongRequestSessionStep.Confirm;
        _sessions.Set(session);
        await SendConfirmPromptAsync(item, webRid, session, ct);
        return true;
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

        _system.Add($"{item.Nickname} 确认点歌《{selected.SongName}》- {selected.Artist}，正在解析...");
        var track = await _kugou.ResolveCandidateAsync(selected, item.Nickname, ct);
        if (track == null)
        {
            _sessions.Clear(item.UserId);
            var notFound = Render("songNotFound", item.Nickname, ("song", selected.SongName))
                ?? $"没找到《{selected.SongName}》，请换个歌名";
            SendReply(webRid, item.UserId, notFound);
            _system.Add($"未找到歌曲《{selected.SongName}》");
            _log.LogSongRequest(item.Nickname, selected.SongName, false, "未找到歌曲");
            return true;
        }

        await EnqueueTrackAsync(item, webRid, track, permission.User!, ct);
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
            var notFound = Render("songNotFound", item.Nickname, ("song", songName))
                ?? $"没找到《{songName}》，请换个歌名";
            SendReply(webRid, item.UserId, notFound);
            _system.Add($"未找到歌曲《{songName}》");
            _log.LogSongRequest(displayUser, songName, false, "未找到歌曲");
            return;
        }

        if (candidates.Count == 1)
        {
            var session = new SongRequestSession
            {
                UserId = item.UserId,
                Nickname = item.Nickname,
                Keyword = songName,
                Step = SongRequestSessionStep.Confirm,
                Selected = candidates[0]
            };
            _sessions.Set(session);
            await SendConfirmPromptAsync(item, webRid, session, ct);
            return;
        }

        var sessionMulti = new SongRequestSession
        {
            UserId = item.UserId,
            Nickname = item.Nickname,
            Keyword = songName,
            Step = SongRequestSessionStep.ChooseArtist,
            Candidates = candidates
        };
        _sessions.Set(sessionMulti);

        var artists = ArtistNameMatcher.FormatArtistList(candidates);
        var chooseMsg = Render("songRequestChooseArtist", item.Nickname,
            ("song", songName), ("artists", artists))
            ?? $"找到多首《{songName}》，请回复歌手名：{artists}";
        SendReply(webRid, item.UserId, chooseMsg);
        _system.Add($"{item.Nickname} 点歌《{songName}》待选歌手：{artists}");
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
            msg = Render("songRequestConfirmPoints", item.Nickname,
                ("song", selected.SongName), ("artist", selected.Artist), ("cost", cost.ToString()))
                ?? $"是否确定点歌《{selected.SongName}》- {selected.Artist}？需要 {cost} 积分，回复 确定 开始点歌";
        }
        else
        {
            msg = Render("songRequestConfirm", item.Nickname,
                ("song", selected.SongName), ("artist", selected.Artist))
                ?? $"是否确定点歌《{selected.SongName}》- {selected.Artist}？回复 确定 开始点歌";
        }

        if (remind)
        {
            msg = "请回复 确定 开始点歌，或发送 取消 放弃\n" + msg;
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

        _permission.RecordSuccessfulRequest(item);

        var ahead = _queue.GetAheadCount(added.Id);
        _replyQueue.EnqueueSongRequestReply(webRid, item.UserId, item.Nickname, track.SongName, ahead);

        _system.Add($"已加入队列: {item.Nickname} - {track.SongName}（前面 {ahead} 首）");
        _log.LogSongRequest(displayUser, track.SongName, true);
        RequestHandled?.Invoke();
        await Task.CompletedTask;
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
        if (string.IsNullOrWhiteSpace(msg) && templateKey == "songRequestCooldown")
        {
            msg = $"点歌太频繁，请稍后再试";
        }

        SendReply(webRid, item.UserId, msg);
        _system.Add($"{item.Nickname} 点歌被拒绝：{permission.RejectReason}");
        _log.LogSongRequest(displayUser, songName, false, permission.RejectReason);
    }

    private string? Render(string templateKey, string nickname, params (string key, string value)[] extra)
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
