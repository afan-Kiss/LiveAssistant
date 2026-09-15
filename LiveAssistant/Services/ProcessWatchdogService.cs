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
        CancellationToken ct = default)
    {
        try
        {
            if (GetMissingRequiredFiles().Count > 0)
            {
                return;
            }

            if (!await douyinHealth())
            {
                await TryStartProcessAsync(
                    _config.Settings.Douyin.DouyinExePath,
                    global::LiveAssistant.SidecarLocator.DouyinStartArgs(_config.Settings.Douyin.DouyinExePath),
                    "抖音API",
                    ct);
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
        catch (Exception ex)
        {
            _log.Error("app", "Sidecar 守护检查异常", ex);
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
        => TryStartProcessAsync(
            _config.Settings.Douyin.DouyinExePath,
            global::LiveAssistant.SidecarLocator.DouyinStartArgs(_config.Settings.Douyin.DouyinExePath),
            "抖音API",
            ct,
            force: true);

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
                UseShellExecute = true
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
