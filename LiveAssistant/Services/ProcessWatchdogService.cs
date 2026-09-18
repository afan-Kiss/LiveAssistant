using System.Diagnostics;
using System.Net.Http;
using LiveAssistant.Config;

namespace LiveAssistant.Services;

public sealed class ProcessWatchdogService
{
    private static readonly HttpClient KgapiHealthClient = new() { Timeout = TimeSpan.FromSeconds(2) };
    private static DateTime _kgapijsLastStartAttemptUtc = DateTime.MinValue;
    private static readonly TimeSpan KgapiJsRetryInterval = TimeSpan.FromSeconds(60);

    private readonly ConfigManager _config;
    private readonly LogService _log;
    private readonly SystemMessageService _system;

    public ProcessWatchdogService(ConfigManager config, LogService log, SystemMessageService system)
    {
        _config = config;
        _log = log;
        _system = system;
    }

    public IReadOnlyList<string> GetMissingRequiredFiles()
    {
        var root = AppPaths.ExeDirectory;
        var douyin = SidecarLocator.ResolveDouyin(_config.Settings.Douyin.DouyinExePath, root);
        var kugou = SidecarLocator.ResolveKugou(_config.Settings.Kugou.KugouExePath, root);
        return SidecarLocator.GetMissingRequiredFiles(douyin, kugou);
    }

    public async Task EnsureSidecarsAsync(
        Func<Task<bool>> douyinHealth,
        Func<Task<bool>> kugouHealth,
        Func<Task<bool>>? kuaishouHealth = null,
        CancellationToken ct = default)
    {
        try
        {
            if (GetMissingRequiredFiles().Count > 0)
            {
                // 抖音/酷狗缺文件时仍可尝试拉起快手 jar
            }
            else
            {
            if (!await douyinHealth())
            {
                await EnsureDouyinCdpAsync(ct);
            }
            else
            {
                _log.Info(
                    $"DOUYIN_CDP_WATCHDOG stage=reuse result=health_ok baseUrl={_config.Settings.Douyin.BaseUrl} pid=0");
            }

                if (!await kugouHealth())
                {
                    await TryStartProcessAsync(_config.Settings.Kugou.KugouExePath, "", "酷狗", ct);
                }

                if (await kugouHealth())
                {
                    await EnsureKgapiJsAsync(ct);
                }
            }

            if (kuaishouHealth != null
                && _config.Settings.Kuaishou.Enabled
                && !await kuaishouHealth())
            {
                await TryStartKuaishouAsync(ct);
            }
        }
        catch (Exception ex)
        {
            _log.Error("app", "Sidecar 守护检查异常", ex);
        }
    }

