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
    /// <summary>会话级锁：webRid+userId，避免跨房间互相卡住搜索。</summary>
    private readonly SongRequestUserGateRegistry _sessionGates = new();
    /// <summary>扣费/入队事务锁：仅 userId，防止同用户并发超扣。</summary>
    private readonly SongRequestUserGateRegistry _chargeGates = new();
    /// <summary>点歌/确认取链全局串行队列；弹幕处理仍可多平台并发。</summary>
    private readonly SemaphoreSlim _songRequestQueue = new(1, 1);
    private readonly SongRequestDeduper _deduper = new();
    private readonly SongRequestSessionStore _sessions = new();
    private readonly object _orphanHintGate = new();
    private readonly Dictionary<string, DateTime> _orphanHintAt = new(StringComparer.Ordinal);

    /// <summary>测试：扣费成功后、最终提交前抛异常，强制走补偿。</summary>
    internal Action? TestAfterChargeBeforeFinalize { get; set; }

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

    /// <summary>
    /// 点歌真实入队且积分事务 commit 成功后触发。不含搜索/确认/失败路径。
    /// 旧监听方可继续只用 <see cref="RequestHandled"/>。
    /// </summary>
    public event Action<SongRequestSucceededEvent>? SongRequestSucceeded;

    public async Task<bool> HandleDanmakuAsync(DanmakuItem item, string webRid, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(item.UserId))
        {
            return false;
        }

        var key = PendingSongKey.Create(webRid, item.UserId);
        SongRequestSession? pendingConfirm = null;
        string? newSongName = null;

        using (await _sessionGates.AcquireAsync(key.StorageKey, ct))
        {
            var session = _sessions.Get(key);
            if (session != null)
            {
                if (SongRequestConfirmParser.IsCancel(item.Content))
                {
                    _sessions.Clear(key);
                    SendReply(webRid, item.UserId, RenderOrFallback("songRequestCancelled", item.Nickname, "已取消点歌"), item.Nickname);
                    return true;
                }

                if (SongNameParser.TryParse(item.Content, out _))
                {
                    _sessions.Clear(key);
                    // 下面按新点歌处理
                }
                else if (session.Step is SongRequestSessionStep.Confirm or SongRequestSessionStep.ChooseArtist)
                {
                    if (session.Step == SongRequestSessionStep.ChooseArtist)
                    {
                        session.Selected ??= session.Candidates.Count > 0 ? session.Candidates[0] : null;
                        session.Step = SongRequestSessionStep.Confirm;
                        _sessions.Set(session);
                    }

                    if (!SongRequestConfirmParser.IsConfirm(item.Content))
                    {
                        return false; // 闲聊不消费
                    }

                    if (session.ConfirmConsumed)
                    {
                        _log.DouyinInfo($"点歌确认去重: {item.Nickname} user={item.UserId} webRid={webRid}");
                        return true;
                    }

                    if (session.Selected == null)
                    {
                        _sessions.Clear(key);
                        return true;
                    }

                    var permission = _permission.Evaluate(item);
                    if (!permission.Allowed)
                    {
                        _sessions.Clear(key);
                        Reject(item, webRid, session.Selected.SongName, item.Nickname, permission);
                        return true;
                    }

                    var songPermission = _permission.EvaluateSong(session.Selected.SongName, permission.User!, item);
                    if (!songPermission.Allowed)
                    {
                        _sessions.Clear(key);
                        Reject(item, webRid, session.Selected.SongName, item.Nickname, songPermission);
                        return true;
                    }

                    session.ConfirmConsumed = true;
                    _sessions.Set(session);
                    pendingConfirm = session;
                }
            }

            if (pendingConfirm == null)
            {
                if (!SongNameParser.TryParse(item.Content, out var songName))
                {
                    if (SongRequestConfirmParser.IsConfirm(item.Content)
                        || SongRequestConfirmParser.IsCancel(item.Content))
                    {
                        TrySendOrphanConfirmHint(webRid, item.UserId, item.Nickname);
                        return true;
                    }

                    return false;
                }

                if (!_deduper.TryAdmit(item.UserId, item.Content))
                {
                    _log.DouyinInfo($"点歌去重: {item.Nickname} {item.Content}");
                    return true;
                }

                _sessions.Clear(key);
                newSongName = songName;
            }
        }

        // 会话锁已释放：外部 HTTP / 扣费在外执行
        if (pendingConfirm != null)
        {
            return await CompleteConfirmAsync(item, webRid, pendingConfirm, ct);
        }

        if (newSongName != null)
        {
            await StartNewRequestAsync(item, webRid, newSongName, ct);
            return true;
        }

        return false;
    }

    private async Task<bool> CompleteConfirmAsync(
        DanmakuItem item,
        string webRid,
        SongRequestSession session,
        CancellationToken ct)
    {
        var key = PendingSongKey.Create(webRid, item.UserId);
        var selected = session.Selected!;
        var permissionUser = _permission.Evaluate(item).User
                             ?? new UserProfile { UserId = item.UserId, Nickname = item.Nickname };

        try
        {
            var enqueued = await ResolveAndEnqueueAsync(item, webRid, selected, permissionUser, ct);
            using (await _sessionGates.AcquireAsync(key.StorageKey, ct))
            {
                if (enqueued)
                {
                    _sessions.Clear(key);
                    NoteRecentConfirmHandled(webRid, item.UserId);
                }
                else
                {
                    session.ConfirmConsumed = false;
                    _sessions.Set(session);
                }
            }

            return true;
        }
        catch
        {
            using (await _sessionGates.AcquireAsync(key.StorageKey, CancellationToken.None))
            {
                session.ConfirmConsumed = false;
                _sessions.Set(session);
            }

            throw;
        }
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

        await EnterSongRequestQueueAsync(item.Nickname, songName, ct);
        try
        {
            await StartNewRequestCoreAsync(item, webRid, songName, displayUser, ct);
        }
        finally
        {
            _songRequestQueue.Release();
        }
    }

    private async Task StartNewRequestCoreAsync(
        DanmakuItem item, string webRid, string songName, string displayUser, CancellationToken ct)
    {
        _system.Add($"{item.Nickname} 点歌《{songName}》，正在搜索...");
        // HTTP 搜索：不持有任何用户锁；由 _songRequestQueue 全局串行
        var candidates = await _kugou.SearchCandidatesAsync(songName, displayLimit: 3, ct);
        if (candidates.Count == 0)
        {
            var notFound = RenderOrFallback("songNotFound", item.Nickname,
                $"没找到《{songName}》，请换个歌名", ("song", songName));
            SendReply(webRid, item.UserId, notFound, item.Nickname);
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

        var confirmSession = new SongRequestSession
        {
            WebRid = webRid,
            UserId = item.UserId,
            Nickname = item.Nickname,
            Keyword = songName,
            Step = SongRequestSessionStep.Confirm,
            Candidates = candidates,
            Selected = selected
        };

        using (await _sessionGates.AcquireAsync(PendingSongKey.Create(webRid, item.UserId).StorageKey, ct))
        {
            _sessions.Set(confirmSession);
        }

        await SendConfirmPromptAsync(item, webRid, confirmSession, ct);
    }

    private async Task<bool> ResolveAndEnqueueAsync(
        DanmakuItem item,
        string webRid,
        SongSearchCandidate selected,
        UserProfile user,
        CancellationToken ct)
    {
        await EnterSongRequestQueueAsync(item.Nickname, selected.SongName, ct);
        try
        {
            return await ResolveAndEnqueueCoreAsync(item, webRid, selected, user, ct);
        }
        finally
        {
            _songRequestQueue.Release();
        }
    }

    private async Task<bool> ResolveAndEnqueueCoreAsync(
        DanmakuItem item,
        string webRid,
        SongSearchCandidate selected,
        UserProfile user,
        CancellationToken ct)
    {
        _system.Add($"{item.Nickname} 点歌《{selected.SongName}》- {selected.Artist}，正在解析...");
        TrackInfo? track;
        try
        {
            // HTTP 取链：不持有 charge gate；由 _songRequestQueue 全局串行
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
            return false;
        }

        using (await _chargeGates.AcquireAsync(item.UserId, ct))
        {
            return await EnqueueTrackAsync(item, webRid, track!, user, ct);
        }
    }

    private Task SendConfirmPromptAsync(
        DanmakuItem item,
        string webRid,
        SongRequestSession session,
        CancellationToken ct,
        bool remind = false)
    {
        _ = ct;
        _ = remind;
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

        _system.Add($"已@ {item.Nickname}：{msg}");
        SendReply(webRid, item.UserId, msg, item.Nickname);
        return Task.CompletedTask;
    }

    private Task<bool> EnqueueTrackAsync(
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
            return Task.FromResult(false);
        }

        var displayUser = string.IsNullOrWhiteSpace(item.Nickname) ? item.UserId : item.Nickname;
        var pointsBefore = user.Points;
        var queueBefore = _queue.WaitingCount;
        var priority = _permission.GetQueuePriority(user);
        var bypassCapacity = user.Role is UserRole.Admin or UserRole.Manager;
        var maxSize = _config.Settings.Queue.MaxSize;
        if (track.IsPreview && _kugou.LoginSnapshot.LoggedIn)
        {
            _system.Add($"《{track.SongName}》只能试听：请点「酷狗登录」扫码，系统会自动领取试用会员");
        }

        LogSongTransaction("reserve_queue", item.UserId, track.SongName, null, pointsBefore, pointsBefore,
            null, null, null, null);

        if (!_queue.TryAddWithPriority(
                new QueueItem
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
                },
                priority,
                maxSize,
                bypassCapacity,
                out var added)
            || added == null)
        {
            var fullMsg = RenderOrFallback("queueFull", item.Nickname, "队列已满，请稍后再点", ("name", item.Nickname));
            _replyQueue.EnqueueMention(webRid, item.UserId, fullMsg, nickname: item.Nickname);
            _system.Add($"{displayUser} 点歌入队失败：队列已满");
            _log.DouyinInfo(
                $"SONG_REQUEST userId={item.UserId} sessionStep=confirm song={track.SongName} " +
                $"permission=queueFull confirmConsumed=true queueBefore={queueBefore} queueAfter={_queue.WaitingCount} " +
                $"pointsBefore={pointsBefore} pointsAfter={pointsBefore} result=queueFull");
            _log.LogSongRequest(displayUser, track.SongName, false, "队列已满");
            return Task.FromResult(true);
        }

        LogSongTransaction("deduct_points", item.UserId, track.SongName, added.Id, pointsBefore, pointsBefore,
            null, null, null, null);

        SongRequestChargeResult charge;
        try
        {
            charge = _permission.TryCommitSuccessfulRequest(item, added.Id);
        }
        catch (Exception ex)
        {
            // 统一事务回滚后积分应未变；仅删队列
            var removed = SafeRemove(added.Id);
            var pointsAfter = _permission.Evaluate(item).User?.Points ?? pointsBefore;
            LogSongTransaction("rollback", item.UserId, track.SongName, added.Id, pointsBefore, pointsAfter,
                "charge_exception", ex, removed, pointsAfter == pointsBefore);
            _log.DouyinWarn(
                $"SONG_REQUEST_ROLLBACK userId={item.UserId} song={track.SongName} queueItemId={added.Id} " +
                $"exception={ex.GetType().Name}:{ex.Message} pointsBefore={pointsBefore} pointsAfter={pointsAfter}");
            _system.Add($"{displayUser} 点歌入队失败：扣积分异常，已取消入队");
            _replyQueue.EnqueueMention(webRid, item.UserId, "点歌失败，请稍后再试", nickname: item.Nickname);
            _log.LogSongRequest(displayUser, track.SongName, false, "扣积分异常已回滚");
            return Task.FromResult(true);
        }

        if (!charge.Success)
        {
            var removed = SafeRemove(added.Id);
            LogSongTransaction("rollback", item.UserId, track.SongName, added.Id,
                charge.PointsBefore, charge.PointsAfter, charge.FailureReason ?? "charge_failed",
                null, removed, charge.PointsAfter == charge.PointsBefore);

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

            _replyQueue.EnqueueMention(webRid, item.UserId, msg, nickname: item.Nickname);
            _system.Add($"{displayUser} 点歌入队失败：积分不足（需要 {cost}）");
            _log.LogSongRequest(displayUser, track.SongName, false);
            return Task.FromResult(true);
        }

        try
        {
            TestAfterChargeBeforeFinalize?.Invoke();
        }
        catch (Exception ex)
        {
            // 已建立 song_request_charges 生命周期：禁止再走 RestoreCharge（会与 Remove 回调双退款）。
            // 统一经 queueItemId 幂等 RefundSongRequestCharge；Remove 回调若已退则返回 already_refunded。
            var removed = SafeRemove(added.Id);
            var pointsCurrent = _permission.Evaluate(item).User?.Points ?? pointsBefore;
            if (!removed)
            {
                _log.Error("song_request",
                    $"SONG_REQUEST_COMPENSATION_FAILED userId={item.UserId} queueItemId={added.Id} " +
                    $"reason=queue_remove_failed pointsDeducted={charge.PointsDeducted} " +
                    $"pointsBefore={charge.PointsBefore} pointsCurrent={pointsCurrent} " +
                    $"exception={ex.GetType().Name}:{ex.Message}");
                _system.Add($"{displayUser} 点歌事务异常，队列删除失败需人工检查（userId={item.UserId}）");
            }
            else
            {
                var refund = _permission.RefundSongRequestCharge(added.Id, "post_charge_abort");
                pointsCurrent = _permission.Evaluate(item).User?.Points ?? pointsBefore;
                if (!refund.Success)
                {
                    _log.Error("song_request",
                        $"SONG_REQUEST_COMPENSATION_FAILED userId={item.UserId} queueItemId={added.Id} " +
                        $"pointsDeducted={charge.PointsDeducted} pointsBefore={charge.PointsBefore} " +
                        $"pointsCurrent={pointsCurrent} queueRemoved=true " +
                        $"pointsRestored={refund.PointsRestored} creditRestored={refund.CreditRestored} " +
                        $"refundResult={refund.Result} failureReason={refund.FailureReason} " +
                        $"exception={ex.GetType().Name}:{ex.Message}");
                    _system.Add($"{displayUser} 点歌事务异常，需要人工检查积分（userId={item.UserId}）");
                }
                else
                {
                    LogSongTransaction("rollback", item.UserId, track.SongName, added.Id,
                        charge.PointsBefore, pointsCurrent, "post_charge_abort", ex, true, true);
                    _system.Add($"{displayUser} 点歌入队失败：已取消入队并退回积分");
                }
            }

            _replyQueue.EnqueueMention(webRid, item.UserId, "点歌失败，请稍后再试", nickname: item.Nickname);
            _log.LogSongRequest(displayUser, track.SongName, false, "post_charge_abort");
            return Task.FromResult(true);
        }

        var ahead = _queue.GetAheadCount(added.Id);
        _replyQueue.EnqueueSongRequestReply(webRid, item.UserId, item.Nickname, track.SongName, ahead);
        _system.Add($"已加入队列: {item.Nickname} - {track.SongName}（前面 {ahead} 首）");
        LogSongTransaction("commit", item.UserId, track.SongName, added.Id,
            charge.PointsBefore, charge.PointsAfter, null, null, null, null);
        _log.DouyinInfo(
            $"SONG_REQUEST userId={item.UserId} sessionStep=confirm song={track.SongName} " +
            $"permission=ok confirmConsumed=true queueBefore={queueBefore} queueAfter={_queue.WaitingCount} " +
            $"queueItemId={added.Id} pointsBefore={charge.PointsBefore} pointsAfter={charge.PointsAfter} result=enqueued");
        _log.LogSongRequest(displayUser, track.SongName, true);
        RequestHandled?.Invoke();
        try
        {
            SongRequestSucceeded?.Invoke(new SongRequestSucceededEvent
            {
                UserId = item.UserId,
                Nickname = item.Nickname,
                SongName = track.SongName,
                Artist = track.Artist,
                AheadCount = ahead,
                QueueItemId = added.Id,
                Platform = string.IsNullOrWhiteSpace(item.Platform)
                    ? (KuaishouService.IsKuaishouRoom(webRid) ? "kuaishou" : "douyin")
                    : item.Platform,
                RoomKey = string.IsNullOrWhiteSpace(item.RoomKey) ? webRid : item.RoomKey
            });
        }
        catch (Exception ex)
        {
            _log.Error("song_request", "SongRequestSucceeded 监听方异常（已隔离）", ex);
        }

        return Task.FromResult(true);
    }

    private bool SafeRemove(long id)
    {
        try
        {
            return _queue.Remove(id);
        }
        catch (Exception ex)
        {
            _log.Error("song_request", $"队列 Remove 失败 queueItemId={id}", ex);
            return false;
        }
    }

    private void LogSongTransaction(
        string stage,
        string userId,
        string song,
        long? queueId,
        int pointsBefore,
        int pointsAfter,
        string? reason,
        Exception? exception,
        bool? queueRemoved,
        bool? pointsRestored)
    {
        var parts = new List<string>
        {
            "SONG_REQUEST_TRANSACTION",
            $"stage={stage}",
            $"userId={userId}",
            $"song={song}",
            $"queueId={(queueId?.ToString() ?? "-")}",
            $"pointsBefore={pointsBefore}",
            $"pointsAfter={pointsAfter}"
        };
        if (!string.IsNullOrWhiteSpace(reason))
        {
            parts.Add($"reason={reason}");
        }

        if (exception != null)
        {
            parts.Add($"exception={exception.GetType().Name}:{exception.Message}");
        }

        if (queueRemoved.HasValue)
        {
            parts.Add($"queueRemoved={queueRemoved.Value.ToString().ToLowerInvariant()}");
        }

        if (pointsRestored.HasValue)
        {
            parts.Add($"pointsRestored={pointsRestored.Value.ToString().ToLowerInvariant()}");
        }

        _log.DouyinInfo(string.Join(" ", parts));
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
        SendReply(webRid, item.UserId, msg, item.Nickname);
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

        SendReply(webRid, item.UserId, msg, item.Nickname);
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

    private async Task EnterSongRequestQueueAsync(string? nickname, string songLabel, CancellationToken ct)
    {
        if (await _songRequestQueue.WaitAsync(0, ct))
        {
            return;
        }

        var who = string.IsNullOrWhiteSpace(nickname) ? "观众" : nickname.Trim();
        _system.Add($"{who} 点歌《{songLabel}》排队中，请稍候...");
        _log.KugouInfo($"点歌排队: {who} 《{songLabel}》");
        await _songRequestQueue.WaitAsync(ct);
    }

    private void SendReply(string webRid, string userId, string? content, string? nickname = null)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return;
        }

        _replyQueue.EnqueueMention(webRid, userId, content, nickname: nickname);
    }

    private void TrySendOrphanConfirmHint(string webRid, string userId, string? nickname = null)
    {
        var hintKey = PendingSongKey.Create(webRid, userId).StorageKey;
        var now = DateTime.UtcNow;
        lock (_orphanHintGate)
        {
            if (_orphanHintAt.TryGetValue(hintKey, out var last) && now - last < TimeSpan.FromSeconds(30))
            {
                return;
            }

            _orphanHintAt[hintKey] = now;
            foreach (var dead in _orphanHintAt.Where(kv => now - kv.Value > TimeSpan.FromMinutes(10)).Select(kv => kv.Key).ToList())
            {
                _orphanHintAt.Remove(dead);
            }
        }

        SendReply(webRid, userId, "当前没有待确认的点歌，请重新发送：点歌 歌名", nickname);
    }

    private void NoteRecentConfirmHandled(string webRid, string userId)
    {
        var hintKey = PendingSongKey.Create(webRid, userId).StorageKey;
        lock (_orphanHintGate)
        {
            _orphanHintAt[hintKey] = DateTime.UtcNow;
        }
    }
}

/// <summary>点歌成功（入队 + 积分事务已提交）详情。</summary>
public sealed class SongRequestSucceededEvent
{
    public string UserId { get; init; } = "";
    public string Nickname { get; init; } = "";
    public string SongName { get; init; } = "";
    public string Artist { get; init; } = "";
    public int AheadCount { get; init; }
    public long QueueItemId { get; init; }
    public string Platform { get; init; } = "douyin";
    public string RoomKey { get; init; } = "";
}
