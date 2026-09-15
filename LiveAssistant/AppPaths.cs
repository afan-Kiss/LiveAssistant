namespace LiveAssistant;

/// <summary>
/// 单文件发布时 AppContext.BaseDirectory 可能指向临时解压目录；
/// 对外路径统一以 EXE 所在目录为准（不依赖 Assembly.Location）。
/// </summary>
internal static class AppPaths
{
    private static string? _cachedExeDirectory;
    private static string? _cachedDataDirectory;

    public static string ExeDirectory => ResolveExeDirectory();

    public static string ConfigDirectory => Path.Combine(ExeDirectory, "Config");

    public static string SidecarDirectory => ExeDirectory;

    public static string LogsDirectory => Path.Combine(ExeDirectory, "logs");

    /// <summary>单文件发布时 BaseDirectory 通常指向临时解压目录。</summary>
    public static bool IsSingleFilePublish
    {
        get
        {
            try
            {
                return !string.Equals(
                    Path.GetFullPath(AppContext.BaseDirectory),
                    Path.GetFullPath(ExeDirectory),
                    StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }
    }

    public static string ResolveDataDirectory()
    {
        if (_cachedDataDirectory != null)
        {
            return _cachedDataDirectory;
        }

        var overrideDir = Environment.GetEnvironmentVariable("LA_DATA_DIR");
        if (!string.IsNullOrWhiteSpace(overrideDir))
        {
            _cachedDataDirectory = Path.GetFullPath(overrideDir);
            return _cachedDataDirectory;
        }

        var baseDir = ExeDirectory;
        var localData = Path.GetFullPath(Path.Combine(baseDir, "data"));
        var devData = Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", "data"));

        // 部署/单文件：优先 EXE 旁 data，避免误用源码树 data
        if (Directory.Exists(localData))
        {
            _cachedDataDirectory = localData;
        }
        else if (Directory.Exists(devData))
        {
            _cachedDataDirectory = devData;
        }
        else
        {
            var appData = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "LiveAssistant", "data");
            _cachedDataDirectory = Directory.Exists(appData) ? appData : localData;
        }

        return _cachedDataDirectory;
    }

    private static string ResolveExeDirectory()
    {
        var testDir = Environment.GetEnvironmentVariable("LA_TEST_EXE_DIR");
        if (!string.IsNullOrWhiteSpace(testDir))
        {
            return Path.GetFullPath(testDir);
        }

        if (_cachedExeDirectory != null)
        {
            return _cachedExeDirectory;
        }

        try
        {
            var processPath = Environment.ProcessPath;
            if (!string.IsNullOrWhiteSpace(processPath))
            {
                var dir = Path.GetDirectoryName(processPath);
                if (!string.IsNullOrWhiteSpace(dir))
                {
                    _cachedExeDirectory = Path.GetFullPath(dir);
                    return _cachedExeDirectory;
                }
            }
        }
        catch
        {
            // fall through
        }

        _cachedExeDirectory = Path.GetFullPath(AppContext.BaseDirectory);
        return _cachedExeDirectory;
    }
}
