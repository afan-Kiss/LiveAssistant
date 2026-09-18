using LiveAssistant.Config;
using LiveAssistant.Models;
using LiveAssistant.Utils;

namespace LiveAssistant.Services;

/// <summary>
/// 轮询快手侧车弹幕/礼物 feed，派发到点歌全链路。
/// </summary>
public sealed class KuaishouDanmakuService : IDisposable
{
    private readonly KuaishouService _ks;
    private readonly ConfigManager _config;
    private readonly LogService _log;
    private readonly SystemMessageService _system;
    private readonly GiftService _gift;
    private readonly OutboundReplyTracker? _outboundTracker;
    private readonly DanmakuDeduplicator _deduper;
    private CancellationTokenSource? _cts;
    private Task? _pollTask;
    private long _afterDanmaku;
    private long _afterGift;
    private int _disposed;
    private int _generation;
    private readonly object _lifecycleLock = new();

    public event Action<DanmakuItem>? DanmakuReceived;

    public bool IsRunning { get; private set; }
    /// <summary>用户意图保持连接（断线后由守护线程自动重连）。</summary>
    public bool WantConnected { get; private set; }
    public string ConnectionStatus { get; private set; } = "未连接";
    public string RoomTitle { get; private set; } = "-";
    public string RoomId { get; private set; } = "";
    public string RoomKey => string.IsNullOrWhiteSpace(RoomId) ? "" : KuaishouService.RoomKey(RoomId);
    public bool IsLiveConnected =>
        IsRunning && string.Equals(ConnectionStatus, "已连接", StringComparison.Ordinal);

    public KuaishouDanmakuService(
        KuaishouService ks,
        ConfigManager config,
        LogService log,
        SystemMessageService system,
        GiftService gift,
        OutboundReplyTracker? outboundTracker = null,
        DanmakuDeduplicator? deduper = null)
    {
        _ks = ks;
        _config = config;
        _log = log;
        _system = system;
        _gift = gift;
        _outboundTracker = outboundTracker;
        _deduper = deduper ?? new DanmakuDeduplicator(config.DataDirectory, "kuaishou_danmaku_dedupe.json");
    }

    public async Task StartAsync(CancellationToken ct = default)
    {
        var settings = _config.Settings.Kuaishou;
        if (!settings.Enabled)
        {
            throw new InvalidOperationException("快手点歌未启用，请先在后台开启");
        }

        var roomId = settings.RoomId?.Trim() ?? "";
        var cookie = settings.Cookie?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(roomId))
        {
            throw new InvalidOperationException("请配置快手房间号 roomId");
        }

        if (string.IsNullOrWhiteSpace(cookie))
        {
            throw new InvalidOperationException("请配置快手 Cookie");
        }

        await StopAsync(clearWantConnected: false);
        _ks.ReloadBaseUrl();

        if (!await _ks.HealthCheckAsync(ct))
        {
            throw new InvalidOperationException(
                $"快手侧车不可达：{settings.BaseUrl}（请先启动 ks-ui-server / KsUiServer）");
        }

        var (ok, err) = await _ks.ConnectAsync(roomId, cookie, ct);
        if (!ok)
        {
            settings.ConnectFailureStreak = Math.Min(99, settings.ConnectFailureStreak + 1);
            _config.Save();
            var cookieHint = KuaishouCookieHelper.Evaluate(
                cookie, settings.CookieSavedAtUtcTicks, settings.CookieExpiresAtUtcTicks, settings.ConnectFailureStreak).Hint;
            throw new InvalidOperationException($"快手连接失败：{err}（{cookieHint}）");
        }

        settings.ConnectFailureStreak = 0;
        if (settings.CookieSavedAtUtcTicks <= 0)
        {
            settings.CookieSavedAtUtcTicks = DateTime.UtcNow.Ticks;
        }

        _config.Save();

        RoomId = roomId;
        var bridge = await _ks.GetBridgeStatusAsync(ct);
        RoomTitle = string.IsNullOrWhiteSpace(bridge?.RoomTitle) ? roomId : bridge!.RoomTitle!;
        ConnectionStatus = bridge?.Connected == true ? "已连接" : (bridge?.Status ?? "连接中");

