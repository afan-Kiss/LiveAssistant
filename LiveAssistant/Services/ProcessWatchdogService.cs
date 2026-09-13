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

    public async Task EnsureSidecarsAsync(
        Func<Task<bool>> douyinHealth,
        Func<Task<bool>> kugouHealth,
        CancellationToken ct = default)
    {
        if (!await douyinHealth())
        {
            await TryStartProcessAsync(_config.Settings.Douyin.DouyinExePath, "-api", "抖音", ct);
        }

        if (!await kugouHealth())
        {
            await TryStartProcessAsync(_config.Settings.Kugou.KugouExePath, "", "酷狗", ct);
        }
    }

    public Task RestartDouyinAsync(CancellationToken ct = default)
        => TryStartProcessAsync(_config.Settings.Douyin.DouyinExePath, "-api", "抖音", ct, force: true);

    public Task RestartKugouAsync(CancellationToken ct = default)
        => TryStartProcessAsync(_config.Settings.Kugou.KugouExePath, "", "酷狗", ct, force: true);

    private async Task TryStartProcessAsync(string exePath, string args, string name, CancellationToken ct, bool force = false)
    {
        if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
        {
            _system.Add($"{name}服务未配置或文件不存在: {exePath}");
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
                catch
                {
                    // ignore
                }
                finally
                {
                    p.Dispose();
                }
            }
        }

        try
        {
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
        catch (Exception ex)
        {
            _log.Error($"启动{name}失败", ex);
            _system.Add($"启动{name}服务失败: {ex.Message}");
        }
    }
}
