using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LiveAssistant.Config;
using LiveAssistant.Database;
using LiveAssistant.Models;

namespace LiveAssistant.Services;

/// <summary>
/// 配置中心：模板/随机池/礼物规则等存 SQLite，客户端启动与后台同步时应用。
/// </summary>
public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    private readonly ConfigManager _config;
    private readonly ReplyTemplateRepository _templates;
    private readonly RandomPoolRepository _randomPool;
    private readonly GiftRuleRepository _giftRules;
    private readonly LevelPermissionRepository _levelPerms;
    private readonly SyncCacheRepository _cache;

    public SettingsStore(
        ConfigManager config,
        ReplyTemplateRepository templates,
        RandomPoolRepository randomPool,
        GiftRuleRepository giftRules,
        LevelPermissionRepository levelPerms,
        SyncCacheRepository cache)
    {
        _config = config;
        _templates = templates;
        _randomPool = randomPool;
        _giftRules = giftRules;
        _levelPerms = levelPerms;
        _cache = cache;
    }

    public void InitializeFromFilesIfEmpty()
    {
        if (_templates.Count() == 0 && _config.ReplyTemplates.Count > 0)
        {
            _templates.SaveAll(_config.ReplyTemplates);
        }

        if (_randomPool.ListAll().Count == 0 && _config.Settings.RandomPlaylist.Items.Count > 0)
        {
            foreach (var item in _config.Settings.RandomPlaylist.Items)
            {
                _randomPool.Add(new RandomPoolItem
                {
                    SongName = item.Title ?? item.Keyword,
                    Artist = item.Artist ?? "",
                    SongId = item.SongId ?? "",
                    Hash = item.Hash ?? "",
                    Keyword = item.Keyword,
                    Enabled = true
                });
            }
        }

        SeedDefaultGiftRules();
        SeedDefaultLevelPermissions();
    }

    private void SeedDefaultGiftRules()
    {
        if (_giftRules.ListAll().Count > 0)
        {
            return;
        }

        foreach (var (name, pts) in new[] { ("小心心", 1), ("玫瑰", 5), ("嘉年华", 5000) })
        {
            _giftRules.Add(new GiftRule { GiftName = name, Points = pts, Enabled = true });
        }
    }

    private void SeedDefaultLevelPermissions()
    {
        if (_levelPerms.ListAll().Count > 0)
        {
            return;
        }

        _levelPerms.SaveAll(new[]
        {
            new LevelPermission { Level = 0, CanRequest = true, CooldownSeconds = 30 },
            new LevelPermission { Level = 1, CanRequest = true, CooldownSeconds = 20 },
            new LevelPermission { Level = 2, CanRequest = true, CooldownSeconds = 10 },
            new LevelPermission { Level = 3, CanRequest = true, CooldownSeconds = 0 }
        });
    }

    public SyncBundle BuildBundle()
    {
        ApplyDbToMemory();
        return new SyncBundle
        {
            Version = ComputeVersion(),
            Settings = _config.Settings,
            ReplyTemplates = _templates.GetAll(),
            RandomPool = _randomPool.ListAll(),
            GiftRules = _giftRules.ListAll(),
            SongRequestPolicy = _config.Settings.SongRequestPolicy,
            LevelPermissions = _levelPerms.ListAll()
        };
    }

    public void ApplyBundle(SyncBundle bundle, bool persistCache = true)
    {
        _config.ApplySettings(bundle.Settings);
        _config.ApplyReplyTemplates(bundle.ReplyTemplates);
        _templates.SaveAll(bundle.ReplyTemplates);
        _config.Settings.SongRequestPolicy = bundle.SongRequestPolicy;

        PersistRandomPool(bundle.RandomPool);
        PersistGiftRules(bundle.GiftRules);
        _levelPerms.SaveAll(bundle.LevelPermissions);
        SyncRandomPoolToConfig();
        _config.Save();

        if (persistCache)
        {
            _cache.Set("sync_bundle", JsonSerializer.Serialize(bundle, JsonOptions));
            _cache.Set("sync_version", bundle.Version);
        }
    }

    public bool TryLoadCachedBundle()
    {
        var json = _cache.Get("sync_bundle");
        if (string.IsNullOrWhiteSpace(json))
        {
            ApplyDbToMemory();
            return false;
        }

        var bundle = JsonSerializer.Deserialize<SyncBundle>(json, JsonOptions);
        if (bundle == null)
        {
            ApplyDbToMemory();
            return false;
        }

        ApplyBundle(bundle, persistCache: false);
        return true;
    }

    public void ApplyDbToMemory()
    {
        var tpl = _templates.GetAll();
        if (tpl.Count > 0)
        {
            _config.ApplyReplyTemplates(tpl);
        }
        SyncRandomPoolToConfig();
    }

    public string ComputeVersion()
    {
        var bundle = new SyncBundle
        {
            Settings = _config.Settings,
            ReplyTemplates = _templates.GetAll(),
            RandomPool = _randomPool.ListAll(),
            GiftRules = _giftRules.ListAll(),
            SongRequestPolicy = _config.Settings.SongRequestPolicy,
            LevelPermissions = _levelPerms.ListAll()
        };
        var json = JsonSerializer.Serialize(bundle, JsonOptions);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)))[..16];
    }

    private void SyncRandomPoolToConfig()
    {
        var enabled = _randomPool.ListEnabled();
        _config.Settings.RandomPlaylist.Items = enabled.Select(x => new RandomPlaylistItem
        {
            Keyword = !string.IsNullOrWhiteSpace(x.Keyword) ? x.Keyword : x.SongName,
            Title = x.SongName,
            Artist = x.Artist,
            SongId = x.SongId,
            Hash = x.Hash
        }).ToList();
    }

    private void PersistRandomPool(List<RandomPoolItem> items)
    {
        var repo = _randomPool;
        var existing = repo.ListAll();
        foreach (var e in existing)
        {
            repo.Remove(e.Id);
        }
        foreach (var item in items)
        {
            repo.Add(item);
        }
    }

    private void PersistGiftRules(List<GiftRule> rules)
    {
        var existing = _giftRules.ListAll();
        foreach (var e in existing)
        {
            _giftRules.Remove(e.Id);
        }
        foreach (var rule in rules)
        {
            _giftRules.Add(rule);
        }
    }
}
