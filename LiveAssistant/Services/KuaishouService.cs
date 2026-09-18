using LiveAssistant.Config;
using LiveAssistant.Models;
using LiveAssistant.Utils;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LiveAssistant.Services;

/// <summary>
/// 快手侧车（ks-demo :18900）HTTP 客户端：状态 / 连接 / 发弹幕 / 拉 feed。
/// </summary>
public sealed class KuaishouService : IDisposable
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly ConfigManager _config;
    private readonly LogService _log;
    private readonly object _clientLock = new();
    private HttpClient _client;
    private int _disposed;
    private DateTime _lastHealthWarnUtc = DateTime.MinValue;
    private DateTime _lastFeedWarnUtc = DateTime.MinValue;

    public KuaishouService(ConfigManager config, LogService log)
    {
        _config = config;
        _log = log;
        _client = CreateClient(config.Settings.Kuaishou.BaseUrl);
    }

    private static HttpClient CreateClient(string? baseUrl)
    {
        var url = string.IsNullOrWhiteSpace(baseUrl) ? "http://127.0.0.1:18900" : baseUrl.Trim();
        var client = HttpJson.CreateClient(url);
        // 轮询侧不宜过长阻塞 Stop；单次请求上限 8s
        client.Timeout = TimeSpan.FromSeconds(8);
        return client;
    }

    public void ReloadBaseUrl()
    {
        var url = _config.Settings.Kuaishou.BaseUrl?.Trim();
        if (string.IsNullOrWhiteSpace(url))
        {
            url = "http://127.0.0.1:18900";
        }

        HttpClient old;
        lock (_clientLock)
        {
            old = _client;
            _client = CreateClient(url);
        }

        // 延迟释放，避免进行中的请求撞上 ObjectDisposedException
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(15));
                old.Dispose();
            }
            catch
            {
                // ignore
            }
        });
    }

    private HttpClient Client
    {
        get
        {
            lock (_clientLock)
            {
                return _client;
            }
        }
    }

    public static string RoomKey(string roomId)
        => $"ks:{(roomId ?? "").Trim()}";

    public static bool IsKuaishouRoom(string? roomKey)
        => !string.IsNullOrWhiteSpace(roomKey)
           && roomKey.StartsWith("ks:", StringComparison.OrdinalIgnoreCase);

    public static string ExtractRoomId(string? roomKey)
    {
        if (string.IsNullOrWhiteSpace(roomKey))
        {
            return "";
        }

        return IsKuaishouRoom(roomKey) ? roomKey["ks:".Length..].Trim() : roomKey.Trim();
    }

    public async Task<bool> HealthCheckAsync(CancellationToken ct = default)
    {
        try
        {
            using var resp = await Client.GetAsync("api/health", ct);
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            // 守护线程会周期性调用，避免日志刷屏
            if (DateTime.UtcNow - _lastHealthWarnUtc > TimeSpan.FromMinutes(2))
            {
                _lastHealthWarnUtc = DateTime.UtcNow;
                _log.KuaishouWarn($"health 失败: {ex.Message}");
            }

            return false;
        }
    }

    public async Task<KuaishouBridgeStatus?> GetBridgeStatusAsync(CancellationToken ct = default)
    {
        try
        {
            return await Client.GetFromJsonAsync<KuaishouBridgeStatus>("api/bridge/status", JsonOpts, ct);
        }
        catch (Exception ex)
        {
            if (DateTime.UtcNow - _lastFeedWarnUtc > TimeSpan.FromMinutes(2))
            {
                _lastFeedWarnUtc = DateTime.UtcNow;
                _log.KuaishouWarn($"bridge/status 失败: {ex.Message}");
            }

            return null;
        }
    }

    public async Task<(bool Ok, string Error)> ConnectAsync(string roomId, string cookie, CancellationToken ct = default)
    {
        try
        {
            using var resp = await Client.PostAsJsonAsync("api/connect", new { roomId, cookie }, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
            {
                return (false, TryExtractError(body) ?? $"HTTP {(int)resp.StatusCode}");
            }

            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
            if (doc.RootElement.TryGetProperty("ok", out var okEl) && okEl.ValueKind == JsonValueKind.False)
            {
                return (false, TryExtractError(body) ?? "连接失败");
            }

            return (true, "");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    public async Task DisconnectAsync(CancellationToken ct = default)
    {
        try
        {
            using var _ = await Client.PostAsJsonAsync("api/disconnect", new { }, ct);
        }
        catch (ObjectDisposedException)
        {
            // 关闭中
        }
        catch (Exception ex)
        {
            _log.KuaishouWarn($"disconnect 失败: {ex.Message}");
        }
    }

    public async Task<bool> SendAtReplyAsync(string nickname, string content, CancellationToken ct = default)
    {
        try
        {
            using var resp = await Client.PostAsJsonAsync("api/danmu", new
            {
                content,
                atName = nickname ?? ""
            }, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
            {
                _log.KuaishouWarn($"发弹幕失败: {TryExtractError(body) ?? resp.StatusCode.ToString()}");
                return false;
            }

            try
            {
                using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
                if (doc.RootElement.TryGetProperty("ok", out var okEl) && okEl.ValueKind == JsonValueKind.False)
                {
                    _log.KuaishouWarn($"发弹幕失败: {TryExtractError(body) ?? "ok=false"}");
                    return false;
                }

                if (doc.RootElement.TryGetProperty("data", out var data)
                    && data.ValueKind == JsonValueKind.Object
                    && data.TryGetProperty("ok", out var dataOk)
                    && dataOk.ValueKind == JsonValueKind.False)
                {
                    _log.KuaishouWarn($"发弹幕失败: {TryExtractError(body) ?? "data.ok=false"}");
                    return false;
                }
            }
            catch (JsonException)
            {
                // 非 JSON 但 HTTP 成功：保守视为成功
            }

            return true;
        }
        catch (Exception ex)
        {
            _log.KuaishouWarn($"发弹幕异常: {ex.Message}");
            return false;
        }
    }

    public async Task<KuaishouFeedData?> GetDanmakuFeedAsync(long after, bool catchUp, CancellationToken ct = default)
    {
        try
        {
            var path = $"api/live/danmaku/feed?after={after}&catch_up={(catchUp ? "1" : "0")}";
            var env = await Client.GetFromJsonAsync<KuaishouEnvelope<KuaishouFeedData>>(path, JsonOpts, ct);
            return env?.Data;
        }
        catch (Exception ex)
        {
            if (DateTime.UtcNow - _lastFeedWarnUtc > TimeSpan.FromMinutes(1))
            {
                _lastFeedWarnUtc = DateTime.UtcNow;
                _log.KuaishouWarn($"danmaku feed 失败: {ex.Message}");
            }

            return null;
        }
    }

    public async Task<KuaishouGiftFeedData?> GetGiftFeedAsync(long after, bool catchUp, CancellationToken ct = default)
    {
        try
        {
            var path = $"api/live/gift/feed?after={after}&catch_up={(catchUp ? "1" : "0")}";
            var env = await Client.GetFromJsonAsync<KuaishouEnvelope<KuaishouGiftFeedData>>(path, JsonOpts, ct);
            return env?.Data;
        }
        catch (Exception ex)
        {
            if (DateTime.UtcNow - _lastFeedWarnUtc > TimeSpan.FromMinutes(1))
            {
                _lastFeedWarnUtc = DateTime.UtcNow;
                _log.KuaishouWarn($"gift feed 失败: {ex.Message}");
            }

            return null;
        }
    }

    private static string? TryExtractError(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var e))
            {
                return e.GetString();
            }

            if (doc.RootElement.TryGetProperty("message", out var m))
            {
                return m.GetString();
            }
        }
        catch
        {
            // ignore
        }

        return null;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        HttpClient client;
        lock (_clientLock)
        {
            client = _client;
        }

        try { client.Dispose(); } catch { /* ignore */ }
    }
}

