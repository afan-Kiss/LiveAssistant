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
    private readonly SemaphoreSlim _dailyRecommendLock = new(1, 1);
    private List<KugouSongItem> _dailyRecommendPlaylist = new();
    private int _dailyRecommendIndex;
    private string? _dailyRecommendSessionUserId;

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

            return result.Data.Songs;
        }, new List<KugouSongItem>());

    public Task<KugouSongItem?> SearchFirstAsync(string keyword, CancellationToken ct = default)
        => SafeAsync("search", async () =>
        {
            var songs = await SearchAsync(keyword, 1, 10, ct);
            return songs.Count == 0 ? null : songs[0];
        }, null);

    public Task<KugouLoginSnapshot> RefreshLoginStatusAsync(CancellationToken ct = default)
    {
        var inFlight = _loginRefreshTask;
        if (inFlight != null && !inFlight.IsCompleted)
        {
            return AwaitLoginRefresh(inFlight, ct);
        }

        return StartOrJoinLoginRefreshAsync(ct);
    }

    private static async Task<KugouLoginSnapshot> AwaitLoginRefresh(Task<KugouLoginSnapshot> task, CancellationToken ct)
        => await task.WaitAsync(ct);

    private async Task<KugouLoginSnapshot> StartOrJoinLoginRefreshAsync(CancellationToken ct)
    {
        Task<KugouLoginSnapshot> task;
        await _loginRefreshLock.WaitAsync(ct);
        try
        {
            if (_loginRefreshTask == null || _loginRefreshTask.IsCompleted)
            {
                _loginRefreshTask = Task.Run(async () => await FetchLoginStatusAsync(ct), ct);
            }

            task = _loginRefreshTask;
        }
        finally
        {
            _loginRefreshLock.Release();
        }

        return await task.WaitAsync(ct);
    }

    private async Task<KugouLoginSnapshot> FetchLoginStatusAsync(CancellationToken ct)
    {
        var status = await HttpJson.GetAsync<KugouResponse<KugouLoginStatusData>>(
            _client, "api/v1/login/status?refresh=1", ct);
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

            var result = await ClaimDailyVipAsync(ct);
            if (result != null && !force)
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

    /// <summary>从酷狗「每日推荐」歌单顺序取下一首可播放曲目；播完歌单后自动重新拉取。</summary>
    public async Task<TrackInfo?> PickRandomTrackAsync(
        Func<string?, string?, bool> wasRecentlyPlayed,
        CancellationToken ct = default)
    {
        _ = wasRecentlyPlayed;
        if (_settings.RequireFullPlayback)
        {
            await EnsureLoginReadyAsync(forceRefresh: false, ct);
            if (!_lastLoggedIn)
            {
                _log.KugouWarn("每日推荐完整播放需要先扫码登录酷狗");
                return null;
            }

            await TryAutoClaimVipAsync(ct);
        }

        const int maxRefreshAttempts = 3;

        for (var refreshAttempt = 0; refreshAttempt < maxRefreshAttempts; refreshAttempt++)
        {
            var triedInBatch = 0;
            while (triedInBatch < 200)
            {
                var song = await TakeNextDailyRecommendSongAsync(ct);
                if (song == null)
                {
                    break;
                }

                triedInBatch++;
                var keyword = BuildDailyRecommendKeyword(song);
                var track = await ResolveDailyRecommendSongAsync(song, keyword, ct);
                if (track != null && IsAcceptableForPlayback(track))
                {
                    return track;
                }
            }

            if (triedInBatch == 0)
            {
                _log.KugouWarn("每日推荐歌单为空");
                return null;
            }

            await ResetDailyRecommendPlaylistAsync(ct);
            _log.KugouWarn("每日推荐歌单内歌曲均无法播放，正在重新获取");
        }

        _log.KugouWarn("每日推荐取歌失败，已重试多次");
        return null;
    }

    public Task<List<KugouSongItem>> FetchEverydayRecommendAsync(CancellationToken ct = default)
        => SafeAsync("everyday_recommend", () => FetchEverydayRecommendCoreAsync(ct), new List<KugouSongItem>());

    private async Task<List<KugouSongItem>> FetchEverydayRecommendCoreAsync(CancellationToken ct)
    {
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

        var moeSongs = await TryFetchEverydayFromMoeAsync(ct);
        if (moeSongs.Count > 0)
        {
            return moeSongs;
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
                _dailyRecommendPlaylist = songs;
                _dailyRecommendIndex = 0;
                _dailyRecommendSessionUserId = ResolveDailyRecommendUserId();
                _log.KugouInfo($"已获取每日推荐歌单，共 {_dailyRecommendPlaylist.Count} 首");
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
        => SafeAsync("song_url", () => GetPlayUrlCoreAsync(ctx, ct), null);

    private async Task<KugouUrlData?> GetPlayUrlCoreAsync(KugouSongContext ctx, CancellationToken ct)
        => await GetPlayUrlCoreAsync(ctx, allowPreviewFallback: true, tryAlternates: true, ct);

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
            await RefreshLoginStatusAsync(ct);
            if (!_lastLoggedIn)
            {
                LogUrlFetchDiagnostic(ctx, quality, "precheck", null, null, "login_required");
                return null;
            }
        }

        var previewAllowed = allowPreviewFallback && CanAcceptPreview();
        string? lastFailReason = null;

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
                if (IsSessionDroppedResponse(result))
                {
                    InvalidateLoginCache();
                    lastFailReason = "session_expired";
                    LogUrlFetchDiagnostic(ctx, quality, mode, result?.Data, lastFailReason, null);
                    await RefreshLoginStatusAsync(ct);
                    break;
                }

                if (result?.Code != 0 || string.IsNullOrWhiteSpace(result.Data?.Url))
                {
                    lastFailReason = ClassifySidecarFailure(result?.Msg, mode);
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

                LogUrlFetchDiagnostic(ctx, quality, mode, result.Data, "ok", null);
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

    private static string ClassifySidecarFailure(string? msg, string mode)
    {
        var text = msg?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(text))
        {
            return "no_response";
        }

        var low = text.ToLowerInvariant();
        if (low.Contains("登录已掉线", StringComparison.Ordinal) ||
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
        => result is { Code: 40101 };

    private void InvalidateLoginCache()
    {
        _loginCheckedAt = DateTime.MinValue;
        _lastLoggedIn = false;
        _loginSnapshot = new KugouLoginSnapshot();
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
            var urlData = isRandom && _settings.RequireFullPlayback
                ? await GetPlayUrlCoreAsync(ctx, allowPreviewFallback: false, tryAlternates: true, ct)
                : await GetPlayUrlAsync(ctx, ct);
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

    /// <summary>探测完整版播放是否可用（独立于 ResolveTrack 取链逻辑）。</summary>
    public async Task<KugouFullPlaybackStatus> CheckFullPlaybackStatusAsync(CancellationToken ct = default)
    {
        var login = await RefreshLoginStatusAsync(ct);
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
            return status;
        }

        try
        {
            var probe = await HttpJson.PostAsync<KugouResponse<KugouUrlData>>(_client, "api/v1/song/url",
                new { hash = "8574d02543b5f902469fb4e27e3a350d", mode = "full", quality = "auto" }, ct);
            if (probe?.Code == 0 && !string.IsNullOrWhiteSpace(probe.Data?.Url))
            {
                status.FullPlaybackAvailable = true;
                status.Reason = "";
                return status;
            }

            var reason = probe?.Msg?.Trim() ?? "完整版不可用";
            if (reason.Contains("旧版扫码", StringComparison.Ordinal) || reason.Contains("登录态无效", StringComparison.Ordinal))
            {
                reason = "session失效，请重新扫码登录";
            }

            status.FullPlaybackAvailable = false;
            status.Reason = reason;
            return status;
        }
        catch (Exception ex)
        {
            status.FullPlaybackAvailable = false;
            status.Reason = ex.Message;
            return status;
        }
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
