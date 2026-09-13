using LiveAssistant.Config;
using LiveAssistant.Database;
using LiveAssistant.GiftProtocol;
using LiveAssistant.Models;

namespace LiveAssistant.Services;

/// <summary>
/// 礼物专用采集：Sidecar(room/cookie) → im/fetch → GiftProtocolPipeline → GiftService.HandleGiftEvent。
/// </summary>
public sealed class GiftCollectorService : IDisposable
{
    private readonly ConfigManager _config;
    private readonly GiftService _gifts;
    private readonly LogService _log;
    private readonly GiftImFetchClient _imFetch;
    private readonly ICookieProvider _cookies;
    private readonly IGiftRoomResolver _rooms;
    private readonly GiftEventDeduplicator _deduper;
    private readonly GiftCursorStore _cursorStore;
    private CancellationTokenSource? _cts;
    private string _webRid = "";
    private string _cursor = "";
    private string _internalExt = "";
    private string? _roomId;

    public GiftCollectorService(
        ConfigManager config,
        DouyinService douyin,
        GiftService gifts,
        LogService log,
        GiftImFetchClient? imFetch = null,
        ICookieProvider? cookies = null,
        IGiftRoomResolver? rooms = null,
        GiftEventDeduplicator? deduper = null,
        GiftCursorStore? cursorStore = null,
        GiftRepository? giftRepo = null)
    {
        _config = config;
        _gifts = gifts;
        _log = log;
        _imFetch = imFetch ?? new GiftImFetchClient();
        _cookies = cookies ?? new FileCookieProvider(config, douyin);
        _rooms = rooms ?? new SidecarGiftRoomResolver(douyin);
        _cursorStore = cursorStore ?? new GiftCursorStore(config.DataDirectory);
        _deduper = deduper ?? new GiftEventDeduplicator(
            TimeSpan.FromMinutes(30),
            id => giftRepo?.ExistsByEventId(id) == true || gifts.ExistsByEventId(id));
    }

    public bool IsRunning => _cts is { IsCancellationRequested: false };
    public string CurrentWebRid => _webRid;
    public GiftEventDeduplicator Deduplicator => _deduper;
    public string CurrentCursor => _cursor;
    public string CurrentInternalExt => _internalExt;

