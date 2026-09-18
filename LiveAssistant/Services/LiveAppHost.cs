using LiveAssistant.Admin;
using LiveAssistant.Config;
using LiveAssistant.Database;
using LiveAssistant.Models;
using LiveAssistant.Services.AiSpeech;
using LiveAssistant.Utils;

namespace LiveAssistant.Services;

public sealed class LiveAppHost : IDisposable
{
    private readonly ConfigManager _config;
    private readonly LogService _log;
    private readonly SystemMessageService _system;
    private readonly AppDatabase _db;
    private readonly DouyinService _douyin;
    private readonly KuaishouService _kuaishou;
    private readonly KuaishouDanmakuService _ksDanmaku;
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
    private readonly MachineSetupService _machineSetup;
    private readonly MovieInteractionService _movieInteraction;
    private readonly MovieScoreSyncService _movieScoreSync;
    private readonly DateTime _startedAt = DateTime.Now;
    private string _lastPlayedTrackKey = "";
    private CancellationTokenSource? _watchCts;
    private volatile bool _douyinSidecarOk;
    private volatile bool _ksSidecarOk;
    private volatile bool _kugouSidecarOk;
    private volatile string _kugouLoginStatus = "未检测";
    private volatile string _kugouVipLabel = "";
    private volatile bool _kugouFullPlaybackAvailable;
    private volatile string _kugouFullPlaybackReason = "";
    private long _kugouStatusCheckedAtTicks;
    private volatile bool _kugouLoginWarned;
    private volatile bool _cdpLoginWarned;
    private volatile string _adminAccountStatus = "未检测";
    private volatile string _adminNickname = "-";
    private volatile string _currentTask = "空闲";
    private volatile bool _isRunning;
    private int _disposed;
    /// <summary>限制弹幕处理并发，避免高峰期 Task 堆积拖垮线程池。</summary>
    private readonly SemaphoreSlim _danmakuProcessGate = new(8, 8);
    private DateTime _lastDanmakuOverloadLogUtc = DateTime.MinValue;

