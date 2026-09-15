using System.Diagnostics;
using System.Net.Http;
using LiveAssistant.Config;

namespace LiveAssistant.Services;

public sealed class ProcessWatchdogService
{
    private static readonly HttpClient KgapiHealthClient = new() { Timeout = TimeSpan.FromSeconds(2) };
    private static bool _kgapijsStartAttempted;

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

        if (_kgapijsStartAttempted)
        {
            return;
        }

        var kgDir = Path.Combine(AppPaths.ExeDirectory, SidecarLocator.KugouJsFolderName);
        var appJs = Path.Combine(kgDir, "app.js");
        if (!File.Exists(appJs))
        {
            return;
        }

        _kgapijsStartAttempted = true;
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = "/c set PORT=16521&& set HOST=127.0.0.1&& node app.js",
                WorkingDirectory = kgDir,
                UseShellExecute = false,
                CreateNoWindow = true
            });
            _log.Info("已启动 kgapijs 协议服务 (127.0.0.1:16521)");
            await Task.Delay(2500, ct);
        }
        catch (Exception ex)
        {
            _kgapijsStartAttempted = false;
            _log.Warn($"启动 kgapijs 失败: {ex.Message}");
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
