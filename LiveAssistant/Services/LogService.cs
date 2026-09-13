namespace LiveAssistant.Services;

public sealed class LogService
{
    private readonly string _logPath;
    private readonly object _lock = new();

    public LogService(string dataDir)
    {
        var logDir = Path.GetFullPath(Path.Combine(dataDir, "..", "logs"));
        if (!Directory.Exists(logDir))
        {
            logDir = Path.Combine(dataDir, "logs");
        }
        Directory.CreateDirectory(logDir);
        _logPath = Path.Combine(logDir, "app.log");
    }

    public void Info(string message) => Write("INFO", message);
    public void Warn(string message) => Write("WARN", message);
    public void Error(string message, Exception? ex = null)
    {
        var text = ex == null ? message : $"{message} | {ex.Message}";
        Write("ERROR", text);
    }

    private void Write(string level, string message)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{level}] {message}";
        lock (_lock)
        {
            try
            {
                File.AppendAllText(_logPath, line + Environment.NewLine);
            }
            catch
            {
                // ignore log failures
            }
        }
    }
}
