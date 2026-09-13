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
    private Task? _runTask;

    public AdminWebHost(AdminAppContext ctx)
    {
        _ctx = ctx;
    }

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
        _runTask = _app.RunAsync();
    }

    private void MapRoutes(WebApplication app, string pathBase)
    {
        app.MapPost("/api/auth/login", (LoginRequest req) =>
        {
            var admin = _ctx.Config.Settings.Admin;
            if (req.Username == admin.Username && req.Password == admin.Password)
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
            return Results.Json(new
            {
                douyinOnline = status.DouyinOnline,
                kugouOnline = status.KugouOnline,
                danmakuConnection = status.DanmakuConnection,
                currentSong = status.CurrentSong,
                playbackMode = _ctx.Host.Engine.Mode.ToString(),
                randomFillEnabled = _ctx.Config.Settings.Playback.RandomFillEnabled,
                queueCount = status.QueueCount,
                uptime = status.Uptime.ToString()
            });
        }));

        app.MapPost("/api/playback/skip", (HttpContext http) => Auth(http, () =>
        {
            _ctx.Commands.Enqueue(AdminCommandType.Skip);
            return Results.Json(new { ok = true });
        }));

        app.MapPost("/api/playback/pause", (HttpContext http) => Auth(http, () =>
        {
            _ctx.Commands.Enqueue(AdminCommandType.Pause);
            return Results.Json(new { ok = true });
        }));

        app.MapPost("/api/playback/resume", (HttpContext http) => Auth(http, () =>
        {
            _ctx.Commands.Enqueue(AdminCommandType.Resume);
            return Results.Json(new { ok = true });
        }));

        app.MapPost("/api/playback/random", (RandomToggleRequest req, HttpContext http) => Auth(http, () =>
        {
            _ctx.Config.Settings.Playback.RandomFillEnabled = req.Enabled;
            _ctx.Config.Save();
            _ctx.Commands.Enqueue(AdminCommandType.SetRandomFill, req.Enabled.ToString());
            return Results.Json(new { ok = true });
        }));

        app.MapGet("/api/queue", (HttpContext http) => Auth(http, () =>
        {
            var items = _ctx.Host.Queue.GetAllItems().Select(x => new
            {
                x.Id, x.UserId, x.Nickname, x.SongName, x.Artist, x.Status, x.SortOrder, x.IsRandom
            });
            return Results.Json(items);
        }));

        app.MapDelete("/api/queue/{id:long}", (long id, HttpContext http) => Auth(http, () =>
        {
            _ctx.Commands.Enqueue(AdminCommandType.DeleteQueueItem, id.ToString());
            return Results.Json(new { ok = true });
        }));

        app.MapPost("/api/queue/{id:long}/pin", (long id, HttpContext http) => Auth(http, () =>
        {
            _ctx.Commands.Enqueue(AdminCommandType.PinQueueItem, id.ToString());
            return Results.Json(new { ok = true });
        }));

        app.MapPost("/api/queue/{id:long}/play", (long id, HttpContext http) => Auth(http, () =>
        {
            _ctx.Commands.Enqueue(AdminCommandType.PlayNow, id.ToString());
            return Results.Json(new { ok = true });
        }));

        app.MapGet("/api/templates", (HttpContext http) => Auth(http, () =>
            Results.Json(_ctx.Config.ReplyTemplates)));

        app.MapPut("/api/templates", (Dictionary<string, string> body, HttpContext http) => Auth(http, () =>
        {
            _ctx.Config.SetReplyTemplates(body);
            _ctx.Commands.Enqueue(AdminCommandType.ReloadConfig);
            return Results.Json(new { ok = true });
        }));

        app.MapGet("/api/random", (HttpContext http) => Auth(http, () =>
            Results.Json(_ctx.Config.Settings.RandomPlaylist)));

        app.MapPut("/api/random", (RandomPlaylistSettings body, HttpContext http) => Auth(http, () =>
        {
            _ctx.Config.Settings.RandomPlaylist = body;
            _ctx.Config.Save();
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
            if (req.Level.HasValue) _ctx.Users.SetLevel(userId, req.Level.Value);
            if (!string.IsNullOrWhiteSpace(req.Role) && Enum.TryParse<UserRole>(req.Role, true, out var role))
                _ctx.Users.SetRole(userId, role);
            if (!string.IsNullOrWhiteSpace(req.Status) && Enum.TryParse<UserStatus>(req.Status, true, out var status))
                _ctx.Users.SetStatus(userId, status);
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

        app.MapGet("/api/sync/commands", (HttpContext http) =>
        {
            var cmds = _ctx.Commands.DequeuePending().Select(c => new { type = c.Type.ToString(), payload = c.Payload });
            return Results.Json(cmds);
        });

        app.MapGet("/api/sync/config", (HttpContext http) =>
        {
            var hash = ComputeConfigHash();
            return Results.Json(new { hash, config = _ctx.Config.Settings });
        });

        app.MapGet("/", () => Results.Redirect($"{pathBase}/index.html"));
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

    private string ComputeConfigHash()
    {
        var json = JsonSerializer.Serialize(_ctx.Config.Settings);
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(json + JsonSerializer.Serialize(_ctx.Config.ReplyTemplates)));
        return Convert.ToHexString(bytes)[..16];
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
    private sealed record UserUpdateRequest(int? Points, int? Level, string? Role, string? Status);
}
