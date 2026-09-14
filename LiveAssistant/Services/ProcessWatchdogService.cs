using System.Diagnostics;
using LiveAssistant.Config;

namespace LiveAssistant.Services;

public sealed class ProcessWatchdogService
{
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
        => SidecarLocator.GetMissingRequiredFiles(
            _config.Settings.Douyin.DouyinExePath,
            _config.Settings.Kugou.KugouExePath);

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
        }
        catch (Exception ex)
        {
            _log.Error("app", "Sidecar 守护检查异常", ex);
        }
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
