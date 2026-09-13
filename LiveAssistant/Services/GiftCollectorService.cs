using LiveAssistant.Config;
using LiveAssistant.GiftProtocol;
using LiveAssistant.Models;

namespace LiveAssistant.Services;

/// <summary>
/// 礼物专用采集：Sidecar(room/cookie) → im/fetch → GiftProtocolPipeline → GiftService.HandleGiftEvent。
/// 不采集弹幕/点赞/进场。
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
    private CancellationTokenSource? _cts;
    private string _webRid = "";

    public GiftCollectorService(
        ConfigManager config,
        DouyinService douyin,
        GiftService gifts,
        LogService log,
        GiftImFetchClient? imFetch = null,
        ICookieProvider? cookies = null,
        IGiftRoomResolver? rooms = null,
        GiftEventDeduplicator? deduper = null)
    {
        _config = config;
        _gifts = gifts;
        _log = log;
        _imFetch = imFetch ?? new GiftImFetchClient();
        _cookies = cookies ?? new FileCookieProvider(config, douyin);
        _rooms = rooms ?? new SidecarGiftRoomResolver(douyin);
        _deduper = deduper ?? new GiftEventDeduplicator(TimeSpan.FromMinutes(30));
    }

    public bool IsRunning => _cts is { IsCancellationRequested: false };
    public string CurrentWebRid => _webRid;
    public GiftEventDeduplicator Deduplicator => _deduper;

    public void StartGiftCollector(string webRid)
    {
        StopGiftCollector();
        _webRid = webRid.Trim();
        _gifts.BindRoom(_webRid);
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
        _log.GiftInfo($"GiftCollector 已启动 web_rid={_webRid}");
    }

    public void StopGiftCollector()
    {
        _cts?.Cancel();
        _cts = null;
    }

    private async Task RunLoopAsync(string webRid, CancellationToken ct)
    {
        var settings = _config.Settings.Gift;
        var comboTimeout = TimeSpan.FromSeconds(Math.Max(1, settings.ComboTimeoutSeconds));
        var pipeline = new GiftProtocolPipeline(comboTimeout);
        var userUniqueId = GiftImFetchClient.NewUserUniqueId();
        var cursor = "";
        var internalExt = "";
        string? roomId = null;
        var backoffMs = Math.Max(1000, settings.ReconnectDelayMs);
        var idleMs = Math.Max(1000,
            settings.IdleImFetchIntervalMs > 0 ? settings.IdleImFetchIntervalMs : settings.ImFetchIntervalMs);
        var activeMs = Math.Max(200, settings.ActiveImFetchIntervalMs);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(roomId))
                {
                    roomId = await _rooms.ResolveRoomIdAsync(webRid, ct);
                }

                var cookie = await _cookies.GetActiveCookieAsync(ct);

                var result = await _imFetch.FetchAsync(
                    roomId!, webRid, cookie, userUniqueId, cursor, internalExt, ct);

                cursor = PreferNewerCursor(cursor, result.Cursor);
                if (!string.IsNullOrWhiteSpace(result.InternalExt))
                {
                    internalExt = result.InternalExt;
                }

                var emitted = 0;
                foreach (var giftMsg in result.Gifts)
                {
                    foreach (var ev in pipeline.ProcessGiftMessage(giftMsg))
                    {
                        if (Emit(ev))
                        {
                            emitted++;
                        }
                    }
                }

                foreach (var expired in pipeline.FlushExpired())
                {
                    if (Emit(expired))
                    {
                        emitted++;
                    }
                }

                backoffMs = Math.Max(1000, settings.ReconnectDelayMs);
                // 无礼物：慢轮询；有礼物：快速连拉，降低延迟
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
                roomId = null;
                cursor = "";
                internalExt = "";
                await DelayAsync(backoffMs, ct);
                backoffMs = Math.Min(backoffMs * 2, 60_000);
            }
            catch (Exception ex)
            {
                _log.GiftWarn($"GiftCollector 异常，自动重连: {ex.Message}");
                _log.Error("gift-collector", "采集异常", ex);
                roomId = null;
                await DelayAsync(backoffMs, ct);
                backoffMs = Math.Min(backoffMs * 2, 60_000);
            }
        }

        foreach (var leftover in pipeline.FlushAll())
        {
            Emit(leftover);
        }

        _log.GiftInfo($"GiftCollector 已停止 web_rid={webRid}");
    }

    private bool Emit(GiftEvent ev)
    {
        try
        {
            if (!_deduper.TryAdmit(ev))
            {
                _log.LogGiftDuplicate(ev.Nickname, ev.GiftName, ev.EventId, "内存去重(30m)");
                return false;
            }

            return _gifts.HandleGiftEvent(ev);
        }
        catch (Exception ex)
        {
            _log.Error("gift-collector", "HandleGiftEvent 异常", ex);
            return false;
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

    public void Dispose() => StopGiftCollector();
}
