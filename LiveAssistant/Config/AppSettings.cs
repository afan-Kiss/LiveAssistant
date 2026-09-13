using System.Text.Json;
using System.Text.Json.Serialization;
using LiveAssistant.Models;

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
    public int PollIntervalMs { get; set; } = 1500;
    public string DouyinExePath { get; set; } = "";
    /// <summary>Sidecar cookies.json 路径；空则按 exe 旁 data/cookies.json 推断。</summary>
    public string CookieStorePath { get; set; } = "";
}

public sealed class KugouSettings
{
    public string BaseUrl { get; set; } = "http://127.0.0.1:17888";
    public string ApiKey { get; set; } = "";
    public string KugouExePath { get; set; } = "";
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

public sealed class AdminSettings
{
    public bool Enabled { get; set; } = true;
    public int Port { get; set; } = 5088;
    public string Path { get; set; } = "/diangexitong";
    public string Username { get; set; } = "admin";
    public string Password { get; set; } = "";
    public string? PasswordEnvVar { get; set; } = "LIVEASSISTANT_ADMIN_PASSWORD";

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
    /// <summary>礼物 im/fetch 轮询间隔（毫秒）。</summary>
    public int ImFetchIntervalMs { get; set; } = 10000;
    /// <summary>连击超时自动结算（秒）。</summary>
    public int ComboTimeoutSeconds { get; set; } = 10;
    /// <summary>断线/Cookie 失效后的重连退避基数（毫秒）。</summary>
    public int ReconnectDelayMs { get; set; } = 3000;
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
        var baseDir = AppContext.BaseDirectory;
        _configDir = Path.Combine(baseDir, "Config");
        _dataDir = Path.Combine(Path.GetDirectoryName(baseDir.TrimEnd(Path.DirectorySeparatorChar)) ?? baseDir, "..", "data");
        _dataDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "data"));
        if (!Directory.Exists(_dataDir))
        {
            _dataDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "data"));
        }
        if (!Directory.Exists(_dataDir))
        {
            _dataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LiveAssistant", "data");
        }
        Directory.CreateDirectory(_dataDir);
    }

    public string DataDirectory => _dataDir;

    public void Load()
    {
        var appPath = Path.Combine(_configDir, "appsettings.json");
        if (File.Exists(appPath))
        {
            var json = File.ReadAllText(appPath);
            Settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
        }

        var dataAppPath = Path.Combine(_dataDir, "appsettings.json");
        if (File.Exists(dataAppPath))
        {
            var json = File.ReadAllText(dataAppPath);
            var dataSettings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
            if (dataSettings != null)
            {
                Settings = dataSettings;
            }
        }

        var tplPath = Path.Combine(_configDir, "ReplyTemplates.json");
        if (File.Exists(tplPath))
        {
            var json = File.ReadAllText(tplPath);
            ReplyTemplates = JsonSerializer.Deserialize<Dictionary<string, string>>(json, JsonOptions) ?? new();
        }

        var dataTplPath = Path.Combine(_dataDir, "ReplyTemplates.json");
        if (File.Exists(dataTplPath))
        {
            var json = File.ReadAllText(dataTplPath);
            ReplyTemplates = JsonSerializer.Deserialize<Dictionary<string, string>>(json, JsonOptions) ?? ReplyTemplates;
        }
    }

    public void Save()
    {
        Directory.CreateDirectory(_dataDir);
        var json = JsonSerializer.Serialize(Settings, JsonOptions);
        File.WriteAllText(Path.Combine(_dataDir, "appsettings.json"), json);

        var tplJson = JsonSerializer.Serialize(ReplyTemplates, JsonOptions);
        File.WriteAllText(Path.Combine(_dataDir, "ReplyTemplates.json"), tplJson);
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
