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

        var health = await _douyin.GetHealthAsync(ct);
        DouyinLoginNickname = health?.Nickname ?? "-";

        var room = await _douyin.ResolveRoomAsync(_webRid, ct);
        RoomOwnerNickname = room?.Owner?.Nickname ?? "-";
        RoomTitle = room?.Title ?? _webRid;

        await _douyin.StartCollectAsync(_webRid, ct);
        // 连接/重连时只追赶游标，不派发缓冲区历史弹幕，避免旧点歌被重复入队
        await CatchUpCursorAsync(ct);
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

    /// <summary>将游标推进到缓冲区末尾，跳过历史弹幕，仅处理此后新消息。</summary>
    private async Task CatchUpCursorAsync(CancellationToken ct)
    {
        var rounds = 0;
        while (!ct.IsCancellationRequested && rounds < 50)
        {
            rounds++;
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

            var feedDone = feed?.Items == null || feed.Items.Count == 0;
            var atDone = atFeed?.Items == null || atFeed.Items.Count == 0;
            if (feedDone && atDone)
            {
                break;
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
                    Timestamp = timestamp
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
        return SongRequestConfirmParser.IsConfirm(content)
            || SongRequestConfirmParser.IsCancel(content)
            || SkipSongParser.TryParse(content)
            || PointsQueryParser.TryParse(content)
            || SongNameParser.TryParse(content, out _)
            || content.StartsWith("禁言", StringComparison.OrdinalIgnoreCase);
    }

    private bool ShouldIgnoreBotMessage(string msgId, string nickname, string content)
    {
        if (SongNameParser.IsBotReply(content))
        {
            return true;
        }

        if (_outboundTracker?.IsRecentOutbound(msgId, content) == true)
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(DouyinLoginNickname)
            && !DouyinLoginNickname.Equals("-", StringComparison.Ordinal)
            && nickname.Equals(DouyinLoginNickname, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

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
