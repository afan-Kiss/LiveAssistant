using LiveAssistant.Models;
using LiveAssistant.Utils;

namespace LiveAssistant.Services;

public sealed class DanmakuService : IDisposable
{
    private readonly DouyinService _douyin;
    private readonly LogService _log;
    private readonly SystemMessageService _system;
    private readonly DanmakuDeduplicator _deduper;
    private readonly OutboundReplyTracker? _outboundTracker;
    private readonly int _pollIntervalMs;
    private CancellationTokenSource? _cts;
    private int _after;
    private int _afterAt;
    private string _webRid = "";

    public event Action<DanmakuItem>? DanmakuReceived;

    public bool IsRunning { get; private set; }
    public string ConnectionStatus { get; private set; } = "未连接";
    /// <summary>直播间主播昵称（来自 room/resolve 的 owner）。</summary>
    public string RoomOwnerNickname { get; private set; } = "-";

    /// <summary>抖音侧车 Cookie 登录昵称（用于 @ 回复/房管操作）。</summary>
    public string DouyinLoginNickname { get; private set; } = "-";

    public string RoomTitle { get; private set; } = "-";

    public DanmakuService(
        DouyinService douyin,
        LogService log,
        SystemMessageService system,
        DanmakuDeduplicator? deduper = null,
        OutboundReplyTracker? outboundTracker = null,
        int pollIntervalMs = 1500)
    {
        _douyin = douyin;
        _log = log;
        _system = system;
        _deduper = deduper ?? new DanmakuDeduplicator();
        _outboundTracker = outboundTracker;
        _pollIntervalMs = Math.Clamp(pollIntervalMs, 300, 10_000);
    }

    public async Task StartAsync(string webRid, CancellationToken ct = default)
    {
        _webRid = webRid.Trim();
        if (string.IsNullOrEmpty(_webRid))
        {
            throw new InvalidOperationException("请配置直播间 web_rid");
        }

        Stop();

        _after = 0;
        _afterAt = 0;

        var health = await _douyin.GetHealthAsync(ct);
        if (health?.LoginOk != true)
        {
            ConnectionStatus = "抖音 CDP 未登录，请先完成扫码登录";
            _system.Add("抖音 CDP 未登录，请先完成扫码登录");
            _log.DouyinWarn($"DOUYIN_CDP_LOGIN result=fail hint={health?.LoginHint}");
            throw new InvalidOperationException("抖音 CDP 未登录，请先完成扫码登录");
        }

        DouyinLoginNickname = string.IsNullOrWhiteSpace(health.Nickname) ? "-" : health.Nickname.Trim();
        _log.DouyinInfo($"DOUYIN_CDP_LOGIN result=ok nickname={DouyinLoginNickname}");

        var canSend = health.CanSend ?? health.LoginOk;
        var canModerate = health.CanModerate ?? false;
        if (health.LoginOk && !canSend)
        {
            ConnectionStatus = "抖音账号无发送权限";
            _system.Add("抖音账号无发送权限（请确认 Chrome 登录的是主播账号）");
            _log.DouyinWarn("DOUYIN_PERMISSION_DENIED can_send=false");
            throw new InvalidOperationException("抖音账号无发送权限");
        }

        if (!canModerate)
        {
            _system.Add("提示：当前登录号可能不是主播，禁言功能可能不可用");
            _log.DouyinWarn("DOUYIN_PERMISSION_DENIED can_moderate=false");
        }

        var room = await _douyin.ResolveRoomAsync(_webRid, ct);
        if (room == null || string.IsNullOrWhiteSpace(room.RoomId))
        {
            ConnectionStatus = "解析直播间失败";
            throw new InvalidOperationException("解析直播间失败");
        }

        RoomOwnerNickname = string.IsNullOrWhiteSpace(room.Owner?.Nickname) ? "-" : room.Owner.Nickname.Trim();
        RoomTitle = string.IsNullOrWhiteSpace(room.Title) ? _webRid : room.Title;
        _log.DouyinInfo($"DOUYIN_CDP_START stage=resolve webRid={_webRid} roomId={room.RoomId} nickname={RoomOwnerNickname}");

        await _douyin.StopOtherCollectSessionsAsync(_webRid, ct);
        var reconnected = await _douyin.ReconnectAsync(_webRid, ct);
        if (!reconnected)
        {
            ConnectionStatus = "抖音 CDP 重连浏览器失败";
            throw new InvalidOperationException("抖音 CDP 重连浏览器失败");
        }

        await _douyin.StartCollectAsync(_webRid, ct);
        await CatchUpCursorAsync(ct);

        var postHealth = await _douyin.GetHealthAsync(ct);
        if (postHealth?.LoginOk != true)
        {
            ConnectionStatus = "抖音 CDP 未登录，请先完成扫码登录";
            _system.Add("抖音 CDP 未登录，请先完成扫码登录");
            throw new InvalidOperationException("抖音 CDP 未登录，请先完成扫码登录");
        }

        _cts = new CancellationTokenSource();
        IsRunning = true;
        ConnectionStatus = "已连接";
        _system.Add("直播间监控已启动");
        _log.DouyinInfo($"弹幕监控启动 web_rid={_webRid}");

        _ = Task.Run(() => PollLoopAsync(_cts.Token));
    }

