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
    private readonly KeywordReplyRepository _keywordReplies;
    private readonly SyncCacheRepository _cache;

    public SettingsStore(
        ConfigManager config,
        ReplyTemplateRepository templates,
        RandomPoolRepository randomPool,
        GiftRuleRepository giftRules,
        LevelPermissionRepository levelPerms,
        KeywordReplyRepository keywordReplies,
        SyncCacheRepository cache)
    {
        _config = config;
        _templates = templates;
        _randomPool = randomPool;
        _giftRules = giftRules;
        _levelPerms = levelPerms;
        _keywordReplies = keywordReplies;
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

        foreach (var (name, pts, perm) in new[] { ("小心心", 1, 1), ("玫瑰", 5, 0), ("嘉年华", 5000, -1) })
        {
            _giftRules.Add(new GiftRule
            {
                GiftName = name,
                Points = pts,
                SongPermissionCount = perm,
                Enabled = true,
                AllowSongRequest = perm != 0
            });
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
            new LevelPermission { Level = 0, CanRequest = true, CooldownSeconds = 30, QueuePriority = 0 },
            new LevelPermission { Level = 1, CanRequest = true, CooldownSeconds = 20, QueuePriority = 5 },
            new LevelPermission { Level = 2, CanRequest = true, CooldownSeconds = 10, QueuePriority = 10 },
            new LevelPermission { Level = 3, CanRequest = true, CooldownSeconds = 0, QueuePriority = 20 }
        });
    }

    public SyncBundle BuildBundle()
    {
        ApplyDbToMemory();
        var bundle = CreateBundleSnapshot();
        bundle.Version = ComputeVersion();
        return bundle;
    }

    public void ApplyBundle(SyncBundle bundle, bool persistCache = true)
    {
        _config.ApplySettings(bundle.Settings);
        _config.ApplyReplyTemplates(bundle.ReplyTemplates);
        _templates.SaveAll(bundle.ReplyTemplates);
        _config.Settings.SongRequestPolicy = bundle.SongRequestPolicy;
        _config.Settings.Welcome = bundle.Welcome;
        _config.Settings.BanVote = bundle.BanVote;
        _config.Settings.Cleanup = bundle.Cleanup;
        _config.Settings.KeywordReply.Enabled = bundle.Settings.KeywordReply.Enabled;

        PersistRandomPool(bundle.RandomPool);
        PersistGiftRules(bundle.GiftRules);
        _levelPerms.SaveAll(bundle.LevelPermissions);
        PersistKeywordReplies(bundle.KeywordReplies);
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
        var json = JsonSerializer.Serialize(CreateBundleSnapshot(), JsonOptions);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)))[..16];
    }

    private SyncBundle CreateBundleSnapshot() => new()
    {
        Version = "",
        Settings = _config.Settings,
        ReplyTemplates = _templates.GetAll(),
        RandomPool = _randomPool.ListAll(),
        GiftRules = _giftRules.ListAll(),
        SongRequestPolicy = _config.Settings.SongRequestPolicy,
        LevelPermissions = _levelPerms.ListAll(),
        KeywordReplies = _keywordReplies.ListAll(),
        Welcome = _config.Settings.Welcome,
        BanVote = _config.Settings.BanVote,
        Cleanup = _config.Settings.Cleanup
    };

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
        var existing = _randomPool.ListAll();
        foreach (var e in existing)
        {
            _randomPool.Remove(e.Id);
        }
        foreach (var item in items)
        {
            _randomPool.Add(item);
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

    private void PersistKeywordReplies(List<KeywordReplyRule> rules)
    {
        var existing = _keywordReplies.ListAll();
        foreach (var e in existing)
        {
            _keywordReplies.Remove(e.Id);
        }
        foreach (var rule in rules)
        {
            _keywordReplies.Add(rule);
        }
    }
}
