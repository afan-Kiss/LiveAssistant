namespace LiveAssistant.Services;

/// <summary>
/// Cookie 提供抽象，便于替换文件实现 / 未来接口实现。
/// </summary>
public interface ICookieProvider
{
    /// <summary>获取当前可用登录 Cookie；无效时抛 <see cref="CookieInvalidException"/>。</summary>
    Task<string> GetActiveCookieAsync(CancellationToken ct = default);
}

/// <summary>
/// 房间解析抽象（测试可替换）。
/// </summary>
public interface IGiftRoomResolver
{
    Task<string> ResolveRoomIdAsync(string webRid, CancellationToken ct = default);
}
