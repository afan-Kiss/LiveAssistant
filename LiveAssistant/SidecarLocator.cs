namespace LiveAssistant;

/// <summary>
/// 优先使用 LiveAssistant.exe 同目录（及常见子目录）里的 sidecar，方便把三个软件放一起。
/// 不匹配福袋/无人直播等无关 exe。
/// </summary>
internal static class SidecarLocator
{
    private static readonly string[] SearchSubdirs =
    {
        "",
        "sidecars",
        "sidecars\\douyin-cdp",
        "douyin-cdp",
        "bin",
        "抖音",
        "酷狗"
    };

    private static readonly string[] DouyinExact =
    {
        "cdp-danmaku.exe",
        "抖音直播弹幕v1.3.exe"
    };

    private static readonly string[] KugouExact =
    {
        "酷狗api_v1.5.exe",
        "酷狗api.exe",
        "KugouAPI.exe"
    };

    public const string PreferredDouyinFileName = "cdp-danmaku.exe";
    public const string PreferredKugouFileName = "酷狗api_v1.5.exe";
    public const string KugouJsFolderName = "kgapijs";
    public const string KgapiJsEntryFileName = "app.js";

    public static bool IsKgapiJsReady(string? directory)
        => !string.IsNullOrWhiteSpace(directory)
           && File.Exists(Path.Combine(directory.Trim(), KgapiJsEntryFileName));

    /// <summary>在 EXE 旁、酷狗 exe 旁、sidecars/ 等位置查找可用的 kgapijs 目录。</summary>
    public static string? FindKgapiJsDirectory(string? searchRoot = null)
    {
        searchRoot = string.IsNullOrWhiteSpace(searchRoot) ? AppPaths.ExeDirectory : searchRoot.Trim();
        var candidates = new List<string>
        {
            Path.Combine(searchRoot, KugouJsFolderName),
            Path.Combine(searchRoot, "sidecars", KugouJsFolderName)
        };

        var kugouExe = ResolveKugou("", searchRoot);
        var kugouDir = string.IsNullOrWhiteSpace(kugouExe) ? null : Path.GetDirectoryName(kugouExe);
        if (!string.IsNullOrWhiteSpace(kugouDir))
        {
            candidates.Add(Path.Combine(kugouDir, KugouJsFolderName));
        }

        var dir = searchRoot;
        for (var i = 0; i < 6; i++)
        {
            candidates.Add(Path.Combine(dir, "sidecars", KugouJsFolderName));
            var parent = Directory.GetParent(dir);
            if (parent == null)
            {
                break;
            }

            dir = parent.FullName;
        }

        foreach (var candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (IsKgapiJsReady(candidate))
            {
                return Path.GetFullPath(candidate);
            }
        }

        return null;
    }

    /// <summary>返回缺失的必要 sidecar 文件名（用于启动提示）。</summary>
    public static IReadOnlyList<string> GetMissingRequiredFiles(string douyinExePath, string kugouExePath)
    {
        var missing = new List<string>();

        if (string.IsNullOrWhiteSpace(douyinExePath) || !File.Exists(douyinExePath))
        {
            missing.Add(PreferredDouyinFileName);
        }

        if (string.IsNullOrWhiteSpace(kugouExePath) || !File.Exists(kugouExePath))
        {
            missing.Add(PreferredKugouFileName);
        }
        else
        {
            var kugouDir = Path.GetDirectoryName(kugouExePath);
            if (!string.IsNullOrWhiteSpace(kugouDir)
                && !IsKgapiJsReady(Path.Combine(kugouDir, KugouJsFolderName)))
            {
                missing.Add($"{KugouJsFolderName}\\");
            }
        }

        return missing;
    }

    public static string ResolveDouyin(string configuredPath, string? searchRoot = null)
        => Resolve(configuredPath, searchRoot, DouyinExact, isDouyin: true);

    public static string ResolveKugou(string configuredPath, string? searchRoot = null)
        => Resolve(configuredPath, searchRoot, KugouExact, isDouyin: false);

    public static IEnumerable<string> EnumerateSearchDirs(string searchRoot)
    {
        foreach (var sub in SearchSubdirs)
        {
            var dir = string.IsNullOrEmpty(sub) ? searchRoot : Path.Combine(searchRoot, sub);
            yield return dir;
        }
    }

    private static string Resolve(string configuredPath, string? searchRoot, string[] exactNames, bool isDouyin)
    {
        var root = string.IsNullOrWhiteSpace(searchRoot) ? AppPaths.ExeDirectory : searchRoot;

        foreach (var candidate in EnumerateLocalCandidates(root, exactNames, isDouyin))
        {
            if (File.Exists(candidate))
            {
                return Path.GetFullPath(candidate);
            }
        }

        var configured = NormalizeConfigured(configuredPath, root);
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
        {
            return Path.GetFullPath(configured);
        }

        return configured;
    }

    private static IEnumerable<string> EnumerateLocalCandidates(string root, string[] exactNames, bool isDouyin)
    {
        foreach (var dir in EnumerateSearchDirs(root))
        {
            if (!Directory.Exists(dir))
            {
                continue;
            }

            foreach (var name in exactNames)
            {
                yield return Path.Combine(dir, name);
            }

            foreach (var file in Directory.EnumerateFiles(dir, "*.exe"))
            {
                var fileName = Path.GetFileName(file);
                if (isDouyin ? IsDouyinApiExe(fileName) : IsKugouApiExe(fileName))
                {
                    yield return file;
                }
            }
        }
    }

    internal static bool IsDouyinApiExe(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName) || !fileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (LooksLikeFudaiOrUnrelated(fileName))
        {
            return false;
        }

        if (fileName.Contains("弹幕助手", StringComparison.OrdinalIgnoreCase)
            || fileName.Contains("douyin-danmaku", StringComparison.OrdinalIgnoreCase)
            || fileName.Contains("douyin-api", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return fileName.Equals("cdp-danmaku.exe", StringComparison.OrdinalIgnoreCase)
               || fileName.Equals("cdp-server.exe", StringComparison.OrdinalIgnoreCase)
               || fileName.Contains("抖音直播弹幕", StringComparison.OrdinalIgnoreCase)
               || fileName.Contains("douyin-cdp", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>CDP 原生程序打开即起 API，不附加旧助手参数。</summary>
    public static string DouyinStartArgs(string exePath)
    {
        _ = exePath;
        return "";
    }

    internal static bool IsKugouApiExe(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName) || !fileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return fileName.StartsWith("酷狗api", StringComparison.OrdinalIgnoreCase)
               || fileName.Equals("KugouAPI.exe", StringComparison.OrdinalIgnoreCase)
               || (fileName.StartsWith("kgapi", StringComparison.OrdinalIgnoreCase)
                   && fileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));
    }

    internal static bool LooksLikeFudaiOrUnrelated(string fileName)
    {
        return fileName.Contains("福袋", StringComparison.OrdinalIgnoreCase)
               || fileName.Contains("无人直播", StringComparison.OrdinalIgnoreCase)
               || fileName.Contains("maoyan", StringComparison.OrdinalIgnoreCase)
               || fileName.Contains("猫眼", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeConfigured(string configuredPath, string root)
    {
        if (string.IsNullOrWhiteSpace(configuredPath))
        {
            return "";
        }

        var path = configuredPath.Trim();
        if (!Path.IsPathRooted(path))
        {
            path = Path.Combine(root, path);
        }

        try
        {
            return Path.GetFullPath(path);
        }
        catch
        {
            return path;
        }
    }
}
