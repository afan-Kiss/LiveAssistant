using LiveAssistant.Config;

namespace LiveAssistant.Services;

/// <summary>
/// CDP 模式下从 GET /api/cookie/export 取当前 Chrome 登录 Cookie，再交给 GiftImFetchClient。
/// </summary>
public sealed class FileCookieProvider : ICookieProvider
{
    private readonly DouyinService _douyin;

    public FileCookieProvider(ConfigManager config, DouyinService douyin, IGiftRoomResolver? rooms = null)
    {
        _ = config;
        _ = rooms;
        _douyin = douyin;
    }

    public void BindWebRid(string webRid)
    {
        _ = webRid;
    }

    public void InvalidateCache()
    {
    }

    public async Task<string> GetActiveCookieAsync(CancellationToken ct = default)
    {
        var cookie = await _douyin.ExportCookieAsync(ct);
        if (string.IsNullOrWhiteSpace(cookie) || !SidecarCookieStore.HasSessionId(cookie))
        {
            throw new CookieInvalidException("gift_cookie_unavailable");
        }

        return cookie;
    }
}

/// <summary>
/// 通过 CDP room/resolve 获取 room_id。Cookie 不从 resolve 读取。
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
        if (room == null || string.IsNullOrWhiteSpace(room.RoomId))
        {
            throw new InvalidOperationException("无法从 CDP 解析 room_id");
        }

        return room.RoomId.Trim();
    }

    public void ClearResolvedCookie() => ResolvedCookie = null;
}
