using System.Text.Json;
using System.Text.Json.Serialization;
using LiveAssistant.Models;
using LiveAssistant.Utils;

namespace LiveAssistant.Config;

public sealed class AppSettings
{
    public DouyinSettings Douyin { get; set; } = new();
    public KuaishouSettings Kuaishou { get; set; } = new();
    public KugouSettings Kugou { get; set; } = new();
    public PlaybackSettings Playback { get; set; } = new();
    public RandomPlaylistSettings RandomPlaylist { get; set; } = new();
    public UiSettings Ui { get; set; } = new();
    public QueueSettings Queue { get; set; } = new();
    public ReplySettings Reply { get; set; } = new();
    public EmergencySettings Emergency { get; set; } = new();
    public AdminSettings Admin { get; set; } = new();
    public AdminTunnelSettings AdminTunnel { get; set; } = new();
    public WelcomeSettings Welcome { get; set; } = new();
    public BanVoteSettings BanVote { get; set; } = new();
    public GiftSettings Gift { get; set; } = new();
    public UserLevelSettings UserLevel { get; set; } = new();
    public KeywordReplySettings KeywordReply { get; set; } = new();
    public SongRequestPolicySettings SongRequestPolicy { get; set; } = new();
    public CleanupSettings Cleanup { get; set; } = new();
    public AiSpeechSettings AiSpeech { get; set; } = new();
    public MovieInteractionSettings MovieInteraction { get; set; } = new();
}

/// <summary>
/// 电影互动评分：礼物→可评分积分→弹幕好评/差评→本地落库→可选同步服务器。
/// 与现有用户积分 / 点歌权限完全独立。
/// </summary>
public sealed class MovieInteractionSettings
{
    public bool Enabled { get; set; } = true;
    /// <summary>服务器根地址，勿写死域名；空则不同步。</summary>
    public string ServerBaseUrl { get; set; } = "";
    public string ApiToken { get; set; } = "";
    public bool SyncEnabled { get; set; }
    /// <summary>可评分积分有效期（秒），默认 3 分钟。</summary>
    public int CreditExpireSeconds { get; set; } = 180;
    /// <summary>过期状态清理间隔（秒）。</summary>
    public int CleanupIntervalSeconds { get; set; } = 45;
    /// <summary>1 钻对应多少电影评分积分。</summary>
    public int PointsPerDiamond { get; set; } = 10;
}

public sealed class AiSpeechSettings
{
    public bool Enabled { get; set; }
    /// <summary>测试模式：仍读真实弹幕，但按间隔单队列处理，便于首次直播观察。</summary>
    public bool TestMode { get; set; }
    public string OllamaUrl { get; set; } = "http://127.0.0.1:11434";
    /// <summary>互动模型名（Ollama）；默认推荐 qwen3:8b，可自由选择。</summary>
    public string Model { get; set; } = "qwen3:8b";
    public string TtsUrl { get; set; } = "http://127.0.0.1:9880";
    public string Voice { get; set; } = "my_voice";
    /// <summary>抖音 AI 口播输出设备；-1=系统默认。双开播时建议勿与快手共用 CABLE。</summary>
    public int OutputDeviceNumber { get; set; } = -1;
    public string OutputDeviceName { get; set; } = "";
    /// <summary>快手 AI 口播输出；null 表示与抖音 OutputDevice 相同（兼容旧配置）。</summary>
    public int? KuaishouOutputDeviceNumber { get; set; }
    /// <summary>快手 AI 口播设备名；null/空且 Number 未设时回退抖音设备。</summary>
    public string? KuaishouOutputDeviceName { get; set; }
    /// <summary>兼容旧字段；与 ReplyIntervalSeconds 同步。</summary>
    public int MinIntervalSeconds { get; set; } = 8;
    public int MaxQueueSize { get; set; } = 5;
    public int MaxReplyLength { get; set; } = 80;
    public int OllamaTimeoutSeconds { get; set; } = 45;
    public int TtsTimeoutSeconds { get; set; } = 30;
    public int MaxAgeSeconds { get; set; } = 30;
    /// <summary>弹幕评分阈值；低于此分不进入 AI。默认 1，过滤无加分的无意义短弹幕。</summary>
    public int ScoreThreshold { get; set; } = 1;
    /// <summary>兼容旧配置的回退人格；优先使用 Config/AiSpeech/personality.txt。</summary>
    public string SystemPrompt { get; set; } = "";

