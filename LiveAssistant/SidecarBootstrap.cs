namespace LiveAssistant;

/// <summary>
/// 启动前把 sidecar 从同目录 sidecars/ 或上级仓库 sidecars/ 补齐到 EXE 旁。
/// </summary>
internal static class SidecarBootstrap
{
    public static void EnsureReady()
    {
        var target = AppPaths.ExeDirectory;
        if (IsReady(target))
        {
            return;
        }

        var donor = FindDonorDirectory(target);
        if (donor == null)
        {
            return;
        }

        CopyIfMissing(Path.Combine(donor, SidecarLocator.PreferredDouyinFileName),
            Path.Combine(target, SidecarLocator.PreferredDouyinFileName));

        var donorKugou = FindKugouExe(donor);
        if (!string.IsNullOrWhiteSpace(donorKugou))
        {
            CopyIfMissing(donorKugou, Path.Combine(target, SidecarLocator.PreferredKugouFileName));
        }

        CopyTreeIfMissing(
            Path.Combine(donor, SidecarLocator.KugouJsFolderName),
            Path.Combine(target, SidecarLocator.KugouJsFolderName));
    }

    private static bool IsReady(string root)
    {
        var douyin = SidecarLocator.ResolveDouyin("", root);
        var kugou = SidecarLocator.ResolveKugou("", root);
        return SidecarLocator.GetMissingRequiredFiles(douyin, kugou).Count == 0;
    }

    private static string? FindDonorDirectory(string start)
    {
        var local = Path.Combine(start, "sidecars");
        if (HasKugouPayload(local))
        {
            return local;
        }

        var dir = start;
        for (var i = 0; i < 6; i++)
        {
            var candidate = Path.Combine(dir, "sidecars");
            if (HasKugouPayload(candidate))
            {
                return candidate;
            }

            var parent = Directory.GetParent(dir);
            if (parent == null)
            {
                break;
            }

            dir = parent.FullName;
        }

        return null;
    }

    private static bool HasKugouPayload(string dir)
    {
        if (!Directory.Exists(dir))
        {
            return false;
        }

        return !string.IsNullOrWhiteSpace(FindKugouExe(dir))
               && Directory.Exists(Path.Combine(dir, SidecarLocator.KugouJsFolderName));
    }

    private static string? FindKugouExe(string dir)
    {
        if (!Directory.Exists(dir))
        {
            return null;
        }

        var preferred = Path.Combine(dir, SidecarLocator.PreferredKugouFileName);
        if (File.Exists(preferred))
        {
            return preferred;
        }

        foreach (var file in Directory.EnumerateFiles(dir, "*.exe"))
        {
            if (SidecarLocator.IsKugouApiExe(Path.GetFileName(file)))
            {
                return file;
            }
        }

        return null;
    }

    private static void CopyIfMissing(string source, string destination)
    {
        if (!File.Exists(source) || File.Exists(destination))
        {
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(destination) ?? AppPaths.ExeDirectory);
        File.Copy(source, destination, overwrite: false);
    }

    private static void CopyTreeIfMissing(string source, string destination)
    {
        if (!Directory.Exists(source) || Directory.Exists(destination))
        {
            return;
        }

        CopyDirectory(source, destination);
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var dir in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(dir.Replace(source, destination, StringComparison.OrdinalIgnoreCase));
        }

        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var target = file.Replace(source, destination, StringComparison.OrdinalIgnoreCase);
            var targetDir = Path.GetDirectoryName(target);
            if (!string.IsNullOrWhiteSpace(targetDir))
            {
                Directory.CreateDirectory(targetDir);
            }

            if (!File.Exists(target))
            {
                File.Copy(file, target, overwrite: false);
            }
        }
    }
}
