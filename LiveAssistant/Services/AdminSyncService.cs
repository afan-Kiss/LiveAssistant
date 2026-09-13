using System.Text.Json;
using LiveAssistant.Config;
using LiveAssistant.Models;

namespace LiveAssistant.Services;

/// <summary>
/// WinForms 后台连接模块：轮询管理后台 API，获取配置与命令，统一进入 PlaybackCommandQueue。
/// </summary>
public sealed class AdminSyncService : IDisposable
{
    private readonly ConfigManager _config;
    private readonly AdminCommandService _commands;
    private readonly PlaybackCommandQueue _playback;
    private readonly PlaybackEngine _engine;
    private readonly QueueService _queue;
    private readonly LogService _log;
    private readonly HttpClient _client;
    private CancellationTokenSource? _cts;
    private string _configHash = "";

    public AdminSyncService(
        ConfigManager config,
        AdminCommandService commands,
        PlaybackCommandQueue playback,
        PlaybackEngine engine,
        QueueService queue,
        LogService log)
    {
        _config = config;
        _commands = commands;
        _playback = playback;
        _engine = engine;
        _queue = queue;
        _log = log;
        var port = config.Settings.Admin.Port;
        _client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
    }

    public void Start()
    {
        if (!_config.Settings.Admin.Enabled)
        {
            return;
        }

        Stop();
        _cts = new CancellationTokenSource();
        _ = Task.Run(() => SyncLoopAsync(_cts.Token));
        _log.Info("后台同步模块已启动");
    }

    public void Stop()
    {
        _cts?.Cancel();
        _cts = null;
    }

    private async Task SyncLoopAsync(CancellationToken ct)
    {
        var path = _config.Settings.Admin.Path.TrimEnd('/');
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await PullConfigAsync(path, ct);
                await PullCommandsAsync(path, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _log.Error("admin", "后台同步异常", ex);
            }

            try
            {
                await Task.Delay(1000, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task PullConfigAsync(string path, CancellationToken ct)
    {
        var resp = await _client.GetAsync($"{path}/api/sync/config", ct);
        if (!resp.IsSuccessStatusCode)
        {
            return;
        }

        var json = await resp.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);
        var hash = doc.RootElement.GetProperty("hash").GetString() ?? "";
        if (hash == _configHash)
        {
            return;
        }

        _configHash = hash;
        _config.Load();
        _log.Info("后台配置已同步");
    }

    private async Task PullCommandsAsync(string path, CancellationToken ct)
    {
        var resp = await _client.GetAsync($"{path}/api/sync/commands", ct);
        if (!resp.IsSuccessStatusCode)
        {
            return;
        }

        var json = await resp.Content.ReadAsStringAsync(ct);
        var items = JsonSerializer.Deserialize<List<AdminCommandDto>>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        }) ?? new List<AdminCommandDto>();

        foreach (var cmd in items)
        {
            ExecuteCommand(cmd);
        }
    }

    private void ExecuteCommand(AdminCommandDto cmd)
    {
        switch (cmd.Type?.ToLowerInvariant())
        {
            case "skip":
                _ = _playback.EnqueueSkipAsync();
                break;
            case "pause":
                _playback.EnqueuePause();
                break;
            case "resume":
                _playback.EnqueueResume();
                break;
            case "setrandomfill":
                if (bool.TryParse(cmd.Payload, out var enabled))
                {
                    _config.Settings.Playback.RandomFillEnabled = enabled;
                    _config.Save();
                }
                break;
            case "setplaybackmode":
                if (Enum.TryParse<PlaybackMode>(cmd.Payload, true, out var mode))
                {
                    _engine.SetMode(mode);
                }
                break;
            case "deletequeueitem":
                if (long.TryParse(cmd.Payload, out var delId))
                {
                    _queue.Remove(delId);
                }
                break;
            case "pinqueueitem":
                if (long.TryParse(cmd.Payload, out var pinId))
                {
                    _queue.PinToTop(pinId);
                }
                break;
            case "playnow":
                if (long.TryParse(cmd.Payload, out var playId))
                {
                    _playback.EnqueuePlayNow(playId);
                }
                break;
            case "reloadconfig":
                _config.Load();
                break;
        }
    }

    public void Dispose()
    {
        Stop();
        _client.Dispose();
    }

    private sealed class AdminCommandDto
    {
        public string? Type { get; set; }
        public string? Payload { get; set; }
    }
}