    // ---- V2 开关 ----
    public bool ReplyDanmaku { get; set; } = true;
    public bool ThankGift { get; set; } = true;
    public bool WelcomeUser { get; set; }
    public bool ThankLike { get; set; }
    public bool AutoRoomSummary { get; set; }
    /// <summary>点歌真实入队并扣积分成功后播报；默认关闭，兼容旧配置。</summary>
    public bool AnnounceSongRequest { get; set; }

    /// <summary>AI 语音用户音量百分比：叠加在自动归一化之后。100=归一化后原量，120≈+1.6dB。过高易软限幅导致黏糊。范围 50～200。</summary>
    public int VolumePercent { get; set; } = 120;

    /// <summary>弹幕回复最小间隔（秒）；与 MinIntervalSeconds 保持同步。</summary>
    public int ReplyIntervalSeconds { get; set; } = 8;
    public int GiftMergeSeconds { get; set; } = 3;
    public int WelcomeIntervalSeconds { get; set; } = 20;
    public int LikeIntervalSeconds { get; set; } = 60;
    public int SummaryIntervalSeconds { get; set; } = 60;
    public int GlobalMinGapSeconds { get; set; } = 2;
    public int WelcomeMaxNames { get; set; } = 3;

    /// <summary>上下文模式：auto / none / user / room。</summary>
    public string ContextMode { get; set; } = "auto";
    public int UserContextCount { get; set; } = 8;
    public int HostReplyContextCount { get; set; } = 4;
    public int RoomContextCount { get; set; } = 40;
    public int ContextTtlMinutes { get; set; } = 15;
    public int RoomWindowSeconds { get; set; } = 60;
    /// <summary>单用户上下文最大跟踪用户数；超限 LRU 淘汰，防止长直播内存增长。</summary>
    public int MaxTrackedUsers { get; set; } = 2000;
    /// <summary>同类型任务连续出队上限；达到后插入异类任务，避免礼物洪峰饿死弹幕。</summary>
    public int SameKindBurstLimit { get; set; } = 3;

    /// <summary>情感预设 id；auto 表示按事件类型选择。</summary>
    public string Emotion { get; set; } = "auto";
    /// <summary>TTS 语速；略低于 1.0 可减轻黏嘴（推荐 0.92～1.0）。</summary>
    public double Speed { get; set; } = 0.95;

    /// <summary>礼物感谢：ai / template。默认 template，口播更稳、更自然。</summary>
    public string GiftThankMode { get; set; } = "template";
    public string GiftThankTemplate { get; set; } = "感谢 {nickname} 送的 {giftName}，谢谢支持。";
    public int SummaryMinDanmaku { get; set; } = 8;

    /// <summary>可选：ollama.exe 路径；空则自动探测。</summary>
    public string OllamaExePath { get; set; } = "";
    /// <summary>可选：GPT-SoVITS 启动 bat；空则自动探测。</summary>
    public string TtsStartScriptPath { get; set; } = "";
    /// <summary>可选：GPT-SoVITS 工作目录（含 service/main.py）。</summary>
    public string TtsWorkingDirectory { get; set; } = "";
    /// <summary>可选：conda activate.bat 路径。</summary>
    public string CondaActivateBat { get; set; } = "";
    /// <summary>Conda 环境名，默认 GPTSoVits。</summary>
    public string CondaEnvName { get; set; } = "GPTSoVits";

    /// <summary>是否已单独配置快手 AI 输出（未配置则与抖音共用，可能串台）。</summary>
    [JsonIgnore]
    public bool HasSeparateKuaishouOutput =>
        KuaishouOutputDeviceNumber.HasValue
        || !string.IsNullOrWhiteSpace(KuaishouOutputDeviceName);

    /// <summary>按平台解析 AI 输出设备；快手未单独配置（字段皆未设）时回退抖音设备。</summary>
    public (string Name, int Number) ResolveOutputDevice(string? platform)
    {
        var isKs = string.Equals(platform?.Trim(), "kuaishou", StringComparison.OrdinalIgnoreCase)
                   || string.Equals(platform?.Trim(), "ks", StringComparison.OrdinalIgnoreCase);
        if (isKs && HasSeparateKuaishouOutput)
        {
            // Number 有值（含 -1=系统默认）或以非空名称单独配置时，都视为快手专用
            var name = KuaishouOutputDeviceName ?? "";
            var num = KuaishouOutputDeviceNumber ?? -1;
            return (name, num);
        }

        return (OutputDeviceName ?? "", OutputDeviceNumber);
    }
}

