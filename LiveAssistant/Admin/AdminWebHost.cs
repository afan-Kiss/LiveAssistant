using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LiveAssistant.Config;
using LiveAssistant.Models;
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
        _app.Use(async (context, next) =>
        {
            context.Request.PathBase = pathBase;
            await next();
        });

        var wwwroot = Path.Combine(AppContext.BaseDirectory, "Admin", "wwwroot");
        if (Directory.Exists(wwwroot))
        {
            _app.UseStaticFiles(new StaticFileOptions
            {
                FileProvider = new PhysicalFileProvider(wwwroot),
                RequestPath = ""
            });
        }

        MapRoutes(_app, pathBase);
        _ = _app.RunAsync();
    }

    private void MapRoutes(WebApplication app, string pathBase)
    {
        app.MapPost("/api/auth/login", (LoginRequest req) =>
        {
            var admin = _ctx.Config.Settings.Admin;
            var password = admin.ResolvePassword();
            if (string.IsNullOrEmpty(password))
            {
                return Results.Json(new { ok = false, message = "未配置后台密码，请设置环境变量 LIVEASSISTANT_ADMIN_PASSWORD 或 appsettings admin.password" });
            }
            if (req.Username == admin.Username && req.Password == password)
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
            return Results.Json(new
            {
                douyinOnline = status.DouyinOnline,
                kugouOnline = status.KugouOnline,
                danmakuConnection = status.DanmakuConnection,
                adminAccountStatus = status.AdminAccountStatus,
                adminNickname = status.AdminNickname,
                currentSong = status.CurrentSong,
                nowPlayingUser = nowPlaying?.Nickname,
                nowPlayingSong = nowPlaying?.SongName,
                playbackMode = status.PlaybackMode,
                randomFillEnabled = _ctx.Config.Settings.Playback.RandomFillEnabled,
                queueCount = status.QueueCount,
                uptime = status.Uptime.ToString(),
                startedAt = status.StartedAt.ToString("O"),
                currentTask = status.CurrentTask,
                lastError = status.LastError
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
            Results.Json(_ctx.Config.Settings.SongRequestPolicy)));

        app.MapPut("/api/song-request-policy", (SongRequestPolicySettings body, HttpContext http) => Auth(http, () =>
        {
            _ctx.Config.Settings.SongRequestPolicy = body;
            _ctx.Config.Save();
            _ctx.Commands.Enqueue(AdminCommandType.ReloadConfig);
            return Results.Json(new { ok = true });
        }));

        app.MapGet("/api/level-permissions", (HttpContext http) => Auth(http, () =>
            Results.Json(_ctx.LevelPermissions.ListAll())));

        app.MapPut("/api/level-permissions", (List<LevelPermission> body, HttpContext http) => Auth(http, () =>
        {
            _ctx.LevelPermissions.SaveAll(body);
            _ctx.Commands.Enqueue(AdminCommandType.ReloadConfig);
            return Results.Json(new { ok = true });
        }));

        app.MapGet("/api/users", (HttpContext http) => Auth(http, () =>
        {
            var users = _ctx.Users.ListUsers(500).Select(u => new
            {
                u.UserId, u.Nickname, role = u.Role.ToString(), status = u.Status.ToString(),
                u.Points, u.Level, u.RequestCount
            });
            return Results.Json(users);
        }));

        app.MapPut("/api/users/{userId}", (string userId, UserUpdateRequest req, HttpContext http) => Auth(http, () =>
        {
            if (req.Points.HasValue) _ctx.Users.SetPoints(userId, req.Points.Value);
            if (req.PointsDelta.HasValue) _ctx.Users.AdjustPoints(userId, req.PointsDelta.Value);
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
            _ctx.Log.AdminInfo($"用户更新 userId={userId} role={req.Role} points={req.Points} delta={req.PointsDelta}");
            _ctx.Commands.Enqueue(AdminCommandType.ReloadConfig);
            return Results.Json(new { ok = true });
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

        app.MapGet("/api/sync/bundle", (HttpContext http) =>
            Results.Json(_ctx.Settings.BuildBundle()));

        app.MapGet("/api/sync/commands", (HttpContext http) =>
        {
            var cmds = _ctx.Commands.DequeuePending().Select(c => new { type = c.Type.ToString(), payload = c.Payload });
            return Results.Json(cmds);
        });

        app.MapGet("/api/sync/config", (HttpContext http) =>
        {
            var bundle = _ctx.Settings.BuildBundle();
            return Results.Json(new { hash = bundle.Version, config = bundle.Settings });
        });

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
        var token = http.Request.Headers.Authorization.FirstOrDefault()?.Replace("Bearer ", "");
        if (string.IsNullOrWhiteSpace(token) || !_sessions.TryGetValue(token, out var exp) || exp < DateTime.Now)
        {
            return Results.Unauthorized();
        }
        return action();
    }

    public void Dispose()
    {
        if (_app != null)
        {
            _app.StopAsync().GetAwaiter().GetResult();
        }
    }

    private sealed record LoginRequest(string Username, string Password);
    private sealed record RandomToggleRequest(bool Enabled);
    private sealed record UserUpdateRequest(int? Points, int? PointsDelta, int? Level, string? Role, string? Status);
    private sealed record EnabledRequest(bool Enabled);
}
