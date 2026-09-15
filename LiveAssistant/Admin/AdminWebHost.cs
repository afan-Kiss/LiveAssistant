using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LiveAssistant.Config;
using LiveAssistant.Models;
using LiveAssistant.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.FileProviders;

namespace LiveAssistant.Admin;

public sealed class AdminWebHost : IDisposable
{
    private readonly AdminAppContext _ctx;
    private readonly Dictionary<string, DateTime> _sessions = new();
    private WebApplication? _app;

    public AdminWebHost(AdminAppContext ctx) => _ctx = ctx;

    public void Start()
    {
        if (!_ctx.Config.Settings.Admin.Enabled || _app != null)
        {
            return;
        }

        var port = _ctx.Config.Settings.Admin.Port;
        var pathBase = _ctx.Config.Settings.Admin.Path.TrimEnd('/');

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ApplicationName = typeof(AdminWebHost).Assembly.FullName,
            Args = Array.Empty<string>()
        });
        builder.WebHost.UseUrls($"http://127.0.0.1:{port}");

        _app = builder.Build();
        if (!string.IsNullOrWhiteSpace(pathBase))
        {
            _app.UsePathBase(pathBase);
        }

        var wwwroot = Path.Combine(AppPaths.ExeDirectory, "Admin", "wwwroot");
        if (Directory.Exists(wwwroot))
        {
            _app.UseStaticFiles(new StaticFileOptions
            {
                FileProvider = new PhysicalFileProvider(wwwroot),
                RequestPath = ""
            });
        }

        // 云端/前端 health 探活用，无需登录（index.html 请求 /health）
        _app.MapGet("/health", () => Results.Json(new { ok = true, ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds() }));
        _app.MapGet("/api/ping", () => Results.Json(new { ok = true, ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds() }));
        MapRoutes(_app, pathBase);
        _ = _app.RunAsync();
    }

    private void MapRoutes(WebApplication app, string pathBase)
    {
        app.MapPost("/api/auth/login", (LoginRequest req) =>
        {
            var admin = _ctx.Config.Settings.Admin;
            if (!admin.HasAnyPasswordConfigured())
            {
                return Results.Json(new { ok = false, message = "未配置后台账号，请在 appsettings admin.accounts 中设置" });
            }

            if (admin.ValidateCredentials(req.Username, req.Password))
            {
                var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
                _sessions[token] = DateTime.Now.AddHours(12);
                return Results.Json(new { ok = true, token });
            }

            return Results.Json(new { ok = false, message = "用户名或密码错误" });
        });

        app.MapGet("/api/status", (HttpContext http) => Auth(http, () =>
        {
            var status = _ctx.Host.GetRuntimeStatus();
            var nowPlaying = _ctx.Host.Queue.NowPlaying;
            var track = _ctx.Host.PlaybackCommands.Playback.CurrentTrack;
            return Results.Json(new
            {
                douyinOnline = status.DouyinOnline,
                kugouOnline = status.KugouOnline,
                kugouLoginStatus = status.KugouLoginStatus,
                kugouVipLabel = status.KugouVipLabel,
                kugouFullPlaybackAvailable = status.KugouFullPlaybackAvailable,
                kugouFullPlaybackReason = status.KugouFullPlaybackReason,
                kugouLoginPageUrl = status.KugouLoginPageUrl,
                danmakuConnection = status.DanmakuConnection,
                roomOwnerNickname = status.RoomOwnerNickname,
                douyinLoginStatus = status.DouyinLoginStatus,
                douyinLoginNickname = status.DouyinLoginNickname,
                adminAccountStatus = status.AdminAccountStatus,
                adminNickname = status.AdminNickname,
                currentSong = status.CurrentSong,
                nowPlayingUser = nowPlaying?.Nickname,
                nowPlayingSong = nowPlaying?.SongName,
                nowPlayingArtist = track?.Artist ?? nowPlaying?.Artist,
                playbackMode = status.PlaybackMode,
                playbackState = status.PlaybackState,
                playbackSource = status.PlaybackSource,
                progressSec = status.ProgressSec,
                durationSec = status.DurationSec,
                remainingSec = status.RemainingSec,
                songRequestEnabled = status.SongRequestEnabled,
                randomFillEnabled = _ctx.Config.Settings.Playback.RandomFillEnabled,
                queueCount = status.QueueCount,
                waitingQueueCount = status.WaitingQueueCount,
                uptime = status.Uptime.ToString(),
                startedAt = status.StartedAt.ToString("O"),
                currentTask = status.CurrentTask,
                lastError = status.LastError,
                todaySongsPlayed = status.TodaySongsPlayed,
                todayDanmakuCount = status.TodayDanmakuCount,
                todayGiftCount = status.TodayGiftCount,
                todaySongRequestCount = status.TodaySongRequestCount,
                recentErrorCount = status.RecentErrorCount
            });
        }));

        app.MapPost("/api/playback/skip", (HttpContext http) => Auth(http, () => Cmd(AdminCommandType.Skip)));
        app.MapPost("/api/playback/pause", (HttpContext http) => Auth(http, () => Cmd(AdminCommandType.Pause)));
        app.MapPost("/api/playback/resume", (HttpContext http) => Auth(http, () => Cmd(AdminCommandType.Resume)));
        app.MapPost("/api/playback/resume-play", (HttpContext http) => Auth(http, () => Cmd(AdminCommandType.Resume)));
        app.MapPost("/api/playback/play", (HttpContext http) => Auth(http, () => Cmd(AdminCommandType.Play)));
        app.MapPost("/api/playback/previous", (HttpContext http) => Auth(http, () => Cmd(AdminCommandType.Previous)));
        app.MapPost("/api/playback/random/on", (HttpContext http) => Auth(http, () =>
        {
            _ctx.Commands.Enqueue(AdminCommandType.EnableRandomMode);
            return Results.Json(new { ok = true });
        }));
        app.MapPost("/api/playback/random/off", (HttpContext http) => Auth(http, () =>
        {
            _ctx.Commands.Enqueue(AdminCommandType.DisableRandomMode);
            return Results.Json(new { ok = true });
        }));
        app.MapPost("/api/playback/random-fill", (RandomToggleRequest req, HttpContext http) => Auth(http, () =>
        {
            _ctx.Config.Settings.Playback.RandomFillEnabled = req.Enabled;
            _ctx.Config.Save();
            _ctx.Commands.Enqueue(AdminCommandType.SetRandomFill, req.Enabled.ToString());
            return Results.Json(new { ok = true });
        }));
        app.MapPost("/api/playback/clear-queue", (HttpContext http) => Auth(http, () => Cmd(AdminCommandType.ClearQueue)));

        app.MapGet("/api/queue", (HttpContext http) => Auth(http, () =>
        {
            var now = _ctx.Host.Queue.NowPlaying;
            var waiting = _ctx.Host.Queue.Waiting.Select((x, i) => new
            {
                x.Id, index = i + 1, x.UserId, x.Nickname, x.SongName, x.Artist,
                status = x.Status.ToString(), time = x.CreatedAt.ToString("HH:mm:ss")
            });
            return Results.Json(new
            {
                nowPlaying = now == null ? null : new { now.Id, now.Nickname, now.SongName, now.Artist },
                waiting
            });
        }));

        app.MapDelete("/api/queue/{id:long}", (long id, HttpContext http) => Auth(http, () => Cmd(AdminCommandType.DeleteQueueItem, id.ToString())));
        app.MapPost("/api/queue/{id:long}/pin", (long id, HttpContext http) => Auth(http, () => Cmd(AdminCommandType.PinQueueItem, id.ToString())));
        app.MapPost("/api/queue/{id:long}/play", (long id, HttpContext http) => Auth(http, () => Cmd(AdminCommandType.PlayNow, id.ToString())));

        app.MapGet("/api/templates", (HttpContext http) => Auth(http, () =>
            Results.Json(_ctx.ReplyTemplates.GetAll())));

        app.MapPut("/api/templates", (Dictionary<string, string> body, HttpContext http) => Auth(http, () =>
        {
            _ctx.ReplyTemplates.SaveAll(body);
            _ctx.Settings.ApplyDbToMemory();
            _ctx.Commands.Enqueue(AdminCommandType.ReloadConfig);
            return Results.Json(new { ok = true });
        }));

        app.MapGet("/api/random-pool", (HttpContext http) => Auth(http, () =>
            Results.Json(_ctx.RandomPool.ListAll())));

        app.MapPost("/api/random-pool", (RandomPoolItem item, HttpContext http) => Auth(http, () =>
        {
            var id = _ctx.RandomPool.Add(item);
            _ctx.Settings.ApplyDbToMemory();
            _ctx.Commands.Enqueue(AdminCommandType.ReloadConfig);
            return Results.Json(new { ok = true, id });
        }));

        app.MapPut("/api/random-pool/{id:long}", (long id, RandomPoolItem item, HttpContext http) => Auth(http, () =>
        {
            item.Id = id;
            _ctx.RandomPool.Update(item);
            _ctx.Settings.ApplyDbToMemory();
            _ctx.Commands.Enqueue(AdminCommandType.ReloadConfig);
            return Results.Json(new { ok = true });
        }));

        app.MapDelete("/api/random-pool/{id:long}", (long id, HttpContext http) => Auth(http, () =>
        {
            _ctx.RandomPool.Remove(id);
            _ctx.Settings.ApplyDbToMemory();
            _ctx.Commands.Enqueue(AdminCommandType.ReloadConfig);
            return Results.Json(new { ok = true });
        }));

        app.MapGet("/api/gift-rules", (HttpContext http) => Auth(http, () =>
            Results.Json(_ctx.GiftRules.ListAll())));

        app.MapPost("/api/gift-rules", (GiftRule rule, HttpContext http) => Auth(http, () =>
        {
            var id = _ctx.GiftRules.Add(rule);
            return Results.Json(new { ok = true, id });
        }));

        app.MapPut("/api/gift-rules/{id:long}", (long id, GiftRule rule, HttpContext http) => Auth(http, () =>
        {
            rule.Id = id;
            _ctx.GiftRules.Update(rule);
            return Results.Json(new { ok = true });
        }));

        app.MapDelete("/api/gift-rules/{id:long}", (long id, HttpContext http) => Auth(http, () =>
        {
            _ctx.GiftRules.Remove(id);
            return Results.Json(new { ok = true });
        }));

        app.MapGet("/api/song-request-policy", (HttpContext http) => Auth(http, () =>
            Results.Json(_ctx.SongRequestControl.GetSettings())));

        app.MapPut("/api/song-request-policy", (SongRequestControlSettings body, HttpContext http) => Auth(http, () =>
        {
            _ctx.SongRequestControl.SaveSettings(body);
            _ctx.Commands.Enqueue(AdminCommandType.ReloadConfig);
            return Results.Json(new { ok = true });
        }));

        app.MapGet("/api/song-request-control", (HttpContext http) => Auth(http, () =>
            Results.Json(_ctx.SongRequestControl.GetSettings())));

        app.MapPut("/api/song-request-control", (SongRequestControlSettings body, HttpContext http) => Auth(http, () =>
        {
            _ctx.SongRequestControl.SaveSettings(body);
            _ctx.Commands.Enqueue(AdminCommandType.ReloadConfig);
            return Results.Json(new { ok = true });
        }));

        app.MapPost("/api/song-request/toggle", (EnabledRequest req, HttpContext http) => Auth(http, () =>
        {
            _ctx.SongRequestControl.SetRequestEnabled(req.Enabled);
            _ctx.Commands.Enqueue(AdminCommandType.ReloadConfig);
            return Results.Json(new { ok = true, enabled = req.Enabled });
        }));

        app.MapGet("/api/emergency", (HttpContext http) => Auth(http, () =>
        {
            var emergency = _ctx.Config.Settings.Emergency;
            return Results.Json(new
            {
                pauseInteraction = emergency.PauseInteraction,
                pauseSongRequest = emergency.PauseSongRequest,
                songRequestEnabled = _ctx.SongRequestControl.IsRequestEnabled
            });
        }));

        app.MapPost("/api/emergency/pause-interaction", (EnabledRequest req, HttpContext http) => Auth(http, () =>
        {
            _ctx.Config.Settings.Emergency.PauseInteraction = req.Enabled;
            _ctx.Config.Save();
            _ctx.Commands.Enqueue(AdminCommandType.ReloadConfig);
            _ctx.Log.AdminInfo($"紧急操作: 暂停全部互动={(req.Enabled ? "开启" : "关闭")}");
            return Results.Json(new { ok = true, pauseInteraction = req.Enabled });
        }));

        app.MapPost("/api/emergency/close-song-request", (HttpContext http) => Auth(http, () =>
        {
            _ctx.SongRequestControl.SetRequestEnabled(false);
            _ctx.Commands.Enqueue(AdminCommandType.ReloadConfig);
            _ctx.Log.AdminInfo("紧急操作: 关闭点歌");
            return Results.Json(new { ok = true, enabled = false });
        }));

        app.MapPost("/api/emergency/kugou-relogin", (HttpContext http) => Auth(http, () =>
        {
            var status = _ctx.Host.GetRuntimeStatus();
            _ctx.Log.AdminInfo("紧急操作: 酷狗重新登录");
            return Results.Json(new { ok = true, url = status.KugouLoginPageUrl });
        }));

        app.MapGet("/api/ops-log", (HttpContext http, int? limit) => Auth(http, () =>
        {
            var take = Math.Clamp(limit ?? 100, 1, 500);
            var entries = _ctx.Log.ReadRecentOpsEntries(take)
                .Select(e => new { e.Time, e.Event, e.Result, e.Level });
            return Results.Json(entries);
        }));

        app.MapGet("/api/level-permissions", (HttpContext http) => Auth(http, () =>
            Results.Json(_ctx.LevelPermissions.ListAll())));

        app.MapPut("/api/level-permissions", (List<LevelPermission> body, HttpContext http) => Auth(http, () =>
        {
            _ctx.LevelPermissions.SaveAll(body);
            _ctx.Commands.Enqueue(AdminCommandType.ReloadConfig);
            return Results.Json(new { ok = true });
        }));

        app.MapGet("/api/users", (HttpContext http, string? q) => Auth(http, () =>
        {
            var users = (string.IsNullOrWhiteSpace(q)
                    ? _ctx.Users.ListUsers(500)
                    : _ctx.Users.SearchUsers(q!, 200))
                .Select(u => new
                {
                    u.UserId, u.Nickname, role = u.Role.ToString(), status = u.Status.ToString(),
                    u.Points, u.Level, u.RequestCount,
                    u.SongPermissionCredits, u.SongPermissionUnlimited
                });
            return Results.Json(users);
        }));

        app.MapGet("/api/users/{userId}", (string userId, HttpContext http) => Auth(http, () =>
        {
            var detail = _ctx.UserDetail.GetDetail(userId);
            return detail == null ? Results.NotFound() : Results.Json(detail);
        }));

        app.MapPut("/api/users/{userId}", (string userId, UserUpdateRequest req, HttpContext http) => Auth(http, () =>
        {
            if (req.Points.HasValue || req.PointsDelta.HasValue)
            {
                if (string.IsNullOrWhiteSpace(req.Reason))
                {
                    return Results.Json(new { ok = false, message = "修改积分必须填写原因" });
                }

                if (string.IsNullOrWhiteSpace(req.Operator))
                {
                    return Results.Json(new { ok = false, message = "修改积分必须填写修改人" });
                }

                if (req.Points.HasValue)
                {
                    if (!_ctx.Users.TryAdminSetPoints(userId, req.Points.Value, req.Reason!, req.Operator!, out var err))
                    {
                        return Results.Json(new { ok = false, message = err ?? "设置积分失败" });
                    }
                }
                else if (req.PointsDelta.HasValue)
                {
                    if (!_ctx.Users.TryAdminAdjustPoints(userId, req.PointsDelta.Value, req.Reason!, req.Operator!, out var err))
                    {
                        return Results.Json(new { ok = false, message = err ?? "调整积分失败" });
                    }
                }
            }

            if (req.Level.HasValue) _ctx.Users.SetLevel(userId, req.Level.Value);
            if (!string.IsNullOrWhiteSpace(req.Role) && Enum.TryParse<UserRole>(req.Role, true, out var role))
            {
                _ctx.Users.SetRole(userId, role);
                if (role == UserRole.Blacklist)
                {
                    _ctx.Users.SetStatus(userId, UserStatus.Banned);
                }
            }
            if (!string.IsNullOrWhiteSpace(req.Status) && Enum.TryParse<UserStatus>(req.Status, true, out var status))
                _ctx.Users.SetStatus(userId, status);
            _ctx.Log.AdminInfo($"用户更新 userId={userId} role={req.Role} points={req.Points} delta={req.PointsDelta} op={req.Operator}");
            _ctx.Commands.Enqueue(AdminCommandType.ReloadConfig);
            return Results.Json(new { ok = true });
        }));

        app.MapGet("/api/users/{userId}/points-ledger", (string userId, HttpContext http, int? limit, int? offset) =>
            Auth(http, () =>
            {
                var take = Math.Clamp(limit ?? 50, 1, 200);
                var skip = Math.Max(0, offset ?? 0);
                var entries = _ctx.PointsLedger.ListByUser(userId, take, skip);
                return Results.Json(new
                {
                    userId,
                    total = _ctx.PointsLedger.CountByUser(userId),
                    entries = entries.Select(e => new
                    {
                        e.Id,
                        e.Delta,
                        e.BalanceAfter,
                        e.Type,
                        typeLabel = _ctx.PointsLedger.FormatTypeLabel(e.Type),
                        e.Reason,
                        operatorName = e.OperatorName,
                        refId = e.RefId,
                        createdAt = e.CreatedAt.ToString("O")
                    })
                });
            }));

        app.MapPost("/api/templates/preview", (TemplatePreviewRequest req, HttpContext http) => Auth(http, () =>
        {
            var result = _ctx.TemplatePreview.Preview(req.TemplateKey ?? "", req.TestUser ?? "测试用户", req.TemplateContent);
            return Results.Json(result);
        }));

        app.MapGet("/api/gifts", (HttpContext http) => Auth(http, () =>
            Results.Json(_ctx.Gifts.ListRecent(100))));

        app.MapGet("/api/blacklist", (HttpContext http) => Auth(http, () =>
            Results.Json(_ctx.SongBlacklist.ListAll())));

        app.MapPost("/api/blacklist", (SongBlacklistEntry entry, HttpContext http) => Auth(http, () =>
        {
            var id = _ctx.SongBlacklist.Add(entry);
            return Results.Json(new { ok = true, id });
        }));

        app.MapDelete("/api/blacklist/{id:long}", (long id, HttpContext http) => Auth(http, () =>
        {
            _ctx.SongBlacklist.Remove(id);
            return Results.Json(new { ok = true });
        }));

        app.MapGet("/api/ban-votes", (HttpContext http) => Auth(http, () =>
            Results.Json(_ctx.BanVotes.ListActive())));

        app.MapGet("/api/keywords", (HttpContext http) => Auth(http, () =>
            Results.Json(_ctx.KeywordReplies.ListAll())));

        app.MapPost("/api/keywords", (KeywordReplyRule rule, HttpContext http) => Auth(http, () =>
        {
            var id = _ctx.KeywordReplies.Add(rule);
            _ctx.Commands.Enqueue(AdminCommandType.ReloadConfig);
            _ctx.Log.AdminInfo($"添加关键词回复 keyword={rule.Keyword}");
            return Results.Json(new { ok = true, id });
        }));

        app.MapPut("/api/keywords/{id:long}", (long id, KeywordReplyRule rule, HttpContext http) => Auth(http, () =>
        {
            rule.Id = id;
            _ctx.KeywordReplies.Update(rule);
            _ctx.Commands.Enqueue(AdminCommandType.ReloadConfig);
            return Results.Json(new { ok = true });
        }));

        app.MapDelete("/api/keywords/{id:long}", (long id, HttpContext http) => Auth(http, () =>
        {
            _ctx.KeywordReplies.Remove(id);
            _ctx.Commands.Enqueue(AdminCommandType.ReloadConfig);
            return Results.Json(new { ok = true });
        }));

        app.MapGet("/api/welcome", (HttpContext http) => Auth(http, () =>
            Results.Json(_ctx.Config.Settings.Welcome)));

        app.MapPut("/api/welcome", (WelcomeSettings body, HttpContext http) => Auth(http, () =>
        {
            _ctx.Config.Settings.Welcome = body;
            _ctx.Config.Save();
            _ctx.Commands.Enqueue(AdminCommandType.ReloadConfig);
            _ctx.Log.AdminInfo($"欢迎设置更新 enabled={body.Enabled} cooldown={body.CooldownSeconds}");
            return Results.Json(new { ok = true });
        }));

        app.MapGet("/api/ban-vote-settings", (HttpContext http) => Auth(http, () =>
            Results.Json(_ctx.Config.Settings.BanVote)));

        app.MapPut("/api/ban-vote-settings", (BanVoteSettings body, HttpContext http) => Auth(http, () =>
        {
            _ctx.Config.Settings.BanVote = body;
            _ctx.Config.Save();
            _ctx.Commands.Enqueue(AdminCommandType.ReloadConfig);
            _ctx.Log.AdminInfo($"禁言投票设置 votes={body.RequiredVotes} window={body.WindowSeconds} ban={body.BanDurationSeconds}");
            return Results.Json(new { ok = true });
        }));

        app.MapGet("/api/cleanup", (HttpContext http) => Auth(http, () =>
            Results.Json(_ctx.Config.Settings.Cleanup)));

        app.MapPut("/api/cleanup", (CleanupSettings body, HttpContext http) => Auth(http, () =>
        {
            _ctx.Config.Settings.Cleanup = body;
            _ctx.Config.Save();
            _ctx.Commands.Enqueue(AdminCommandType.ReloadConfig);
            return Results.Json(new { ok = true });
        }));

        app.MapPut("/api/keyword-reply-enabled", (EnabledRequest req, HttpContext http) => Auth(http, () =>
        {
            _ctx.Config.Settings.KeywordReply.Enabled = req.Enabled;
            _ctx.Config.Save();
            _ctx.Commands.Enqueue(AdminCommandType.ReloadConfig);
            return Results.Json(new { ok = true });
        }));

        app.MapGet("/api/sync/bundle", (HttpContext http) => Auth(http, () =>
            Results.Json(_ctx.Settings.BuildBundle())));

        app.MapGet("/api/sync/commands", (HttpContext http) => Auth(http, () =>
        {
            var cmds = _ctx.Commands.DequeuePending().Select(c => new { type = c.Type.ToString(), payload = c.Payload });
            return Results.Json(cmds);
        }));

        app.MapGet("/api/sync/config", (HttpContext http) => Auth(http, () =>
        {
            var bundle = _ctx.Settings.BuildBundle();
            return Results.Json(new { hash = bundle.Version, config = bundle.Settings });
        }));

        app.MapGet("/", () => Results.Redirect($"{pathBase}/index.html"));
    }

    private IResult Cmd(AdminCommandType type, string? payload = null)
    {
        _ctx.Commands.Enqueue(type, payload);
        _ctx.Log.AdminInfo($"后台命令 {type} payload={payload ?? ""}");
        return Results.Json(new { ok = true });
    }

    private IResult Auth(HttpContext http, Func<IResult> action)
    {
        // 云端 nginx 已校验登录态，经 SSH 隧道以本机回环 + 隧道密钥访问
        var remote = http.Connection.RemoteIpAddress;
        var isLoopback = remote != null && IPAddress.IsLoopback(remote);
        var tunnelSecret = _ctx.Config.Settings.Admin.TunnelSecret;
        var headerSecret = http.Request.Headers["X-LiveAssistant-Tunnel"].FirstOrDefault();
        if (isLoopback
            && !string.IsNullOrWhiteSpace(tunnelSecret)
            && !string.IsNullOrWhiteSpace(headerSecret)
            && string.Equals(headerSecret, tunnelSecret, StringComparison.Ordinal))
        {
            return action();
        }

        var token = http.Request.Headers.Authorization.FirstOrDefault()?.Replace("Bearer ", "");
        if (string.IsNullOrWhiteSpace(token) || !_sessions.TryGetValue(token, out var exp) || exp < DateTime.Now)
        {
            return Results.Unauthorized();
        }
        return action();
    }

    public void Dispose()
    {
        var app = _app;
        _app = null;
        if (app == null)
        {
            return;
        }

        try
        {
            // 绝不能在 UI 线程上 StopAsync().GetResult()，会和 WinForms 同步上下文死锁导致关不掉
            var stopTask = Task.Run(async () =>
            {
                try
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    await app.StopAsync(cts.Token).ConfigureAwait(false);
                }
                catch
                {
                    // ignore
                }

                try
                {
                    await app.DisposeAsync().ConfigureAwait(false);
                }
                catch
                {
                    // ignore
                }
            });
            stopTask.Wait(TimeSpan.FromSeconds(3));
        }
        catch
        {
            // ignore
        }
    }

    private sealed record LoginRequest(string Username, string Password);
    private sealed record RandomToggleRequest(bool Enabled);
    private sealed record UserUpdateRequest(
        int? Points, int? PointsDelta, int? Level, string? Role, string? Status, string? Reason, string? Operator);
    private sealed record EnabledRequest(bool Enabled);
    private sealed record TemplatePreviewRequest(string? TemplateKey, string? TestUser, string? TemplateContent);
}
