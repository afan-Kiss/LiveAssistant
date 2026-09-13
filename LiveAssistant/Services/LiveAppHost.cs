using LiveAssistant.Config;
using LiveAssistant.Database;
using LiveAssistant.Models;

namespace LiveAssistant.Services;

public sealed class LiveAppHost : IDisposable
{
    private readonly ConfigManager _config;
    private readonly LogService _log;
    private readonly SystemMessageService _system;
    private readonly AppDatabase _db;
    private readonly DouyinService _douyin;
    private readonly KugouService _kugou;
    private readonly QueueService _queue;
    private readonly ReplyService _reply;
    private readonly RandomPlaylistService _random;
    private readonly PlaybackService _playback;
    private readonly PlaybackCommandQueue _playbackCommands;
    private readonly PlaybackEngine _engine;
    private readonly DanmakuService _danmaku;
    private readonly UserRepository _users;
    private readonly SongRequestService _songRequest;
    private readonly ProcessWatchdogService _watchdog;
    private CancellationTokenSource? _watchCts;

    public LiveAppHost()
    {
        _config = new ConfigManager();
        _config.Load();

        _log = new LogService(_config.DataDirectory);
        _system = new SystemMessageService(_config.Settings.Ui.MaxSystemMessageLines);
        _db = new AppDatabase(_config.DataDirectory);

        var recovered = _db.RecoverPlayingQueueItems();
        if (recovered > 0)
        {
            _log.Info($"播放恢复保护: 已将 {recovered} 条 playing 队列项恢复为 waiting");
            _system.Add($"启动恢复: {recovered} 条未完成播放已重置为等待");
        }

        _users = new UserRepository(_db);
        _douyin = new DouyinService(_config.Settings.Douyin, _log);
        _kugou = new KugouService(_config.Settings.Kugou, _log);
        _queue = new QueueService(_db);
        _reply = new ReplyService(_config);
        _random = new RandomPlaylistService(_config, _db);
        _playback = new PlaybackService(_log);
        _playback.SetVolume(_config.Settings.Playback.Volume);

        _playbackCommands = new PlaybackCommandQueue(
            _config, _queue, _kugou, _random, _playback, _reply, _system, _log);
        _engine = new PlaybackEngine(_config, _playbackCommands, _system);
        _danmaku = new DanmakuService(_douyin, _log, _system);
        _songRequest = new SongRequestService(_config, _kugou, _douyin, _queue, _users, _reply, _system, _log);
        _watchdog = new ProcessWatchdogService(_config, _log, _system);

        _danmaku.DanmakuReceived += OnDanmakuReceived;
        _songRequest.RequestHandled += () => _ = _engine.EnsurePlayingAsync();

        _log.Info("LiveAssistant 已启动");
    }

    public ConfigManager Config => _config;
    public LogService Log => _log;
    public SystemMessageService SystemMessages => _system;
    public QueueService Queue => _queue;
    public PlaybackCommandQueue PlaybackCommands => _playbackCommands;
    public PlaybackEngine Engine => _engine;
    public DanmakuService Danmaku => _danmaku;

    public event Action<DanmakuItem>? DanmakuReceived;
    public event Action? StateChanged;

    public async Task StartAsync(CancellationToken ct = default)
    {
        _watchCts = new CancellationTokenSource();
        _ = WatchSidecarsAsync(_watchCts.Token);

        var webRid = _config.Settings.Douyin.WebRid;
        if (string.IsNullOrWhiteSpace(webRid))
        {
            _system.Add("未配置 web_rid，请在设置中填写后点击「连接」");
            return;
        }

        await _danmaku.StartAsync(webRid, ct);
        _system.Add(_reply.Render("systemConnected", new Dictionary<string, string>()));
        await _engine.EnsurePlayingAsync();
        NotifyStateChanged();
    }

    public void Stop()
    {
        _danmaku.Stop();
        _engine.Stop();
        _system.Add("已停止监控");
        NotifyStateChanged();
    }

    public async Task ConnectAsync(string webRid, CancellationToken ct = default)
    {
        _config.Settings.Douyin.WebRid = webRid.Trim();
        _config.Save();
        await StartAsync(ct);
    }

    private async void OnDanmakuReceived(DanmakuItem item)
    {
        try
        {
            DanmakuReceived?.Invoke(item);

            if (_config.Settings.Emergency.PauseInteraction)
            {
                return;
            }

            var webRid = _config.Settings.Douyin.WebRid;
            if (string.IsNullOrWhiteSpace(webRid))
            {
                return;
            }

            await _songRequest.HandleDanmakuAsync(item, webRid);
            NotifyStateChanged();
        }
        catch (Exception ex)
        {
            _log.Error("app", "处理弹幕异常", ex);
        }
    }

    private async Task WatchSidecarsAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await _watchdog.EnsureSidecarsAsync(
                    () => _douyin.HealthCheckAsync(ct),
                    () => _kugou.HealthCheckAsync(ct),
                    ct);

                var dyOk = await _douyin.HealthCheckAsync(ct);
                var kgOk = await _kugou.HealthCheckAsync(ct);
                if (!dyOk)
                {
                    _system.Add(_reply.Render("systemDouyinDown", new Dictionary<string, string>()));
                }
                if (!kgOk)
                {
                    _system.Add(_reply.Render("systemKugouDown", new Dictionary<string, string>()));
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _log.Error("app", "Sidecar 守护循环异常", ex);
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(15), ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _log.Error("app", "Sidecar 守护延迟异常", ex);
            }
        }
    }

    public void NotifyStateChanged() => StateChanged?.Invoke();

    public void Dispose()
    {
        _watchCts?.Cancel();
        _danmaku.Dispose();
        _playbackCommands.Dispose();
        _playback.Dispose();
        _db.Dispose();
        _log.Info("LiveAssistant 已退出");
    }
}