    public LiveAppHost()
    {
        _config = new ConfigManager();
        _config.Load();

        _log = new LogService(_config.DataDirectory);
        _system = new SystemMessageService(_config.Settings.Ui.MaxSystemMessageLines);
        _db = new AppDatabase(_config.DataDirectory);

        var pointsLedgerRepo = new PointsLedgerRepository(_db);
        _users = new UserRepository(_db, pointsLedgerRepo);
        var songCharges = new SongRequestChargeRepository(_db, pointsLedgerRepo);

        // 启动遗弃前先退还 charged 且未 fulfilled 的点歌
        var pendingAbandon = _db.ListPendingQueueItemsForAbandon();
        var refundedOnStartup = 0;
        foreach (var pending in pendingAbandon)
        {
            if (pending.IsRandom)
            {
                continue;
            }

            var refund = songCharges.RefundSongRequestCharge(pending.Id, "startup_abandon");
            if (refund.Result is "success")
            {
                refundedOnStartup++;
                _log.Info(
                    $"SONG_REQUEST_REFUND queueItemId={pending.Id} reason=startup_abandon " +
                    $"pointsRestored={refund.PointsRestored} creditRestored={refund.CreditRestored} " +
                    $"result={refund.Result}");
            }
            else if (refund.Result is not "no_charge" and not "already_fulfilled" and not "already_refunded")
            {
                _log.Info(
                    $"SONG_REQUEST_REFUND queueItemId={pending.Id} reason=startup_abandon " +
                    $"pointsRestored={refund.PointsRestored} creditRestored={refund.CreditRestored} " +
                    $"result={refund.Result}");
            }
        }

        var abandoned = _db.AbandonPendingQueueOnStartup();
        if (abandoned > 0)
        {
            _log.Info($"启动清空未播放队列: {abandoned} 条（退款 {refundedOnStartup} 条）");
            _system.Add($"启动已清空 {abandoned} 条未播放点歌，等待新点歌");
        }

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
        _kuaishou = new KuaishouService(_config, _log);
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
            onSendSucceeded: content => _system.Add($"弹幕已发出：{TruncateForUi(content, 80)}"),
            sendMention: SendPlatformMentionAsync);
        _random = new RandomPlaylistService(_config, _db);
        _playback = new PlaybackService(_log);
        _playback.SetVolume(_config.Settings.Playback.Volume);
        _playback.SetDeviceNumber(AudioOutputDevices.ResolveDeviceNumber(
            _config.Settings.Playback.OutputDeviceName,
            _config.Settings.Playback.OutputDeviceNumber));

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
            _config, _users, _queue, songBlacklist, _levelPermRepo, _userLevel, _giftRepo, _songRequestControl, _log, songCharges);
        _playbackCommands.SongCharges = _permission;
        _songRequest = new SongRequestService(_config, _kugou, _queue, _permission, _reply, _replyQueue, _system, _log);
        _gift = new GiftService(_config, _douyin, _giftRepo, _users, _userLevel, _giftRuleRepo, _log, _system, _reply, _replyQueue);
        _ksDanmaku = new KuaishouDanmakuService(_kuaishou, _config, _log, _system, _gift, _outboundTracker);
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

        _queue.OnWaitingItemRemoved = (id, reason) =>
            _permission.RefundQueueItemIfNeeded(id, reason);

        _adminTunnel = new AdminTunnelService(_config, _log, _system);
        _aiSpeech = new AiSpeechCoordinator(_config, _log, _outboundTracker);
        _aiSpeech.StatusChanged += () => NotifyStateChanged();
        _machineSetup = new MachineSetupService(_config, _log, _playback, _aiSpeech);
        _movieInteraction = new MovieInteractionService(_config, _db, _log);
        _movieScoreSync = new MovieScoreSyncService(_config, _movieInteraction, _log);

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
            Reply = _reply,
            MovieInteraction = _movieInteraction
        });

        _danmaku.DanmakuReceived += OnDanmakuReceived;
        _ksDanmaku.DanmakuReceived += OnDanmakuReceived;
        _songRequest.RequestHandled += () =>
        {
            _health.RecordSongRequest();
            _ = _engine.EnsurePlayingAsync();
        };
        _songRequest.SongRequestSucceeded += e =>
        {
            try { _aiSpeech.TryEnqueueSongRequest(e); }
            catch (Exception ex) { _log.Error("ai_speech", "点歌成功投递 AI 模块异常（已隔离）", ex); }
        };
        _playbackCommands.Playback.StateChanged += OnPlaybackStateChanged;
        _queue.QueueChanged += () => NotifyStateChanged();
        _log.ErrorRecorded += () => _health.RecordError();
        _gift.GiftReceived += g =>
        {
            _health.RecordGift();
            try { _aiSpeech.TryEnqueueGift(g); }
            catch (Exception ex) { _log.Error("ai_speech", "礼物投递 AI 模块异常（已隔离）", ex); }
            try { _movieInteraction.OnGiftReceived(g); }
            catch (Exception ex) { _log.Error("movie_score", "礼物投递电影评分模块异常（已隔离）", ex); }
        };

        _adminWeb.Start();
        _adminTunnel.Start();
        _backendSync.Start();
        _dataCleanup.Start();
        _movieInteraction.Start();
        _movieScoreSync.Start();

        _log.Info($"{AppBranding.DisplayName} 已启动");
        if (_config.Settings.Admin.Enabled)
        {
            _system.Add($"管理后台: http://127.0.0.1:{_config.Settings.Admin.Port}{_config.Settings.Admin.Path}");
        }

        StartSidecarWatchdog();
    }

    public MachineSetupService MachineSetup => _machineSetup;

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
            KuaishouOnline = _ksDanmaku.IsLiveConnected,
            KugouOnline = _kugouSidecarOk,
            DouyinStatus = _douyinSidecarOk ? "在线" : "离线",
            KuaishouStatus = _ksSidecarOk ? "在线" : "离线",
            KuaishouConnection = _ksDanmaku.ConnectionStatus,
            KuaishouRoomId = string.IsNullOrWhiteSpace(_ksDanmaku.RoomId)
                ? (_config.Settings.Kuaishou.RoomId ?? "")
                : _ksDanmaku.RoomId,
            KuaishouRoomTitle = _ksDanmaku.RoomTitle,
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
        var ks = _config.Settings.Kuaishou;
        var ksReady = ks.Enabled
            && !string.IsNullOrWhiteSpace(ks.RoomId)
            && !string.IsNullOrWhiteSpace(ks.Cookie);

        if (string.IsNullOrWhiteSpace(webRid) && !ksReady)
        {
            _system.Add("未配置抖音 web_rid 或快手房间，请在设置/后台填写后连接");
            return;
        }

        _isRunning = true;
        _currentTask = "连接直播间";

        if (!string.IsNullOrWhiteSpace(webRid))
        {
            await _danmaku.StartAsync(webRid, ct);
            _gift.Start(webRid);
            _giftCollector.StartGiftCollector(webRid);
        }

        if (ksReady)
        {
            try
            {
                await _ksDanmaku.StartAsync(ct);
            }
            catch (Exception ex)
            {
                _log.KuaishouWarn($"启动快手通道失败: {ex.Message}");
                _system.Add($"快手连接失败：{ex.Message}");
            }
        }

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
        _ksDanmaku.Stop();
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

    public async Task ConnectKuaishouAsync(CancellationToken ct = default)
    {
        _config.Settings.Kuaishou.Enabled = true;
        _config.Save();
        _isRunning = true;
        _currentTask = "连接快手直播间";
        await _ksDanmaku.StartAsync(ct);
        _currentTask = "监控中";
        _system.Add(_reply.Render("systemConnected", new Dictionary<string, string>()));
        await _engine.EnsurePlayingAsync();
        NotifyStateChanged();
    }

    public void DisconnectKuaishou()
    {
        _ksDanmaku.Stop();
        if (!_danmaku.IsRunning)
        {
            _isRunning = false;
            _currentTask = "已停止";
        }

        _system.Add("已断开快手直播间");
        NotifyStateChanged();
    }

    public void SaveKuaishouSettings(bool enabled, string baseUrl, string roomId, string? cookie, int? pollIntervalMs = null)
    {
        var ks = _config.Settings.Kuaishou;
        var prevBase = ks.BaseUrl?.Trim() ?? "";
        var wasRunning = _ksDanmaku.IsRunning || _ksDanmaku.WantConnected;
        ks.Enabled = enabled;
        if (!string.IsNullOrWhiteSpace(baseUrl))
        {
            ks.BaseUrl = baseUrl.Trim();
        }

        ks.RoomId = roomId?.Trim() ?? "";
        if (cookie != null && cookie.Trim().Length > 0)
        {
            ks.Cookie = cookie.Trim();
            ks.CookieSavedAtUtcTicks = DateTime.UtcNow.Ticks;
            var exp = KuaishouCookieHelper.TryParseEarliestExpiryUtc(ks.Cookie);
            ks.CookieExpiresAtUtcTicks = exp?.Ticks ?? 0;
            ks.ConnectFailureStreak = 0;
        }

        if (pollIntervalMs is > 0)
        {
            ks.PollIntervalMs = pollIntervalMs.Value;
        }

        _config.Save();
        _kuaishou.ReloadBaseUrl();

        var baseChanged = !string.Equals(prevBase, ks.BaseUrl?.Trim() ?? "", StringComparison.OrdinalIgnoreCase);
        if (wasRunning && enabled && (baseChanged || !string.IsNullOrWhiteSpace(cookie)))
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await _ksDanmaku.StartAsync();
                    _system.Add("快手设置已更新并重新连接");
                    NotifyStateChanged();
                }
                catch (Exception ex)
                {
                    _log.KuaishouWarn($"保存后重连失败: {ex.Message}");
                    _system.Add($"快手重连失败：{ex.Message}");
                }
            });
        }
        else if (!enabled && _ksDanmaku.IsRunning)
        {
            _ksDanmaku.Stop();
        }

        NotifyStateChanged();
    }

    public object GetAudioOutputSnapshot()
    {
        var pb = _config.Settings.Playback;
        var ai = _config.Settings.AiSpeech;
        var devices = AudioOutputDevices.ListDevices()
            .Select(d => new { deviceNumber = d.DeviceNumber, name = d.Name })
            .ToList();
        var songDev = AudioOutputDevices.ResolveDeviceNumber(pb.OutputDeviceName, pb.OutputDeviceNumber);
        var (dyName, dyNum) = ai.ResolveOutputDevice("douyin");
        var (ksName, ksNum) = ai.ResolveOutputDevice("kuaishou");
        var dyDev = AudioOutputDevices.ResolveDeviceNumber(dyName, dyNum);
        var ksDev = AudioOutputDevices.ResolveDeviceNumber(ksName, ksNum);
        return new
        {
            devices,
            song = new
            {
                outputDeviceNumber = songDev,
                outputDeviceName = pb.OutputDeviceName ?? "",
                resolvedName = devices.FirstOrDefault(d => d.deviceNumber == songDev)?.name ?? "系统默认"
            },
            ai = new
            {
                outputDeviceNumber = dyDev,
                outputDeviceName = ai.OutputDeviceName ?? "",
                resolvedName = devices.FirstOrDefault(d => d.deviceNumber == dyDev)?.name ?? "系统默认"
            },
            aiDouyin = new
            {
                outputDeviceNumber = dyDev,
                outputDeviceName = ai.OutputDeviceName ?? "",
                resolvedName = devices.FirstOrDefault(d => d.deviceNumber == dyDev)?.name ?? "系统默认"
            },
            aiKuaishou = new
            {
                outputDeviceNumber = ksDev,
                outputDeviceName = ai.HasSeparateKuaishouOutput ? (ai.KuaishouOutputDeviceName ?? "") : (ai.OutputDeviceName ?? ""),
                resolvedName = devices.FirstOrDefault(d => d.deviceNumber == ksDev)?.name ?? "系统默认",
                separate = ai.HasSeparateKuaishouOutput
            },
            hint = "【双通道 AI】抖音弹幕口播 → 抖音设备；快手弹幕口播 → 快手设备，避免串台。\n"
                 + "【推荐】歌曲与快手 AI 选 CABLE Input（快手「系统声音」采 CABLE）；抖音 AI 选系统默认/耳机（抖音「应用进程」采本软件）。\n"
                 + "注意：抖音若用进程采音，仍会听到本软件播到任意设备的声音；要完全隔离需两路虚拟线且抖音也不要采进程。改设备后下一句 AI 生效。",
            syncSuggested = songDev == dyDev && dyDev == ksDev
        };
    }

    public object SaveAudioOutput(
        int? songDeviceNumber,
        string? songDeviceName,
        int? aiDeviceNumber,
        string? aiDeviceName,
        bool syncAiToSong,
        int? aiKuaishouDeviceNumber = null,
        string? aiKuaishouDeviceName = null,
        bool? syncKuaishouAiToSong = null)
    {
        var devices = AudioOutputDevices.ListDevices();
        var pb = _config.Settings.Playback;

        if (songDeviceNumber.HasValue || !string.IsNullOrWhiteSpace(songDeviceName))
        {
            var num = songDeviceNumber ?? pb.OutputDeviceNumber;
            var name = songDeviceName?.Trim() ?? pb.OutputDeviceName;
            if (songDeviceNumber.HasValue)
            {
                var match = devices.FirstOrDefault(d => d.DeviceNumber == songDeviceNumber.Value);
                if (match != null)
                {
                    num = match.DeviceNumber;
                    name = match.Name;
                }
            }

            num = AudioOutputDevices.ResolveDeviceNumber(name, num);
            pb.OutputDeviceNumber = num;
            pb.OutputDeviceName = num < 0 ? "" : name;
            _playback.SetDeviceNumber(num);
        }

        if (syncAiToSong)
        {
            _config.Settings.AiSpeech.OutputDeviceNumber = pb.OutputDeviceNumber;
            _config.Settings.AiSpeech.OutputDeviceName = pb.OutputDeviceName ?? "";
            _aiSpeech.ApplyDeviceFromSettings("douyin");
        }
        else if (aiDeviceNumber.HasValue || !string.IsNullOrWhiteSpace(aiDeviceName))
        {
            var num = aiDeviceNumber ?? _config.Settings.AiSpeech.OutputDeviceNumber;
            var name = aiDeviceName?.Trim() ?? _config.Settings.AiSpeech.OutputDeviceName;
            if (aiDeviceNumber.HasValue)
            {
                var match = devices.FirstOrDefault(d => d.DeviceNumber == aiDeviceNumber.Value);
                if (match != null)
                {
                    num = match.DeviceNumber;
                    name = match.Name;
                }
            }

            num = AudioOutputDevices.ResolveDeviceNumber(name, num);
            _config.Settings.AiSpeech.OutputDeviceNumber = num;
            _config.Settings.AiSpeech.OutputDeviceName = num < 0 ? "" : name;
            _aiSpeech.ApplyDeviceFromSettings("douyin");
        }

        if (syncKuaishouAiToSong == true)
        {
            _config.Settings.AiSpeech.KuaishouOutputDeviceNumber = pb.OutputDeviceNumber;
            _config.Settings.AiSpeech.KuaishouOutputDeviceName = pb.OutputDeviceName ?? "";
            _aiSpeech.ApplyDeviceFromSettings("kuaishou");
        }
        else if (aiKuaishouDeviceNumber.HasValue || aiKuaishouDeviceName != null)
        {
            var num = aiKuaishouDeviceNumber ?? _config.Settings.AiSpeech.KuaishouOutputDeviceNumber ?? -1;
            var name = aiKuaishouDeviceName?.Trim()
                       ?? _config.Settings.AiSpeech.KuaishouOutputDeviceName
                       ?? "";
            if (aiKuaishouDeviceNumber.HasValue)
            {
                var match = devices.FirstOrDefault(d => d.DeviceNumber == aiKuaishouDeviceNumber.Value);
                if (match != null)
                {
                    num = match.DeviceNumber;
                    name = match.Name;
                }
            }

            num = AudioOutputDevices.ResolveDeviceNumber(name, num);
            _config.Settings.AiSpeech.KuaishouOutputDeviceNumber = num;
            _config.Settings.AiSpeech.KuaishouOutputDeviceName = num < 0 ? "" : name;
            _aiSpeech.ApplyDeviceFromSettings("kuaishou");
        }

        _config.Save();
        NotifyStateChanged();
        return GetAudioOutputSnapshot();
    }

    public object GetKuaishouAdminSnapshot()
    {
        var ks = _config.Settings.Kuaishou;
        var cookieEval = KuaishouCookieHelper.Evaluate(
            ks.Cookie, ks.CookieSavedAtUtcTicks, ks.CookieExpiresAtUtcTicks, ks.ConnectFailureStreak);
        return new
        {
            enabled = ks.Enabled,
            baseUrl = ks.BaseUrl,
            roomId = ks.RoomId,
            cookieConfigured = !string.IsNullOrWhiteSpace(ks.Cookie),
            cookiePreview = MaskCookie(ks.Cookie),
            cookieHint = cookieEval.Hint,
            cookieStale = cookieEval.Stale,
            cookieSavedAt = ks.CookieSavedAtUtcTicks > 0
                ? new DateTime(ks.CookieSavedAtUtcTicks, DateTimeKind.Utc).ToLocalTime().ToString("yyyy-MM-dd HH:mm")
                : "",
            connectFailureStreak = ks.ConnectFailureStreak,
            pollIntervalMs = ks.PollIntervalMs,
            online = _ksDanmaku.IsLiveConnected,
            connection = _ksDanmaku.ConnectionStatus,
            roomTitle = _ksDanmaku.RoomTitle,
            sidecarOk = _ksSidecarOk,
            wantConnected = _ksDanmaku.WantConnected,
            featuresNote = "点歌/队列/积分/关键词/切歌等与抖音共用「点歌管理」「回复管理」模块；快手弹幕经侧车接入同一处理链路。"
        };
    }

    public async Task<object> GetKuaishouDiagnoseAsync(CancellationToken ct = default)
    {
        var ks = _config.Settings.Kuaishou;
        var jar = Path.Combine(AppPaths.ExeDirectory, "sidecars", "kuaishou", "ks-ui-server.jar");
        var java = ProcessWatchdogService.ResolveJavaExePublic();
        var port = 18900;
        try
        {
            if (Uri.TryCreate(ks.BaseUrl?.Trim() ?? "", UriKind.Absolute, out var uri) && uri.Port > 0)
            {
                port = uri.Port;
            }
        }
        catch { /* ignore */ }

        var health = false;
        string healthErr = "";
        try { health = await _kuaishou.HealthCheckAsync(ct); }
        catch (Exception ex) { healthErr = ex.Message; }

        KuaishouBridgeStatus? bridge = null;
        try { bridge = await _kuaishou.GetBridgeStatusAsync(ct); } catch { /* ignore */ }

        var portOpen = false;
        try
        {
            using var tcp = new System.Net.Sockets.TcpClient();
            var connect = tcp.ConnectAsync("127.0.0.1", port);
            var done = await Task.WhenAny(connect, Task.Delay(800, ct));
            if (done == connect)
            {
                await connect;
                portOpen = tcp.Connected;
            }
        }
        catch { /* ignore */ }

        var cookieEval = KuaishouCookieHelper.Evaluate(
            ks.Cookie, ks.CookieSavedAtUtcTicks, ks.CookieExpiresAtUtcTicks, ks.ConnectFailureStreak);

        var checks = new List<(string name, bool ok, string detail)>
        {
            ("Java 运行时", !string.IsNullOrWhiteSpace(java), java ?? "未找到 JAVA_HOME / java.exe"),
            ("ks-ui-server.jar", File.Exists(jar), File.Exists(jar) ? jar : "缺少 sidecars/kuaishou/ks-ui-server.jar"),
            ($"端口 :{port}", portOpen, portOpen ? "已监听" : "未监听（侧车未启动？）"),
            ("侧车 Health", health, health ? "api/health 正常" : (string.IsNullOrWhiteSpace(healthErr) ? "不可达" : healthErr)),
            ("桥接连接", bridge?.Connected == true, bridge == null ? "无状态" : $"{bridge.Status} / {bridge.RoomTitle}"),
            ("房间号", !string.IsNullOrWhiteSpace(ks.RoomId), string.IsNullOrWhiteSpace(ks.RoomId) ? "未配置" : ks.RoomId),
            ("Cookie", !cookieEval.Stale && !string.IsNullOrWhiteSpace(ks.Cookie), cookieEval.Hint),
            ("通道意图", _ksDanmaku.WantConnected, _ksDanmaku.WantConnected ? $"运行中 / {_ksDanmaku.ConnectionStatus}" : "未连接（后台点「连接快手」）")
        };

        return new
        {
            ok = checks.All(c => c.ok),
            summary = checks.All(c => c.ok) ? "快手链路就绪" : "存在待处理项，请按下方清单排查",
            baseUrl = ks.BaseUrl,
            checks = checks.Select(c => new { name = c.name, ok = c.ok, detail = c.detail }).ToList(),
            snapshot = GetKuaishouAdminSnapshot()
        };
    }

    private static string MaskCookie(string? cookie)
    {
        if (string.IsNullOrWhiteSpace(cookie))
        {
            return "";
        }

        var s = cookie.Trim();
        return s.Length <= 12 ? "***" : s[..6] + "…" + s[^4..] + $"（{s.Length}字）";
    }

    private async Task<MentionSendResult> SendPlatformMentionAsync(
        string webRid, string userId, string content, string? nickname, CancellationToken ct)
    {
        if (KuaishouService.IsKuaishouRoom(webRid))
        {
            var nick = nickname?.Trim();
            if (string.IsNullOrWhiteSpace(nick))
            {
                nick = _users.GetUser(userId)?.Nickname;
            }

            if (string.IsNullOrWhiteSpace(nick))
            {
                nick = PlatformUserIds.RawForApi(userId);
            }

            var ok = await _kuaishou.SendAtReplyAsync(nick!, content, ct);
            if (ok)
            {
                // 快手侧车会把正文变成「@昵称 内容」，两侧都 Track 便于回声过滤
                var full = $"@{nick} {content}".Trim();
                _outboundTracker.Track(Guid.NewGuid().ToString("N"), content, roomKey: webRid);
                _outboundTracker.Track(Guid.NewGuid().ToString("N"), full, roomKey: webRid);
            }

            return new MentionSendResult
            {
                Ok = ok,
                HttpStatus = ok ? 200 : 400,
                ErrorReason = ok ? "" : "快手发弹幕失败",
                ReplyType = "mention"
            };
        }

        // 抖音走详细发送，带 nickname 便于 CDP 纯文字 @；保留 PlatformMessageId 供回显过滤
        return await _douyin.SendMentionDetailedAsync(
            webRid, PlatformUserIds.RawForApi(userId), content, nickname, ct);
    }

    private void OnDanmakuReceived(DanmakuItem item)
    {
        // 电影评分是旁路观察：不得 return、不得改 DanmakuItem、不得影响后续点歌/AI。
        try { _movieInteraction.OnDanmaku(item); }
        catch (Exception ex) { _log.Error("movie_score", "弹幕投递电影评分模块异常（已隔离）", ex); }
        _ = ProcessDanmakuSafeAsync(item);
    }

    private async Task ProcessDanmakuSafeAsync(DanmakuItem item)
    {
        // 点歌/确认/切歌等业务指令绕过并发门控，避免高峰被闲聊挤掉
        if (IsPriorityDanmaku(item.Content))
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

            return;
        }

        if (!await _danmakuProcessGate.WaitAsync(0))
        {
            if (DateTime.UtcNow - _lastDanmakuOverloadLogUtc > TimeSpan.FromSeconds(30))
            {
                _lastDanmakuOverloadLogUtc = DateTime.UtcNow;
                _log.Warn("弹幕处理过载，已跳过部分闲聊消息");
            }

            return;
        }

        try
        {
            await ProcessDanmakuAsync(item);
        }
        catch (Exception ex)
        {
            _log.Error("app", "处理弹幕异常", ex);
            _log.SetLastError("app", ex.Message);
        }
        finally
        {
            try { _danmakuProcessGate.Release(); } catch { /* ignore */ }
        }
    }

    private static bool IsPriorityDanmaku(string? content)
    {
        content = (content ?? "").Trim();
        if (content.Length == 0)
        {
            return false;
        }

        return SongRequestConfirmParser.IsConfirm(content)
               || SongRequestConfirmParser.IsCancel(content)
               || SongNameParser.TryParse(content, out _)
               || SkipSongParser.TryParse(content)
               || PointsQueryParser.TryParse(content)
               || content.StartsWith("禁言", StringComparison.OrdinalIgnoreCase)
               || content.StartsWith("解除禁言", StringComparison.OrdinalIgnoreCase)
               || content.StartsWith("解禁", StringComparison.OrdinalIgnoreCase);
    }

    private async Task ProcessDanmakuAsync(DanmakuItem item)
    {
        DanmakuReceived?.Invoke(item);

        if (_config.Settings.Emergency.PauseInteraction)
        {
            return;
        }

        var webRid = !string.IsNullOrWhiteSpace(item.RoomKey)
            ? item.RoomKey!
            : item.Platform == "kuaishou"
                ? KuaishouService.RoomKey(_config.Settings.Kuaishou.RoomId)
                : _config.Settings.Douyin.WebRid;

        if (string.IsNullOrWhiteSpace(webRid))
        {
            return;
        }

        item.RoomKey = webRid;

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
            var roomOwner = item.Platform == "kuaishou" || KuaishouService.IsKuaishouRoom(webRid)
                ? (string.IsNullOrWhiteSpace(_ksDanmaku.RoomTitle) || _ksDanmaku.RoomTitle == "-"
                    ? _config.Settings.Kuaishou.RoomId
                    : _ksDanmaku.RoomTitle)
                : _danmaku.RoomOwnerNickname;
            // 快手勿套用抖音登录昵称做自过滤，避免误杀同名观众
            var loginNick = item.Platform == "kuaishou" || KuaishouService.IsKuaishouRoom(webRid)
                ? null
                : (string.IsNullOrWhiteSpace(_adminNickname) ? _danmaku.DouyinLoginNickname : _adminNickname);
            _aiSpeech.TryEnqueueDanmaku(item, roomOwner, loginNick);
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
        var wasKsDown = false;
        var lastKsReconnectUtc = DateTime.MinValue;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var kgOk = await _kugou.HealthCheckAsync(ct);
                await _watchdog.EnsureSidecarsAsync(
                    () => _douyin.HealthCheckAsync(ct),
                    () => Task.FromResult(kgOk),
                    () => _kuaishou.HealthCheckAsync(ct),
                    ct);

                var health = await _douyin.GetHealthAsync(ct);
                var dyOk = health != null;
                _douyinSidecarOk = dyOk;
                _kugouSidecarOk = kgOk;
                _ksSidecarOk = await _kuaishou.HealthCheckAsync(ct);

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
                    _adminNickname = string.IsNullOrWhiteSpace(health.Nickname) ? "-" : health.Nickname;
                    if (!health.LoginOk && !_cdpLoginWarned)
                    {
                        _system.Add("抖音 CDP 未登录，请先完成扫码登录");
                        _cdpLoginWarned = true;
                    }
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

                // 快手：侧车恢复或桥断开时自动重连（限流）
                if (_ksDanmaku.WantConnected && _config.Settings.Kuaishou.Enabled)
                {
                    var needReconnect = false;
                    if (_ksSidecarOk && wasKsDown)
                    {
                        needReconnect = true;
                    }
                    else if (_ksSidecarOk && !_ksDanmaku.IsLiveConnected)
                    {
                        needReconnect = true;
                    }

                    if (needReconnect && DateTime.UtcNow - lastKsReconnectUtc > TimeSpan.FromSeconds(20))
                    {
                        lastKsReconnectUtc = DateTime.UtcNow;
                        try
                        {
                            _log.KuaishouInfo("快手 Sidecar/桥接恢复，尝试重连");
                            await _ksDanmaku.StartAsync(ct);
                            _system.Add("快手服务已恢复并重连");
                        }
                        catch (Exception ex)
                        {
                            _log.KuaishouWarn($"快手自动重连失败: {ex.Message}");
                        }
                    }
                }

                wasKsDown = !_ksSidecarOk;

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
        try { _danmaku.DanmakuReceived -= OnDanmakuReceived; } catch { /* ignore */ }
        try { _ksDanmaku.DanmakuReceived -= OnDanmakuReceived; } catch { /* ignore */ }
        try { _banVote.Dispose(); } catch { /* ignore */ }
        try { _aiSpeech.Dispose(); } catch { /* ignore */ }
        try { _movieScoreSync.Dispose(); } catch { /* ignore */ }
        try { _movieInteraction.Dispose(); } catch { /* ignore */ }
        try { _dataCleanup.Dispose(); } catch { /* ignore */ }
        try { _backendSync.Dispose(); } catch { /* ignore */ }
        try { _adminTunnel.Dispose(); } catch { /* ignore */ }
        try { _adminWeb.Dispose(); } catch { /* ignore */ }
        try { _giftCollector.Dispose(); } catch { /* ignore */ }
        try { _gift.Dispose(); } catch { /* ignore */ }
        try { _danmaku.Dispose(); } catch { /* ignore */ }
        try { _ksDanmaku.Dispose(); } catch { /* ignore */ }
        try { _kuaishou.Dispose(); } catch { /* ignore */ }
        try { _replyQueue.Dispose(); } catch { /* ignore */ }
        try { _playbackCommands.Dispose(); } catch { /* ignore */ }
        try { _playback.Dispose(); } catch { /* ignore */ }
        try { _danmakuProcessGate.Dispose(); } catch { /* ignore */ }
        try { _db.Dispose(); } catch { /* ignore */ }
        try { _log.Info($"{AppBranding.DisplayName} 已退出"); } catch { /* ignore */ }
    }
}
