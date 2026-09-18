namespace LiveAssistant.Utils;

/// <summary>
/// 双平台用户主键：快手统一加 ks: 前缀，避免与抖音 userId 数字撞库。
/// 抖音保持原始 id，兼容已有积分数据。
/// </summary>
public static class PlatformUserIds
{
    public const string KuaishouPrefix = "ks:";

    public static bool IsKuaishouCanonical(string? userId)
        => !string.IsNullOrWhiteSpace(userId)
           && userId.StartsWith(KuaishouPrefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>入库 / 业务用稳定 id。</summary>
    public static string Canonical(string? platform, string? userId, string? roomKey = null)
    {
        var uid = (userId ?? "").Trim();
        if (uid.Length == 0)
        {
            return "";
        }

        var isKs = string.Equals(platform?.Trim(), "kuaishou", StringComparison.OrdinalIgnoreCase)
                   || string.Equals(platform?.Trim(), "ks", StringComparison.OrdinalIgnoreCase)
                   || (!string.IsNullOrWhiteSpace(roomKey)
                       && roomKey.StartsWith(KuaishouPrefix, StringComparison.OrdinalIgnoreCase));

        if (!isKs)
        {
            return uid;
        }

        if (uid.StartsWith(KuaishouPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var rest = uid[KuaishouPrefix.Length..].Trim();
            return rest.Length == 0 ? "" : KuaishouPrefix + rest;
        }

        return KuaishouPrefix + uid;
    }

    /// <summary>调平台 API 时去掉本地前缀。</summary>
    public static string RawForApi(string? canonicalUserId)
    {
        var uid = (canonicalUserId ?? "").Trim();
        if (uid.StartsWith(KuaishouPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return uid[KuaishouPrefix.Length..].Trim();
        }

        return uid;
    }
}
