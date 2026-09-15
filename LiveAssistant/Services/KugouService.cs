using LiveAssistant.Config;
using LiveAssistant.Models;
using LiveAssistant.Utils;
using System.Text.Json;

namespace LiveAssistant.Services;

public sealed class KugouService
{
    private readonly KugouSettings _settings;
    private readonly LogService _log;
    private readonly HttpClient _client;
    private readonly string? _sessionFilePath;
    private DateTime _loginCheckedAt = DateTime.MinValue;
    private bool _lastLoggedIn;
    private KugouLoginSnapshot _loginSnapshot = new();
    private readonly SemaphoreSlim _loginRefreshLock = new(1, 1);
    private Task<KugouLoginSnapshot>? _loginRefreshTask;
    private readonly SemaphoreSlim _vipClaimLock = new(1, 1);
    private string? _lastVipClaimDate;
    private DateTime _vipClaimBackoffUntilUtc = DateTime.MinValue;
    private static readonly TimeSpan VipClaimFailureBackoff = TimeSpan.FromMinutes(20);
    private readonly SemaphoreSlim _dailyRecommendLock = new(1, 1);
    private readonly Random _dailyRecommendRandom = new();
    private List<KugouSongItem> _dailyRecommendPlaylist = new();
    private int _dailyRecommendIndex;
    private string? _dailyRecommendSessionUserId;
    private int _loginGeneration;
    private KugouFullPlaybackStatus? _cachedFullPlaybackStatus;
    private DateTime _fullPlaybackCheckedAt = DateTime.MinValue;
    private string? _cachedProbeHash;
    private KugouUrlData? _cachedProbeUrlData;
    private int _fallbackProbeIndex;
    private int _searchFallbackKeywordIndex;
    private readonly Dictionary<string, (KugouUrlData Data, DateTime ExpiresAt)> _urlCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan UrlCacheTtl = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan FullPlaybackCacheTtl = TimeSpan.FromSeconds(90);
    private static readonly string[] SearchFallbackKeywords =
    [
        "热门",
        "抖音热歌",
        "流行",
        "华语",
        "经典老歌",
        "网络歌曲",
        "伤感情歌",
        "DJ"
    ];

    public KugouService(KugouSettings settings, LogService log, HttpClient? client = null, string? dataDirectory = null)
    {
        _settings = settings;
        _log = log;
        _client = client ?? HttpJson.CreateClient(settings.BaseUrl, settings.ApiKey, "X-API-Key");
        _client.Timeout = TimeSpan.FromSeconds(45);
        if (!string.IsNullOrWhiteSpace(dataDirectory))
        {
            _sessionFilePath = Path.Combine(dataDirectory, "session.json");
        }
    }

    public KugouLoginSnapshot LoginSnapshot => _loginSnapshot;

    public string LoginPageUrl => $"{_settings.BaseUrl.TrimEnd('/')}/login";

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