public sealed class DouyinSettings
{
    public string BaseUrl { get; set; } = "http://127.0.0.1:17891";
    public string ApiToken { get; set; } = "";
    public string WebRid { get; set; } = "";
    /// <summary>弹幕轮询间隔（毫秒）。越小越快，建议 500～1000，过低可能增加侧车压力。</summary>
    public int PollIntervalMs { get; set; } = 800;
    /// <summary>CDP 程序路径。空则按 cdp-danmaku.exe / sidecars/douyin-cdp 查找。</summary>
    public string CdpExePath { get; set; } = "";
    public string DouyinExePath { get; set; } = "cdp-danmaku.exe";
    /// <summary>Sidecar cookies.json 路径；空则按 exe 旁 data/cookies.json 推断。</summary>
    public string CookieStorePath { get; set; } = "";
}

public sealed class KuaishouSettings
{
    /// <summary>是否启用快手点歌通道。</summary>
    public bool Enabled { get; set; }
    public string BaseUrl { get; set; } = "http://127.0.0.1:18900";
    public string RoomId { get; set; } = "";
    /// <summary>live.kuaishou.com 登录 Cookie。</summary>
    public string Cookie { get; set; } = "";
    public int PollIntervalMs { get; set; } = 800;

    /// <summary>最近一次保存 Cookie 的 UTC 时间（Ticks）。</summary>
    public long CookieSavedAtUtcTicks { get; set; }

    /// <summary>从 Cookie 解析出的最早过期点（Ticks）；0=未知。</summary>
    public long CookieExpiresAtUtcTicks { get; set; }

    /// <summary>连续连接失败次数（成功后清零）。</summary>
    public int ConnectFailureStreak { get; set; }
}

public sealed class KugouSettings
{
    public string BaseUrl { get; set; } = "http://127.0.0.1:17888";
    public string ApiKey { get; set; } = "";
    public string KugouExePath { get; set; } = "";

    /// <summary>已登录时拒绝 1 分钟试听链，换歌或提示重新登录。</summary>
    public bool RequireFullPlayback { get; set; } = true;

    /// <summary>每日自动领取概念版试用会员（对齐 MoeKoeMusic）。</summary>
    public bool AutoClaimVip { get; set; } = true;
}

public sealed class PlaybackSettings
{
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public PlaybackMode Mode { get; set; } = PlaybackMode.RequestWithRandomFill;

    public int Volume { get; set; } = 80;
    public bool AutoSkipOnError { get; set; } = true;
    public bool RandomFillEnabled { get; set; } = true;

    /// <summary>歌曲播放输出设备号；-1=系统默认。多平台直播时选 VB-CABLE 的 CABLE Input。</summary>
    public int OutputDeviceNumber { get; set; } = -1;

    /// <summary>按名称优先匹配（设备热插拔后编号可能变）。</summary>
    public string OutputDeviceName { get; set; } = "";
}

public sealed class RandomPlaylistItem
{
    public string Keyword { get; set; } = "";
    public string? SongId { get; set; }
    public string? Hash { get; set; }
    public string? Title { get; set; }
    public string? Artist { get; set; }
}

public sealed class RandomPlaylistSettings
{
    /// <summary>kugou=酷狗每日推荐随机播放；fixed=使用 items / 后台随机池。</summary>
    public string Source { get; set; } = "kugou";

    public int NoRepeatMinutes { get; set; } = 30;
    public bool Shuffle { get; set; } = true;
    public List<RandomPlaylistItem> Items { get; set; } = new();
}

public sealed class UiSettings
{
    public int MaxDanmakuLines { get; set; } = 100;
    public int MaxSystemMessageLines { get; set; } = 200;
}

public sealed class QueueSettings
{
    public int MaxSize { get; set; } = 50;
    public int RequestCooldownSeconds { get; set; } = 30;
    public int SongRequestCooldownSeconds { get; set; } = 30;
}

public sealed class ReplySettings
{
    public int MaxPerSecond { get; set; } = 2;
    public int MaxRetries { get; set; } = 3;
    public int RetryDelayMs { get; set; } = 1500;
    public int SongRequestBatchWindowMs { get; set; } = 5000;
}

public sealed class EmergencySettings
{
    public bool PauseInteraction { get; set; }
    public bool PauseSongRequest { get; set; }
}