    private async Task TryStartKuaishouAsync(CancellationToken ct)
    {
        var jar = Path.Combine(AppPaths.ExeDirectory, "sidecars", "kuaishou", "ks-ui-server.jar");
        if (!File.Exists(jar))
        {
            return;
        }

        var port = ParsePortFromBaseUrl(_config.Settings.Kuaishou.BaseUrl, 18900);
        if (await IsLocalPortOpenAsync(port, ct))
        {
            _log.KuaishouInfo($"快手侧车端口 :{port} 已占用，跳过启动");
            return;
        }

        var java = ResolveJavaExe();
        if (string.IsNullOrWhiteSpace(java))
        {
            _log.KuaishouWarn("未找到 Java，无法自动启动 ks-ui-server.jar（请安装 JDK17 或配置 JAVA_HOME）");
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = java,
                Arguments = $"-jar \"{jar}\"",
                WorkingDirectory = Path.GetDirectoryName(jar) ?? AppPaths.ExeDirectory,
                UseShellExecute = false,
                CreateNoWindow = true
            });
            _system.Add("已启动快手侧车 ks-ui-server");
            _log.KuaishouInfo($"启动快手侧车: {java} -jar {jar}");
            await Task.Delay(2500, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.Error("app", "启动快手侧车失败", ex);
        }
    }

    internal static string? ResolveJavaExePublic() => ResolveJavaExe();

    private static string? ResolveJavaExe()
    {
        var home = Environment.GetEnvironmentVariable("JAVA_HOME");
        if (!string.IsNullOrWhiteSpace(home))
        {
            var candidate = Path.Combine(home.Trim(), "bin", OperatingSystem.IsWindows() ? "java.exe" : "java");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        var portable = Path.Combine(AppPaths.ExeDirectory, "sidecars", "kuaishou", "jre", "bin",
            OperatingSystem.IsWindows() ? "java.exe" : "java");
        if (File.Exists(portable))
        {
            return portable;
        }

        return OperatingSystem.IsWindows() ? "java.exe" : "java";
    }

    private static int ParsePortFromBaseUrl(string? baseUrl, int fallback)
    {
        try
        {
            if (Uri.TryCreate(string.IsNullOrWhiteSpace(baseUrl) ? "" : baseUrl.Trim(), UriKind.Absolute, out var uri)
                && uri.Port > 0)
            {
                return uri.Port;
            }
        }
        catch
        {
            // ignore
        }

        return fallback;
    }

    private static async Task<bool> IsLocalPortOpenAsync(int port, CancellationToken ct)
    {
        try
        {
            using var tcp = new System.Net.Sockets.TcpClient();
            var connect = tcp.ConnectAsync("127.0.0.1", port);
            var completed = await Task.WhenAny(connect, Task.Delay(800, ct));
            if (completed != connect)
            {
                return false;
            }

            await connect;
            return tcp.Connected;
        }
        catch
        {
            return false;
        }
    }

    private async Task EnsureKgapiJsAsync(CancellationToken ct)
    {
        if (await IsKgapiJsHealthyAsync(ct))
        {
            return;
        }

        if (DateTime.UtcNow - _kgapijsLastStartAttemptUtc < KgapiJsRetryInterval)
        {
            return;
        }

        var targetKgapiJs = Path.Combine(AppPaths.ExeDirectory, SidecarLocator.KugouJsFolderName);
        if (!SidecarLocator.IsKgapiJsReady(targetKgapiJs))
        {
            var donor = SidecarLocator.FindKgapiJsDirectory(AppPaths.ExeDirectory);
            if (!string.IsNullOrWhiteSpace(donor))
            {
                SidecarBootstrap.EnsureKgapiJsTree(donor, targetKgapiJs);
            }
        }

        if (!SidecarLocator.IsKgapiJsReady(targetKgapiJs))
        {
            _log.Warn("kgapijs 目录缺少 app.js，每日推荐不可用（请运行 scripts/sync-sidecars.py 或从酷狗 build/bin 拷贝 kgapijs）");
            _system.Add("缺少酷狗 kgapijs，每日推荐不可用；请重新发布或运行 sync-sidecars 同步");
            return;
        }

        _kgapijsLastStartAttemptUtc = DateTime.UtcNow;
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "node",
                Arguments = "app.js",
                WorkingDirectory = targetKgapiJs,
                UseShellExecute = false,
                CreateNoWindow = true,
                Environment =
                {
                    ["PORT"] = "16521",
                    ["HOST"] = "127.0.0.1"
                }
            });
            _log.Info("已启动 kgapijs 协议服务 (127.0.0.1:16521)");
            _system.Add("已启动酷狗协议服务，每日推荐即将可用");
            await Task.Delay(2500, ct);
        }
        catch (Exception ex)
        {
            _log.Warn($"启动 kgapijs 失败: {ex.Message}");
            _system.Add($"启动酷狗协议服务失败: {ex.Message}，随机补位将使用搜索歌单");
        }
    }

    private static async Task<bool> IsKgapiJsHealthyAsync(CancellationToken ct)
    {
        foreach (var port in new[] { 16521, 3000 })
        {
            try
            {
                using var response = await KgapiHealthClient.GetAsync($"http://127.0.0.1:{port}/", ct);
                if (response.IsSuccessStatusCode)
                {
                    return true;
                }
            }
            catch
            {
                // try next port
            }
        }

        return false;
    }

    public Task RestartDouyinAsync(CancellationToken ct = default)
        => EnsureDouyinCdpAsync(ct, force: true);

    private async Task EnsureDouyinCdpAsync(CancellationToken ct, bool force = false)
    {
        var baseUrl = _config.Settings.Douyin.BaseUrl;
        var configured = string.IsNullOrWhiteSpace(_config.Settings.Douyin.CdpExePath)
            ? _config.Settings.Douyin.DouyinExePath
            : _config.Settings.Douyin.CdpExePath;
        var exe = SidecarLocator.ResolveDouyin(configured);
        var port = ParsePortFromBaseUrl(baseUrl, 17891);
        if (!force && await IsLocalPortOpenAsync(port, ct))
        {
            _log.Info($"DOUYIN_CDP_WATCHDOG stage=reuse result=port_open baseUrl={baseUrl} pid=0");
            return;
        }

        if (string.IsNullOrWhiteSpace(exe) || !File.Exists(exe) || !SidecarLocator.IsDouyinApiExe(Path.GetFileName(exe)))
        {
            _log.Warn($"DOUYIN_CDP_WATCHDOG stage=reject result=missing_cdp_exe baseUrl={baseUrl} pid=0");
            _system.Add("缺少抖音 CDP 程序 cdp-danmaku.exe");
            return;
        }

        var processName = Path.GetFileNameWithoutExtension(exe);
        var existing = Process.GetProcessesByName(processName);
        try
        {
            if (existing.Length > 0 && !force)
            {
                var pid = existing[0].Id;
                _log.Info($"DOUYIN_CDP_WATCHDOG stage=reuse result=process_exists baseUrl={baseUrl} pid={pid}");
                return;
            }

            if (force)
            {
                foreach (var p in existing)
                {
                    try { p.Kill(true); } catch { /* ignore */ }
                }
            }

            var started = Process.Start(new ProcessStartInfo
            {
                FileName = exe,
                Arguments = SidecarLocator.DouyinStartArgs(exe),
                WorkingDirectory = Path.GetDirectoryName(exe) ?? "",
                UseShellExecute = false,
                CreateNoWindow = true
            });
            var newPid = started?.Id ?? 0;
            _log.Info($"DOUYIN_CDP_WATCHDOG stage=start result=ok baseUrl={baseUrl} pid={newPid}");
            _system.Add("已启动抖音 CDP");
            await Task.Delay(3000, ct);
        }
        finally
        {
            foreach (var p in existing)
            {
                p.Dispose();
            }
        }
    }

    public Task RestartKugouAsync(CancellationToken ct = default)
        => TryStartProcessAsync(_config.Settings.Kugou.KugouExePath, "", "酷狗", ct, force: true);

    private async Task TryStartProcessAsync(string exePath, string args, string name, CancellationToken ct, bool force = false)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
            {
                var fileName = name.Contains("抖音", StringComparison.Ordinal)
                    ? SidecarLocator.PreferredDouyinFileName
                    : SidecarLocator.PreferredKugouFileName;
                _system.Add($"缺少必要文件: {fileName}");
                _log.Warn($"{name} exe 不存在: {exePath}");
                return;
            }

            var processName = Path.GetFileNameWithoutExtension(exePath);
            var existing = Process.GetProcessesByName(processName);
            if (existing.Length > 0 && !force)
            {
                foreach (var p in existing)
                {
                    p.Dispose();
                }
                return;
            }

            if (force)
            {
                foreach (var p in existing)
                {
                    try
                    {
                        p.Kill(true);
                    }
                    catch (Exception ex)
                    {
                        _log.Warn($"结束 {name} 进程失败: {ex.Message}");
                    }
                    finally
                    {
                        p.Dispose();
                    }
                }
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = args,
                WorkingDirectory = Path.GetDirectoryName(exePath) ?? "",
                UseShellExecute = false,
                CreateNoWindow = true
            });
            _system.Add($"已启动{name}服务");
            _log.Info($"启动 {name}: {exePath} {args}");
            await Task.Delay(3000, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.Error("app", $"启动{name}服务失败", ex);
            _system.Add($"启动{name}服务失败: {ex.Message}");
        }
    }
}