    public void StartGiftCollector(string webRid)
    {
        var next = webRid.Trim();
        var switched = !string.IsNullOrWhiteSpace(_webRid)
                       && !string.Equals(_webRid, next, StringComparison.Ordinal);

        // 停旧循环并落盘旧房间 cursor
        StopGiftCollector(saveCursor: true);

        if (switched)
        {
            _log.GiftInfo($"[room-switch] from={_webRid} to={next} 清理 combo/内存游标");
            _deduper.ClearMemory(); // 持久层去重仍生效；内存按新场次重建
        }

        _webRid = next;
        _gifts.BindRoom(_webRid);
        RestoreCursor(_webRid);

        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                await RunLoopAsync(_webRid, token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.Error("gift-collector", "采集循环意外退出", ex);
            }
        }, token);
        _log.GiftInfo($"GiftCollector 已启动 web_rid={_webRid} cursor={Truncate(_cursor)}");
    }

    public void StopGiftCollector()
        => StopGiftCollector(saveCursor: true);

    private void StopGiftCollector(bool saveCursor)
    {
        _cts?.Cancel();
        _cts = null;
        if (saveCursor && !string.IsNullOrWhiteSpace(_webRid))
        {
            PersistCursor(_webRid);
        }
    }

    private async Task RunLoopAsync(string webRid, CancellationToken ct)
    {
        var settings = _config.Settings.Gift;
        var comboTimeout = TimeSpan.FromSeconds(Math.Max(1, settings.ComboTimeoutSeconds));
        var pipeline = new GiftProtocolPipeline(comboTimeout);
        var userUniqueId = GiftImFetchClient.NewUserUniqueId();
        var backoffMs = Math.Max(1000, settings.ReconnectDelayMs);
        var idleMs = Math.Max(1000,
            settings.IdleImFetchIntervalMs > 0 ? settings.IdleImFetchIntervalMs : settings.ImFetchIntervalMs);
        var activeMs = Math.Max(200, settings.ActiveImFetchIntervalMs);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(_roomId))
                {
                    _roomId = await _rooms.ResolveRoomIdAsync(webRid, ct);
                    _log.GiftInfo($"[room] web_rid={webRid} room_id={_roomId}");
                }

                var cookie = await _cookies.GetActiveCookieAsync(ct);

                var result = await _imFetch.FetchAsync(
                    _roomId!, webRid, cookie, userUniqueId, _cursor, _internalExt, ct);

                _cursor = PreferNewerCursor(_cursor, result.Cursor);
                if (!string.IsNullOrWhiteSpace(result.InternalExt))
                {
                    _internalExt = result.InternalExt;
                }

                PersistCursor(webRid);

                var emitted = 0;
                foreach (var giftMsg in result.Gifts)
                {
                    _log.GiftInfo(
                        $"[raw-gift] msgId={giftMsg.Common?.MsgId} giftId={giftMsg.GiftId} " +
                        $"repeat={giftMsg.RepeatCount} combo={giftMsg.ComboCount} end={giftMsg.RepeatEnd} " +
                        $"group={giftMsg.GroupId} user={giftMsg.User?.Id}/{giftMsg.User?.NickName} " +
                        $"diamond={giftMsg.Gift?.DiamondCount}");

                    var batch = pipeline.ProcessGiftMessage(giftMsg);
                    if (batch.Count == 0)
                    {
                        _log.GiftInfo(
                            $"[combo] pending user={giftMsg.User?.Id} giftId={giftMsg.GiftId} " +
                            $"group={giftMsg.GroupId} repeat={giftMsg.RepeatCount}");
                    }

                    foreach (var ev in batch)
                    {
                        _log.GiftInfo(
                            $"[normalize] eventId={ev.EventId} user={ev.UserId}/{ev.Nickname} " +
                            $"gift={ev.GiftId}/{ev.GiftName} count={ev.Count} diamond={ev.DiamondCount} " +
                            $"value={ev.Value} repeat={ev.RepeatCount} repeatEnd={ev.RepeatEnd}");
                        if (Emit(ev))
                        {
                            emitted++;
                        }
                    }
                }

                foreach (var expired in pipeline.FlushExpired())
                {
                    _log.GiftInfo(
                        $"[combo-timeout] eventId={expired.EventId} user={expired.UserId} " +
                        $"gift={expired.GiftName} count={expired.Count} value={expired.Value}");
                    if (Emit(expired))
                    {
                        emitted++;
                    }
                }

                backoffMs = Math.Max(1000, settings.ReconnectDelayMs);
                var delay = emitted > 0 || result.Gifts.Count > 0 ? activeMs : idleMs;
                await DelayAsync(delay, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (CookieInvalidException ex)
            {
                _log.GiftWarn($"Cookie 失效，等待重试: {ex.Message}");
                _roomId = null;
                // Cookie 失效不丢 cursor，避免重登后重复历史；由去重挡回流
                await DelayAsync(backoffMs, ct);
                backoffMs = Math.Min(backoffMs * 2, 60_000);
            }
            catch (Exception ex)
            {
                _log.GiftWarn($"GiftCollector 异常，自动重连: {ex.Message}");
                _log.Error("gift-collector", "采集异常", ex);
                _roomId = null;
                PersistCursor(webRid);
                await DelayAsync(backoffMs, ct);
                backoffMs = Math.Min(backoffMs * 2, 60_000);
            }
        }

        foreach (var leftover in pipeline.FlushAll())
        {
            _log.GiftInfo(
                $"[combo-flush] eventId={leftover.EventId} user={leftover.UserId} " +
                $"gift={leftover.GiftName} count={leftover.Count} value={leftover.Value}");
            Emit(leftover);
        }

        PersistCursor(webRid);
        pipeline.Reset();
        _log.GiftInfo($"GiftCollector 已停止 web_rid={webRid}");
    }

    private bool Emit(GiftEvent ev)
    {
        try
        {
            if (!_deduper.TryAdmit(ev))
            {
                _log.LogGiftDuplicate(ev.Nickname, ev.GiftName, ev.EventId, "去重命中(内存/持久层)");
                _log.GiftInfo($"[dedupe] drop eventId={ev.EventId}");
                return false;
            }

            _log.GiftInfo($"[dedupe] admit eventId={ev.EventId}");
            return _gifts.HandleGiftEvent(ev);
        }
        catch (Exception ex)
        {
            _log.Error("gift-collector", "HandleGiftEvent 异常", ex);
            return false;
        }
    }

    private void RestoreCursor(string webRid)
    {
        var state = _cursorStore.Load(webRid);
        if (state == null)
        {
            _cursor = "";
            _internalExt = "";
            _roomId = null;
            _log.GiftInfo($"[cursor] restore miss web_rid={webRid}");
            return;
        }

        _cursor = state.Cursor ?? "";
        _internalExt = state.InternalExt ?? "";
        _roomId = string.IsNullOrWhiteSpace(state.RoomId) ? null : state.RoomId;
        _log.GiftInfo(
            $"[cursor] restore web_rid={webRid} room_id={_roomId} cursor={Truncate(_cursor)} " +
            $"ext={Truncate(_internalExt)} updated={state.UpdatedAt}");
    }

    private void PersistCursor(string webRid)
    {
        try
        {
            _cursorStore.Save(webRid, _roomId, _cursor, _internalExt);
        }
        catch (Exception ex)
        {
            _log.GiftWarn($"[cursor] save failed: {ex.Message}");
        }
    }

    private static string PreferNewerCursor(string current, string incoming)
    {
        if (string.IsNullOrWhiteSpace(incoming))
        {
            return current;
        }

        if (string.IsNullOrWhiteSpace(current))
        {
            return incoming;
        }

        return incoming;
    }

    private static string Truncate(string? s, int max = 48)
    {
        if (string.IsNullOrEmpty(s))
        {
            return "";
        }

        return s.Length <= max ? s : s[..max] + "...";
    }

    private static async Task DelayAsync(int ms, CancellationToken ct)
    {
        try
        {
            await Task.Delay(ms, ct);
        }
        catch (OperationCanceledException)
        {
            // ignore
        }
    }

    public void Dispose() => StopGiftCollector(saveCursor: true);
}