public sealed class AdminAccount
{
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
}

public sealed class AdminSettings
{
    public bool Enabled { get; set; } = true;
    public int Port { get; set; } = 5088;
    public string Path { get; set; } = "/diangexitong";
    /// <summary>已废弃：请用 Accounts。保留仅为兼容旧配置。</summary>
    public string Username { get; set; } = "";
    /// <summary>已废弃：请用 Accounts。</summary>
    public string Password { get; set; } = "";
    public string? PasswordEnvVar { get; set; }
    public List<AdminAccount> Accounts { get; set; } = new();
    /// <summary>云端 nginx 反代到本机时携带的隧道密钥。</summary>
    public string TunnelSecret { get; set; } = "la-tunnel-9f3c2e8b7a1d4c6e";

    public string ResolvePassword()
    {
        if (!string.IsNullOrWhiteSpace(PasswordEnvVar))
        {
            var fromEnv = Environment.GetEnvironmentVariable(PasswordEnvVar);
            if (!string.IsNullOrWhiteSpace(fromEnv))
            {
                return fromEnv;
            }
        }
        return Password;
    }

    public bool ValidateCredentials(string? username, string? password)
    {
        if (string.IsNullOrWhiteSpace(username) || password is null)
        {
            return false;
        }

        foreach (var account in Accounts)
        {
            if (string.IsNullOrWhiteSpace(account.Username))
            {
                continue;
            }

            if (string.Equals(account.Username, username, StringComparison.Ordinal)
                && account.Password == password)
            {
                return true;
            }
        }

        // 兼容旧单账号配置
        if (!string.IsNullOrWhiteSpace(Username))
        {
            var legacyPassword = ResolvePassword();
            if (!string.IsNullOrEmpty(legacyPassword)
                && string.Equals(Username, username, StringComparison.Ordinal)
                && legacyPassword == password)
            {
                return true;
            }
        }

        return false;
    }

    public bool HasAnyPasswordConfigured()
    {
        if (Accounts.Any(a => !string.IsNullOrWhiteSpace(a.Username) && a.Password != null))
        {
            return true;
        }

        return !string.IsNullOrWhiteSpace(Username) && !string.IsNullOrEmpty(ResolvePassword());
    }
}

/// <summary>
/// 软件启动后自动 SSH 反向隧道到云服务器（remotePort -> 本机 Admin.Port）。
/// 密码优先从 DeployCredentials.local.json 读取，勿写入已跟踪的配置文件。
/// </summary>
public sealed class AdminTunnelSettings
{
    public bool Enabled { get; set; } = true;
    public string Host { get; set; } = "";
    public string User { get; set; } = "root";
    public string Password { get; set; } = "";
    public string CredentialsFile { get; set; } = "DeployCredentials.local.json";
    public int RemotePort { get; set; } = 15088;
    /// <summary>0 表示使用 Admin.Port。</summary>
    public int LocalPort { get; set; }
    public int ReconnectDelayMs { get; set; } = 5000;
}

public sealed class WelcomeSettings
{
    public bool Enabled { get; set; } = true;
    public int CooldownSeconds { get; set; } = 300;
    public string Template { get; set; } = "欢迎{name}来到直播间 ❤️";
}

public sealed class BanVoteSettings
{
    public bool Enabled { get; set; } = true;
    public int RequiredVotes { get; set; } = 5;
    public int WindowSeconds { get; set; } = 60;
    public int BanDurationSeconds { get; set; } = 600;
}

public sealed class GiftSettings
{
    public int PointsPerValue { get; set; } = 1;
    public int PollIntervalMs { get; set; } = 2000;
    /// <summary>兼容旧配置；优先使用 IdleImFetchIntervalMs。</summary>
    public int ImFetchIntervalMs { get; set; } = 3000;
    /// <summary>无礼物时的 im/fetch 间隔（毫秒）。</summary>
    public int IdleImFetchIntervalMs { get; set; } = 3000;
    /// <summary>刚收到礼物后的快速拉取间隔（毫秒）。</summary>
    public int ActiveImFetchIntervalMs { get; set; } = 800;
    /// <summary>连击超时自动结算（秒）。</summary>
    public int ComboTimeoutSeconds { get; set; } = 10;
    /// <summary>断线/Cookie 失效后的重连退避基数（毫秒）。</summary>
    public int ReconnectDelayMs { get; set; } = 3000;
    /// <summary>单次礼物最大件数（异常保护）。</summary>
    public int MaxGiftCount { get; set; } = 10000;
    /// <summary>单次礼物最大总钻石价值（异常保护）。</summary>
    public int MaxGiftValue { get; set; } = 5_000_000;
    /// <summary>单次礼物最大积分增量（异常保护）。</summary>
    public int MaxPointsDelta { get; set; } = 5_000_000;
}