            return result.Data.Songs.Select(NormalizeKugouSongItem).ToList();
        }, new List<KugouSongItem>());

    public Task<KugouSongItem?> SearchFirstAsync(string keyword, CancellationToken ct = default)
        => SafeAsync("search", async () =>
        {
            var songs = await SearchAsync(keyword, 1, 10, ct);
            return songs.Count == 0 ? null : songs[0];
        }, null);

    public Task<KugouLoginSnapshot> RefreshLoginStatusAsync(CancellationToken ct = default)
        => RefreshLoginStatusAsync(forceRefresh: false, ct);

    public Task<KugouLoginSnapshot> RefreshLoginStatusAsync(bool forceRefresh, CancellationToken ct = default)
    {
        if (!forceRefresh && IsLoginCacheFresh())
        {
            return Task.FromResult(_loginSnapshot);
        }

        var inFlight = _loginRefreshTask;
        if (inFlight != null && !inFlight.IsCompleted)
        {
            return AwaitLoginRefresh(inFlight, ct);
        }

        return StartOrJoinLoginRefreshAsync(forceRefresh, ct);
    }

    private bool IsLoginCacheFresh()
    {
        var cacheTtl = _settings.RequireFullPlayback ? TimeSpan.FromSeconds(30) : TimeSpan.FromSeconds(45);
        return _loginCheckedAt > DateTime.MinValue && DateTime.UtcNow - _loginCheckedAt < cacheTtl;
    }

    private static async Task<KugouLoginSnapshot> AwaitLoginRefresh(Task<KugouLoginSnapshot> task, CancellationToken ct)
        => await task.WaitAsync(ct);

    private async Task<KugouLoginSnapshot> StartOrJoinLoginRefreshAsync(bool forceRefresh, CancellationToken ct)
    {
        Task<KugouLoginSnapshot> task;
        await _loginRefreshLock.WaitAsync(ct);
        try
        {
            if (_loginRefreshTask == null || _loginRefreshTask.IsCompleted)
            {
                var generation = Volatile.Read(ref _loginGeneration);
                _loginRefreshTask = Task.Run(
                    async () => await FetchLoginStatusAsync(forceRefresh, generation, ct), ct);
            }

            task = _loginRefreshTask;
        }
        finally
        {
            _loginRefreshLock.Release();
        }

        return await task.WaitAsync(ct);
    }

    private async Task<KugouLoginSnapshot> FetchLoginStatusAsync(bool forceRefresh, int generation, CancellationToken ct)
    {
        // 取链遇到 session_expired 会 Invalidate 并把 generation +1；若此时仍 join 旧 in-flight，
        // 旧逻辑会直接返回已被清空的 snapshot，导致连续「播放地址获取失败」。
        for (var round = 0; round < 3; round++)
        {
            var path = forceRefresh || round > 0
                ? "api/v1/login/status?refresh=1"
                : "api/v1/login/status";
            var status = await HttpJson.GetAsync<KugouResponse<KugouLoginStatusData>>(_client, path, ct);
            var currentGeneration = Volatile.Read(ref _loginGeneration);
            if (generation != currentGeneration)
            {
                generation = currentGeneration;
                forceRefresh = true;
                continue;
            }

            _loginCheckedAt = DateTime.UtcNow;
            var wasLoggedIn = _lastLoggedIn;
            var previousUserId = _loginSnapshot.UserId;
            _lastLoggedIn = status?.Code == 0 && status.Data?.LoggedIn == true;
            _loginSnapshot = new KugouLoginSnapshot
            {
                LoggedIn = _lastLoggedIn,
                UserId = status?.Data?.UserId?.Trim() ?? "",
                Nickname = status?.Data?.Nickname?.Trim() ?? "",
                VipLabel = status?.Data?.VipLabel?.Trim() ?? "",
                VipType = status?.Data?.VipType?.Trim() ?? "",
                HasVipToken = status?.Data?.HasVipToken == true,
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

            var userChanged = !string.Equals(previousUserId, _loginSnapshot.UserId, StringComparison.Ordinal);
            if (!wasLoggedIn && _lastLoggedIn || userChanged || (!_lastLoggedIn && wasLoggedIn))
            {
                InvalidateDailyRecommendCache();
            }

            return _loginSnapshot;
        }

        return _loginSnapshot;
    }

    /// <summary>清除酷狗 sidecar 本地登录态（换号 / 旧扫码残留时必须先退出）。</summary>
    public Task<bool> LogoutAsync(CancellationToken ct = default)
        => SafeAsync("logout", async () =>
        {
            await _loginRefreshLock.WaitAsync(ct);
            try
            {
                _loginRefreshTask = null;
            }
            finally
            {
                _loginRefreshLock.Release();
            }

            InvalidateLoginCache();
            _lastVipClaimDate = null;
            var result = await HttpJson.PostAsync<KugouResponse<object>>(
                _client, "api/v1/login/logout", new { }, ct);
            if (result?.Code == 0)
            {
                _log.KugouInfo("酷狗已退出登录，本地 session 已清除");
            }

            return result?.Code == 0;
        }, false);

    /// <summary>每日自动领取概念版试用会员（对齐 MoeKoeMusic getVip）。</summary>
    public Task<KugouVipClaimResult?> TryAutoClaimVipAsync(CancellationToken ct = default)
        => TryAutoClaimVipAsync(force: false, ct);

    public async Task<KugouVipClaimResult?> TryAutoClaimVipAsync(bool force, CancellationToken ct = default)
    {
        await _vipClaimLock.WaitAsync(ct);
        try
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

            // 领取失败（如 502/51002）不要每十几秒狂打，会拖垮 sidecar 登录态导致取链全失败
            if (!force && DateTime.UtcNow < _vipClaimBackoffUntilUtc)
            {
                return null;
            }

            var result = await ClaimDailyVipAsync(ct);
            if (result == null)
            {
                _vipClaimBackoffUntilUtc = DateTime.UtcNow.Add(VipClaimFailureBackoff);
                return null;
            }

            _vipClaimBackoffUntilUtc = DateTime.MinValue;
            if (result.AlreadyClaimed || result.Claimed || result.Upgraded || !force)
            {
                _lastVipClaimDate = today;
            }

            return result;
        }
        finally
        {
            _vipClaimLock.Release();
        }
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

    /// <summary>从酷狗「每日推荐」歌单随机取下一首可播放曲目；播完歌单后自动重新拉取并重新打乱。</summary>
    public async Task<TrackInfo?> PickRandomTrackAsync(
        Func<string?, string?, bool> wasRecentlyPlayed,
        CancellationToken ct = default)
    {
        if (_settings.RequireFullPlayback)
        {
            await EnsureLoginReadyAsync(forceRefresh: false, ct);
            if (!_lastLoggedIn)
            {
                _log.KugouWarn("每日推荐完整播放需要先扫码登录酷狗");
                return null;
            }

        }

        const int maxRefreshAttempts = 2;
        const int maxTriesPerBatch = 30;

        for (var refreshAttempt = 0; refreshAttempt < maxRefreshAttempts; refreshAttempt++)
        {
            TrackInfo? repeatCandidate = null;
            var triedInBatch = 0;
            while (triedInBatch < maxTriesPerBatch)
            {
                ct.ThrowIfCancellationRequested();
                var song = await TakeNextDailyRecommendSongAsync(ct);
                if (song == null)
                {
                    break;
                }

                triedInBatch++;
                var keyword = BuildDailyRecommendKeyword(song);
                var track = await ResolveDailyRecommendSongAsync(song, keyword, ct);
                if (track == null || !IsAcceptableForPlayback(track))
                {
                    continue;
                }

                if (!wasRecentlyPlayed(track.SongId, track.Hash))
                {
                    return track;
                }

                repeatCandidate = track;
            }

            if (triedInBatch == 0)
            {
                _log.KugouWarn("每日推荐歌单为空");
                break;
            }

            if (repeatCandidate != null)
            {
                return repeatCandidate;
            }

            if (refreshAttempt + 1 < maxRefreshAttempts)
            {
                await ResetDailyRecommendPlaylistAsync(ct);
                _log.KugouWarn("每日推荐歌单内歌曲均无法播放，正在重新获取");
            }
        }

        var lastChance = await PickFallbackRandomTrackAsync(wasRecentlyPlayed, ct);
        if (lastChance != null)
        {
            return lastChance;
        }

        _log.KugouWarn("每日推荐取歌失败，已重试多次");
        return null;
    }

    /// <summary>每日推荐失败时的稳定备用曲目（完整版探测同款 hash）。</summary>
    public Task<TrackInfo?> PickFallbackRandomTrackAsync(
        Func<string?, string?, bool>? wasRecentlyPlayed = null,
        CancellationToken ct = default)
        => TryFallbackRandomTracksAsync(wasRecentlyPlayed, ct);

    private async Task<TrackInfo?> TryFallbackRandomTracksAsync(
        Func<string?, string?, bool>? wasRecentlyPlayed,
        CancellationToken ct)
    {
        wasRecentlyPlayed ??= (_, _) => false;
        var orderedHashes = GetRotatedProbeHashes().ToList();
        var eligibleHashes = orderedHashes.Where(hash => !wasRecentlyPlayed(null, hash)).ToList();
        if (eligibleHashes.Count == 0)
        {
            eligibleHashes = orderedHashes;
        }

        TrackInfo? repeatCandidate = null;
        foreach (var hash in eligibleHashes)
        {
            ct.ThrowIfCancellationRequested();
            var urlData = await ResolveFallbackProbeUrlAsync(hash, ct);
            if (urlData == null || string.IsNullOrWhiteSpace(urlData.Url))
            {
                continue;
            }

            var track = BuildTrackInfo(
                urlData,
                hash,
                FirstNonEmpty(urlData.SongName, "备用曲目"),
                FirstNonEmpty(urlData.Artist),
                FirstNonEmpty(urlData.SongId, urlData.Id, hash),
                requester: "随机",
                isRandom: true);
            if (!IsAcceptableForPlayback(track))
            {
                continue;
            }

            if (!wasRecentlyPlayed(track.SongId, track.Hash))
            {
                AdvanceFallbackProbeIndex(hash);
                _log.KugouInfo($"使用备用曲目: {track.SongName}");
                return track;
            }

            repeatCandidate = track;
        }

        if (repeatCandidate != null)
        {
            AdvanceFallbackProbeIndex(repeatCandidate.Hash);
            _log.KugouInfo($"使用备用曲目(轮换): {repeatCandidate.SongName}");
            return repeatCandidate;
        }

        return null;
    }

    private async Task<KugouUrlData?> ResolveFallbackProbeUrlAsync(string hash, CancellationToken ct)
    {
        if (string.Equals(_cachedProbeHash, hash, StringComparison.OrdinalIgnoreCase)
            && _cachedProbeUrlData != null
            && !string.IsNullOrWhiteSpace(_cachedProbeUrlData.Url))
        {
            return _cachedProbeUrlData;
        }

        return await GetPlayUrlCoreAsync(
            KugouSongContext.FromHash(hash, null),
            allowPreviewFallback: false,
            tryAlternates: false,
            ct);
    }

    private IEnumerable<string> GetRotatedProbeHashes()
    {
        for (var i = 0; i < FullPlaybackProbeHashes.Length; i++)
        {
            yield return FullPlaybackProbeHashes[(_fallbackProbeIndex + i) % FullPlaybackProbeHashes.Length];
        }
    }

    private void AdvanceFallbackProbeIndex(string hash)
    {
        for (var i = 0; i < FullPlaybackProbeHashes.Length; i++)
        {
            if (string.Equals(FullPlaybackProbeHashes[i], hash, StringComparison.OrdinalIgnoreCase))
            {
                _fallbackProbeIndex = (i + 1) % FullPlaybackProbeHashes.Length;
                return;
            }
        }

        _fallbackProbeIndex = (_fallbackProbeIndex + 1) % FullPlaybackProbeHashes.Length;
    }

    public Task<List<KugouSongItem>> FetchEverydayRecommendAsync(CancellationToken ct = default)
        => SafeAsync("everyday_recommend", () => FetchEverydayRecommendCoreAsync(ct), new List<KugouSongItem>());

    private async Task<List<KugouSongItem>> FetchEverydayRecommendCoreAsync(CancellationToken ct)
    {
        var moeSongs = await TryFetchEverydayFromMoeAsync(ct);
        if (moeSongs.Count > 0)
        {
            return moeSongs;
        }

        string? sidecarMsg = null;
        try
        {
            var result = await HttpJson.PostAsync<KugouResponse<KugouSearchData>>(
                _client, "api/v1/everyday/recommend", new { }, ct);
            sidecarMsg = result?.Msg?.Trim();
            if (result?.Code == 0 && result.Data?.Songs is { Count: > 0 } songs)
            {
                return NormalizeDailyRecommendSongs(songs);
            }
        }
        catch (Exception ex)
        {
            _log.KugouWarn($"侧车每日推荐请求异常: {ex.Message}");
        }

        var searchFallback = await FetchSearchFallbackPlaylistAsync(ct);
        if (searchFallback.Count > 0)
        {
            return searchFallback;
        }

        if (!string.IsNullOrWhiteSpace(sidecarMsg))
        {
            _log.KugouWarn($"获取每日推荐失败: {sidecarMsg}");
        }
        else if (!TryLoadSidecarSession(out _))
        {
            _log.KugouWarn("获取每日推荐失败：未找到登录 session，无法拉取个性化歌单（请先扫码登录酷狗）");
        }
        else
        {
            _log.KugouWarn("获取每日推荐失败：请确认酷狗 API 在线且 kgapijs 协议服务可用");
        }

        return new List<KugouSongItem>();
    }

    /// <summary>每日推荐/kgapijs 不可用时，用侧车搜索 API 拉一批随机补位歌单。</summary>
    private async Task<List<KugouSongItem>> FetchSearchFallbackPlaylistAsync(CancellationToken ct)
    {
        for (var i = 0; i < SearchFallbackKeywords.Length; i++)
        {
            var keyword = SearchFallbackKeywords[(_searchFallbackKeywordIndex + i) % SearchFallbackKeywords.Length];
            var songs = await SearchAsync(keyword, 1, 30, ct);
            if (songs.Count == 0)
            {
                continue;
            }

            _searchFallbackKeywordIndex = (_searchFallbackKeywordIndex + i + 1) % SearchFallbackKeywords.Length;
            var normalized = NormalizeDailyRecommendSongs(songs);
            if (normalized.Count == 0)
            {
                continue;
            }

            var preview = string.Join(" | ", normalized.Take(3).Select(s => s.SongName));
            _log.KugouInfo(
                $"每日推荐不可用，已用搜索补位 {normalized.Count} 首 keyword={keyword} preview={preview}");
            return normalized;
        }

        return new List<KugouSongItem>();
    }

    private async Task<KugouSongItem?> TakeNextDailyRecommendSongAsync(CancellationToken ct)
    {
        const int maxFetchAttempts = 2;
        for (var fetchAttempt = 0; fetchAttempt < maxFetchAttempts; fetchAttempt++)
        {
            await _dailyRecommendLock.WaitAsync(ct);
            try
            {
                if (ShouldRefreshDailyRecommendCache())
                {
                    InvalidateDailyRecommendCacheCore();
                }

                while (_dailyRecommendIndex < _dailyRecommendPlaylist.Count)
                {
                    var song = _dailyRecommendPlaylist[_dailyRecommendIndex++];
                    if (!IsEmptyDailySong(song))
                    {
                        return song;
                    }
                }
            }
            finally
            {
                _dailyRecommendLock.Release();
            }

            var songs = await FetchEverydayRecommendCoreAsync(ct);
            if (songs.Count == 0)
            {
                return null;
            }

            await _dailyRecommendLock.WaitAsync(ct);
            try
            {
                _dailyRecommendPlaylist = ShuffleDailyRecommendSongs(songs);
                _dailyRecommendIndex = 0;
                _dailyRecommendSessionUserId = ResolveDailyRecommendUserId();
                _log.KugouInfo($"已获取每日推荐歌单（随机顺序），共 {_dailyRecommendPlaylist.Count} 首");
            }
            finally
            {
                _dailyRecommendLock.Release();
            }
        }

        return null;
    }

    private async Task ResetDailyRecommendPlaylistAsync(CancellationToken ct)
    {
        await _dailyRecommendLock.WaitAsync(ct);
        try
        {
            InvalidateDailyRecommendCacheCore();
        }
        finally
        {
            _dailyRecommendLock.Release();
        }
    }

    private void InvalidateDailyRecommendCache()
    {
        if (!_dailyRecommendLock.Wait(TimeSpan.FromSeconds(2)))
        {
            _log.KugouWarn("清空每日推荐缓存超时");
            return;
        }

        try
        {
            InvalidateDailyRecommendCacheCore();
        }
        finally
        {
            _dailyRecommendLock.Release();
        }
    }

    private void InvalidateDailyRecommendCacheCore()
    {
        _dailyRecommendPlaylist.Clear();
        _dailyRecommendIndex = 0;
        _dailyRecommendSessionUserId = null;
    }

    private bool ShouldRefreshDailyRecommendCache()
    {
        if (_dailyRecommendPlaylist.Count == 0
            || string.IsNullOrWhiteSpace(_dailyRecommendSessionUserId))
        {
            return false;
        }

        if (!TryLoadSidecarSession(out var session))
        {
            return false;
        }

        var userId = session.UserId?.Trim() ?? "";
        return string.IsNullOrWhiteSpace(userId)
            || !string.Equals(_dailyRecommendSessionUserId, userId, StringComparison.Ordinal);
    }

    private List<KugouSongItem> ShuffleDailyRecommendSongs(List<KugouSongItem> songs)
    {
        var list = songs.ToList();
        for (var i = list.Count - 1; i > 0; i--)
        {
            var j = _dailyRecommendRandom.Next(i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }

        return list;
    }

    private static List<KugouSongItem> NormalizeDailyRecommendSongs(IEnumerable<KugouSongItem> songs)
        => songs
            .Select(NormalizeDailyRecommendSong)
            .Where(s => !IsEmptyDailySong(s))
            .ToList();

    private static KugouSongItem NormalizeDailyRecommendSong(KugouSongItem song)
    {
        if (!string.IsNullOrWhiteSpace(song.Hash))
        {
            song.Hash = song.Hash.Trim().ToLowerInvariant();
        }

        return song;
    }

    private static bool IsEmptyDailySong(KugouSongItem song)
        => string.IsNullOrWhiteSpace(song.Hash) && string.IsNullOrWhiteSpace(song.SongName);

    private async Task<List<KugouSongItem>> TryFetchEverydayFromMoeAsync(CancellationToken ct)
    {
        if (!TryLoadSidecarSession(out var session))
        {
            _log.KugouWarn("每日推荐跳过未登录请求：未找到有效 session.json");
            return new List<KugouSongItem>();
        }

        var cookie = KugouSessionCookie.BuildHeader(session);
        if (string.IsNullOrWhiteSpace(cookie))
        {
            _log.KugouWarn("每日推荐跳过未登录请求：session 缺少 token/userid");
            return new List<KugouSongItem>();
        }

        foreach (var baseUrl in new[] { "http://127.0.0.1:16521", "http://127.0.0.1:3000" })
        {
            try
            {
                using var client = HttpJson.CreateClient(baseUrl);
                client.Timeout = TimeSpan.FromSeconds(20);
                var response = await GetMoeEverydayRecommendAsync(client, cookie, ct);
                if (response?.Status != 1 || response.Data?.SongList == null || response.Data.SongList.Count == 0)
                {
                    continue;
                }

                var songs = response.Data.SongList
                    .Select(MapMoeEverydaySong)
                    .Where(s => !IsEmptyDailySong(s))
                    .ToList();
                if (songs.Count > 0)
                {
                    var preview = string.Join(" | ", songs.Take(3).Select(s => s.SongName));
                    _log.KugouInfo(
                        $"已通过登录态获取每日推荐 {songs.Count} 首 ({baseUrl}) userid={session.UserId} preview={preview}");
                    return songs;
                }
            }
            catch (Exception ex)
            {
                _log.KugouWarn($"每日推荐请求失败 ({baseUrl}): {ex.Message}");
            }
        }

        return new List<KugouSongItem>();
    }

    private static async Task<MoeEverydayRecommendResponse?> GetMoeEverydayRecommendAsync(
        HttpClient client,
        string cookie,
        CancellationToken ct)
    {
        var path = "everyday/recommend?cookie=" + Uri.EscapeDataString(cookie);
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.TryAddWithoutValidation("Authorization", cookie);
        request.Headers.TryAddWithoutValidation("Cookie", cookie);
        using var response = await client.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        var text = await response.Content.ReadAsStringAsync(ct);
        return JsonSerializer.Deserialize<MoeEverydayRecommendResponse>(text, HttpJson.Options);
    }

    private string? ResolveDailyRecommendUserId()
    {
        if (TryLoadSidecarSession(out var session))
        {
            return session.UserId?.Trim();
        }

        return string.IsNullOrWhiteSpace(_loginSnapshot.UserId)
            ? null
            : _loginSnapshot.UserId.Trim();
    }

    private bool TryLoadSidecarSession(out KugouSidecarSession session)
    {
        session = new KugouSidecarSession();
        if (string.IsNullOrWhiteSpace(_sessionFilePath) || !File.Exists(_sessionFilePath))
        {
            return false;
        }

        try
        {
            var json = File.ReadAllText(_sessionFilePath);
            if (json.Length > 0 && json[0] == '\uFEFF')
            {
                json = json[1..];
            }

            var loaded = JsonSerializer.Deserialize<KugouSidecarSession>(json, HttpJson.Options);
            if (loaded == null
                || string.IsNullOrWhiteSpace(loaded.Token)
                || string.IsNullOrWhiteSpace(loaded.UserId))
            {
                return false;
            }

            session = loaded;
            return true;
        }
        catch (Exception ex)
        {
            _log.KugouWarn($"读取 session.json 失败: {ex.Message}");
            return false;
        }
    }

    private static KugouSongItem MapMoeEverydaySong(MoeEverydaySong song)
    {
        var songId = ReadJsonId(song.AlbumAudioId);
        if (string.IsNullOrWhiteSpace(songId))
        {
            songId = ReadJsonId(song.MixSongId);
        }

        long albumAudioId = 0;
        if (long.TryParse(songId, out var parsedId))
        {
            albumAudioId = parsedId;
        }

        return new KugouSongItem
        {
            Hash = song.Hash?.Trim().ToLowerInvariant() ?? "",
            SongName = FirstNonEmpty(song.OriAudioName, song.SongName),
            Artist = song.AuthorName?.Trim() ?? "",
            AlbumId = song.AlbumId?.Trim() ?? "",
            SongId = songId,
            Id = songId,
            AlbumAudioId = albumAudioId,
            Duration = song.TimeLength
        };
    }

    private static string ReadJsonId(JsonElement value)
    {
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString()?.Trim() ?? "",
            JsonValueKind.Number when value.TryGetInt64(out var number) => number.ToString(),
            _ => ""
        };
    }

    private static string BuildDailyRecommendKeyword(KugouSongItem song)
    {
        var songName = song.SongName?.Trim() ?? "";
        var artist = song.Artist?.Trim() ?? "";
        if (!string.IsNullOrWhiteSpace(artist) && !string.IsNullOrWhiteSpace(songName))
        {
            return $"{artist} {songName}";
        }

        return songName;
    }

    public Task<KugouUrlData?> GetPlayUrlAsync(string? hash, string? keyword, CancellationToken ct = default)
        => GetPlayUrlAsync(KugouSongContext.FromHash(hash, keyword), ct);

    internal Task<KugouUrlData?> GetPlayUrlAsync(KugouSongContext ctx, CancellationToken ct = default)
        => GetPlayUrlAsync(ctx, tryAlternates: true, ct);

    internal Task<KugouUrlData?> GetPlayUrlAsync(KugouSongContext ctx, bool tryAlternates, CancellationToken ct = default)
        => SafeAsync("song_url", () => GetPlayUrlCoreAsync(ctx, allowPreviewFallback: true, tryAlternates, ct), null);

    private async Task<KugouUrlData?> GetPlayUrlCoreAsync(
        KugouSongContext ctx,
        bool allowPreviewFallback,
        bool tryAlternates,
        CancellationToken ct)
    {
        const string quality = "auto";
        await EnsureLoginReadyAsync(forceRefresh: false, ct);
        if (_settings.RequireFullPlayback && !_lastLoggedIn)
        {
            LogUrlFetchDiagnostic(ctx, quality, "precheck", null, null, "login_required");
            return null;
        }

        if (!string.IsNullOrWhiteSpace(ctx.Hash) && TryGetCachedUrl(ctx, out var cachedUrl))
        {
            NormalizePreviewFlags(cachedUrl!);
            if (!ShouldRejectPreview(cachedUrl!))
            {
                LogUrlFetchDiagnostic(ctx, quality, "cache", cachedUrl, "ok", null);
                return cachedUrl;
            }
        }

        var previewAllowed = allowPreviewFallback && CanAcceptPreview();
        string? lastFailReason = null;
        var vipClaimTried = false;

        for (var attempt = 0; attempt < 3; attempt++)
        {
            if (attempt > 0)
            {
                await RefreshLoginStatusAsync(forceRefresh: true, ct);
                previewAllowed = allowPreviewFallback && CanAcceptPreview();
                if (_settings.RequireFullPlayback && !_lastLoggedIn)
                {
                    lastFailReason = "login_required";
                    LogUrlFetchDiagnostic(ctx, quality, "precheck", null, lastFailReason, null);
                    break;
                }
            }

            var sessionDropped = false;
            foreach (var mode in BuildUrlModes(previewAllowed))
            {
                var result = await PostSongUrlAsync(ctx, mode, ct);
                if (IsSessionDroppedResponse(result))
                {
                    InvalidateLoginCache();
                    lastFailReason = "session_expired";
                    sessionDropped = true;
                    LogUrlFetchDiagnostic(ctx, quality, mode, result?.Data, lastFailReason, result?.Msg);
                    break;
                }

                if (result?.Code != 0 || string.IsNullOrWhiteSpace(result.Data?.Url))
                {
                    lastFailReason = ClassifySidecarFailure(result?.Code, result?.Msg, mode);
                    if (lastFailReason == "session_expired")
                    {
                        InvalidateLoginCache();
                        sessionDropped = true;
                        LogUrlFetchDiagnostic(ctx, quality, mode, result?.Data, lastFailReason, result?.Msg);
                        break;
                    }

                    // VIP 曲伪「旧版扫码」：遵守领取退避，成功后再重试一次；失败则继续备用 hash
                    if (lastFailReason == "copyright_restricted"
                        && !vipClaimTried
                        && _settings.AutoClaimVip
                        && _lastLoggedIn
                        && mode is "full" or "auto")
                    {
                        vipClaimTried = true;
                        LogUrlFetchDiagnostic(ctx, quality, mode, result?.Data, "vip_claim_retry", result?.Msg);
                        var claim = await TryAutoClaimVipAsync(force: false, ct);
                        if (claim != null && (claim.Claimed || claim.Upgraded || claim.AlreadyClaimed))
                        {
                            result = await PostSongUrlAsync(ctx, mode, ct);
                            if (result?.Code == 0
                                && !string.IsNullOrWhiteSpace(result.Data?.Url)
                                && !IsSessionDroppedResponse(result))
                            {
                                NormalizePreviewFlags(result.Data);
                                if (!ShouldRejectPreview(result.Data))
                                {
                                    CacheUrl(ctx, result.Data);
                                    LogUrlFetchDiagnostic(ctx, quality, mode, result.Data, "ok", "after_vip_claim");
                                    return result.Data;
                                }

                                lastFailReason = "preview_rejected";
                            }
                            else
                            {
                                lastFailReason = ClassifySidecarFailure(result?.Code, result?.Msg, mode);
                            }
                        }
                    }

                    LogUrlFetchDiagnostic(ctx, quality, mode, result?.Data, lastFailReason, result?.Msg);
                    continue;
                }

                NormalizePreviewFlags(result.Data);

                if (ShouldRejectPreview(result.Data))
                {
                    lastFailReason = "preview_rejected";
                    LogUrlFetchDiagnostic(ctx, quality, mode, result.Data, lastFailReason, null);
                    continue;
                }

                CacheUrl(ctx, result.Data);
                LogUrlFetchDiagnostic(ctx, quality, mode, result.Data, "ok", null);
                return result.Data;
            }

            if (!sessionDropped)
            {
                break;
            }
        }

        if (tryAlternates && !string.IsNullOrWhiteSpace(ctx.Keyword))
        {
            var songs = await SearchAsync(
                ComposeSearchKeyword(ctx.Keyword, ctx.Artist), 1, 30, ct);
            foreach (var song in songs)
            {
                var hash = song.Hash?.Trim() ?? "";
                if (string.IsNullOrWhiteSpace(hash) || hash.Equals(ctx.Hash, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(ctx.Artist)
                    && !ArtistNameMatcher.IsSameArtist(ctx.Artist, song.Artist))
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

        LogUrlFetchDiagnostic(ctx, quality, "final", null, lastFailReason ?? "all_modes_failed", null);
        return null;
    }

    private bool CanAcceptPreview() => !_settings.RequireFullPlayback;

    private IEnumerable<string> BuildUrlModes(bool includePreview)
    {
        if (includePreview)
        {
            yield return "auto";
            yield return "full";
            yield return "preview";
            yield break;
        }

        yield return "full";
        yield return "auto";
    }

    private bool ShouldRejectPreview(KugouUrlData data)
        => _settings.RequireFullPlayback && (data.IsPreview || LooksLikePreviewUrl(data.Url));

    private static void NormalizePreviewFlags(KugouUrlData data)
    {
        if (data.IsPreview || string.IsNullOrWhiteSpace(data.Url))
        {
            return;
        }

        if (LooksLikePreviewUrl(data.Url))
        {
            data.IsPreview = true;
        }
    }

    internal static bool LooksLikePreviewUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return false;
        }

        var low = url.ToLowerInvariant();
        return low.Contains("/yp/p_", StringComparison.Ordinal) || low.Contains("/yp/p/", StringComparison.Ordinal);
    }

    private void LogUrlFetchDiagnostic(
        KugouSongContext ctx,
        string quality,
        string mode,
        KugouUrlData? data,
        string? failReason,
        string? detail)
    {
        var song = Label(ctx);
        var hash = ctx.Hash?.Trim() ?? "";
        var isPreview = data?.IsPreview == true;
        var urlType = data == null || string.IsNullOrWhiteSpace(data.Url)
            ? "-"
            : (isPreview || LooksLikePreviewUrl(data.Url) ? "preview" : "full");
        var loginStatus = _lastLoggedIn ? "logged_in" : "not_logged_in";
        var vipType = string.IsNullOrWhiteSpace(_loginSnapshot.VipType) ? "-" : _loginSnapshot.VipType;
        var reason = string.IsNullOrWhiteSpace(failReason) ? "-" : failReason;
        var extra = string.IsNullOrWhiteSpace(detail) ? "" : $" detail={detail}";
        _log.KugouInfo(
            $"KUGOU_URL song={song} hash={hash} mode={mode} quality={quality} " +
            $"is_preview={isPreview} vip_type={vipType} login_status={loginStatus} " +
            $"result_url_type={urlType} fail_reason={reason}{extra}");
    }

    private static string ClassifySidecarFailure(int? code, string? msg, string mode)
    {
        var text = msg?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(text))
        {
            return "no_response";
        }

        // 会员曲无权益时 sidecar 常返回 502 + 「旧版扫码/登录态无效」，与真掉线区分开
        if (IsPrivilegeMaskedAsStaleSession(code, text))
        {
            return "copyright_restricted";
        }

        var low = text.ToLowerInvariant();
        if (IsSessionDroppedResponse(code, text) ||
            low.Contains("登录已掉线", StringComparison.Ordinal) ||
            low.Contains("需重新登录", StringComparison.Ordinal) ||
            low.Contains("session expired", StringComparison.Ordinal))
        {
            return "session_expired";
        }

        if (low.Contains("need vip", StringComparison.Ordinal) ||
            low.Contains("need pay", StringComparison.Ordinal) ||
            low.Contains("会员", StringComparison.Ordinal) ||
            low.Contains("付费", StringComparison.Ordinal))
        {
            return "copyright_restricted";
        }

        if (low.Contains("got preview url", StringComparison.Ordinal) ||
            low.Contains("试听链", StringComparison.Ordinal))
        {
            return "preview_rejected";
        }

        if (low.Contains("upstream", StringComparison.Ordinal) ||
            low.Contains("协议服务", StringComparison.Ordinal))
        {
            return "kgapijs_failed";
        }

        if (low.Contains("tracker", StringComparison.Ordinal))
        {
            return "tracker_failed";
        }

        if (low.Contains("未登录", StringComparison.Ordinal) && mode == "auto")
        {
            return "not_logged_in";
        }

        return "sidecar_error";
    }

    private static bool IsSessionDroppedResponse(KugouResponse<KugouUrlData>? result)
        => result != null && IsSessionDroppedResponse(result.Code, result.Msg);

    private static bool IsSessionDroppedResponse(int? code, string? message)
    {
        if (IsPrivilegeMaskedAsStaleSession(code, message))
        {
            return false;
        }

        return code == 40101 || IsStaleSessionMessage(message);
    }

    /// <summary>
    /// VIP/付费曲在权益失效时，sidecar 常误报「旧版扫码残留 / 登录态无效」(HTTP/业务码 502)，
    /// 但同账号免费曲与试听链仍可用。不能当真正掉线去清登录态。
    /// </summary>
    internal static bool IsPrivilegeMaskedAsStaleSession(int? code, string? message)
    {
        if (code is not (502 or 500))
        {
            return false;
        }

        var text = message?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        return text.Contains("旧版扫码", StringComparison.Ordinal)
               || text.Contains("登录态无效", StringComparison.Ordinal);
    }

    internal static bool IsStaleSessionMessage(string? message)
    {
        var text = message?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        return text.Contains("旧版扫码", StringComparison.Ordinal)
            || text.Contains("登录态无效", StringComparison.Ordinal)
            || text.Contains("登录已掉线", StringComparison.Ordinal)
            || text.Contains("需重新登录", StringComparison.Ordinal)
            || text.Contains("token 不匹配", StringComparison.OrdinalIgnoreCase)
            || text.Contains("20018", StringComparison.Ordinal);
    }

    private void InvalidateLoginCache()
    {
        Interlocked.Increment(ref _loginGeneration);
        _loginCheckedAt = DateTime.MinValue;
        _lastLoggedIn = false;
        _loginSnapshot = new KugouLoginSnapshot();
        _cachedFullPlaybackStatus = null;
        _fullPlaybackCheckedAt = DateTime.MinValue;
        _cachedProbeHash = null;
        _cachedProbeUrlData = null;
        _urlCache.Clear();

        // 丢弃可 join 的旧 refresh，避免后续 forceRefresh 吃到 generation 已失效的空结果
        if (_loginRefreshLock.Wait(0))
        {
            try
            {
                _loginRefreshTask = null;
            }
            finally
            {
                _loginRefreshLock.Release();
            }
        }
    }

    private static string BuildUrlCacheKey(KugouSongContext ctx)
        => $"{ctx.Hash?.Trim().ToLowerInvariant()}|{ctx.AlbumAudioId}|{ctx.AlbumId?.Trim()}";

    private bool TryGetCachedUrl(KugouSongContext ctx, out KugouUrlData? data)
    {
        data = null;
        if (string.IsNullOrWhiteSpace(ctx.Hash))
        {
            return false;
        }

        var key = BuildUrlCacheKey(ctx);
        if (!_urlCache.TryGetValue(key, out var entry) || DateTime.UtcNow >= entry.ExpiresAt)
        {
            _urlCache.Remove(key);
            return false;
        }

        data = entry.Data;
        return data != null && !string.IsNullOrWhiteSpace(data.Url);
    }

    private void CacheUrl(KugouSongContext ctx, KugouUrlData data)
    {
        if (string.IsNullOrWhiteSpace(ctx.Hash))
        {
            return;
        }

        _urlCache[BuildUrlCacheKey(ctx)] = (data, DateTime.UtcNow.Add(UrlCacheTtl));
    }

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

    public async Task<TrackInfo?> ResolveCandidateAsync(
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
        var track = await ResolveFromSongAsync(song, candidate.SongName, isRandom: false, requester, ct, tryAlternates: false);
        if (track != null)
        {
            return track;
        }

        return await ResolveMatchingSongAsync(
            candidate.Hash,
            candidate.SongId,
            candidate.AlbumAudioId,
            candidate.SongName,
            candidate.Artist,
            isRandom: false,
            requester,
            ct);
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

        var effectiveAlbumAudioId = albumAudioId > 0 ? albumAudioId : ParseLongId(songId);
        if (!string.IsNullOrWhiteSpace(songHash) || effectiveAlbumAudioId > 0)
        {
            var ctx = new KugouSongContext
            {
                Hash = songHash,
                Keyword = name,
                Artist = artist?.Trim(),
                AlbumId = albumId?.Trim(),
                AlbumAudioId = effectiveAlbumAudioId
            };
            // 点歌也允许同歌手备用 hash：主 hash 常因会话抖动失败，备用版可恢复取链
            var urlData = isRandom && _settings.RequireFullPlayback
                ? await GetPlayUrlCoreAsync(ctx, allowPreviewFallback: false, tryAlternates: true, ct)
                : await GetPlayUrlCoreAsync(ctx, allowPreviewFallback: true, tryAlternates: true, ct);
            if (urlData != null && !string.IsNullOrWhiteSpace(urlData.Url))
            {
                return BuildTrackInfo(
                    urlData,
                    songHash,
                    FirstNonEmpty(urlData.SongName, name),
                    FirstNonEmpty(urlData.Artist, artist),
                    FirstNonEmpty(urlData.SongId, urlData.Id, songId),
                    requester,
                    isRandom,
                    albumId,
                    effectiveAlbumAudioId);
            }

            _log.KugouWarn(
                $"hash 取链失败，尝试按原曲目标识重匹配: hash={songHash} album_audio_id={effectiveAlbumAudioId} song={name}");
            var matched = await ResolveMatchingSongAsync(
                songHash, songId, effectiveAlbumAudioId, name, artist, isRandom, requester, ct);
            if (matched != null)
            {
                return matched;
            }

            if (!isRandom)
            {
                _log.KugouWarn($"点歌曲目取链失败，拒绝播放其他版本: hash={songHash} song={name} artist={artist}");
                return null;
            }
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        return await ResolveTrackAsync(name, ct);
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
        CancellationToken ct,
        bool tryAlternates = true)
    {
        NormalizeKugouSongItem(song);
        var urlData = await GetPlayUrlCoreAsync(
            KugouSongContext.FromSong(song, keyword),
            allowPreviewFallback: true,
            tryAlternates,
            ct);
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

    private async Task<TrackInfo?> ResolveMatchingSongAsync(
        string? hash,
        string? songId,
        long albumAudioId,
        string songName,
        string? artist,
        bool isRandom,
        string? requester,
        CancellationToken ct)
    {
        var keyword = ComposeSearchKeyword(songName, artist);
        var songs = await SearchAsync(keyword, 1, 30, ct);
        foreach (var song in songs)
        {
            if (!SongIdentityMatches(song, hash, songId, albumAudioId))
            {
                continue;
            }

            var track = await ResolveFromSongAsync(song, songName, isRandom, requester, ct, tryAlternates: false);
            if (track != null)
            {
                return track;
            }
        }

        // 同歌手同名备用：随机补位与点歌确认均可，避免主 hash 会话抖动时直接失败
        if (!string.IsNullOrWhiteSpace(artist))
        {
            foreach (var song in songs)
            {
                if (!ArtistNameMatcher.IsSameArtist(artist, song.Artist)
                    || !SongNameMatches(song.SongName, songName))
                {
                    continue;
                }

                if (SongIdentityMatches(song, hash, songId, albumAudioId))
                {
                    continue;
                }

                var track = await ResolveFromSongAsync(song, songName, isRandom, requester, ct, tryAlternates: false);
                if (track != null)
                {
                    _log.KugouInfo(
                        $"同歌手备用取链成功: {song.SongName} - {song.Artist} hash={song.Hash} requested_hash={hash}");
                    return track;
                }
            }
        }

        return null;
    }

    private static bool SongNameMatches(string? left, string? right)
    {
        left = left?.Trim() ?? "";
        right = right?.Trim() ?? "";
        if (left.Length == 0 || right.Length == 0)
        {
            return false;
        }

        return left.Equals(right, StringComparison.OrdinalIgnoreCase)
               || left.Contains(right, StringComparison.OrdinalIgnoreCase)
               || right.Contains(left, StringComparison.OrdinalIgnoreCase);
    }

    private static KugouSongItem NormalizeKugouSongItem(KugouSongItem song)
    {
        song.Hash = song.Hash?.Trim().ToLowerInvariant() ?? "";
        if (song.AlbumAudioId <= 0)
        {
            if (long.TryParse(song.SongId?.Trim(), out var fromSongId) && fromSongId > 0)
            {
                song.AlbumAudioId = fromSongId;
            }
            else if (long.TryParse(song.Id?.Trim(), out var fromId) && fromId > 0)
            {
                song.AlbumAudioId = fromId;
            }
        }

        if (string.IsNullOrWhiteSpace(song.SongId))
        {
            song.SongId = song.AlbumAudioId > 0
                ? song.AlbumAudioId.ToString()
                : song.Id?.Trim() ?? "";
        }

        return song;
    }

    private static bool SongIdentityMatches(
        KugouSongItem song,
        string? hash,
        string? songId,
        long albumAudioId)
    {
        NormalizeKugouSongItem(song);
        if (albumAudioId > 0 && song.AlbumAudioId == albumAudioId)
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(hash)
            && hash.Equals(song.Hash, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var sid = FirstNonEmpty(song.SongId, song.Id);
        return !string.IsNullOrWhiteSpace(songId)
               && !string.IsNullOrWhiteSpace(sid)
               && songId.Equals(sid, StringComparison.OrdinalIgnoreCase);
    }

    private static string ComposeSearchKeyword(string songName, string? artist)
    {
        var name = songName?.Trim() ?? "";
        var art = artist?.Trim() ?? "";
        if (!string.IsNullOrWhiteSpace(art) && !string.IsNullOrWhiteSpace(name))
        {
            return $"{art} {name}";
        }

        return name;
    }

    private static long ParseLongId(string? value)
        => long.TryParse(value?.Trim(), out var id) && id > 0 ? id : 0;

    /// <summary>每日推荐专用：只取完整版链接，不降级试听。</summary>
    private async Task<TrackInfo?> ResolveDailyRecommendSongAsync(
        KugouSongItem song,
        string? keyword,
        CancellationToken ct)
    {
        var urlData = await GetPlayUrlCoreAsync(
            KugouSongContext.FromSong(song, keyword),
            allowPreviewFallback: false,
            tryAlternates: true,
            ct);
        if (urlData == null || string.IsNullOrWhiteSpace(urlData.Url))
        {
            return null;
        }

        var track = BuildTrackInfo(
            urlData,
            song.Hash ?? "",
            FirstNonEmpty(urlData.SongName, song.SongName, keyword),
            FirstNonEmpty(urlData.Artist, song.Artist),
            FirstNonEmpty(urlData.SongId, urlData.Id, song.SongId, song.Id),
            requester: "随机",
            isRandom: true,
            song.AlbumId,
            song.AlbumAudioId);
        return IsAcceptableForPlayback(track) ? track : null;
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
    {
        NormalizePreviewFlags(urlData);
        return new()
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
    }

    /// <summary>直播完整音模式：拒绝试听链（含 URL 路径检测）。</summary>
    public bool IsAcceptableForPlayback(TrackInfo? track)
    {
        if (track == null || string.IsNullOrWhiteSpace(track.PlayUrl))
        {
            return false;
        }

        if (!_settings.RequireFullPlayback)
        {
            return true;
        }

        return !track.IsPreview && !LooksLikePreviewUrl(track.PlayUrl);
    }

    private async Task<bool> EnsureLoginReadyAsync(bool forceRefresh, CancellationToken ct)
    {
        var cacheTtl = _settings.RequireFullPlayback ? TimeSpan.FromSeconds(30) : TimeSpan.FromSeconds(45);
        if (!forceRefresh && DateTime.UtcNow - _loginCheckedAt < cacheTtl)
        {
            return _lastLoggedIn;
        }

        await RefreshLoginStatusAsync(forceRefresh, ct);
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

    // 健康探测用免费/稳定曲目；旧 hash 对部分账号只能试听，会误报「完整版不可用」。
    private static readonly string[] FullPlaybackProbeHashes =
    [
        "69f342d52afb4ea64301a22d119d3ac0",
        "f15843ca55658254f674508ec64b5b63",
    ];

    /// <summary>探测完整版播放是否可用（独立于 ResolveTrack 取链逻辑）。</summary>
    public Task<KugouFullPlaybackStatus> CheckFullPlaybackStatusAsync(CancellationToken ct = default)
        => CheckFullPlaybackStatusAsync(null, forceProbe: false, ct);

    public async Task<KugouFullPlaybackStatus> CheckFullPlaybackStatusAsync(
        KugouLoginSnapshot? login,
        bool forceProbe,
        CancellationToken ct = default)
    {
        login ??= _loginSnapshot;
        if (!forceProbe
            && _cachedFullPlaybackStatus != null
            && DateTime.UtcNow - _fullPlaybackCheckedAt < FullPlaybackCacheTtl
            && _cachedFullPlaybackStatus.LoggedIn == login.LoggedIn
            && _cachedFullPlaybackStatus.VipLabel == login.VipLabel)
        {
            return _cachedFullPlaybackStatus;
        }

        var status = new KugouFullPlaybackStatus
        {
            LoggedIn = login.LoggedIn,
            VipLabel = login.VipLabel,
            CheckedAtUtc = DateTime.UtcNow
        };

        if (!login.LoggedIn)
        {
            status.FullPlaybackAvailable = false;
            status.Reason = "未登录";
            CacheFullPlaybackStatus(status);
            return status;
        }

        try
        {
            string? lastReason = null;
            foreach (var hash in FullPlaybackProbeHashes)
            {
                var probe = await HttpJson.PostAsync<KugouResponse<KugouUrlData>>(_client, "api/v1/song/url",
                    new { hash, mode = "full", quality = "auto" }, ct);
                if (IsFullPlaybackUrl(probe?.Data))
                {
                    status.FullPlaybackAvailable = true;
                    status.Reason = "";
                    _cachedProbeHash = hash;
                    _cachedProbeUrlData = probe!.Data;
                    CacheUrl(KugouSongContext.FromHash(hash, null), probe.Data!);
                    CacheFullPlaybackStatus(status);
                    return status;
                }

                lastReason = probe?.Msg?.Trim();
                if (IsSessionDroppedResponse(probe))
                {
                    InvalidateLoginCache();
                    status.LoggedIn = false;
                    status.FullPlaybackAvailable = false;
                    status.Reason = "登录态已失效，请点「酷狗登录」用手机酷狗重新扫码";
                    CacheFullPlaybackStatus(status);
                    return status;
                }

                if (IsPrivilegeMaskedAsStaleSession(probe?.Code, lastReason))
                {
                    // 探测曲若也返回伪会话，说明 VIP 权益异常，但不清登录
                    lastReason = "会员权益不可用（完整版受限）";
                }
            }

            status.FullPlaybackAvailable = false;
            status.Reason = string.IsNullOrWhiteSpace(lastReason) ? "完整版不可用" : lastReason;
            CacheFullPlaybackStatus(status);
            return status;
        }
        catch (Exception ex)
        {
            status.FullPlaybackAvailable = false;
            status.Reason = ex.Message;
            CacheFullPlaybackStatus(status);
            return status;
        }
    }

    private void CacheFullPlaybackStatus(KugouFullPlaybackStatus status)
    {
        _cachedFullPlaybackStatus = status;
        _fullPlaybackCheckedAt = DateTime.UtcNow;
    }

    private static bool IsFullPlaybackUrl(KugouUrlData? data)
        => data != null
            && !string.IsNullOrWhiteSpace(data.Url)
            && !data.IsPreview
            && !LooksLikePreviewUrl(data.Url);

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
