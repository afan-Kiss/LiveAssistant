using LiveAssistant.Config;

namespace LiveAssistant.Services;

/// <summary>
/// 基于 Sidecar cookies.json 的 CookieProvider 实现。
/// 先通过 /api/cookie 检查登录状态，再读本地完整 Cookie；
/// 若 cookies.json 缺失但 Sidecar 已登录，则复用 room/resolve 的 Cookie（带缓存，避免每轮 im/fetch 重复解析房间）。
/// </summary>
public sealed class FileCookieProvider : ICookieProvider
{
    private static readonly TimeSpan FallbackCacheTtl = TimeSpan.FromMinutes(10);

    private readonly ConfigManager _config;
    private readonly DouyinService _douyin;
    private readonly IGiftRoomResolver? _rooms;
    private readonly object _cacheLock = new();
    private string _boundWebRid = "";
    private string? _cachedFallbackCookie;
    private string _cachedFallbackWebRid = "";
    private DateTime _cachedFallbackUtc = DateTime.MinValue;

    public FileCookieProvider(ConfigManager config, DouyinService douyin, IGiftRoomResolver? rooms = null)
    {
        _config = config;
        _douyin = douyin;
        _rooms = rooms;
    }

    public void BindWebRid(string webRid)
    {
        var next = webRid?.Trim() ?? "";
        lock (_cacheLock)
        {
            if (string.Equals(_boundWebRid, next, StringComparison.Ordinal))
            {
                return;
            }

            _boundWebRid = next;
            _cachedFallbackCookie = null;
            _cachedFallbackWebRid = "";
            _cachedFallbackUtc = DateTime.MinValue;
        }
    }

    public void InvalidateCache()
    {
        lock (_cacheLock)
        {
            _cachedFallbackCookie = null;
            _cachedFallbackWebRid = "";
            _cachedFallbackUtc = DateTime.MinValue;
        }

        _rooms?.ClearResolvedCookie();
    }

    public async Task<string> GetActiveCookieAsync(CancellationToken ct = default)
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
        if (!string.IsNullOrWhiteSpace(path)
            && SidecarCookieStore.TryReadActiveCookie(path, out var cookie, out _, out _))
        {
            return cookie;
        }

        var webRid = ResolveWebRid();
        if (string.IsNullOrWhiteSpace(webRid))
        {
            throw new CookieInvalidException("未配置 web_rid，无法从 Sidecar 获取 Cookie");
        }

        var fallback = await TryResolveCookieFromSidecarAsync(webRid, ct);
        if (!string.IsNullOrWhiteSpace(fallback))
        {
            return fallback;
        }

        if (string.IsNullOrWhiteSpace(path))
        {
            throw new CookieInvalidException("未配置 CookieStorePath / DouyinExePath，无法读取 cookies.json");
        }

        throw new CookieInvalidException($"cookies.json 不存在或无效: {path}");
    }

    private string ResolveWebRid()
    {
        lock (_cacheLock)
        {
            if (!string.IsNullOrWhiteSpace(_boundWebRid))
            {
                return _boundWebRid;
            }
        }

        return _config.Settings.Douyin.WebRid?.Trim() ?? "";
    }

    private async Task<string?> TryResolveCookieFromSidecarAsync(string webRid, CancellationToken ct)
    {
        var fromResolver = NormalizeCookie(_rooms?.ResolvedCookie);
        if (fromResolver != null)
        {
            RememberFallbackCookie(webRid, fromResolver);
            return fromResolver;
        }

        string? cached;
        lock (_cacheLock)
        {
            if (!string.IsNullOrWhiteSpace(_cachedFallbackCookie)
                && string.Equals(_cachedFallbackWebRid, webRid, StringComparison.Ordinal)
                && DateTime.UtcNow - _cachedFallbackUtc < FallbackCacheTtl)
            {
                cached = _cachedFallbackCookie;
            }
            else
            {
                cached = null;
            }
        }

        if (cached != null)
        {
            return cached;
        }

        var room = await _douyin.ResolveRoomAsync(webRid, ct);
        var resolved = NormalizeCookie(room?.Raw?.Cookie);
        if (resolved != null)
        {
            RememberFallbackCookie(webRid, resolved);
        }

        return resolved;
    }

    private void RememberFallbackCookie(string webRid, string cookie)
    {
        lock (_cacheLock)
        {
            _cachedFallbackCookie = cookie;
            _cachedFallbackWebRid = webRid;
            _cachedFallbackUtc = DateTime.UtcNow;
        }
    }

    private static string? NormalizeCookie(string? raw)
    {
        var cookie = raw?.Trim();
        if (string.IsNullOrWhiteSpace(cookie) || !SidecarCookieStore.HasSessionId(cookie))
        {
            return null;
        }

        return cookie;
    }
}

/// <summary>
/// 通过 Sidecar room/resolve 获取 room_id。
/// </summary>
public sealed class SidecarGiftRoomResolver : IGiftRoomResolver
{
    private readonly DouyinService _douyin;

    public SidecarGiftRoomResolver(DouyinService douyin)
    {
        _douyin = douyin;
    }

    public string? ResolvedCookie { get; private set; }

    public async Task<string> ResolveRoomIdAsync(string webRid, CancellationToken ct = default)
    {
        var room = await _douyin.ResolveRoomAsync(webRid, ct);
        ResolvedCookie = room?.Raw?.Cookie?.Trim();
        if (room == null || string.IsNullOrWhiteSpace(room.RoomId))
        {
            throw new InvalidOperationException("无法从 Sidecar 解析 room_id");
        }

        return room.RoomId.Trim();
    }

    public void ClearResolvedCookie() => ResolvedCookie = null;
}