    public void Stop()
    {
        _cts?.Cancel();
        _cts = null;
        IsRunning = false;
        ConnectionStatus = "已停止";
        _log.DouyinInfo("弹幕监控已停止");
    }

    private async Task PollLoopAsync(CancellationToken ct)
    {
        var feedFailCount = 0;
        var atFeedFailCount = 0;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var feedTask = _douyin.PollDanmakuAsync(_webRid, _after, 50, ct);
                var atFeedTask = _douyin.PollAtDanmakuAsync(_webRid, _afterAt, 50, ct);
                await Task.WhenAll(feedTask, atFeedTask);
                var feed = await feedTask;
                var atFeed = await atFeedTask;

                if (feed != null)
                {
                    feedFailCount = 0;
                    _after = feed.MessageCount;
                    DispatchItems(feed.Items);
                }
                else
                {
                    feedFailCount++;
                }

                if (atFeed != null)
                {
                    atFeedFailCount = 0;
                    _afterAt = atFeed.MentionCount > 0 ? atFeed.MentionCount : atFeed.MessageCount;
                    DispatchItems(atFeed.Items);
                }
                else
                {
                    atFeedFailCount++;
                }

                ConnectionStatus = (feed?.Running ?? atFeed?.Running ?? false) ? "已连接"
                    : feed != null || atFeed != null ? "监控中" : "等待抖音API";

                if (feedFailCount >= 3 || atFeedFailCount >= 3)
                {
                    _system.Add("连接断开，正在重连...");
                    try
                    {
                        await _douyin.ReconnectAsync(_webRid, ct);
                        await _douyin.StartCollectAsync(_webRid, ct);
                        await CatchUpCursorAsync(ct);
                        _system.Add("重连成功");
                        _log.DouyinInfo("弹幕重连成功");
                        feedFailCount = 0;
                        atFeedFailCount = 0;
                    }
                    catch (Exception rex)
                    {
                        _log.DouyinWarn($"重连失败: {rex.Message}");
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                feedFailCount++;
                atFeedFailCount++;
                _log.DouyinWarn($"弹幕轮询失败(feed={feedFailCount},at={atFeedFailCount}): {ex.Message}");
                _log.Error("douyin", "poll_loop", ex);
                ConnectionStatus = "连接异常";
                if (feedFailCount >= 3 || atFeedFailCount >= 3)
                {
                    _system.Add("连接断开，正在重连...");
                    try
                    {
                        await _douyin.ReconnectAsync(_webRid, ct);
                        await _douyin.StartCollectAsync(_webRid, ct);
                        await CatchUpCursorAsync(ct);
                        _system.Add("重连成功");
                        _log.DouyinInfo("弹幕重连成功");
                        feedFailCount = 0;
                        atFeedFailCount = 0;
                    }
                    catch (Exception rex)
                    {
                        _log.Error("douyin", "reconnect", rex);
                    }
                }
            }

            try
            {
                await Task.Delay(_pollIntervalMs, ct);
            }
            catch (TaskCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _log.Error("douyin", "poll_delay", ex);
            }
        }
    }

