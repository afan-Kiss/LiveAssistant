using System.Globalization;
using System.Text.RegularExpressions;

namespace LiveAssistant.Services;

/// <summary>快手 Cookie 过期启发式解析（无服务端校验时的本地提示）。</summary>
public static partial class KuaishouCookieHelper
{
    private static readonly Regex ExpiresRx = ExpiresRegex();
    private static readonly Regex MaxAgeRx = MaxAgeRegex();

    public static DateTime? TryParseEarliestExpiryUtc(string? cookie)
    {
        if (string.IsNullOrWhiteSpace(cookie))
        {
            return null;
        }

        DateTime? earliest = null;
        foreach (Match m in ExpiresRx.Matches(cookie))
        {
            var raw = m.Groups[1].Value.Trim();
            if (DateTime.TryParse(raw, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dt)
                || DateTime.TryParse(raw, CultureInfo.GetCultureInfo("en-US"),
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out dt))
            {
                if (earliest == null || dt < earliest)
                {
                    earliest = dt;
                }
            }
        }

        foreach (Match m in MaxAgeRx.Matches(cookie))
        {
            if (int.TryParse(m.Groups[1].Value, out var sec) && sec > 0)
            {
                var dt = DateTime.UtcNow.AddSeconds(sec);
                if (earliest == null || dt < earliest)
                {
                    earliest = dt;
                }
            }
        }

        return earliest;
    }

    public static (bool Stale, string Hint) Evaluate(string? cookie, long savedAtTicks, long expiresAtTicks, int failureStreak)
    {
        if (string.IsNullOrWhiteSpace(cookie))
        {
            return (true, "未配置 Cookie");
        }

        if (failureStreak >= 3)
        {
            return (true, $"连续连接失败 {failureStreak} 次，Cookie 可能已失效，请重新从浏览器复制");
        }

        if (expiresAtTicks > 0)
        {
            var exp = new DateTime(expiresAtTicks, DateTimeKind.Utc);
            if (exp <= DateTime.UtcNow)
            {
                return (true, $"Cookie 已过期（约 {exp.ToLocalTime():yyyy-MM-dd HH:mm}）");
            }

            if (exp <= DateTime.UtcNow.AddDays(2))
            {
                return (false, $"Cookie 将在 {exp.ToLocalTime():yyyy-MM-dd HH:mm} 左右过期，建议提前更新");
            }
        }

        if (savedAtTicks > 0)
        {
            var saved = new DateTime(savedAtTicks, DateTimeKind.Utc);
            if (DateTime.UtcNow - saved > TimeSpan.FromDays(14))
            {
                return (false, $"Cookie 已保存超过 {(int)(DateTime.UtcNow - saved).TotalDays} 天，若连接失败请更新");
            }
        }

        return (false, "Cookie 已配置");
    }

    [GeneratedRegex(@"expires=([^;]+)", RegexOptions.IgnoreCase)]
    private static partial Regex ExpiresRegex();

    [GeneratedRegex(@"max-age=(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex MaxAgeRegex();
}
