namespace LiveAssistant;

/// <summary>
/// 单文件发布时 AppContext.BaseDirectory 可能指向临时解压目录；
/// 对外路径统一以 EXE 所在目录为准。
/// </summary>
internal static class AppPaths
{
    private static string? _cachedExeDirectory;

    public static string ExeDirectory => ResolveExeDirectory();

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
