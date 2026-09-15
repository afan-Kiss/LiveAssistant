namespace LiveAssistant.Services;

public sealed class LogService
{
    private readonly string _logDir;
    private readonly object _lock = new();

    public LogService(string dataDir)
    {
        var logDir = Path.GetFullPath(Path.Combine(dataDir, "..", "logs"));
        if (!Directory.Exists(Path.GetDirectoryName(logDir) ?? logDir))
        {
            logDir = Path.Combine(dataDir, "logs");
        }
        _logDir = logDir;
        Directory.CreateDirectory(_logDir);
    }

    public string LogDirectory => _logDir;

    public void Info(string message) => Write("app", "INFO", message);
    public void Warn(string message) => Write("app", "WARN", message);

    public void DouyinInfo(string message) => Write("douyin", "INFO", message);
    public void DouyinWarn(string message) => Write("douyin", "WARN", message);

    public void KugouInfo(string message) => Write("kugou", "INFO", message);
    public void KugouWarn(string message) => Write("kugou", "WARN", message);

    public void PlaybackInfo(string message) => Write("playback", "INFO", message);
    public void PlaybackWarn(string message) => Write("playback", "WARN", message);

    public void Error(string message, Exception? ex = null) => Error("app", message, ex);

    public event Action? ErrorRecorded;

    public void Error(string source, string message, Exception? ex = null)
    {
        var text = ex == null ? message : $"{message} | {ex.GetType().Name}: {ex.Message}";
        Write(source, "ERROR", text);
        if (source != "error")
        {
            Write("error", "ERROR", $"[{source}] {text}");
        }
        Write("app", "ERROR", $"[{source}] {text}");
        try { ErrorRecorded?.Invoke(); } catch { /* ignore */ }
    }

    public void LogPlayback(
        string song,
        string source,
        string user,
        string? url,
        bool success,
        string? errorReason = null)
    {
        var urlPart = string.IsNullOrWhiteSpace(url) ? "-" : url;
        var result = success ? "SUCCESS" : "FAILED";
        var err = string.IsNullOrWhiteSpace(errorReason) ? "" : $" reason={errorReason}";
        var line = $"song={song} source={source} user={user} url={urlPart} result={result}{err}";
        Write("playback", success ? "INFO" : "WARN", line);
        if (!success)
        {
            Write("error", "ERROR", $"[playback] {line}");
        }
    }

    public void LogSongRequest(string user, string song, bool success, string? reason = null)
    {
        var result = success ? "SUCCESS" : "FAILED";
        var reasonPart = string.IsNullOrWhiteSpace(reason) ? "" : $" reason={reason}";
        var line = $"user={user} song={song} result={result}{reasonPart}";
        Write("song_request", success ? "INFO" : "WARN", line);
        if (!success)
        {
            Write("error", "WARN", $"[song_request] {line}");
        }
    }

    public void LogPlaybackError(string song, string user, string source, string? url, string? errorReason)
    {
        var urlPart = string.IsNullOrWhiteSpace(url) ? "-" : url;
        var err = string.IsNullOrWhiteSpace(errorReason) ? "unknown" : errorReason;
        var line = $"song={song} user={user} source={source} url={urlPart} error={err}";
        Write("playback_error", "ERROR", line);
        Write("error", "ERROR", $"[playback_error] {line}");
    }

    public void GiftInfo(string message) => Write("gift", "INFO", message);
    public void GiftWarn(string message) => Write("gift", "WARN", message);
    public void AdminInfo(string message) => Write("admin", "INFO", message);

    /// <summary>供后台运营日志页读取，面向非开发人员。</summary>
    public IReadOnlyList<OpsLogEntry> ReadRecentOpsEntries(int limit = 100)
    {
        limit = Math.Clamp(limit, 1, 500);
        var entries = new List<OpsLogEntry>();
        foreach (var fileKey in new[] { "admin", "error", "app", "song_request", "gift" })
        {
            entries.AddRange(ReadLogFileEntries(fileKey, limit));
        }

        return entries
            .OrderByDescending(e => e.SortKey)
            .Take(limit)
            .ToList();
    }