public sealed class UserLevelSettings
{
    public List<int> Thresholds { get; set; } = new() { 0, 100, 500, 2000, 10000 };
}

public sealed class KeywordReplySettings
{
    public bool Enabled { get; set; }
}

public sealed class CleanupSettings
{
    public bool Enabled { get; set; } = true;
    public int IntervalHours { get; set; } = 24;
    public int QueueRetentionDays { get; set; } = 7;
    public int GiftRetentionDays { get; set; } = 30;
    /// <summary>电影互动事件流保留天数（不影响评分账本）。</summary>
    public int MovieInteractionStreamRetentionDays { get; set; } = 7;
}

public sealed class ConfigManager
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _configDir;
    private readonly string _dataDir;

    public AppSettings Settings { get; private set; } = new();
    public Dictionary<string, string> ReplyTemplates { get; private set; } = new();

    public ConfigManager()
    {
        _configDir = AppPaths.ConfigDirectory;
        _dataDir = AppPaths.ResolveDataDirectory();
        Directory.CreateDirectory(_dataDir);
    }

    public string DataDirectory => _dataDir;

    public void Load()
    {
        AppSettings? fileSettings = null;
        var appPath = Path.Combine(_configDir, "appsettings.json");
        fileSettings = TryLoadAppSettingsFile(appPath, "Config/appsettings.json");
        if (fileSettings != null)
        {
            Settings = fileSettings;
        }

        var dataAppPath = Path.Combine(_dataDir, "appsettings.json");
        var dataSettings = TryLoadAppSettingsFile(dataAppPath, "data/appsettings.json");
        if (dataSettings != null)
        {
            // 数据目录配置会整表覆盖；补回文件里的后台账号/云端隧道，避免被旧缓存清空
            PreserveAdminRuntimeSettings(fileSettings, dataSettings);
            Settings = dataSettings;
        }

        ApplyTunnelCredentialsFile();
        ResolveSidecarPaths();
        MigrateRequireConfirmDefault();
        MigrateAiSpeechDefaults();

        ReplyTemplates = TryLoadTemplatesFile(Path.Combine(_configDir, "ReplyTemplates.json")) ?? ReplyTemplates;
        ReplyTemplates = TryLoadTemplatesFile(Path.Combine(_dataDir, "ReplyTemplates.json")) ?? ReplyTemplates;
    }

    /// <summary>
    /// 空模型补默认推荐；评分阈值校正；同步间隔字段；确保提示词默认文件。
    /// </summary>
    private void MigrateAiSpeechDefaults()
    {
        var ai = Settings.AiSpeech;
        var changed = false;
        if (string.IsNullOrWhiteSpace(ai.Model))
        {
            ai.Model = "qwen3:8b";
            changed = true;
        }

        if (ai.ScoreThreshold < 0 || ai.ScoreThreshold > 20)
        {
            ai.ScoreThreshold = 1;
            changed = true;
        }

        // ReplyIntervalSeconds ↔ MinIntervalSeconds 兼容同步
        if (ai.ReplyIntervalSeconds <= 0 && ai.MinIntervalSeconds > 0)
        {
            ai.ReplyIntervalSeconds = ai.MinIntervalSeconds;
            changed = true;
        }
        else if (ai.MinIntervalSeconds <= 0 && ai.ReplyIntervalSeconds > 0)
        {
            ai.MinIntervalSeconds = ai.ReplyIntervalSeconds;
            changed = true;
        }
        else if (ai.ReplyIntervalSeconds > 0 && ai.MinIntervalSeconds > 0
                 && ai.ReplyIntervalSeconds != ai.MinIntervalSeconds)
        {
            // 优先新字段
            ai.MinIntervalSeconds = ai.ReplyIntervalSeconds;
            changed = true;
        }

        if (string.IsNullOrWhiteSpace(ai.ContextMode))
        {
            ai.ContextMode = "auto";
            changed = true;
        }

        if (string.IsNullOrWhiteSpace(ai.Emotion))
        {
            ai.Emotion = "auto";
            changed = true;
        }

        if (string.IsNullOrWhiteSpace(ai.GiftThankMode))
        {
            ai.GiftThankMode = "ai";
            changed = true;
        }

        if (string.IsNullOrWhiteSpace(ai.GiftThankTemplate))
        {
            ai.GiftThankTemplate = "感谢 {nickname} 送的 {giftName}，谢谢支持。";
            changed = true;
        }

        if (ai.Speed <= 0 || ai.Speed > 3)
        {
            ai.Speed = 1.0;
            changed = true;
        }

        if (ai.VolumePercent < 50 || ai.VolumePercent > 200)
        {
            ai.VolumePercent = 120;
            changed = true;
        }

        try
        {
            Services.AiSpeech.AiPromptStore.EnsureDefaults();
            Services.AiSpeech.AiPersonalityLoader.EnsureDefaultFileExists();
        }
        catch
        {
            // ignore
        }

        if (!changed)
        {
            return;
        }

        try
        {
            Save();
            StartupDiagnostics.Write($"已补齐 AI 语音默认配置 model={ai.Model} scoreThreshold={ai.ScoreThreshold}");
        }
        catch (Exception ex)
        {
            StartupDiagnostics.Write($"纠正 AI 语音默认配置失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 点歌必须确认后才入队。旧 data 配置或后台误存 false 时，每次启动纠正并落盘。
    /// </summary>
    private void MigrateRequireConfirmDefault()
    {
        if (Settings.SongRequestPolicy.RequireConfirm)
        {
            return;
        }

        Settings.SongRequestPolicy.RequireConfirm = true;
        try
        {
            Save();
            StartupDiagnostics.Write("已强制开启 requireConfirm（点歌需回复「确定」才入队）");
        }
        catch (Exception ex)
        {
            StartupDiagnostics.Write($"纠正 requireConfirm 保存失败: {ex.Message}");
        }
    }

    private AppSettings? TryLoadAppSettingsFile(string path, string label)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        var json = ReadConfigJson(path, label);
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
        }
        catch (Exception ex)
        {
            StartupDiagnostics.Write($"配置解析失败 {label}: {ex.Message}");
            return null;
        }
    }

    private Dictionary<string, string>? TryLoadTemplatesFile(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        var json = ReadConfigJson(path, path);
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(json, JsonOptions);
        }
        catch (Exception ex)
        {
            StartupDiagnostics.Write($"模板解析失败 {path}: {ex.Message}");
            return null;
        }
    }

    private static string ReadConfigJson(string path, string label)
    {
        if (AtomicFileWriter.IsCorruptOrEmpty(path))
        {
            StartupDiagnostics.Write($"检测到损坏配置 {label}，尝试恢复");
            var recovered = AtomicFileWriter.RecoverCorruptFile(path, msg => StartupDiagnostics.Write(msg));
            return recovered ?? "";
        }

        try
        {
            return File.ReadAllText(path);
        }
        catch (Exception ex)
        {
            StartupDiagnostics.Write($"读取配置失败 {label}: {ex.Message}");
            AtomicFileWriter.RecoverCorruptFile(path, msg => StartupDiagnostics.Write(msg));
            return "";
        }
    }

    private static void PreserveAdminRuntimeSettings(AppSettings? fileSettings, AppSettings dataSettings)
    {
        if (fileSettings == null)
        {
            return;
        }

        // 安装包内的账号/隧道密钥优先，避免 LocalAppData 旧缓存把新电脑配置冲掉
        if (fileSettings.Admin.Accounts is { Count: > 0 })
        {
            dataSettings.Admin.Accounts = fileSettings.Admin.Accounts;
        }

        if (!string.IsNullOrWhiteSpace(fileSettings.Admin.TunnelSecret))
        {
            dataSettings.Admin.TunnelSecret = fileSettings.Admin.TunnelSecret;
        }

        if (!string.IsNullOrWhiteSpace(fileSettings.AdminTunnel.Host))
        {
            dataSettings.AdminTunnel.Host = fileSettings.AdminTunnel.Host;
        }

        if (!string.IsNullOrWhiteSpace(fileSettings.AdminTunnel.Password))
        {
            dataSettings.AdminTunnel.Password = fileSettings.AdminTunnel.Password;
            if (!string.IsNullOrWhiteSpace(fileSettings.AdminTunnel.User))
            {
                dataSettings.AdminTunnel.User = fileSettings.AdminTunnel.User;
            }

            if (fileSettings.AdminTunnel.RemotePort > 0)
            {
                dataSettings.AdminTunnel.RemotePort = fileSettings.AdminTunnel.RemotePort;
            }

            if (fileSettings.AdminTunnel.LocalPort > 0)
            {
                dataSettings.AdminTunnel.LocalPort = fileSettings.AdminTunnel.LocalPort;
            }

            dataSettings.AdminTunnel.Enabled = fileSettings.AdminTunnel.Enabled;
        }
    }

    /// <summary>
    /// 从 DeployCredentials.local.json 填充云端隧道（有文件则补齐空字段）。
    /// </summary>
    private void ApplyTunnelCredentialsFile()
    {
        foreach (var path in new[]
                 {
                     Path.Combine(_configDir, "DeployCredentials.local.json"),
                     Path.Combine(_dataDir, "DeployCredentials.local.json"),
                     Path.Combine(AppPaths.ExeDirectory, "Config", "DeployCredentials.local.json")
                 })
        {
            try
            {
                if (!File.Exists(path))
                {
                    continue;
                }

                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                if (!doc.RootElement.TryGetProperty("server", out var server))
                {
                    continue;
                }

                if (string.IsNullOrWhiteSpace(Settings.AdminTunnel.Host)
                    && server.TryGetProperty("host", out var host))
                {
                    Settings.AdminTunnel.Host = host.GetString() ?? "";
                }

                if (string.IsNullOrWhiteSpace(Settings.AdminTunnel.User)
                    && server.TryGetProperty("user", out var user))
                {
                    Settings.AdminTunnel.User = user.GetString() ?? "root";
                }

                if (string.IsNullOrWhiteSpace(Settings.AdminTunnel.Password)
                    && server.TryGetProperty("password", out var password))
                {
                    Settings.AdminTunnel.Password = password.GetString() ?? "";
                }

                if (Settings.AdminTunnel.RemotePort <= 0
                    && server.TryGetProperty("adminTunnelPort", out var remotePort)
                    && remotePort.TryGetInt32(out var rp) && rp > 0)
                {
                    Settings.AdminTunnel.RemotePort = rp;
                }

                if (Settings.AdminTunnel.LocalPort <= 0
                    && server.TryGetProperty("adminLocalPort", out var localPort)
                    && localPort.TryGetInt32(out var lp) && lp > 0)
                {
                    Settings.AdminTunnel.LocalPort = lp;
                }

                Settings.AdminTunnel.Enabled = true;
                if (!string.IsNullOrWhiteSpace(Settings.AdminTunnel.Host)
                    && !string.IsNullOrWhiteSpace(Settings.AdminTunnel.Password))
                {
                    return;
                }
            }
            catch
            {
                // ignore bad credential files
            }
        }
    }

    /// <summary>
    /// 同目录 sidecar 优先于配置里的绝对路径，避免拷到一起仍去找旧目录。
    /// </summary>
    private void ResolveSidecarPaths()
    {
        var configured = string.IsNullOrWhiteSpace(Settings.Douyin.CdpExePath)
            ? Settings.Douyin.DouyinExePath
            : Settings.Douyin.CdpExePath;
        Settings.Douyin.DouyinExePath = global::LiveAssistant.SidecarLocator.ResolveDouyin(configured);
        Settings.Kugou.KugouExePath = global::LiveAssistant.SidecarLocator.ResolveKugou(Settings.Kugou.KugouExePath);
    }

    public void Save()
    {
        Directory.CreateDirectory(_dataDir);
        var json = JsonSerializer.Serialize(Settings, JsonOptions);
        AtomicFileWriter.WriteAllText(Path.Combine(_dataDir, "appsettings.json"), json);

        var tplJson = JsonSerializer.Serialize(ReplyTemplates, JsonOptions);
        AtomicFileWriter.WriteAllText(Path.Combine(_dataDir, "ReplyTemplates.json"), tplJson);
    }

    public void SetReplyTemplates(Dictionary<string, string> templates)
    {
        ReplyTemplates = templates;
        Save();
    }

    public void ApplySettings(AppSettings settings)
    {
        Settings = settings;
        Save();
    }

    public void ApplyReplyTemplates(Dictionary<string, string> templates)
    {
        ReplyTemplates = templates;
    }
}
