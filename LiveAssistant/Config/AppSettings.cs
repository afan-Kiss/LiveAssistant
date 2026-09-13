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
    public EmergencySettings Emergency { get; set; } = new();
}

public sealed class DouyinSettings
{
    public string BaseUrl { get; set; } = "http://127.0.0.1:4723";
    public string ApiToken { get; set; } = "";
    public string WebRid { get; set; } = "";
    public int PollIntervalMs { get; set; } = 1500;
    public string DouyinExePath { get; set; } = "";
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
}

public sealed class EmergencySettings
{
    public bool PauseInteraction { get; set; }
    public bool PauseSongRequest { get; set; }
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
}
