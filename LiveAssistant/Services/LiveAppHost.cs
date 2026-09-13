using LiveAssistant.Admin;
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
    private readonly ReplyQueue _replyQueue;
    private readonly RandomPlaylistService _random;
    private readonly PlaybackService _playback;
    private readonly PlaybackCommandQueue _playbackCommands;
    private readonly PlaybackEngine _engine;
    private readonly DanmakuService _danmaku;
    private readonly UserRepository _users;
    private readonly GiftRepository _giftRepo;
    private readonly GiftRuleRepository _giftRuleRepo;
    private readonly BanVoteRepository _banVoteRepo;
    private readonly SongBlacklistRepository _songBlacklistRepo;
    private readonly KeywordReplyRepository _keywordReplyRepo;
    private readonly ReplyTemplateRepository _replyTemplateRepo;
    private readonly RandomPoolRepository _randomPoolRepo;
    private readonly LevelPermissionRepository _levelPermRepo;
    private readonly WelcomeCooldownRepository _welcomeCooldownRepo;
    private readonly SettingsStore _settingsStore;
    private readonly SongRequestPermissionService _permission;
    private readonly SongRequestService _songRequest;
    private readonly GiftService _gift;
    private readonly GiftCollectorService _giftCollector;
    private readonly BanVoteService _banVote;
    private readonly WelcomeService _welcome;
    private readonly KeywordReplyService _keywordReply;
    private readonly UserLevelService _userLevel;
    private readonly CommandQueueService _commandQueue;
    private readonly BackendSyncService _backendSync;
    private readonly DataCleanupService _dataCleanup;
    private readonly AdminWebHost _adminWeb;
    private readonly ProcessWatchdogService _watchdog;
    private readonly DateTime _startedAt = DateTime.Now;
    private CancellationTokenSource? _watchCts;
    private volatile bool _douyinSidecarOk;
    private volatile bool _kugouSidecarOk;
    private volatile string _adminAccountStatus = "未检测";
    private volatile string _adminNickname = "-";
    private volatile string _currentTask = "空闲";
    private volatile bool _isRunning;

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
        _giftRepo = new GiftRepository(_db);
        _giftRuleRepo = new GiftRuleRepository(_db);
        _banVoteRepo = new BanVoteRepository(_db);
        _songBlacklistRepo = new SongBlacklistRepository(_db);
        _keywordReplyRepo = new KeywordReplyRepository(_db);
        _replyTemplateRepo = new ReplyTemplateRepository(_db);
        _randomPoolRepo = new RandomPoolRepository(_db);
        _levelPermRepo = new LevelPermissionRepository(_db);
        _welcomeCooldownRepo = new WelcomeCooldownRepository(_db);

        _settingsStore = new SettingsStore(
            _config, _replyTemplateRepo, _randomPoolRepo, _giftRuleRepo, _levelPermRepo,
            _keywordReplyRepo, new SyncCacheRepository(_db));
        _settingsStore.InitializeFromFilesIfEmpty();
        _settingsStore.ApplyDbToMemory();

        _douyin = new DouyinService(_config.Settings.Douyin, _log);
        _kugou = new KugouService(_config.Settings.Kugou, _log);
        _queue = new QueueService(_db);
        _reply = new ReplyService(_config);
        _replyQueue = new ReplyQueue(_douyin, _log, _config.Settings.Reply);
        _random = new RandomPlaylistService(_config, _db);
        _playback = new PlaybackService(_log);
        _playback.SetVolume(_config.Settings.Playback.Volume);

        _playbackCommands = new PlaybackCommandQueue(
            _config, _queue, _kugou, _random, _playback, _reply, _system, _log);
        _engine = new PlaybackEngine(_config, _playbackCommands, _system);
        _danmaku = new DanmakuService(_douyin, _log, _system);

        var songBlacklist = new SongBlacklistService(_songBlacklistRepo);
        _userLevel = new UserLevelService(_config, _users);
        _permission = new SongRequestPermissionService(
            _config, _users, _queue, songBlacklist, _levelPermRepo, _userLevel);
        _songRequest = new SongRequestService(_config, _kugou, _queue, _permission, _reply, _replyQueue, _system, _log);
        _gift = new GiftService(_config, _douyin, _giftRepo, _users, _userLevel, _giftRuleRepo, _log, _system);
        _giftCollector = new GiftCollectorService(_config, _douyin, _gift, _log, giftRepo: _giftRepo);
        _banVote = new BanVoteService(_config, _banVoteRepo, _users, _douyin, _replyQueue, _reply, _system, _log);
        _welcome = new WelcomeService(_config, _reply, _replyQueue, _system, _welcomeCooldownRepo);
        _keywordReply = new KeywordReplyService(_config, _keywordReplyRepo, new NullAIReplyService());
        _commandQueue = new CommandQueueService(new AdminCommandRepository(_db));
        _backendSync = new BackendSyncService(
            _config, _commandQueue, _settingsStore, _playbackCommands, _engine, _queue, _reply, _log);
        _dataCleanup = new DataCleanupService(_config, _db, _log);
        _watchdog = new ProcessWatchdogService(_config, _log, _system);

        _adminWeb = new AdminWebHost(new AdminAppContext
        {
            Config = _config,
            Host = this,
            Commands = _commandQueue,
            Settings = _settingsStore,
            Users = _users,
            Gifts = _giftRepo,
            GiftRules = _giftRuleRepo,
            RandomPool = _randomPoolRepo,
            ReplyTemplates = _replyTemplateRepo,
            SongBlacklist = _songBlacklistRepo,
            KeywordReplies = _keywordReplyRepo,
            BanVotes = _banVoteRepo,
            LevelPermissions = _levelPermRepo,
            Log = _log
        });

        _danmaku.DanmakuReceived += OnDanmakuReceived;
        _songRequest.RequestHandled += () => _ = _engine.EnsurePlayingAsync();
        _playbackCommands.Playback.StateChanged += () => NotifyStateChanged();
        _queue.QueueChanged += () => NotifyStateChanged();

        _adminWeb.Start();
        _backendSync.Start();
        _dataCleanup.Start();

        _log.Info("LiveAssistant 已启动");
        if (_config.Settings.Admin.Enabled)
        {
            _system.Add($"管理后台: http://127.0.0.1:{_config.Settings.Admin.Port}{_config.Settings.Admin.Path}");
        }
    }

    public ConfigManager Config => _config;
    public LogService Log => _log;
    public SystemMessageService SystemMessages => _system;
    public QueueService Queue => _queue;
    public PlaybackCommandQueue PlaybackCommands => _playbackCommands;
    public PlaybackEngine Engine => _engine;
    public DanmakuService Danmaku => _danmaku;
    public SettingsStore SettingsStore => _settingsStore;
    public DateTime StartedAt => _startedAt;

    public event Action<DanmakuItem>? DanmakuReceived;
    public event Action? StateChanged;

    public RuntimeStatus GetRuntimeStatus()
    {
        var track = _playbackCommands.Playback.CurrentTrack;
        var queueCount = _queue.WaitingCount + (_queue.NowPlaying != null ? 1 : 0);
        return new RuntimeStatus
        {
            DouyinOnline = _douyinSidecarOk,
            KugouOnline = _kugouSidecarOk,
            DouyinStatus = _douyinSidecarOk ? "在线" : "离线",
            KugouStatus = _kugouSidecarOk ? "在线" : "离线",
            DanmakuConnection = _danmaku.ConnectionStatus,
            AdminAccountStatus = _adminAccountStatus,
            AdminNickname = _adminNickname,
            CurrentSong = track == null ? "-" : $"{track.SongName} - {track.Artist}",
            PlaybackMode = _engine.Mode.ToString(),
            QueueCount = queueCount,
            Uptime = DateTime.Now - _startedAt,
            StartedAt = _startedAt,
            CurrentTask = _currentTask,
            LastError = _log.LastError
        };
    }

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

        _isRunning = true;
        _currentTask = "连接直播间";
        await _danmaku.StartAsync(webRid, ct);
        _gift.Start(webRid);
        _giftCollector.StartGiftCollector(webRid);
        _currentTask = "监控中";
        _system.Add(_reply.Render("systemConnected", new Dictionary<string, string>()));
        await _engine.EnsurePlayingAsync();
        NotifyStateChanged();
    }

    public void Stop()
    {
        _isRunning = false;
        _currentTask = "已停止";
        _danmaku.Stop();
        _giftCollector.StopGiftCollector();
        _gift.Stop();
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

            _users.EnsureUser(item.UserId, item.Nickname);

            if (item.MsgType == "member")
            {
                _welcome.HandleMemberJoin(item, webRid);
                return;
            }

            if (item.MsgType == "gift")
            {
                return;
            }

            await _keywordReply.TryHandleAsync(item, _replyQueue, _reply, _users, webRid);
            await _banVote.HandleDanmakuAsync(item, webRid);
            await _songRequest.HandleDanmakuAsync(item, webRid);
            NotifyStateChanged();
        }
        catch (Exception ex)
        {
            _log.Error("app", "处理弹幕异常", ex);
            _log.SetLastError("app", ex.Message);
        }
    }

    private async Task WatchSidecarsAsync(CancellationToken ct)
    {
        var wasDouyinDown = false;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await _watchdog.EnsureSidecarsAsync(
                    () => _douyin.HealthCheckAsync(ct),
                    () => _kugou.HealthCheckAsync(ct),
                    ct);

                var health = await _douyin.GetHealthAsync(ct);
                var dyOk = health != null;
                var kgOk = await _kugou.HealthCheckAsync(ct);
                _douyinSidecarOk = dyOk;
                _kugouSidecarOk = kgOk;

                if (health != null)
                {
                    _adminAccountStatus = health.LoginOk ? "管理员已登录" : "未登录";
                    _adminNickname = health.Nickname ?? "-";
                }
                else
                {
                    _adminAccountStatus = "离线";
                    _adminNickname = "-";
                }

                if (dyOk && wasDouyinDown && _isRunning)
                {
                    var webRid = _config.Settings.Douyin.WebRid;
                    if (!string.IsNullOrWhiteSpace(webRid))
                    {
                        _log.DouyinInfo("抖音 Sidecar 恢复，重新连接采集");
                        await _douyin.ReconnectAsync(webRid, ct);
                        await _danmaku.StartAsync(webRid, ct);
                        _gift.Start(webRid);
                        _giftCollector.StartGiftCollector(webRid);
                        _system.Add("抖音服务已恢复并重连");
                    }
                }
                wasDouyinDown = !dyOk;

                if (!dyOk)
                {
                    _system.Add(_reply.Render("systemDouyinDown", new Dictionary<string, string>()));
                }
                if (!kgOk)
                {
                    _system.Add(_reply.Render("systemKugouDown", new Dictionary<string, string>()));
                }

                NotifyStateChanged();
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _log.Error("app", "Sidecar 守护循环异常", ex);
                _log.SetLastError("watchdog", ex.Message);
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(15), ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    public void NotifyStateChanged() => StateChanged?.Invoke();

    public void Dispose()
    {
        _watchCts?.Cancel();
        _dataCleanup.Dispose();
        _backendSync.Dispose();
        _adminWeb.Dispose();
        _giftCollector.Dispose();
        _gift.Dispose();
        _danmaku.Dispose();
        _replyQueue.Dispose();
        _playbackCommands.Dispose();
        _playback.Dispose();
        _db.Dispose();
        _log.Info("LiveAssistant 已退出");
    }
}
