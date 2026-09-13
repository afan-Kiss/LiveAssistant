using LiveAssistant.Config;

namespace LiveAssistant.Services;

/// <summary>
/// 基于 Sidecar cookies.json 的 CookieProvider 实现。
/// 先通过 /api/cookie 检查登录状态，再读本地完整 Cookie。
/// </summary>
public sealed class FileCookieProvider : ICookieProvider
{
    private readonly ConfigManager _config;
    private readonly DouyinService _douyin;

    public FileCookieProvider(ConfigManager config, DouyinService douyin)
    {
        _config = config;
        _douyin = douyin;
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

    public async Task<string> ResolveRoomIdAsync(string webRid, CancellationToken ct = default)
    {
        var room = await _douyin.ResolveRoomAsync(webRid, ct);
        if (room == null || string.IsNullOrWhiteSpace(room.RoomId))
        {
            throw new InvalidOperationException("无法从 Sidecar 解析 room_id");
        }

        return room.RoomId.Trim();
    }
}
