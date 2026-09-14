using LiveAssistant.Config;
using LiveAssistant.Models;
using LiveAssistant.Utils;

namespace LiveAssistant.Services;

public sealed class KugouService
{
    private readonly KugouSettings _settings;
    private readonly LogService _log;
    private readonly HttpClient _client;
    private DateTime _loginCheckedAt = DateTime.MinValue;
    private bool _lastLoggedIn;
    private KugouLoginSnapshot _loginSnapshot = new();
    private string? _lastVipClaimDate;

    public KugouService(KugouSettings settings, LogService log, HttpClient? client = null)
    {
        _settings = settings;
        _log = log;
        _client = client ?? HttpJson.CreateClient(settings.BaseUrl, settings.ApiKey, "X-API-Key");
        _client.Timeout = TimeSpan.FromSeconds(45);
    }

    public KugouLoginSnapshot LoginSnapshot => _loginSnapshot;

    public string LoginPageUrl => $"{_settings.BaseUrl.TrimEnd('/')}/api/v1/login/page";

    public Task<bool> HealthCheckAsync(CancellationToken ct = default)
        => SafeAsync("health", async () =>
        {
            var result = await HttpJson.GetAsync<KugouResponse<object>>(_client, "health", ct);
            return result?.Code == 0;
        }, false);

    public Task<List<KugouSongItem>> SearchAsync(string keyword, int page = 1, int pageSize = 30, CancellationToken ct = default)
        => SafeAsync("search", async () =>
        {
            var result = await HttpJson.PostAsync<KugouResponse<KugouSearchData>>(_client, "api/v1/search",
                new { keyword, page, pagesize = pageSize }, ct);
            if (result?.Code != 0 || result.Data?.Songs == null || result.Data.Songs.Count == 0)
            {
                return new List<KugouSongItem>();
            }

            return result.Data.Songs;
        }, new List<KugouSongItem>());

    public Task<KugouSongItem?> SearchFirstAsync(string keyword, CancellationToken ct = default)
        => SafeAsync("search", async () =>
        {
            var songs = await SearchAsync(keyword, 1, 10, ct);
            return songs.Count == 0 ? null : songs[0];
        }, null);

    public async Task<KugouLoginSnapshot> RefreshLoginStatusAsync(CancellationToken ct = default)
    {
        var status = await HttpJson.GetAsync<KugouResponse<KugouLoginStatusData>>(
            _client, "api/v1/login/status?refresh=1", ct);
        _loginCheckedAt = DateTime.UtcNow;
        _lastLoggedIn = status?.Code == 0 && status.Data?.LoggedIn == true;
        _loginSnapshot = new KugouLoginSnapshot
        {
            LoggedIn = _lastLoggedIn,
            Nickname = status?.Data?.Nickname?.Trim() ?? "",
            VipLabel = status?.Data?.VipLabel?.Trim() ?? "",
            VipEnd = status?.Data?.VipEnd?.Trim() ?? ""
        };

        if (_lastLoggedIn)
        {
            var label = _loginSnapshot.DisplayStatus;
            if (!string.IsNullOrWhiteSpace(_loginSnapshot.VipEnd))
            {
                label += $" 至 {_loginSnapshot.VipEnd}";
            }

            _log.KugouInfo($"酷狗登录: {label}");
        }

        return _loginSnapshot;
    }

    /// <summary>每日自动领取概念版试用会员（对齐 MoeKoeMusic getVip）。</summary>
    public Task<KugouVipClaimResult?> TryAutoClaimVipAsync(CancellationToken ct = default)
        => TryAutoClaimVipAsync(force: false, ct);

    public async Task<KugouVipClaimResult?> TryAutoClaimVipAsync(bool force, CancellationToken ct = default)
    {
        if (!_settings.AutoClaimVip || !_lastLoggedIn)
        {
            return null;
        }

        var today = DateTime.Now.ToString("yyyy-MM-dd");
        if (!force && _lastVipClaimDate == today)
        {
            return null;
        }

        var result = await ClaimDailyVipAsync(ct);
        if (result != null && !force)
        {
            _lastVipClaimDate = today;
        }

        return result;
    }

