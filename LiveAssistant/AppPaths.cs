namespace LiveAssistant;

/// <summary>
/// 单文件发布时 AppContext.BaseDirectory 可能指向临时解压目录；
/// 对外路径统一以 EXE 所在目录为准。
/// </summary>
internal static class AppPaths
{
    public static string ExeDirectory { get; } = ResolveExeDirectory();

    private static string ResolveExeDirectory()
    {
        try
        {
            var processPath = Environment.ProcessPath;
            if (!string.IsNullOrWhiteSpace(processPath))
            {
                var dir = Path.GetDirectoryName(processPath);
                if (!string.IsNullOrWhiteSpace(dir))
                {
                    return Path.GetFullPath(dir);
                }
            }
        }
        catch
        {
            // fall through
        }

        return Path.GetFullPath(AppContext.BaseDirectory);
    }
}
