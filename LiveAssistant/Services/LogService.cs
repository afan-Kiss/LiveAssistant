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

    public void Error(string source, string message, Exception? ex = null)
    {
        var text = ex == null ? message : $"{message} | {ex.GetType().Name}: {ex.Message}";
        Write(source, "ERROR", text);
        if (source != "error")
        {
            Write("error", "ERROR", $"[{source}] {text}");
        }
        Write("app", "ERROR", $"[{source}] {text}");
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
    public void SyncInfo(string message) => Write("sync", "INFO", message);
    public void SyncWarn(string message) => Write("sync", "WARN", message);
    public void BanInfo(string message) => Write("ban", "INFO", message);
    public void BanWarn(string message) => Write("ban", "WARN", message);

    public void LogGift(string userId, string nickname, string giftName, int count, int pointsDelta, int pointsAfter)
    {
        var line = $"userId={userId} user={nickname} gift={giftName} count={count} pointsDelta={pointsDelta} pointsAfter={pointsAfter}";
        Write("gift", "INFO", line);
    }

    public void SetLastError(string source, string message) => _lastError = $"[{source}] {message}";

    private volatile string _lastError = "";
    public string LastError => _lastError;

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