    public Task<KugouVipClaimResult?> ClaimDailyVipAsync(CancellationToken ct = default)
        => SafeAsync("vip_claim", async () =>
        {
            var result = await HttpJson.GetAsync<KugouResponse<KugouVipClaimData>>(
                _client, "api/v1/vip/claim", ct);
            if (result?.Code != 0 || result.Data == null)
            {
                _log.KugouWarn($"自动领取试用会员失败: {result?.Msg ?? "无响应"}");
                return null;
            }

            var data = result.Data;
            var msg = data.Message?.Trim() ?? result.Msg?.Trim() ?? "";
            if (data.Claimed || data.Upgraded)
            {
                _log.KugouInfo($"试用会员: {msg}");
            }
            else if (data.AlreadyClaimed)
            {
                _log.KugouInfo("试用会员: 今日已领取");
            }

            return new KugouVipClaimResult
            {
                Claimed = data.Claimed,
                Upgraded = data.Upgraded,
                AlreadyClaimed = data.AlreadyClaimed,
                Message = msg,
                VipLabel = data.VipLabel?.Trim() ?? ""
            };
        }, null);

    /// <summary>从酷狗曲库随机搜歌并取可播放链接。</summary>
    public async Task<TrackInfo?> PickRandomTrackAsync(
        Func<string?, string?, bool> wasRecentlyPlayed,
        CancellationToken ct = default)
    {
        var rng = Random.Shared;
        var seeds = KugouRandomCatalog.SearchSeeds;

        for (var attempt = 0; attempt < 12; attempt++)
        {
            var keyword = seeds[rng.Next(seeds.Length)];
            var page = rng.Next(1, 6);
            var songs = await SearchAsync(keyword, page, 30, ct);
            if (songs.Count == 0)
            {
                continue;
            }

            var order = Enumerable.Range(0, songs.Count).OrderBy(_ => rng.Next()).ToList();
            foreach (var index in order)
            {
                var song = songs[index];
                var hash = song.Hash?.Trim() ?? "";
                var songId = FirstNonEmpty(song.SongId, song.Id);
                var songName = song.SongName?.Trim() ?? "";

                if (string.IsNullOrWhiteSpace(hash) && string.IsNullOrWhiteSpace(songName))
                {
                    continue;
                }

                if (wasRecentlyPlayed(songId, hash))
                {
                    continue;
                }

                var track = await ResolveFromSongAsync(song, keyword, isRandom: true, requester: "随机", ct);
                if (track != null)
                {
                    return track;
                }
            }
        }

        _log.KugouWarn("酷狗曲库随机取歌失败，已重试多次");
        return null;
    }

    public Task<KugouUrlData?> GetPlayUrlAsync(string? hash, string? keyword, CancellationToken ct = default)
        => GetPlayUrlAsync(KugouSongContext.FromHash(hash, keyword), ct);

    internal Task<KugouUrlData?> GetPlayUrlAsync(KugouSongContext ctx, CancellationToken ct = default)
        => SafeAsync("song_url", () => GetPlayUrlCoreAsync(ctx, ct), null);

    private async Task<KugouUrlData?> GetPlayUrlCoreAsync(KugouSongContext ctx, CancellationToken ct)
        => await GetPlayUrlCoreAsync(ctx, allowPreviewFallback: true, tryAlternates: true, ct);

