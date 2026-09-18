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

    internal DouyinService(DouyinSettings settings, LogService log, HttpClient client)
    {
        _settings = settings;
        _log = log;
        _client = client;
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
            if (result?.Ok == true && result.Data != null)
            {
                _log.DouyinInfo(
                    $"DOUYIN_CDP_HEALTH loginOk={result.Data.LoginOk} nickname={result.Data.Nickname ?? ""}");
                return result.Data;
            }

            _log.DouyinWarn("DOUYIN_CDP_HEALTH result=fail");
            return null;
        }, null);

    public static bool IsWriteCredentialBlocked(DouyinHealthData? health)
    {
        if (health?.WriteGate is not { ValueKind: JsonValueKind.Object } gate)
        {
            return false;
        }

        if (gate.TryGetProperty("awaiting_initial_verification", out var awaiting) && awaiting.GetBoolean())
        {
            return true;
        }

        return gate.TryGetProperty("allowed", out var allowed) && !allowed.GetBoolean();
    }

    public static bool IsWriteCredentialPending(string? reason, int? httpStatus)
        => httpStatus == 423
           || (!string.IsNullOrWhiteSpace(reason)
               && (reason.Contains("凭据验证", StringComparison.Ordinal)
                   || reason.Contains("write_credential_unproven", StringComparison.OrdinalIgnoreCase)
                   || reason.Contains("local_blocked", StringComparison.OrdinalIgnoreCase)));

    public static bool IsBizAuthFailure(string? reason, int? httpStatus)
        => httpStatus == 400
           && !string.IsNullOrWhiteSpace(reason)
           && (reason.Contains("未登录", StringComparison.Ordinal)
               || reason.Contains("无权限", StringComparison.Ordinal)
               || reason.Contains("20003", StringComparison.Ordinal));

    public Task<bool> EnsureWriteGateReadyAsync(CancellationToken ct = default)
    {
        _ = ct;
        return Task.FromResult(true);
    }

    public Task<bool> ArmWriteCandidateProbeAsync(CancellationToken ct = default)
    {
        _ = ct;
        return Task.FromResult(false);
    }

    public Task<bool> VerifyWriteCredentialAsync(CancellationToken ct = default)
    {
        _ = ct;
        return Task.FromResult(false);
    }

    public Task<bool> ClearSendPauseAsync(string kind = "403", CancellationToken ct = default)
    {
        _ = (kind, ct);
        return Task.FromResult(false);
    }

    public Task<bool> ImportCookieAsync(
        string cookie,
        string? name = null,
        bool autoTicket = true,
        CancellationToken ct = default)
    {
        _ = (cookie, name, autoTicket, ct);
        return Task.FromResult(false);
    }

    public Task<bool> SyncCookieProfileAsync(CancellationToken ct = default)
    {
        _ = ct;
        return Task.FromResult(false);
    }

    public Task<DouyinRoomData?> ResolveRoomAsync(string webRid, CancellationToken ct = default)
        => SafeAsync("resolve_room", async () =>
        {
            var result = await HttpJson.PostAsync<DouyinEnvelope<DouyinRoomData>>(_client, "api/live/room/resolve",
                new { web_rid = webRid }, ct);
            return result?.Ok == true ? result.Data : null;
        }, null);

    public async Task StartCollectAsync(string webRid, CancellationToken ct = default)
    {
        var result = await HttpJson.PostAsync<DouyinEnvelope<object>>(
            _client, "api/live/collect/start", new { web_rid = webRid }, ct);
        if (result?.Ok != true)
        {
            var message = string.IsNullOrWhiteSpace(result?.Message) ? "collect/start 失败" : result!.Message!;
            _log.DouyinWarn($"DOUYIN_CDP_COLLECT stage=start webRid={webRid} result=fail errorCode={message}");
            throw new InvalidOperationException(message);
        }

        _log.DouyinInfo($"DOUYIN_CDP_COLLECT stage=start webRid={webRid} result=ok");
    }

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
            var detail = await SendMentionDetailedAsync(webRid, userId, content, ct);
            return detail.Ok;
        }, false);

    public Task<MentionSendResult> SendMentionDetailedAsync(
        string webRid, string userId, string content, CancellationToken ct = default)
        => SendMentionDetailedAsync(webRid, userId, content, cookie: null, ct);

    public Task<MentionSendResult> SendMentionDetailedAsync(
        string webRid,
        string userId,
        string content,
        string? cookie,
        CancellationToken ct = default)
        => SafeAsync("send_mention", async () =>
        {
            _ = cookie;
            return await PostMentionAsync(webRid, userId, content, cookie: null, ct);
        }, new MentionSendResult { Ok = false, ErrorReason = "request_failed", ReplyType = "mention" });

    public Task<List<DouyinCollectSession>?> ListCollectSessionsAsync(CancellationToken ct = default)
        => SafeAsync("collect_sessions", async () =>
        {
            var result = await HttpJson.GetAsync<DouyinEnvelope<List<DouyinCollectSession>>>(
                _client, "api/live/collect/sessions", ct);
            return result?.Ok == true ? result.Data : null;
        }, null);

    public Task<bool> StopCollectAsync(CancellationToken ct = default)
        => SafeAsync("collect_stop", async () =>
        {
            var result = await HttpJson.PostAsync<DouyinEnvelope<object>>(_client, "api/live/collect/stop", new { }, ct);
            var ok = result?.Ok == true;
            _log.DouyinInfo($"DOUYIN_CDP_COLLECT stage=stop result={(ok ? "ok" : "fail")}");
            return ok;
        }, false);

    public async Task StopOtherCollectSessionsAsync(string keepWebRid, CancellationToken ct = default)
    {
        var keep = keepWebRid.Trim();
        if (string.IsNullOrEmpty(keep))
        {
            return;
        }

        var sessions = await ListCollectSessionsAsync(ct);
        if (sessions == null)
        {
            return;
        }

        var otherRunning = false;
        foreach (var session in sessions)
        {
            if (!session.Running)
            {
                continue;
            }

            var webRid = session.WebRid?.Trim() ?? "";
            if (!string.Equals(webRid, keep, StringComparison.Ordinal))
            {
                otherRunning = true;
                break;
            }
        }

        if (!otherRunning)
        {
            return;
        }

        var stopped = await StopCollectAsync(ct);
        _log.DouyinInfo(stopped
            ? $"DOUYIN_CDP_COLLECT stage=stop_other webRid={keep} result=ok"
            : $"DOUYIN_CDP_COLLECT stage=stop_other webRid={keep} result=fail");
    }

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

    public Task<string?> ExportCookieAsync(CancellationToken ct = default)
        => SafeAsync("cookie_export", async () =>
        {
            var result = await HttpJson.GetAsync<DouyinEnvelope<DouyinCookieExportData>>(_client, "api/cookie/export", ct);
            var cookie = result?.Ok == true ? result.Data?.Cookie : null;
            if (string.IsNullOrWhiteSpace(cookie) || !SidecarCookieStore.HasSessionId(cookie))
            {
                _log.DouyinWarn("DOUYIN_CDP_COOKIE_EXPORT result=gift_cookie_unavailable");
                return null;
            }

            _log.DouyinInfo("DOUYIN_CDP_COOKIE_EXPORT result=ok");
            return cookie;
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

    internal static bool IsProfileMismatch(string? reason)
        => !string.IsNullOrWhiteSpace(reason)
           && reason.Contains("ProfileID", StringComparison.OrdinalIgnoreCase);

    private async Task<MentionSendResult> PostMentionAsync(
        string webRid,
        string userId,
        string content,
        string? cookie,
        CancellationToken ct)
    {
        var payload = new Dictionary<string, object?>
        {
            ["web_rid"] = webRid,
            ["user_id"] = userId,
            ["content"] = content
        };
        if (!string.IsNullOrWhiteSpace(cookie))
        {
            payload["cookie"] = cookie;
        }

        var json = JsonSerializer.Serialize(payload, HttpJson.Options);
        using var body = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
        using var response = await _client.PostAsync("api/live/danmaku/mention", body, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        DouyinEnvelope<object>? envelope = null;
        try
        {
            envelope = JsonSerializer.Deserialize<DouyinEnvelope<object>>(text, HttpJson.Options);
        }
        catch
        {
            // ignore parse errors; use raw body as reason
        }

        var ok = response.IsSuccessStatusCode && envelope?.Ok == true;
        var reason = ok
            ? ""
            : envelope?.Message ?? (text.Length > 160 ? text[..160] : text);
        return new MentionSendResult
        {
            Ok = ok,
            HttpStatus = (int)response.StatusCode,
            ErrorReason = reason,
            ReplyType = "mention",
            PlatformMessageId = TryExtractPlatformMessageId(text)
        };
    }

    /// <summary>尽力从侧车响应提取平台 msg_id；字段缺失时返回 null。</summary>
    internal static string? TryExtractPlatformMessageId(string rawJson)
    {
        if (string.IsNullOrWhiteSpace(rawJson))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(rawJson);
            var root = doc.RootElement;
            if (TryReadMsgId(root, out var id))
            {
                return id;
            }

            if (root.TryGetProperty("data", out var data))
            {
                if (TryReadMsgId(data, out id))
                {
                    return id;
                }

                if (data.ValueKind == JsonValueKind.Object
                    && data.TryGetProperty("message", out var message)
                    && TryReadMsgId(message, out id))
                {
                    return id;
                }
            }
        }
        catch
        {
            // ignore parse failures
        }

        return null;
    }

    private static bool TryReadMsgId(JsonElement el, out string? id)
    {
        id = null;
        if (el.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        foreach (var name in new[] { "msg_id", "message_id", "msgId", "messageId" })
        {
            if (!el.TryGetProperty(name, out var prop))
            {
                continue;
            }

            id = prop.ValueKind switch
            {
                JsonValueKind.String => prop.GetString(),
                JsonValueKind.Number => prop.GetRawText(),
                _ => null
            };
            if (!string.IsNullOrWhiteSpace(id))
            {
                id = id.Trim();
                return true;
            }
        }

        return false;
    }

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