public sealed class KuaishouEnvelope<T>
{
    [JsonPropertyName("ok")]
    public bool Ok { get; set; }

    [JsonPropertyName("data")]
    public T? Data { get; set; }

    [JsonPropertyName("error")]
    public string? Error { get; set; }
}

public sealed class KuaishouBridgeStatus
{
    [JsonPropertyName("ok")]
    public bool Ok { get; set; }

    [JsonPropertyName("connected")]
    public bool Connected { get; set; }

    [JsonPropertyName("status")]
    public string? Status { get; set; }

    [JsonPropertyName("roomId")]
    public string? RoomId { get; set; }

    [JsonPropertyName("roomTitle")]
    public string? RoomTitle { get; set; }

    [JsonPropertyName("liveStreamId")]
    public string? LiveStreamId { get; set; }

    [JsonPropertyName("latestSeq")]
    public long LatestSeq { get; set; }
}

public sealed class KuaishouFeedData
{
    [JsonPropertyName("items")]
    public List<KuaishouDanmakuMessage>? Items { get; set; }

    [JsonPropertyName("message_count")]
    public long MessageCount { get; set; }

    [JsonPropertyName("running")]
    public bool Running { get; set; }
}

public sealed class KuaishouDanmakuMessage
{
    [JsonPropertyName("msg_id")]
    public string? MsgId { get; set; }

    [JsonPropertyName("seq")]
    public long Seq { get; set; }

    [JsonPropertyName("content")]
    public string? Content { get; set; }

    [JsonPropertyName("msg_type")]
    public string? MsgType { get; set; }

    [JsonPropertyName("user")]
    public KuaishouUser? User { get; set; }

    [JsonPropertyName("timestamp")]
    public string? Timestamp { get; set; }
}

public sealed class KuaishouUser
{
    [JsonPropertyName("user_id")]
    public string? UserId { get; set; }

    [JsonPropertyName("nickname")]
    public string? Nickname { get; set; }
}

public sealed class KuaishouGiftFeedData
{
    [JsonPropertyName("items")]
    public List<KuaishouGiftMessage>? Items { get; set; }

    [JsonPropertyName("gift_count")]
    public long GiftCount { get; set; }

    [JsonPropertyName("running")]
    public bool Running { get; set; }
}

public sealed class KuaishouGiftMessage
{
    [JsonPropertyName("msg_id")]
    public string? MsgId { get; set; }

    [JsonPropertyName("seq")]
    public long Seq { get; set; }

    [JsonPropertyName("user_id")]
    public string? UserId { get; set; }

    [JsonPropertyName("nickname")]
    public string? Nickname { get; set; }

    [JsonPropertyName("user")]
    public KuaishouUser? User { get; set; }

    [JsonPropertyName("gift_name")]
    public string? GiftName { get; set; }

    [JsonPropertyName("gift_count")]
    public int GiftCount { get; set; }

    [JsonPropertyName("timestamp")]
    public string? Timestamp { get; set; }
}