    private async Task<KugouUrlData?> GetPlayUrlCoreAsync(
        KugouSongContext ctx,
        bool allowPreviewFallback,
        bool tryAlternates,
        CancellationToken ct)
    {
        await EnsureLoginReadyAsync(forceRefresh: false, ct);
        var previewAllowed = allowPreviewFallback && CanAcceptPreview();

        for (var attempt = 0; attempt < 2; attempt++)
        {
            if (attempt > 0)
            {
                await TryAutoClaimVipAsync(force: true, ct);
                await RefreshLoginStatusAsync(ct);
                previewAllowed = allowPreviewFallback && CanAcceptPreview();
            }

            foreach (var mode in BuildUrlModes(previewAllowed))
            {
                var result = await PostSongUrlAsync(ctx, mode, ct);
                if (result?.Code != 0 || string.IsNullOrWhiteSpace(result.Data?.Url))
                {
                    _log.KugouWarn($"取链失败 mode={mode}: {result?.Msg ?? "无响应"} ({Label(ctx)})");
                    continue;
                }

                if (ShouldRejectPreview(result.Data))
                {
                    _log.KugouWarn($"登录态仍返回试听链 mode={mode}: {Label(ctx)}");
                    continue;
                }

                if (result.Data.IsPreview)
                {
                    _log.KugouWarn($"未登录或会员曲，已降级试听: {Label(ctx)}");
                }

                return result.Data;
            }
        }

        if (tryAlternates && !string.IsNullOrWhiteSpace(ctx.Keyword))
        {
            var songs = await SearchAsync(ctx.Keyword, 1, 10, ct);
            foreach (var song in songs)
            {
                var hash = song.Hash?.Trim() ?? "";
                if (string.IsNullOrWhiteSpace(hash) || hash.Equals(ctx.Hash, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var alt = await GetPlayUrlCoreAsync(
                    KugouSongContext.FromSong(song, ctx.Keyword),
                    allowPreviewFallback: previewAllowed,
                    tryAlternates: false,
                    ct);
                if (alt != null)
                {
                    return alt;
                }
            }
        }

        return null;
    }

    private bool CanAcceptPreview()
        => !_settings.RequireFullPlayback || !_lastLoggedIn;

    private IEnumerable<string> BuildUrlModes(bool includePreview)
    {
        yield return "auto";
        yield return "full";
        if (includePreview)
        {
            yield return "preview";
        }
    }

    private bool ShouldRejectPreview(KugouUrlData data)
        => _settings.RequireFullPlayback && _lastLoggedIn && data.IsPreview;

    /// <summary>搜索候选，按歌手去重，最多返回 displayLimit 个。</summary>
    public async Task<List<SongSearchCandidate>> SearchCandidatesAsync(
        string keyword,
        int displayLimit = 3,
        CancellationToken ct = default)
    {
        var songs = await SearchAsync(keyword, 1, 30, ct);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var list = new List<SongSearchCandidate>();
        foreach (var song in songs)
        {
            if (string.IsNullOrWhiteSpace(song.Hash) && string.IsNullOrWhiteSpace(song.SongName))
            {
                continue;
            }

            var candidate = SongSearchCandidate.FromKugou(song, keyword);
            if (string.IsNullOrWhiteSpace(candidate.Artist))
            {
                candidate.Artist = "未知歌手";
            }

            if (!seen.Add(candidate.Artist))
            {
                continue;
            }

            list.Add(candidate);
            if (list.Count >= displayLimit)
            {
                break;
            }
        }

        return list;
    }

    public Task<TrackInfo?> ResolveCandidateAsync(
        SongSearchCandidate candidate,
        string? requester = null,
        CancellationToken ct = default)
    {
        var song = new KugouSongItem
        {
            Hash = candidate.Hash,
            SongName = candidate.SongName,
            Artist = candidate.Artist,
            SongId = candidate.SongId,
            Id = candidate.SongId,
            AlbumId = candidate.AlbumId,
            AlbumAudioId = candidate.AlbumAudioId
        };
        return ResolveFromSongAsync(song, candidate.SongName, isRandom: false, requester, ct);
    }

    public async Task<TrackInfo?> ResolveTrackAsync(string keyword, CancellationToken ct = default)
    {
        var songs = await SearchAsync(keyword, 1, 10, ct);
        foreach (var song in songs)
        {
            if (string.IsNullOrWhiteSpace(song.Hash) && string.IsNullOrWhiteSpace(song.SongName))
            {
                continue;
            }

            var track = await ResolveFromSongAsync(song, keyword, isRandom: false, requester: "", ct);
            if (track != null)
            {
                return track;
            }
        }

        return null;
    }

    /// <summary>
    /// 播放前取最新直链：优先 hash + 专辑信息 → GetPlayUrlAsync，失败再按歌名搜索。
    /// </summary>
    public async Task<TrackInfo?> ResolveFreshTrackAsync(
        string? hash,
        string songName,
        string? artist = null,
        string? songId = null,
        string? requester = null,
        bool isRandom = false,
        string? albumId = null,
        long albumAudioId = 0,
        CancellationToken ct = default)
    {
        var name = songName?.Trim() ?? "";
        var songHash = hash?.Trim() ?? "";

        if (!string.IsNullOrWhiteSpace(songHash) || albumAudioId > 0)
        {
            var ctx = new KugouSongContext
            {
                Hash = songHash,
                Keyword = name,
                AlbumId = albumId?.Trim(),
                AlbumAudioId = albumAudioId
            };
            var urlData = await GetPlayUrlAsync(ctx, ct);
            if (urlData != null && !string.IsNullOrWhiteSpace(urlData.Url))
            {
                return BuildTrackInfo(urlData, songHash, name, artist, songId, requester, isRandom);
            }

            _log.KugouWarn($"hash 取链失败，回退歌名搜索: hash={songHash} song={name}");
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var track = await ResolveTrackAsync(name, ct);
        if (track == null)
        {
            return null;
        }

        track.IsRandom = isRandom;
        track.Requester = requester ?? "";
        if (string.IsNullOrWhiteSpace(track.Artist) && !string.IsNullOrWhiteSpace(artist))
        {
            track.Artist = artist;
        }

        return track;
    }

    public async Task<TrackInfo?> ResolveTrackFromItemAsync(RandomPlaylistItem item, CancellationToken ct = default)
    {
        try
        {
            return await ResolveFreshTrackAsync(
                item.Hash,
                !string.IsNullOrWhiteSpace(item.Keyword) ? item.Keyword! : (item.Title ?? ""),
                item.Artist,
                item.SongId,
                requester: null,
                isRandom: true,
                ct: ct);
        }
        catch (Exception ex)
        {
            _log.KugouWarn($"解析曲目失败: {ex.Message}");
            _log.Error("kugou", "resolve_track_from_item", ex);
            return null;
        }
    }

    private async Task<TrackInfo?> ResolveFromSongAsync(
        KugouSongItem song,
        string? keyword,
        bool isRandom,
        string? requester,
        CancellationToken ct)
    {
        var urlData = await GetPlayUrlAsync(KugouSongContext.FromSong(song, keyword), ct);
        if (urlData == null || string.IsNullOrWhiteSpace(urlData.Url))
        {
            return null;
        }

        return BuildTrackInfo(
            urlData,
            song.Hash ?? "",
            FirstNonEmpty(urlData.SongName, song.SongName, keyword),
            FirstNonEmpty(urlData.Artist, song.Artist),
            FirstNonEmpty(urlData.SongId, urlData.Id, song.SongId, song.Id),
            requester,
            isRandom,
            song.AlbumId,
            song.AlbumAudioId);
    }

    private static TrackInfo BuildTrackInfo(
        KugouUrlData urlData,
        string hash,
        string? songName,
        string? artist,
        string? songId,
        string? requester,
        bool isRandom,
        string? albumId = null,
        long albumAudioId = 0)
        => new()
        {
            SongName = songName?.Trim() ?? "",
            Artist = artist?.Trim() ?? "",
            SongId = songId?.Trim() ?? "",
            Hash = hash.Trim(),
            AlbumId = albumId?.Trim() ?? "",
            AlbumAudioId = albumAudioId,
            PlayUrl = urlData.Url,
            DurationSec = urlData.TimeLength,
            IsRandom = isRandom,
            IsPreview = urlData.IsPreview,
            Requester = requester ?? ""
        };

    private async Task<bool> EnsureLoginReadyAsync(bool forceRefresh, CancellationToken ct)
    {
        if (!forceRefresh && DateTime.UtcNow - _loginCheckedAt < TimeSpan.FromSeconds(45))
        {
            return _lastLoggedIn;
        }

        await RefreshLoginStatusAsync(ct);
        return _lastLoggedIn;
    }

    private async Task<KugouResponse<KugouUrlData>?> PostSongUrlAsync(KugouSongContext ctx, string mode, CancellationToken ct)
    {
        object body = !string.IsNullOrWhiteSpace(ctx.Hash)
            ? new
            {
                hash = ctx.Hash,
                album_id = ctx.AlbumId,
                album_audio_id = ctx.AlbumAudioId > 0 ? ctx.AlbumAudioId : (long?)null,
                mode,
                quality = "auto"
            }
            : new
            {
                keyword = ctx.Keyword,
                mode,
                quality = "auto"
            };

        return await HttpJson.PostAsync<KugouResponse<KugouUrlData>>(_client, "api/v1/song/url", body, ct);
    }

    private static string Label(KugouSongContext ctx)
        => ctx.Keyword ?? ctx.Hash ?? "?";

    private static string FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return "";
    }

    private async Task<T> SafeAsync<T>(string operation, Func<Task<T>> action, T fallback)
    {
        try
        {
            return await action();
        }
        catch (Exception ex)
        {
            _log.KugouWarn($"{operation} 失败: {ex.Message}");
            _log.Error("kugou", operation, ex);
            return fallback;
        }
    }
}
