using System.Globalization;

namespace LiveAssistant.Models;

public sealed class DanmakuItem
{
    public string MsgId { get; set; } = "";
    public string Content { get; set; } = "";
    public string Nickname { get; set; } = "";
    public string UserId { get; set; } = "";
    public string MsgType { get; set; } = "chat";
    public DateTime Timestamp { get; set; } = DateTime.Now;

    public string DisplayLine
    {
        get
        {
            var icon = MsgType switch
            {
                "gift" => "🎁",
                "member" => "👋",
                _ => "😊"
            };
            var text = TruncateSingleLine(Content, 120);
            return $"[{Timestamp:HH:mm:ss}] {icon} {Nickname}：{text}";
        }
    }

    public static DateTime ParseTimestamp(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return DateTime.Now;
        }

        var trimmed = raw.Trim();
        if (long.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out var unix)
            && TryFromUnix(unix, out var fromUnix))
        {
            return fromUnix;
        }

        var formats = new[]
        {
            "yyyy-M-d HH:mm:ss",
            "yyyy-MM-dd HH:mm:ss",
            "yyyy/M/d HH:mm:ss",
            "yyyy/MM/dd HH:mm:ss"
        };

        if (DateTime.TryParseExact(trimmed, formats, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var exact)
            || DateTime.TryParse(trimmed, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out exact)
            || DateTime.TryParse(trimmed, out exact))
        {
            return exact;
        }

        return DateTime.Now;
    }

    private static bool TryFromUnix(long value, out DateTime dt)
    {
        dt = default;
        try
        {
            dt = value >= 1_000_000_000_000
                ? DateTimeOffset.FromUnixTimeMilliseconds(value).LocalDateTime
                : DateTimeOffset.FromUnixTimeSeconds(value).LocalDateTime;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string TruncateSingleLine(string text, int max)
    {
        text = text.Replace("\r", " ").Replace("\n", " ").Trim();
        return text.Length <= max ? text : text[..max] + "…";
    }
}
