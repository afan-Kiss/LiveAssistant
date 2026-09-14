using LiveAssistant.Config;
using LiveAssistant.Models;
using LiveAssistant.Utils;
using System.Text.Json;

namespace LiveAssistant.Services;

public sealed class DouyinService
{
    private readonly DouyinSettings _settings;
    private readonly LogService _log;
    private readonly HttpClient _client;

    public DouyinService(DouyinSettings settings, LogService log)
    {
        _settings = settings;
        _log = log;
        _client = HttpJson.CreateClient(settings.BaseUrl, settings.ApiToken);
    }

    public Task<bool> HealthCheckAsync(CancellationToken ct = default)
        => SafeAsync("health", async () =>
        {
            var result = await HttpJson.GetAsync<DouyinEnvelope<DouyinHealthData>>(_client, "api/health", ct);
            return result?.Ok == true;
        }, false);

    public Task<DouyinHealthData?> GetHealthAsync(CancellationToken ct = default)
        => SafeAsync("get_health", async () =>
        {
            var result = await HttpJson.GetAsync<DouyinEnvelope<DouyinHealthData>>(_client, "api/health", ct);
            return result?.Ok == true ? result.Data : null;
        }, null);

    public Task<DouyinRoomData?> ResolveRoomAsync(string webRid, CancellationToken ct = default)
        => SafeAsync("resolve_room", async () =>
        {
            var result = await HttpJson.PostAsync<DouyinEnvelope<DouyinRoomData>>(_client, "api/live/room/resolve",
                new { web_rid = webRid }, ct);
            return result?.Ok == true ? result.Data : null;
        }, null);

    public Task StartCollectAsync(string webRid, CancellationToken ct = default)
        => SafeVoidAsync("collect_start", async () =>
        {
            await HttpJson.PostAsync<DouyinEnvelope<object>>(_client, "api/live/collect/start",
                new { web_rid = webRid }, ct);
        });

    public Task<DouyinDanmakuFeedData?> PollDanmakuAsync(string webRid, int after, int limit = 50, CancellationToken ct = default)
        => SafeAsync("poll_danmaku", async () =>
        {
            var result = await HttpJson.PostAsync<DouyinEnvelope<DouyinDanmakuFeedData>>(_client, "api/live/danmaku/feed",
                new { web_rid = webRid, after, limit }, ct);
            return result?.Ok == true ? result.Data : null;
        }, null);

    public Task<DouyinDanmakuFeedData?> PollAtDanmakuAsync(string webRid, int after, int limit = 50, CancellationToken ct = default)
        => SafeAsync("poll_at_danmaku", async () =>
        {
            var result = await HttpJson.PostAsync<DouyinEnvelope<DouyinDanmakuFeedData>>(_client, "api/live/danmaku/at/feed",
                new { web_rid = webRid, after, limit }, ct);
            return result?.Ok == true ? result.Data : null;
        }, null);

    public Task<bool> SendMentionAsync(string webRid, string userId, string content, CancellationToken ct = default)
        => SafeAsync("send_mention", async () =>
        {
            var result = await HttpJson.PostAsync<DouyinEnvelope<object>>(_client, "api/live/danmaku/mention",
                new { web_rid = webRid, user_id = userId, content }, ct);
            return result?.Ok == true;
        }, false);

    public Task<bool> ReconnectAsync(string webRid, CancellationToken ct = default)
        => SafeAsync("reconnect", async () =>
        {
            var result = await HttpJson.PostAsync<DouyinEnvelope<object>>(_client, "api/live/room/reconnect",
                new { web_rid = webRid }, ct);
            return result?.Ok == true;
        }, false);

    public Task<DouyinGiftFeedData?> PollGiftAsync(string webRid, int after, int limit = 50, CancellationToken ct = default)
        => SafeAsync("poll_gift", async () =>
        {
            var result = await HttpJson.PostAsync<DouyinEnvelope<DouyinGiftFeedData>>(_client, "api/live/gift/feed",
                new { web_rid = webRid, after, limit }, ct);
            return result?.Ok == true ? result.Data : null;
        }, null);

    public Task<DouyinCookieStatusData?> GetCookieStatusAsync(CancellationToken ct = default)
        => SafeAsync("cookie_status", async () =>
        {
            var result = await HttpJson.GetAsync<DouyinEnvelope<DouyinCookieStatusData>>(_client, "api/cookie", ct);
            return result?.Ok == true ? result.Data : null;
        }, null);

    public Task<bool> ModSilenceAsync(string webRid, string userId, string action, CancellationToken ct = default)
        => SafeAsync("mod_silence", async () =>
        {
            var result = await HttpJson.PostAsync<DouyinEnvelope<object>>(_client, "api/live/mod/silence",
                new { web_rid = webRid, user_id = userId, action }, ct);
            return result?.Ok == true;
        }, false);

    public Task<DouyinUser?> LookupUserAsync(string webRid, string keyword, CancellationToken ct = default)
        => SafeAsync("lookup_user", async () =>
        {
            var result = await HttpJson.PostAsync<DouyinEnvelope<JsonElement>>(_client, "api/live/user/lookup",
                new { web_rid = webRid, keyword }, ct);
            if (result?.Ok != true)
            {
                return null;
            }

            return ParseLookupUser(result.Data);
        }, null);

    internal static DouyinUser? ParseLookupUser(JsonElement data)
    {
        if (data.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in data.EnumerateArray())
            {
                var user = item.Deserialize<DouyinUser>(HttpJson.Options);
                if (user != null && !string.IsNullOrWhiteSpace(user.UserId))
                {
                    return user;
                }
            }

            return null;
        }

        if (data.ValueKind == JsonValueKind.Object)
        {
            if (data.TryGetProperty("user", out var nested))
            {
                return nested.Deserialize<DouyinUser>(HttpJson.Options);
            }

            return data.Deserialize<DouyinUser>(HttpJson.Options);
        }

        return null;
    }

    private async Task<T> SafeAsync<T>(string operation, Func<Task<T>> action, T fallback)
    {
        try
        {
            return await action();
        }
        catch (Exception ex)
        {
            _log.DouyinWarn($"{operation} 失败: {ex.Message}");
            _log.Error("douyin", operation, ex);
            return fallback;
        }
    }

    private async Task SafeVoidAsync(string operation, Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            _log.DouyinWarn($"{operation} 失败: {ex.Message}");
            _log.Error("douyin", operation, ex);
        }
    }
}
