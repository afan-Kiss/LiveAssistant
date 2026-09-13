using LiveAssistant.Config;
using LiveAssistant.Models;
using LiveAssistant.Utils;

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

    public async Task<bool> HealthCheckAsync(CancellationToken ct = default)
    {
        try
        {
            var result = await HttpJson.GetAsync<DouyinEnvelope<DouyinHealthData>>(_client, "api/health", ct);
            return result?.Ok == true;
        }
        catch (Exception ex)
        {
            _log.Warn($"抖音健康检查失败: {ex.Message}");
            return false;
        }
    }

    public async Task<DouyinHealthData?> GetHealthAsync(CancellationToken ct = default)
    {
        var result = await HttpJson.GetAsync<DouyinEnvelope<DouyinHealthData>>(_client, "api/health", ct);
        return result?.Ok == true ? result.Data : null;
    }

    public async Task<DouyinRoomData?> ResolveRoomAsync(string webRid, CancellationToken ct = default)
    {
        var result = await HttpJson.PostAsync<DouyinEnvelope<DouyinRoomData>>(_client, "api/live/room/resolve",
            new { web_rid = webRid }, ct);
        return result?.Ok == true ? result.Data : null;
    }

    public async Task StartCollectAsync(string webRid, CancellationToken ct = default)
    {
        await HttpJson.PostAsync<DouyinEnvelope<object>>(_client, "api/live/collect/start",
            new { web_rid = webRid }, ct);
    }

    public async Task<DouyinDanmakuFeedData?> PollDanmakuAsync(string webRid, int after, int limit = 50, CancellationToken ct = default)
    {
        var result = await HttpJson.PostAsync<DouyinEnvelope<DouyinDanmakuFeedData>>(_client, "api/live/danmaku/feed",
            new { web_rid = webRid, after, limit }, ct);
        return result?.Ok == true ? result.Data : null;
    }

    public async Task<bool> SendMentionAsync(string webRid, string userId, string content, CancellationToken ct = default)
    {
        var result = await HttpJson.PostAsync<DouyinEnvelope<object>>(_client, "api/live/danmaku/mention",
            new { web_rid = webRid, user_id = userId, content }, ct);
        return result?.Ok == true;
    }

    public async Task<bool> ReconnectAsync(string webRid, CancellationToken ct = default)
    {
        var result = await HttpJson.PostAsync<DouyinEnvelope<object>>(_client, "api/live/room/reconnect",
            new { web_rid = webRid }, ct);
        return result?.Ok == true;
    }
}
