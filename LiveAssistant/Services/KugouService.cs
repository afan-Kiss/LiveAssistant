using LiveAssistant.Config;
using LiveAssistant.Models;
using LiveAssistant.Utils;

namespace LiveAssistant.Services;

public sealed class KugouService
{
    private readonly KugouSettings _settings;
    private readonly LogService _log;
    private readonly HttpClient _client;

    public KugouService(KugouSettings settings, LogService log)
    {
        _settings = settings;
        _log = log;
        _client = HttpJson.CreateClient(settings.BaseUrl, settings.ApiKey, "X-API-Key");
    }

    public Task<bool> HealthCheckAsync(CancellationToken ct = default)
        => SafeAsync("health", async () =>
        {
            var result = await HttpJson.GetAsync<KugouResponse<object>>(_client, "health", ct);
            return result?.Code == 0;
        }, false);

    public Task<KugouSongItem?> SearchFirstAsync(string keyword, CancellationToken ct = default)
        => SafeAsync("search", async () =>
        {
            var result = await HttpJson.PostAsync<KugouResponse<KugouSearchData>>(_client, "api/v1/search",
                new { keyword, page = 1, pagesize = 10 }, ct);
            if (result?.Code != 0 || result.Data?.Songs == null || result.Data.Songs.Count == 0)
            {
                return null;
            }
            return result.Data.Songs[0];
        }, null);

    public Task<KugouUrlData?> GetPlayUrlAsync(string? hash, string? keyword, CancellationToken ct = default)
        => SafeAsync("song_url", async () =>
        {
            object body = !string.IsNullOrWhiteSpace(hash)
                ? new { hash, mode = "auto", quality = "auto" }
                : new { keyword, mode = "auto", quality = "auto" };

            var result = await HttpJson.PostAsync<KugouResponse<KugouUrlData>>(_client, "api/v1/song/url", body, ct);
            if (result?.Code != 0 || string.IsNullOrWhiteSpace(result.Data?.Url))
            {
                _log.KugouWarn($"取链失败: {result?.Msg ?? "无响应"}");
                return null;
            }
            return result.Data;
        }, null);

    public async Task<TrackInfo?> ResolveTrackAsync(string keyword, CancellationToken ct = default)
    {
        var song = await SearchFirstAsync(keyword, ct);
        if (song == null)
        {
            return null;
        }

        var hash = song.Hash ?? "";
        var urlData = await GetPlayUrlAsync(hash, keyword, ct);
        if (urlData == null)
        {
            return null;
        }

        return new TrackInfo
        {
            SongName = urlData.SongName ?? song.SongName ?? keyword,
            Artist = urlData.Artist ?? song.Artist ?? "",
            SongId = urlData.SongId ?? urlData.Id ?? song.SongId ?? song.Id ?? "",
            Hash = hash,
            PlayUrl = urlData.Url,
            DurationSec = song.Duration
        };
    }

    public async Task<TrackInfo?> ResolveTrackFromItemAsync(RandomPlaylistItem item, CancellationToken ct = default)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(item.Hash))
            {
                var urlData = await GetPlayUrlAsync(item.Hash, item.Keyword, ct);
                if (urlData != null)
                {
                    return new TrackInfo
                    {
                        SongName = urlData.SongName ?? item.Title ?? item.Keyword,
                        Artist = urlData.Artist ?? item.Artist ?? "",
                        SongId = urlData.SongId ?? urlData.Id ?? item.SongId ?? "",
                        Hash = item.Hash,
                        PlayUrl = urlData.Url,
                        IsRandom = true
                    };
                }
            }

            var keyword = !string.IsNullOrWhiteSpace(item.Keyword) ? item.Keyword : item.Title ?? "";
            if (string.IsNullOrWhiteSpace(keyword))
            {
                return null;
            }

            var track = await ResolveTrackAsync(keyword, ct);
            if (track != null)
            {
                track.IsRandom = true;
            }
            return track;
        }
        catch (Exception ex)
        {
            _log.KugouWarn($"解析曲目失败: {ex.Message}");
            _log.Error("kugou", "resolve_track_from_item", ex);
            return null;
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
