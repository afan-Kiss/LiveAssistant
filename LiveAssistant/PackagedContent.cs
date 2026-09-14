using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace LiveAssistant;

/// <summary>
/// 单文件 EXE 启动时释放/合并内嵌资源到 EXE 旁，保证新电脑开箱可用。
/// </summary>
internal static class PackagedContent
{
    private const string Prefix = "LiveAssistant.Packaged.";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    public static void EnsureExtracted()
    {
        var asm = Assembly.GetExecutingAssembly();
        var names = asm.GetManifestResourceNames()
            .Where(n => n.StartsWith(Prefix, StringComparison.Ordinal))
            .ToArray();
        if (names.Length == 0)
        {
            return;
        }

        foreach (var name in names)
        {
            var relative = RelocateEmbeddedPath(name[Prefix.Length..]);
            var target = Path.Combine(AppPaths.ExeDirectory, relative);
            var dir = Path.GetDirectoryName(target);
            if (!string.IsNullOrWhiteSpace(dir))
            {
                Directory.CreateDirectory(dir);
            }

            using var input = asm.GetManifestResourceStream(name);
            if (input == null)
            {
                continue;
            }

            var rel = relative.Replace('\\', '/');
            if (rel.Equals("Config/appsettings.json", StringComparison.OrdinalIgnoreCase))
            {
                MergeAppSettings(target, input);
                continue;
            }

            // 后台页面每次用内嵌最新版覆盖，避免旧 HTML 缺回车登录等修复
            if (rel.StartsWith("Admin/", StringComparison.OrdinalIgnoreCase))
            {
                using var output = File.Create(target);
                input.CopyTo(output);
                continue;
            }

            // 凭证只在缺失时写入，不覆盖用户已有文件
            if (File.Exists(target)
                && rel.EndsWith("DeployCredentials.local.json", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (File.Exists(target) && rel.StartsWith("Config/", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            using (var output = File.Create(target))
            {
                input.CopyTo(output);
            }
        }
    }

    private static void MergeAppSettings(string target, Stream embedded)
    {
        using var reader = new StreamReader(embedded);
        var embeddedJson = reader.ReadToEnd();
        var embeddedNode = JsonNode.Parse(embeddedJson) as JsonObject ?? new JsonObject();

        if (!File.Exists(target))
        {
            File.WriteAllText(target, embeddedNode.ToJsonString(JsonOptions));
            return;
        }

        JsonObject existing;
        try
        {
            existing = JsonNode.Parse(File.ReadAllText(target)) as JsonObject ?? new JsonObject();
        }
        catch
        {
            File.WriteAllText(target, embeddedNode.ToJsonString(JsonOptions));
            return;
        }

        // 强制同步后台账号 / 隧道密钥 / 云端隧道（新电脑与升级都需要）
        if (embeddedNode["admin"] is JsonObject embAdmin)
        {
            var admin = existing["admin"] as JsonObject ?? new JsonObject();
            if (embAdmin["accounts"] != null)
            {
                admin["accounts"] = embAdmin["accounts"]!.DeepClone();
            }

            if (embAdmin["tunnelSecret"] != null)
            {
                admin["tunnelSecret"] = embAdmin["tunnelSecret"]!.DeepClone();
            }

            if (embAdmin["enabled"] != null)
            {
                admin["enabled"] = embAdmin["enabled"]!.DeepClone();
            }

            if (embAdmin["port"] != null)
            {
                admin["port"] = embAdmin["port"]!.DeepClone();
            }

            if (embAdmin["path"] != null)
            {
                admin["path"] = embAdmin["path"]!.DeepClone();
            }

            existing["admin"] = admin;
        }

        if (embeddedNode["adminTunnel"] is JsonObject embTunnel)
        {
            var tunnel = existing["adminTunnel"] as JsonObject ?? new JsonObject();
            foreach (var kv in embTunnel)
            {
                // 内嵌有值则写入；空字符串不覆盖已有非空
                var val = kv.Value;
                if (val is JsonValue jv && jv.TryGetValue<string>(out var s) && string.IsNullOrWhiteSpace(s))
                {
                    if (tunnel[kv.Key] != null)
                    {
                        continue;
                    }
                }

                tunnel[kv.Key] = val?.DeepClone();
            }

            existing["adminTunnel"] = tunnel;
        }

        File.WriteAllText(target, existing.ToJsonString(JsonOptions));
    }

    private static string RelocateEmbeddedPath(string dotted)
    {
        if (dotted.StartsWith("Config.", StringComparison.Ordinal))
        {
            return Path.Combine("Config", dotted["Config.".Length..]);
        }

        if (dotted.StartsWith("Admin.wwwroot.", StringComparison.Ordinal))
        {
            return Path.Combine("Admin", "wwwroot", dotted["Admin.wwwroot.".Length..]);
        }

        if (dotted.StartsWith("Admin.", StringComparison.Ordinal))
        {
            return Path.Combine("Admin", dotted["Admin.".Length..]);
        }

        return dotted.Replace('.', Path.DirectorySeparatorChar);
    }
}
