using System.Text;

namespace LiveAssistant.Utils;

/// <summary>原子写入配置文件，避免崩溃/断电导致正式文件损坏。</summary>
internal static class AtomicFileWriter
{
    public static void WriteAllText(string path, string content)
    {
        var fullPath = Path.GetFullPath(path);
        var dir = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrWhiteSpace(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var tmp = fullPath + ".tmp";
        File.WriteAllText(tmp, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        using (var fs = new FileStream(tmp, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            fs.Flush(flushToDisk: true);
        }

        var backup = fullPath + ".bak";
        if (File.Exists(fullPath))
        {
            File.Replace(tmp, fullPath, backup, ignoreMetadataErrors: true);
        }
        else
        {
            File.Move(tmp, fullPath);
        }

        if (File.Exists(tmp))
        {
            try { File.Delete(tmp); } catch { /* ignore */ }
        }
    }

    public static bool IsCorruptOrEmpty(string path)
    {
        if (!File.Exists(path))
        {
            return false;
        }

        var info = new FileInfo(path);
        if (info.Length == 0)
        {
            return true;
        }

        var bytes = File.ReadAllBytes(path);
        if (bytes.Length == 0)
        {
            return true;
        }

        return bytes.All(b => b == 0);
    }

    /// <summary>备份损坏文件并尝试从 .bak 恢复；返回可用内容或 null。</summary>
    public static string? RecoverCorruptFile(string path, Action<string>? log = null)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        if (!IsCorruptOrEmpty(path))
        {
            try
            {
                return File.ReadAllText(path);
            }
            catch (Exception ex)
            {
                log?.Invoke($"读取配置失败: {ex.Message}");
            }
        }

        var stamp = DateTime.Now.ToString("yyyyMMddHHmmss");
        var badPath = $"{path}.bad.{stamp}";
        try
        {
            File.Copy(path, badPath, overwrite: true);
            log?.Invoke($"已备份损坏配置: {badPath}");
        }
        catch (Exception ex)
        {
            log?.Invoke($"备份损坏配置失败: {ex.Message}");
        }

        var bakPath = path + ".bak";
        if (File.Exists(bakPath) && !IsCorruptOrEmpty(bakPath))
        {
            try
            {
                var restored = File.ReadAllText(bakPath);
                WriteAllText(path, restored);
                log?.Invoke($"已从备份恢复配置: {bakPath}");
                return restored;
            }
            catch (Exception ex)
            {
                log?.Invoke($"从备份恢复失败: {ex.Message}");
            }
        }

        return null;
    }

    public static string ReadTextOrEmpty(string path)
    {
        if (!File.Exists(path))
        {
            return "";
        }

        if (IsCorruptOrEmpty(path))
        {
            return "";
        }

        return File.ReadAllText(path);
    }
}
