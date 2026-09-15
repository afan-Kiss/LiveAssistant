namespace LiveAssistant;

/// <summary>启动阶段诊断日志（不依赖 LogService，单文件/早期失败也可落盘）。</summary>
internal static class StartupDiagnostics
{
    private static readonly object Lock = new();
    private static string? _logFile;

    public static string LogFilePath => _logFile ??= Path.Combine(AppPaths.LogsDirectory, "startup.log");

    public static void Write(string message)
    {
        try
        {
            lock (Lock)
            {
                Directory.CreateDirectory(AppPaths.LogsDirectory);
                var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}{Environment.NewLine}";
                File.AppendAllText(LogFilePath, line);
            }
        }
        catch
        {
            // 诊断日志失败不得阻断启动
        }
    }

    public static void LogStartupDiagnostic()
    {
        Write("STARTUP_DIAGNOSTIC:");
        Write($"exePath={Environment.ProcessPath ?? "(null)"}");
        Write($"baseDirectory={AppContext.BaseDirectory}");
        Write($"exeDirectory={AppPaths.ExeDirectory}");
        Write($"dataDirectory={AppPaths.ResolveDataDirectory()}");
        Write($"configPath={AppPaths.ConfigDirectory}");
        Write($"sidecarDirectory={AppPaths.SidecarDirectory}");
        Write($"isSingleFile={AppPaths.IsSingleFilePublish}");
        Write($"processId={Environment.ProcessId}");
    }

    public static void LogAnotherInstanceRunning()
    {
        Write("StartupFailed: reason=another_instance_running");
    }

    public static void LogStartupFailed(string reason, Exception? ex = null)
    {
        Write($"StartupFailed: reason={reason}");
        if (ex != null)
        {
            Write($"StartupFailed: exception={ex.GetType().Name}: {ex.Message}");
        }
    }
}