    private IEnumerable<OpsLogEntry> ReadLogFileEntries(string fileKey, int maxLines)
    {
        var path = Path.Combine(_logDir, $"{fileKey}.log");
        if (!File.Exists(path))
        {
            yield break;
        }

        string[] lines;
        lock (_lock)
        {
            try
            {
                lines = File.ReadAllLines(path);
            }
            catch
            {
                yield break;
            }
        }

        var start = Math.Max(0, lines.Length - maxLines);
        for (var i = lines.Length - 1; i >= start; i--)
        {
            var parsed = ParseOpsLine(lines[i], fileKey);
            if (parsed != null)
            {
                yield return parsed;
            }
        }
    }

    private static OpsLogEntry? ParseOpsLine(string line, string source)
    {
        if (string.IsNullOrWhiteSpace(line) || line.Length < 25)
        {
            return null;
        }

        var timePart = line[..23];
        if (!DateTime.TryParse(timePart, out var ts))
        {
            return null;
        }

        var rest = line.Length > 26 ? line[26..].Trim() : "";
        var level = "INFO";
        var message = rest;
        if (rest.StartsWith('['))
        {
            var end = rest.IndexOf(']');
            if (end > 1)
            {
                level = rest[1..end];
                message = rest[(end + 1)..].Trim();
            }
        }

        var (evt, result) = FormatOpsEvent(source, level, message);
        return new OpsLogEntry(ts.ToString("yyyy-MM-dd HH:mm:ss"), evt, result, level, ts.Ticks);
    }

    private static (string Event, string Result) FormatOpsEvent(string source, string level, string message)
    {
        var sourceLabel = source switch
        {
            "admin" => "后台操作",
            "error" => "系统异常",
            "song_request" => "点歌",
            "gift" => "礼物",
            _ => "运行"
        };

        if (message.Contains("result=SUCCESS", StringComparison.OrdinalIgnoreCase))
        {
            return (sourceLabel, "成功");
        }

        if (message.Contains("result=FAILED", StringComparison.OrdinalIgnoreCase))
        {
            return (sourceLabel, "失败");
        }

        if (string.Equals(level, "ERROR", StringComparison.OrdinalIgnoreCase))
        {
            return (sourceLabel, message);
        }

        if (string.Equals(level, "WARN", StringComparison.OrdinalIgnoreCase))
        {
            return (sourceLabel, message);
        }

        return (sourceLabel, message);
    }
    public void SyncInfo(string message) => Write("sync", "INFO", message);
    public void SyncWarn(string message) => Write("sync", "WARN", message);
    public void BanInfo(string message) => Write("ban", "INFO", message);
    public void BanWarn(string message) => Write("ban", "WARN", message);

    public void LogGift(string userId, string nickname, string giftName, int count, int pointsDelta, int pointsAfter)
    {
        var line = $"userId={userId} user={nickname} gift={giftName} count={count} pointsDelta={pointsDelta} pointsAfter={pointsAfter}";
        Write("gift", "INFO", line);
    }

    public void LogGiftDuplicate(string nickname, string giftName, string eventId, string reason)
    {
        var line = $"[GiftDuplicate] 用户={nickname} 礼物={giftName} 事件ID={eventId} 跳过原因={reason}";
        Write("gift", "WARN", line);
    }

    public void SetLastError(string source, string message) => _lastError = $"[{source}] {message}";

    private volatile string _lastError = "";
    public string LastError => _lastError;

    public sealed record OpsLogEntry(string Time, string Event, string Result, string Level, long SortKey);

    private void Write(string fileKey, string level, string message)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}";
        var path = Path.Combine(_logDir, $"{fileKey}.log");
        lock (_lock)
        {
            try
            {
                File.AppendAllText(path, line + Environment.NewLine);
            }
            catch
            {
                // ignore log failures
            }
        }
    }
}
