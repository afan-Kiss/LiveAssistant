using System.Text.Json;
using System.Text.Json.Serialization;
using LiveAssistant.Models;
using LiveAssistant.Utils;

namespace LiveAssistant.Config;

public sealed class AppSettings
{
    public DouyinSettings Douyin { get; set; } = new();
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
}

public sealed class DouyinSettings
{
    public string BaseUrl { get; set; } = "http://127.0.0.1:4723";
    public string ApiToken { get; set; } = "";
    public string WebRid { get; set; } = "";
    /// <summary>弹幕轮询间隔（毫秒）。越小越快，建议 500～1000，过低可能增加侧车压力。</summary>
    public int PollIntervalMs { get; set; } = 800;
    public string DouyinExePath { get; set; } = "";
    /// <summary>Sidecar cookies.json 路径；空则按 exe 旁 data/cookies.json 推断。</summary>
    public string CookieStorePath { get; set; } = "";
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
    /// <summary>kugou=酷狗每日推荐顺序播放；fixed=使用 items / 后台随机池。</summary>
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

        ReplyTemplates = TryLoadTemplatesFile(Path.Combine(_configDir, "ReplyTemplates.json")) ?? ReplyTemplates;
        ReplyTemplates = TryLoadTemplatesFile(Path.Combine(_dataDir, "ReplyTemplates.json")) ?? ReplyTemplates;
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
        Settings.Douyin.DouyinExePath = global::LiveAssistant.SidecarLocator.ResolveDouyin(Settings.Douyin.DouyinExePath);
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
