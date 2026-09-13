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
