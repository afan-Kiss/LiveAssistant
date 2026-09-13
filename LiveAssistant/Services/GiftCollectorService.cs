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
    private readonly DouyinService _douyin;
    private readonly GiftService _gifts;
    private readonly LogService _log;
    private readonly GiftImFetchClient _imFetch;
    private CancellationTokenSource? _cts;
    private string _webRid = "";

    public GiftCollectorService(
        ConfigManager config,
        DouyinService douyin,
        GiftService gifts,
        LogService log,
        GiftImFetchClient? imFetch = null)
    {
        _config = config;
        _douyin = douyin;
        _gifts = gifts;
        _log = log;
        _imFetch = imFetch ?? new GiftImFetchClient();
    }

    public bool IsRunning => _cts is { IsCancellationRequested: false };

    public void StartGiftCollector(string webRid)
    {
        StopGiftCollector();
        _webRid = webRid.Trim();
        _gifts.BindRoom(_webRid);
        _cts = new CancellationTokenSource();
        _ = Task.Run(() => RunLoopAsync(_webRid, _cts.Token));
        _log.GiftInfo("GiftCollector 已启动（礼物专用 im/fetch）");
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
        string? cookie = null;
        var backoffMs = Math.Max(1000, settings.ReconnectDelayMs);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(roomId))
                {
                    roomId = await ResolveRoomIdAsync(webRid, ct);
                }

                cookie = await EnsureCookieAsync(ct);

                var result = await _imFetch.FetchAsync(
                    roomId!, webRid, cookie!, userUniqueId, cursor, internalExt, ct);

                cursor = PreferNewerCursor(cursor, result.Cursor);
                if (!string.IsNullOrWhiteSpace(result.InternalExt))
                {
                    internalExt = result.InternalExt;
                }

                foreach (var giftMsg in result.Gifts)
                {
                    foreach (var ev in pipeline.ProcessGiftMessage(giftMsg))
                    {
                        Emit(ev);
                    }
                }

                foreach (var expired in pipeline.FlushExpired())
                {
                    Emit(expired);
                }

                backoffMs = Math.Max(1000, settings.ReconnectDelayMs);
                await DelayAsync(Math.Max(1000, settings.ImFetchIntervalMs), ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (CookieInvalidException ex)
            {
                _log.GiftWarn($"Cookie 失效，等待重试: {ex.Message}");
                cookie = null;
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
                // 网络/解析异常：保留 cursor，稍后重试；房间信息可刷新
                roomId = null;
                await DelayAsync(backoffMs, ct);
                backoffMs = Math.Min(backoffMs * 2, 60_000);
            }
        }

        foreach (var leftover in pipeline.FlushAll())
        {
            Emit(leftover);
        }

        _log.GiftInfo("GiftCollector 已停止");
    }

    private void Emit(GiftEvent ev)
    {
        try
        {
            _gifts.HandleGiftEvent(ev);
        }
        catch (Exception ex)
        {
            _log.Error("gift-collector", "HandleGiftEvent 异常", ex);
        }
    }

    private async Task<string> ResolveRoomIdAsync(string webRid, CancellationToken ct)
    {
        var room = await _douyin.ResolveRoomAsync(webRid, ct);
        if (room == null || string.IsNullOrWhiteSpace(room.RoomId))
        {
            throw new InvalidOperationException("无法从 Sidecar 解析 room_id");
        }

        return room.RoomId.Trim();
    }

    private async Task<string> EnsureCookieAsync(CancellationToken ct)
    {
        var status = await _douyin.GetCookieStatusAsync(ct);
        if (status == null)
        {
            throw new CookieInvalidException("无法访问 Sidecar /api/cookie");
        }

        if (!status.LoginOk)
        {
            throw new CookieInvalidException(status.LoginHint ?? "Sidecar Cookie 未登录");
        }

        var path = SidecarCookieStore.ResolveStorePath(_config.Settings.Douyin);
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new CookieInvalidException("未配置 CookieStorePath / DouyinExePath，无法读取 cookies.json");
        }

        if (!SidecarCookieStore.TryReadActiveCookie(path, out var cookie, out _, out var error))
        {
            throw new CookieInvalidException(error ?? "读取 cookies.json 失败");
        }

        return cookie;
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

        // 简单策略：非空则前进；避免回退到空。
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