    /// <summary>
    /// 将游标推进到缓冲区末尾，跳过历史弹幕，仅处理此后新消息。
    /// 侧车 StartCollect 后缓冲可能延迟灌入，需等到连续两轮空读且计数稳定。
    /// </summary>
    private async Task CatchUpCursorAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(500, ct);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        var rounds = 0;
        var stableRounds = 0;
        while (!ct.IsCancellationRequested && rounds < 60)
        {
            rounds++;
            var prevAfter = _after;
            var prevAt = _afterAt;

            var feed = await _douyin.PollDanmakuAsync(_webRid, _after, 200, ct);
            var atFeed = await _douyin.PollAtDanmakuAsync(_webRid, _afterAt, 200, ct);

            if (feed != null)
            {
                _after = feed.MessageCount;
            }

            if (atFeed != null)
            {
                _afterAt = atFeed.MentionCount > 0 ? atFeed.MentionCount : atFeed.MessageCount;
            }

            var feedEmpty = feed?.Items == null || feed.Items.Count == 0;
            var atEmpty = atFeed?.Items == null || atFeed.Items.Count == 0;
            var countsStable = _after == prevAfter && _afterAt == prevAt;
            if (feedEmpty && atEmpty && countsStable)
            {
                stableRounds++;
                if (stableRounds >= 2)
                {
                    break;
                }

                try
                {
                    await Task.Delay(300, ct);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                continue;
            }

            stableRounds = 0;
            if (feedEmpty && atEmpty)
            {
                try
                {
                    await Task.Delay(200, ct);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }

        _log.DouyinInfo($"弹幕游标已追赶 feed={_after} at={_afterAt} rounds={rounds}");
    }

    private void DispatchItems(List<DouyinDanmakuMessage>? items)
    {
        if (items == null)
        {
            return;
        }

        foreach (var msg in items)
        {
            var msgType = msg.MsgType ?? "chat";
            if (string.IsNullOrWhiteSpace(msg.Content) && msgType == "chat")
            {
                continue;
            }

            try
            {
                var msgId = ResolveMsgId(msg);
                var userId = msg.User?.UserId ?? "";
                var nickname = msg.User?.Nickname ?? "未知";
                var content = msg.Content ?? "";

                var timestamp = DanmakuItem.ParseTimestamp(msg.Timestamp);
                _log.DouyinInfo(
                    $"DOUYIN_CDP_EVENT eventType={msgType} msgId={msgId} nickname={nickname}");
                _log.DouyinInfo(
                    $"[danmaku-recv] time={timestamp:HH:mm:ss} msg_id={msgId} user_id={userId} nickname={nickname} content={Truncate(content)}");

                if (!_deduper.TryAdmit(msgId))
                {
                    _log.DouyinInfo($"[danmaku-dedupe] drop msg_id={msgId}");
                    continue;
                }

                if (ShouldIgnoreBotMessage(msgId, nickname, content))
                {
                    _log.DouyinInfo($"[danmaku-bot] drop self/bot msg_id={msgId} content={Truncate(content)}");
                    continue;
                }

                // 点歌/确定等同文案可连发（新 msg_id），不做内容短窗去重；其它闲聊仍去重
                if (!ShouldSkipUserContentDedupe(content)
                    && !_deduper.TryAdmitUserContent(userId, content))
                {
                    _log.DouyinInfo($"[danmaku-dedupe] drop duplicate content user={userId}");
                    continue;
                }

                var item = new DanmakuItem
                {
                    MsgId = msgId,
                    Content = content,
                    Nickname = nickname,
                    UserId = userId,
                    MsgType = msgType,
                    Timestamp = timestamp,
                    Platform = "douyin",
                    RoomKey = _webRid
                };
                DanmakuReceived?.Invoke(item);
            }
            catch (Exception ex)
            {
                _log.Error("douyin", "处理弹幕项异常", ex);
            }
        }
    }

    private static bool ShouldSkipUserContentDedupe(string content)
    {
        content = content.Trim();
        // 同一用户可连续「确定」「点歌 xxx」（新 msg_id）；内容短窗去重会误伤第二条确认
        return SongRequestConfirmParser.IsConfirm(content)
               || SongRequestConfirmParser.IsCancel(content)
               || SongNameParser.TryParse(content, out _)
               || SkipSongParser.TryParse(content)
               || PointsQueryParser.TryParse(content)
               || content.StartsWith("禁言", StringComparison.OrdinalIgnoreCase)
               || content.StartsWith("解除禁言", StringComparison.OrdinalIgnoreCase)
               || content.StartsWith("解禁", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 机器人回显过滤：msg_id 精确命中，或登录账号昵称命中。
    /// 禁止仅因正文相同过滤真人。
    /// </summary>
    internal bool ShouldIgnoreBotMessage(string msgId, string nickname, string content)
    {
        if (_outboundTracker?.MatchesTrackedMessageId(msgId) == true)
        {
            return true;
        }

        var isLoginAccount = !string.IsNullOrWhiteSpace(DouyinLoginNickname)
            && !DouyinLoginNickname.Equals("-", StringComparison.Ordinal)
            && nickname.Equals(DouyinLoginNickname, StringComparison.OrdinalIgnoreCase);

        if (isLoginAccount)
        {
            // 登录号自身消息一律视为机器人；内容匹配仅作旁证日志
            if (_outboundTracker?.HasRecentOutboundContent(content, _webRid) == true)
            {
                return true;
            }

            return true;
        }

        // 内容相同但身份不是登录号：必须放行真人
        return false;
    }

    internal void DispatchForTests(List<DouyinDanmakuMessage>? items) => DispatchItems(items);

    internal void SetDouyinLoginNicknameForTests(string nickname)
        => DouyinLoginNickname = string.IsNullOrWhiteSpace(nickname) ? "-" : nickname.Trim();

    private static string ResolveMsgId(DouyinDanmakuMessage msg)
    {
        if (!string.IsNullOrWhiteSpace(msg.MsgId))
        {
            return msg.MsgId.Trim();
        }

        // Sidecar 未给 msg_id 时用稳定回退键，避免每次 Guid 导致无法去重
        var uid = msg.User?.UserId?.Trim() ?? "";
        var content = msg.Content?.Trim() ?? "";
        var ts = msg.Timestamp?.Trim() ?? "";
        return $"fb:{uid}|{content}|{ts}";
    }

    private static string Truncate(string s, int max = 120)
    {
        if (string.IsNullOrEmpty(s) || s.Length <= max)
        {
            return s;
        }

        return s[..max] + "...";
    }

    public void Dispose()
    {
        Stop();
    }
}
