using System.Text.Json;
using LiveAssistant.Config;
using LiveAssistant.Models;

namespace LiveAssistant.Services;

/// <summary>
/// 客户端后台同步：连接管理后台，同步配置/模板/随机池/规则，消费命令队列。
/// 后台断开时使用本地 SQLite 缓存。
/// </summary>
public sealed class BackendSyncService : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    private readonly ConfigManager _config;
    private readonly CommandQueueService _commands;
    private readonly SettingsStore _settings;
    private readonly PlaybackCommandQueue _playback;
    private readonly PlaybackEngine _engine;
    private readonly QueueService _queue;
    private readonly ReplyService _reply;
    private readonly LogService _log;
    private readonly HttpClient _client;
    private CancellationTokenSource? _cts;
    private string _syncVersion = "";
    private bool _backendOnline;

    public bool BackendOnline => _backendOnline;

    public BackendSyncService(
        ConfigManager config,
        CommandQueueService commands,
        SettingsStore settings,
        PlaybackCommandQueue playback,
        PlaybackEngine engine,
        QueueService queue,
        ReplyService reply,
        LogService log)
    {
        _config = config;
        _commands = commands;
        _settings = settings;
        _playback = playback;
        _engine = engine;
        _queue = queue;
        _reply = reply;
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

        _settings.TryLoadCachedBundle();
        Stop();
        _cts = new CancellationTokenSource();
        _ = Task.Run(() => SyncLoopAsync(_cts.Token));
        _log.Info("BackendSyncService 已启动");
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
                var online = await PullBundleAsync(path, ct);
                _backendOnline = online;
                if (online)
                {
                    await PullCommandsAsync(path, ct);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _backendOnline = false;
                _log.Error("sync", "后台同步异常", ex);
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

    private async Task<bool> PullBundleAsync(string path, CancellationToken ct)
    {
        var resp = await _client.GetAsync($"{path}/api/sync/bundle", ct);
        if (!resp.IsSuccessStatusCode)
        {
            return false;
        }

        var json = await resp.Content.ReadAsStringAsync(ct);
        var bundle = JsonSerializer.Deserialize<SyncBundle>(json, JsonOptions);
        if (bundle == null)
        {
            return false;
        }

        if (bundle.Version == _syncVersion)
        {
            return true;
        }

        _syncVersion = bundle.Version;
        _settings.ApplyBundle(bundle);
        _reply.Reload();
        _log.Info("后台配置包已同步");
        return true;
    }

    private async Task PullCommandsAsync(string path, CancellationToken ct)
    {
        var resp = await _client.GetAsync($"{path}/api/sync/commands", ct);
        if (!resp.IsSuccessStatusCode)
        {
            return;
        }

        var json = await resp.Content.ReadAsStringAsync(ct);
        var items = JsonSerializer.Deserialize<List<CommandDto>>(json, JsonOptions) ?? new();
        foreach (var cmd in items)
        {
            ExecuteCommand(cmd);
        }
    }

    private void ExecuteCommand(CommandDto cmd)
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
            case "play":
                _playback.EnqueuePlay();
                break;
            case "previous":
                _playback.EnqueuePrevious();
                break;
            case "clearqueue":
                _queue.ClearWaiting();
                break;
            case "setrandomfill":
                if (bool.TryParse(cmd.Payload, out var enabled))
                {
                    _config.Settings.Playback.RandomFillEnabled = enabled;
                    _config.Save();
                }
                break;
            case "enablerandommode":
                _engine.SetMode(PlaybackMode.RandomOnly);
                break;
            case "disablerandommode":
                _engine.SetMode(PlaybackMode.RequestWithRandomFill);
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
                _settings.ApplyDbToMemory();
                _reply.Reload();
                break;
        }
    }

    public void Dispose()
    {
        Stop();
        _client.Dispose();
    }

    private sealed class CommandDto
    {
        public string? Type { get; set; }
        public string? Payload { get; set; }
    }
}