        var catchUp = await _ks.GetDanmakuFeedAsync(0, catchUp: true, ct);
        _afterDanmaku = catchUp?.MessageCount ?? 0;
        var giftCatch = await _ks.GetGiftFeedAsync(0, catchUp: true, ct);
        _afterGift = giftCatch?.GiftCount ?? 0;
        if (_afterDanmaku <= 0 || _afterGift <= 0)
        {
            var latest = bridge?.LatestSeq ?? (await _ks.GetBridgeStatusAsync(ct))?.LatestSeq ?? 0;
            if (_afterDanmaku <= 0)
            {
                _afterDanmaku = latest;
            }

            if (_afterGift <= 0)
            {
                _afterGift = latest;
            }
        }

        CancellationTokenSource cts;
        int gen;
        lock (_lifecycleLock)
        {
            gen = ++_generation;
            cts = new CancellationTokenSource();
            _cts = cts;
            IsRunning = true;
            WantConnected = true;
            _pollTask = Task.Run(() => PollLoopAsync(gen, cts.Token));
        }

        _system.Add($"快手已连接：{RoomTitle}（{roomId}）");
        _log.KuaishouInfo($"已连接 room={roomId} afterDanmaku={_afterDanmaku} afterGift={_afterGift} gen={gen}");
    }

    public void Stop() => StopAsync(clearWantConnected: true).GetAwaiter().GetResult();

    public async Task StopAsync(bool clearWantConnected = true)
    {
        CancellationTokenSource? cts;
        Task? pollTask;
        lock (_lifecycleLock)
        {
            IsRunning = false;
            ConnectionStatus = "未连接";
            if (clearWantConnected)
            {
                WantConnected = false;
            }

            Interlocked.Increment(ref _generation); // 作废进行中的轮询
            cts = _cts;
            pollTask = _pollTask;
            _cts = null;
            _pollTask = null;
        }

        try { cts?.Cancel(); } catch { /* ignore */ }

        if (pollTask != null)
        {
            try
            {
                await Task.WhenAny(pollTask, Task.Delay(TimeSpan.FromSeconds(5)));
            }
            catch
            {
                // ignore
            }
        }

        try { cts?.Dispose(); } catch { /* ignore */ }

        try
        {
            using var ctsDisc = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await _ks.DisconnectAsync(ctsDisc.Token);
        }
        catch
        {
            // ignore
        }
    }

    private async Task PollLoopAsync(int generation, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && Volatile.Read(ref _generation) == generation)
        {
            try
            {
                await PollOnceAsync(ct);
                if (Volatile.Read(ref _generation) != generation)
                {
                    break;
                }

                var bridge = await _ks.GetBridgeStatusAsync(ct);
                if (bridge != null && Volatile.Read(ref _generation) == generation)
                {
                    ConnectionStatus = bridge.Connected ? "已连接" : (bridge.Status ?? "断开");
                    if (!string.IsNullOrWhiteSpace(bridge.RoomTitle))
                    {
                        RoomTitle = bridge.RoomTitle!;
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                if (Volatile.Read(ref _generation) == generation)
                {
                    _log.KuaishouWarn($"轮询异常: {ex.Message}");
                    ConnectionStatus = "轮询异常";
                }
            }

            var delay = Math.Clamp(_config.Settings.Kuaishou.PollIntervalMs, 300, 10_000);
            try
            {
                await Task.Delay(delay, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
        }

        if (Volatile.Read(ref _generation) == generation)
        {
            IsRunning = false;
        }
    }

    private async Task PollOnceAsync(CancellationToken ct)
    {
        var feed = await _ks.GetDanmakuFeedAsync(_afterDanmaku, catchUp: false, ct);
        if (feed?.Items != null)
        {
            foreach (var msg in feed.Items)
            {
                if (msg.Seq > _afterDanmaku)
                {
                    _afterDanmaku = msg.Seq;
                }

                var item = ToDanmakuItem(msg);
                if (item == null || ShouldIgnoreBotEcho(item))
                {
                    continue;
                }

                if (!_deduper.TryAdmit(item.MsgId))
                {
                    continue;
                }

                // 点歌/确定等指令跳过内容短窗，避免误伤；其它闲聊去重
                if (!ShouldSkipUserContentDedupe(item.Content)
                    && !_deduper.TryAdmitUserContent(item.UserId, item.Content))
                {
                    continue;
                }

                DanmakuReceived?.Invoke(item);
            }

            if (feed.MessageCount > _afterDanmaku)
            {
                _afterDanmaku = feed.MessageCount;
            }
        }

        var gifts = await _ks.GetGiftFeedAsync(_afterGift, catchUp: false, ct);
        if (gifts?.Items == null)
        {
            return;
        }

        foreach (var g in gifts.Items)
        {
            if (g.Seq > _afterGift)
            {
                _afterGift = g.Seq;
            }

            var uid = PlatformUserIds.Canonical(
                "kuaishou",
                g.UserId ?? g.User?.UserId ?? "",
                RoomKey);
            var nick = g.Nickname ?? g.User?.Nickname ?? "";
            var giftName = g.GiftName ?? "";
            var count = Math.Max(1, g.GiftCount);
            if (string.IsNullOrWhiteSpace(uid))
            {
                continue;
            }

            var eventId = !string.IsNullOrWhiteSpace(g.MsgId)
                ? g.MsgId!
                : GiftEvent.BuildEventId(RoomKey, 0, uid, giftName, giftName, count, g.Timestamp ?? g.MsgId);
            // 与抖音礼物 event_id 隔离，防止数字撞库丢积分
            if (!eventId.StartsWith("ks:", StringComparison.OrdinalIgnoreCase)
                && !eventId.StartsWith("gift:ks:", StringComparison.OrdinalIgnoreCase))
            {
                eventId = "ks:" + eventId;
            }

            // 礼物去重：复用 msg_id / eventId
            if (!_deduper.TryAdmit("gift:" + eventId))
            {
                continue;
            }

            _gift.HandleGiftEvent(new GiftEvent
            {
                EventId = eventId,
                UserId = uid,
                Nickname = nick,
                GiftId = giftName,
                GiftName = giftName,
                Count = count,
                Value = 0,
                DiamondCount = 0,
                RoomKey = RoomKey,
                Timestamp = DateTime.UtcNow,
                Time = DateTime.Now,
                CreatedAt = DateTime.Now
            });
        }

        if (gifts.GiftCount > _afterGift)
        {
            _afterGift = gifts.GiftCount;
        }
    }

    private static bool ShouldSkipUserContentDedupe(string content)
    {
        content = content.Trim();
        return SongRequestConfirmParser.IsConfirm(content)
               || SongRequestConfirmParser.IsCancel(content)
               || SongNameParser.TryParse(content, out _)
               || SkipSongParser.TryParse(content)
               || PointsQueryParser.TryParse(content)
               || content.StartsWith("禁言", StringComparison.OrdinalIgnoreCase);
    }

    private bool ShouldIgnoreBotEcho(DanmakuItem item)
    {
        if (SongNameParser.IsBotReply(item.Content))
        {
            return true;
        }

        if (_outboundTracker == null)
        {
            return false;
        }

        if (_outboundTracker.MatchesTrackedMessageId(item.MsgId))
        {
            return true;
        }

        // 仅当带 @ 前缀时才按出站正文过滤，降低误伤真人同文案
        var raw = item.Content.Trim();
        if (raw.StartsWith('@') && _outboundTracker.HasRecentOutboundContent(raw, RoomKey))
        {
            return true;
        }

        if (raw.StartsWith('@'))
        {
            var stripped = SongNameParser.StripMentionPrefix(raw);
            return !string.IsNullOrEmpty(stripped)
                   && _outboundTracker.HasRecentOutboundContent(stripped, RoomKey);
        }

        return false;
    }

    private DanmakuItem? ToDanmakuItem(KuaishouDanmakuMessage msg)
    {
        var uid = msg.User?.UserId ?? "";
        var nick = msg.User?.Nickname ?? "";
        var content = msg.Content ?? "";
        var msgType = (msg.MsgType ?? "chat").Trim().ToLowerInvariant();
        if (msgType is "danmu" or "comment" or "chat")
        {
            msgType = "chat";
        }

        if (string.IsNullOrWhiteSpace(uid) && string.IsNullOrWhiteSpace(content))
        {
            return null;
        }

        // 空闲点赞不进点歌链路噪声；仍保留 like 类型给 AI
        var rawUid = string.IsNullOrWhiteSpace(uid) ? nick : uid;
        return new DanmakuItem
        {
            MsgId = msg.MsgId ?? $"ks-{msg.Seq}",
            Content = content,
            Nickname = nick,
            UserId = PlatformUserIds.Canonical("kuaishou", rawUid, RoomKey),
            MsgType = msgType,
            Timestamp = DanmakuItem.ParseTimestamp(msg.Timestamp),
            Platform = "kuaishou",
            RoomKey = RoomKey
        };
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

            try { StopAsync(clearWantConnected: true).GetAwaiter().GetResult(); } catch { /* ignore */ }
    }
}
