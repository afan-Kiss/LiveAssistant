using System.Text.Json;
using System.Text.Json.Serialization;
using LiveAssistant.Config;

namespace LiveAssistant.Services;

/// <summary>
/// 从 Sidecar 的 cookies.json 读取完整 Cookie。
/// GET /api/cookie 仅返回脱敏摘要，故完整 Cookie 需读本地存储文件（不修改 Sidecar）。
/// </summary>
public static class SidecarCookieStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public static string? ResolveStorePath(DouyinSettings settings)
    {
        if (!string.IsNullOrWhiteSpace(settings.CookieStorePath))
        {
            return settings.CookieStorePath.Trim();
        }

        if (string.IsNullOrWhiteSpace(settings.DouyinExePath))
        {
            return null;
        }

        try
        {
            var exe = Path.GetFullPath(settings.DouyinExePath.Trim());
            var binDir = Path.GetDirectoryName(exe);
            if (string.IsNullOrWhiteSpace(binDir))
            {
                return null;
            }

            // 抖音直播弹幕助手.exe 在 build\ 旁 data\；旧版 douyin-danmaku 在 build\bin\data
            var candidates = new[]
            {
                Path.Combine(binDir, "data", "cookies.json"),
                Path.GetFullPath(Path.Combine(binDir, "..", "..", "data", "cookies.json")),
                Path.GetFullPath(Path.Combine(binDir, "..", "data", "cookies.json"))
            };

            foreach (var path in candidates)
            {
                if (File.Exists(path))
                {
                    return path;
                }
            }

            return candidates[0];
        }
        catch
        {
            return null;
        }
    }

    public static bool TryReadActiveCookie(string storePath, out string cookie, out string? activeName, out string? error)
        => TryReadActiveProfile(storePath, out cookie, out activeName, out _, out error);

    public static bool TryReadActiveProfile(
        string storePath,
        out string cookie,
        out string? activeName,
        out string? profileId,
        out string? error)
    {
        cookie = "";
        activeName = null;
        profileId = null;
        error = null;

        if (string.IsNullOrWhiteSpace(storePath) || !File.Exists(storePath))
        {
            error = "cookies.json 不存在: " + storePath;
            return false;
        }

        try
        {
            var json = File.ReadAllText(storePath);
            var data = JsonSerializer.Deserialize<CookieFile>(json, JsonOptions);
            if (data?.Profiles == null || data.Profiles.Count == 0)
            {
                error = "cookies.json 无账号";
                return false;
            }

            activeName = string.IsNullOrWhiteSpace(data.Active) ? data.Profiles.Keys.First() : data.Active;
            if (!data.Profiles.TryGetValue(activeName, out var profile) || profile == null)
            {
                profile = data.Profiles.Values.FirstOrDefault();
                activeName = data.Profiles.FirstOrDefault(kv => kv.Value == profile).Key;
            }

            profileId = profile?.Id?.Trim();
            cookie = NormalizeStoredCookie(profile?.Cookie);
            if (string.IsNullOrWhiteSpace(cookie))
            {
                error = "当前账号 Cookie 为空";
                return false;
            }

            if (!HasSessionId(cookie))
            {
                error = "Cookie 缺少 sessionid";
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>解包 cookies.json 中多层 {"cookie":"..."} 嵌套，返回扁平 Cookie 串。</summary>
    public static string NormalizeStoredCookie(string? raw)
    {
        var current = raw?.Trim() ?? "";
        for (var depth = 0; depth < 8 && !string.IsNullOrWhiteSpace(current); depth++)
        {
            if (HasSessionId(current))
            {
                return current;
            }

            if (!TryUnwrapCookieJson(current, out var inner))
            {
                break;
            }

            current = inner;
        }

        return current;
    }

    private static bool TryUnwrapCookieJson(string raw, out string inner)
    {
        inner = "";
        if (!raw.StartsWith('{') || !raw.EndsWith('}'))
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(raw);
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("cookie", out var cookieProp)
                || cookieProp.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            inner = cookieProp.GetString()?.Trim() ?? "";
            return !string.IsNullOrWhiteSpace(inner) && !string.Equals(inner, raw, StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    public static bool HasSessionId(string cookie)
        => cookie.Contains("sessionid=", StringComparison.OrdinalIgnoreCase)
           || cookie.Contains("sessionid_ss=", StringComparison.OrdinalIgnoreCase);

    private sealed class CookieFile
    {
        [JsonPropertyName("active")]
        public string? Active { get; set; }

        [JsonPropertyName("profiles")]
        public Dictionary<string, CookieProfileEntry>? Profiles { get; set; }
    }

    private sealed class CookieProfileEntry
    {
        [JsonPropertyName("id")]
        public string? Id { get; set; }

        [JsonPropertyName("cookie")]
        public string? Cookie { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }
    }
}
