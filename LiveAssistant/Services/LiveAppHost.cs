using LiveAssistant.Admin;
using LiveAssistant.Config;
using LiveAssistant.Database;
using LiveAssistant.Models;
using LiveAssistant.Services.AiSpeech;

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
    private readonly OutboundReplyTracker _outboundTracker;
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
    private readonly PointsQueryService _pointsQuery;
    private readonly SkipSongService _skipSong;
    private readonly UserLevelService _userLevel;
    private readonly CommandQueueService _commandQueue;
    private readonly BackendSyncService _backendSync;
    private readonly DataCleanupService _dataCleanup;
    private readonly AdminWebHost _adminWeb;
    private readonly AdminTunnelService _adminTunnel;
    private readonly ProcessWatchdogService _watchdog;
    private readonly LiveHealthService _health;
    private readonly SongRequestControlService _songRequestControl;
    private readonly UserDetailService _userDetail;
    private readonly ReplyTemplatePreviewService _templatePreview;
    private readonly AiSpeechCoordinator _aiSpeech;
    private readonly DateTime _startedAt = DateTime.Now;
    private string _lastPlayedTrackKey = "";
    private CancellationTokenSource? _watchCts;
    private volatile bool _douyinSidecarOk;
    private volatile bool _kugouSidecarOk;
    private volatile string _kugouLoginStatus = "未检测";
    private volatile string _kugouVipLabel = "";
    private volatile bool _kugouFullPlaybackAvailable;
    private volatile string _kugouFullPlaybackReason = "";
    private long _kugouStatusCheckedAtTicks;
    private volatile bool _kugouLoginWarned;
    private volatile string _adminAccountStatus = "未检测";
    private volatile string _adminNickname = "-";
    private volatile string _currentTask = "空闲";
    private volatile bool _isRunning;
    private int _disposed;

    public LiveAppHost()
    {
        _config = new ConfigManager();
        _config.Load();

        _log = new LogService(_config.DataDirectory);
        _system = new SystemMessageService(_config.Settings.Ui.MaxSystemMessageLines);
        _db = new AppDatabase(_config.DataDirectory);

        var abandoned = _db.AbandonPendingQueueOnStartup();
        if (abandoned > 0)
        {
            _log.Info($"启动清空未播放队列: {abandoned} 条");
            _system.Add($"启动已清空 {abandoned} 条未播放点歌，等待新点歌");
        }

        var pointsLedgerRepo = new PointsLedgerRepository(_db);
        _users = new UserRepository(_db, pointsLedgerRepo);
        _giftRepo = new GiftRepository(_db, pointsLedgerRepo);
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
        _kugou = new KugouService(_config.Settings.Kugou, _log, dataDirectory: _config.DataDirectory);
        _queue = new QueueService(_db);
        _reply = new ReplyService(_config);
        _outboundTracker = new OutboundReplyTracker();
        _replyQueue = new ReplyQueue(
            _douyin,
            _log,
            _config.Settings.Reply,
            outboundTracker: _outboundTracker,
            onSendFailed: msg => _system.Add(msg),
            onSendSucceeded: content => _system.Add($"弹幕已发出：{TruncateForUi(content, 80)}"));
        _random = new RandomPlaylistService(_config, _db);
        _playback = new PlaybackService(_log);
        _playback.SetVolume(_config.Settings.Playback.Volume);

        _playbackCommands = new PlaybackCommandQueue(
            _config, _queue, _kugou, _random, _playback, _reply, _system, _log);
        _engine = new PlaybackEngine(_config, _playbackCommands, _system);
        _danmaku = new DanmakuService(
            _douyin, _log, _system, new DanmakuDeduplicator(_config.DataDirectory), _outboundTracker,
            _config.Settings.Douyin.PollIntervalMs);

        var songBlacklist = new SongBlacklistService(_songBlacklistRepo);
        _userLevel = new UserLevelService(_config, _users);
        _health = new LiveHealthService();
        _songRequestControl = new SongRequestControlService(_config, _users);
        _userDetail = new UserDetailService(_users, _giftRepo, pointsLedgerRepo);
        _templatePreview = new ReplyTemplatePreviewService(_config, _replyTemplateRepo, _reply);
        _permission = new SongRequestPermissionService(
            _config, _users, _queue, songBlacklist, _levelPermRepo, _userLevel, _giftRepo, _songRequestControl);
        _songRequest = new SongRequestService(_config, _kugou, _queue, _permission, _reply, _replyQueue, _system, _log);
        _gift = new GiftService(_config, _douyin, _giftRepo, _users, _userLevel, _giftRuleRepo, _log, _system, _reply, _replyQueue);
        _giftCollector = new GiftCollectorService(_config, _douyin, _gift, _log, giftRepo: _giftRepo);
        _banVote = new BanVoteService(_config, _banVoteRepo, _users, _douyin, _replyQueue, _reply, _system, _log);
        _welcome = new WelcomeService(_config, _reply, _replyQueue, _system, _welcomeCooldownRepo);
        _keywordReply = new KeywordReplyService(_config, _keywordReplyRepo, _log);
        _pointsQuery = new PointsQueryService(_users, pointsLedgerRepo);
        _skipSong = new SkipSongService(
            _config, _users, _playback, _queue, _engine, _reply, _replyQueue, _system, _log);
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
            PointsLedger = pointsLedgerRepo,
            Gifts = _giftRepo,
            GiftRules = _giftRuleRepo,
            RandomPool = _randomPoolRepo,
            ReplyTemplates = _replyTemplateRepo,
            SongBlacklist = _songBlacklistRepo,
            KeywordReplies = _keywordReplyRepo,
            BanVotes = _banVoteRepo,
            LevelPermissions = _levelPermRepo,
            Log = _log,
            Health = _health,
            SongRequestControl = _songRequestControl,
            UserDetail = _userDetail,
            TemplatePreview = _templatePreview,
            Reply = _reply
        });
        _adminTunnel = new AdminTunnelService(_config, _log, _system);
        _aiSpeech = new AiSpeechCoordinator(_config, _log, _outboundTracker);
        _aiSpeech.StatusChanged += () => NotifyStateChanged();

        _danmaku.DanmakuReceived += OnDanmakuReceived;
        _songRequest.RequestHandled += () =>
        {
            _health.RecordSongRequest();
            _ = _engine.EnsurePlayingAsync();
        };
        _playbackCommands.Playback.StateChanged += OnPlaybackStateChanged;
        _queue.QueueChanged += () => NotifyStateChanged();
        _log.ErrorRecorded += () => _health.RecordError();
        _gift.GiftReceived += g =>
        {
            _health.RecordGift();
            try { _aiSpeech.TryEnqueueGift(g); }
            catch (Exception ex) { _log.Error("ai_speech", "礼物投递 AI 模块异常（已隔离）", ex); }
        };

        _adminWeb.Start();
        _adminTunnel.Start();
        _backendSync.Start();
        _dataCleanup.Start();

        _log.Info($"{AppBranding.DisplayName} 已启动");
        if (_config.Settings.Admin.Enabled)
        {
            _system.Add($"管理后台: http://127.0.0.1:{_config.Settings.Admin.Port}{_config.Settings.Admin.Path}");
        }

        StartSidecarWatchdog();
    }

    public IReadOnlyList<string> GetMissingSidecarFiles() => _watchdog.GetMissingRequiredFiles();

    private void StartSidecarWatchdog()
    {
        if (_watchCts != null)
        {
            return;
        }

        _watchCts = new CancellationTokenSource();
        _ = WatchSidecarsAsync(_watchCts.Token);
    }

    public ConfigManager Config => _config;
    public LogService Log => _log;
    public SystemMessageService SystemMessages => _system;
    public QueueService Queue => _queue;
    public PlaybackCommandQueue PlaybackCommands => _playbackCommands;
    public PlaybackEngine Engine => _engine;
    public DanmakuService Danmaku => _danmaku;
    public KugouService Kugou => _kugou;
    public SettingsStore SettingsStore => _settingsStore;
    public DateTime StartedAt => _startedAt;
    public LiveHealthService Health => _health;
    public SongRequestControlService SongRequestControl => _songRequestControl;
    public AiSpeechCoordinator AiSpeech => _aiSpeech;

    public event Action<DanmakuItem>? DanmakuReceived;
    public event Action? StateChanged;

    public RuntimeStatus GetRuntimeStatus()
    {
        var track = _playbackCommands.Playback.CurrentTrack;
        var playback = _playbackCommands.Playback;
        var queueCount = _queue.WaitingCount + (_queue.NowPlaying != null ? 1 : 0);
        var health = _health.GetSnapshot();
        var duration = Math.Max(1, playback.DurationSec);
        var progress = playback.ProgressSec;
        return new RuntimeStatus
        {
            DouyinOnline = _douyinSidecarOk,
            KugouOnline = _kugouSidecarOk,
            DouyinStatus = _douyinSidecarOk ? "在线" : "离线",
            KugouStatus = _kugouSidecarOk ? "在线" : "离线",
            KugouLoginStatus = _kugouSidecarOk ? _kugouLoginStatus : "离线",
            KugouVipLabel = _kugouVipLabel,
            KugouFullPlaybackAvailable = _kugouSidecarOk && _kugouFullPlaybackAvailable,
            KugouFullPlaybackReason = _kugouFullPlaybackReason,
            KugouLoginPageUrl = _kugou.LoginPageUrl,
            DanmakuConnection = _danmaku.ConnectionStatus,
            RoomOwnerNickname = _danmaku.RoomOwnerNickname,
            DouyinLoginStatus = _adminAccountStatus,
            DouyinLoginNickname = _adminNickname,
            CurrentSong = track == null ? "-" : $"{track.SongName} - {track.Artist}",
            PlaybackMode = _engine.Mode.ToString(),
            QueueCount = queueCount,
            WaitingQueueCount = _queue.WaitingCount,
            Uptime = DateTime.Now - _startedAt,
            StartedAt = _startedAt,
            CurrentTask = _currentTask,
            LastError = _log.LastError,
            PlaybackState = playback.State.ToString(),
            PlaybackSource = track == null ? "-" : track.IsRandom ? "随机补位" : "点歌",
            ProgressSec = progress,
            DurationSec = duration,
            RemainingSec = Math.Max(0, duration - progress),
            SongRequestEnabled = _songRequestControl.IsRequestEnabled,
            TodayDanmakuCount = health.TodayDanmakuCount,
            TodayGiftCount = health.TodayGiftCount,
            TodaySongRequestCount = health.TodaySongRequestCount,
            TodaySongsPlayed = health.TodaySongsPlayed,
            RecentErrorCount = health.RecentErrorCount
        };
    }

    public void SetSongRequestEnabled(bool enabled) => _songRequestControl.SetRequestEnabled(enabled);

    private void OnPlaybackStateChanged()
    {
        var track = _playbackCommands.Playback.CurrentTrack;
        if (track != null && _playbackCommands.Playback.State is PlaybackState.Playing or PlaybackState.RandomFill)
        {
            var key = $"{track.SongId}|{track.Hash}|{track.SongName}";
            if (_lastPlayedTrackKey != key)
            {
                _lastPlayedTrackKey = key;
                _health.RecordSongPlayed(key);
            }
        }

        NotifyStateChanged();
    }

    public async Task StartAsync(CancellationToken ct = default)
    {
        StartSidecarWatchdog();

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

    private void OnDanmakuReceived(DanmakuItem item)
    {
        _ = ProcessDanmakuSafeAsync(item);
    }

    private async Task ProcessDanmakuSafeAsync(DanmakuItem item)
    {
        try
        {
            await ProcessDanmakuAsync(item);
        }
        catch (Exception ex)
        {
            _log.Error("app", "处理弹幕异常", ex);
            _log.SetLastError("app", ex.Message);
        }
    }

    private async Task ProcessDanmakuAsync(DanmakuItem item)
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
        _users.TouchInteraction(item.UserId, item.Nickname);
        if (item.MsgType != "member" && item.MsgType != "gift")
        {
            _health.RecordDanmaku();
        }

        if (item.MsgType == "member")
        {
            _welcome.HandleMemberJoin(item, webRid);
            try { _aiSpeech.TryEnqueueMemberJoin(item); }
            catch (Exception ex) { _log.Error("ai_speech", "进房投递 AI 模块异常（已隔离）", ex); }
            return;
        }

        if (item.MsgType == "gift")
        {
            return;
        }

        if (item.MsgType is "like" or "digg")
        {
            try { _aiSpeech.TryEnqueueLike(item); }
            catch (Exception ex) { _log.Error("ai_speech", "点赞投递 AI 模块异常（已隔离）", ex); }
            return;
        }

        var contentSummary = TruncateForRoute(item.Content);
        var msgId = item.MsgId ?? "";

        if (_pointsQuery.TryHandle(item, webRid, _reply, _replyQueue))
        {
            _log.DouyinInfo(
                $"DANMAKU_ROUTE msgId={msgId} userId={item.UserId} content={contentSummary} route=points consumed=true");
            return;
        }

        if (await _skipSong.TryHandleAsync(item, webRid))
        {
            _log.DouyinInfo(
                $"DANMAKU_ROUTE msgId={msgId} userId={item.UserId} content={contentSummary} route=skip consumed=true");
            return;
        }

        if (await _banVote.TryHandleAsync(item, webRid))
        {
            _log.DouyinInfo(
                $"DANMAKU_ROUTE msgId={msgId} userId={item.UserId} content={contentSummary} route=ban consumed=true");
            return;
        }

        if (await _songRequest.HandleDanmakuAsync(item, webRid))
        {
            _log.DouyinInfo(
                $"DANMAKU_ROUTE msgId={msgId} userId={item.UserId} content={contentSummary} route=song consumed=true");
            return;
        }

        if (await _keywordReply.TryHandleAsync(item, _replyQueue, _reply, _users, webRid))
        {
            _log.DouyinInfo(
                $"DANMAKU_ROUTE msgId={msgId} userId={item.UserId} content={contentSummary} route=keyword consumed=true");
            return;
        }

        // 仅未被业务模块消费的普通聊天进入 AI 语音
        try
        {
            _aiSpeech.TryEnqueueDanmaku(
                item,
                _danmaku.RoomOwnerNickname,
                string.IsNullOrWhiteSpace(_adminNickname) ? _danmaku.DouyinLoginNickname : _adminNickname);
            _log.DouyinInfo(
                $"DANMAKU_ROUTE msgId={msgId} userId={item.UserId} content={contentSummary} route=ai consumed=false");
        }
        catch (Exception aiEx)
        {
            _log.Error("ai_speech", "弹幕投递 AI 模块异常（已隔离）", aiEx);
        }

        NotifyStateChanged();
    }

    private static string TruncateForRoute(string? s, int max = 40)
    {
        if (string.IsNullOrEmpty(s))
        {
            return "";
        }

        s = s.Replace('\r', ' ').Replace('\n', ' ');
        return s.Length <= max ? s : s[..max] + "...";
    }

    private async Task WatchSidecarsAsync(CancellationToken ct)
    {
        var wasDouyinDown = false;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var kgOk = await _kugou.HealthCheckAsync(ct);
                await _watchdog.EnsureSidecarsAsync(
                    () => _douyin.HealthCheckAsync(ct),
                    () => Task.FromResult(kgOk),
                    ct);

                var health = await _douyin.GetHealthAsync(ct);
                var dyOk = health != null;
                _douyinSidecarOk = dyOk;
                _kugouSidecarOk = kgOk;

                if (kgOk)
                {
                    var login = await _kugou.RefreshLoginStatusAsync(forceRefresh: true, ct);
                    if (login.LoggedIn)
                    {
                        var claim = await _kugou.TryAutoClaimVipAsync(ct);
                        if (claim != null && (claim.Claimed || claim.Upgraded))
                        {
                            login = await _kugou.RefreshLoginStatusAsync(forceRefresh: true, ct);
                            _system.Add($"酷狗试用会员: {claim.Message}");
                        }
                    }

                    _kugouLoginStatus = login.DisplayStatus;
                    _kugouVipLabel = login.VipLabel;

                    var fullStatus = await _kugou.CheckFullPlaybackStatusAsync(login, forceProbe: false, ct);
                    _kugouFullPlaybackAvailable = fullStatus.FullPlaybackAvailable;
                    _kugouFullPlaybackReason = fullStatus.Reason;
                    _kugouStatusCheckedAtTicks = fullStatus.CheckedAtUtc.Ticks;
                    _log.KugouInfo(
                        $"KUGOU_STATUS loggedIn={fullStatus.LoggedIn} vip={fullStatus.VipLabel} " +
                        $"fullPlaybackAvailable={fullStatus.FullPlaybackAvailable} " +
                        $"lastCheckTime={fullStatus.CheckedAtUtc:O}" +
                        (string.IsNullOrWhiteSpace(fullStatus.Reason) ? "" : $" reason={fullStatus.Reason}"));

                    if (!_kugouLoginWarned)
                    {
                        if (!login.LoggedIn)
                        {
                            _system.Add("酷狗未登录：点击「酷狗登录」扫码，登录后会自动领取每日试用会员");
                        }
                        else if (string.IsNullOrWhiteSpace(login.VipLabel))
                        {
                            _system.Add("酷狗已登录，正在自动领取试用会员；若仍无会员请重新扫码");
                        }
                        else if (!fullStatus.FullPlaybackAvailable)
                        {
                            var reason = string.IsNullOrWhiteSpace(fullStatus.Reason)
                                ? "完整版不可用"
                                : fullStatus.Reason;
                            _system.Add($"酷狗完整版不可用：{reason}");
                        }

                        _kugouLoginWarned = true;
                    }
                }
                else
                {
                    _kugouLoginStatus = "离线";
                    _kugouVipLabel = "";
                    _kugouFullPlaybackAvailable = false;
                    _kugouFullPlaybackReason = "Sidecar离线";
                }

                if (health != null)
                {
                    _adminAccountStatus = health.LoginOk ? "已登录" : "未登录";
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

    private static string TruncateForUi(string content, int max)
    {
        content = content.Trim();
        if (content.Length <= max)
        {
            return content;
        }

        return content[..max] + "…";
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try { _watchCts?.Cancel(); } catch { /* ignore */ }
        try { _banVote.Dispose(); } catch { /* ignore */ }
        try { _aiSpeech.Dispose(); } catch { /* ignore */ }
        try { _dataCleanup.Dispose(); } catch { /* ignore */ }
        try { _backendSync.Dispose(); } catch { /* ignore */ }
        try { _adminTunnel.Dispose(); } catch { /* ignore */ }
        try { _adminWeb.Dispose(); } catch { /* ignore */ }
        try { _giftCollector.Dispose(); } catch { /* ignore */ }
        try { _gift.Dispose(); } catch { /* ignore */ }
        try { _danmaku.Dispose(); } catch { /* ignore */ }
        try { _replyQueue.Dispose(); } catch { /* ignore */ }
        try { _playbackCommands.Dispose(); } catch { /* ignore */ }
        try { _playback.Dispose(); } catch { /* ignore */ }
        try { _db.Dispose(); } catch { /* ignore */ }
        try { _log.Info($"{AppBranding.DisplayName} 已退出"); } catch { /* ignore */ }
    }
}
