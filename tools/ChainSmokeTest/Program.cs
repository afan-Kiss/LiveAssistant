using System.Net.Http.Json;
using System.Text.Json;
using LiveAssistant.Config;
using LiveAssistant.Models;
using LiveAssistant.Services;
using LiveAssistant.Utils;

var baseUrl = args.Length > 0 ? args[0].TrimEnd('/') : "http://127.0.0.1:17888";
var failures = new List<string>();

Console.OutputEncoding = System.Text.Encoding.UTF8;
Console.WriteLine("=== 点歌全链路自测 ===");
Console.WriteLine($"Kugou: {baseUrl}");
Console.WriteLine();

await Step("1. 酷狗 health", async () =>
{
    using var http = new HttpClient { BaseAddress = new Uri(baseUrl + "/"), Timeout = TimeSpan.FromSeconds(15) };
    using var doc = JsonDocument.Parse(await http.GetStringAsync("health"));
    if (doc.RootElement.GetProperty("code").GetInt32() != 0)
    {
        throw new InvalidOperationException("health code != 0");
    }
});

await Step("2. 酷狗登录态", async () =>
{
    using var http = new HttpClient { BaseAddress = new Uri(baseUrl + "/"), Timeout = TimeSpan.FromSeconds(15) };
    using var doc = JsonDocument.Parse(await http.GetStringAsync("api/v1/login/status"));
    var loggedIn = doc.RootElement.GetProperty("data").GetProperty("logged_in").GetBoolean();
    Console.WriteLine($"   logged_in={loggedIn}");
});

await Step("3. 弹幕解析「点歌 泡沫」", () =>
{
    if (!SongNameParser.TryParse("点歌 泡沫", out var song) || song != "泡沫")
    {
        throw new InvalidOperationException($"解析失败: {song}");
    }

    Console.WriteLine($"   song={song}");
    return Task.CompletedTask;
});

await Step("4. 酷狗搜索「泡沫」", async () =>
{
    using var http = new HttpClient { BaseAddress = new Uri(baseUrl + "/"), Timeout = TimeSpan.FromSeconds(30) };
    using var resp = await http.PostAsJsonAsync("api/v1/search", new { keyword = "泡沫", page = 1, pagesize = 5 });
    var json = await resp.Content.ReadAsStringAsync();
    using var doc = JsonDocument.Parse(json);
    if (doc.RootElement.GetProperty("code").GetInt32() != 0)
    {
        throw new InvalidOperationException(json);
    }

    var list = doc.RootElement.GetProperty("data").GetProperty("歌单");
    if (list.GetArrayLength() == 0)
    {
        throw new InvalidOperationException("搜索结果为空");
    }

    var first = list[0];
    var name = first.GetProperty("歌曲名称").GetString() ?? "";
    var artist = first.GetProperty("歌手名称").GetString() ?? "";
    var hash = first.GetProperty("hash").GetString() ?? "";
    Console.WriteLine($"   top1={name} - {artist} hash={hash}");
    if (!name.Contains("泡沫", StringComparison.Ordinal))
    {
        throw new InvalidOperationException($"首条不是泡沫: {name}");
    }
});

var logDir = Path.Combine(Path.GetTempPath(), "la-chain-" + Guid.NewGuid().ToString("N"));
var log = new LogService(logDir);
var settings = new KugouSettings
{
    BaseUrl = baseUrl,
    RequireFullPlayback = true,
    AutoClaimVip = true
};
var kugou = new KugouService(settings, log);

await Step("5. KugouService.ResolveTrackAsync(泡沫)", async () =>
{
    await kugou.RefreshLoginStatusAsync();
    await kugou.TryAutoClaimVipAsync();
    var track = await kugou.ResolveTrackAsync("泡沫");
    if (track == null)
    {
        throw new InvalidOperationException("ResolveTrackAsync 返回 null");
    }

    Console.WriteLine($"   {track.SongName} - {track.Artist}");
    Console.WriteLine($"   preview={track.IsPreview} url={(track.PlayUrl?.Length > 60 ? track.PlayUrl[..60] + "..." : track.PlayUrl)}");
    if (string.IsNullOrWhiteSpace(track.PlayUrl))
    {
        throw new InvalidOperationException("PlayUrl 为空");
    }
});

await Step("6. 解析后入队（模拟点歌成功路径）", async () =>
{
    var track = await kugou.ResolveTrackAsync("泡沫");
    if (track == null)
    {
        throw new InvalidOperationException("ResolveTrackAsync 返回 null");
    }

    var dbDir = Path.Combine(logDir, "db");
    Directory.CreateDirectory(dbDir);
    var db = new LiveAssistant.Database.AppDatabase(dbDir);
    var queue = new QueueService(db);
    var added = queue.Add(new QueueItem
    {
        UserId = "chain-test-user",
        Nickname = "链路自测",
        SongName = track.SongName,
        Artist = track.Artist,
        SongId = track.SongId,
        Hash = track.Hash,
        AlbumId = track.AlbumId,
        AlbumAudioId = track.AlbumAudioId,
        PlayUrl = track.PlayUrl,
        IsRandom = false
    });

    if (added == null || string.IsNullOrWhiteSpace(added.PlayUrl))
    {
        throw new InvalidOperationException("入队失败");
    }

    Console.WriteLine($"   queued id={added.Id} {added.SongName} - {added.Artist}");
});

await Step("7. LiveAssistant 管理后台 ping", async () =>
{
    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
    using var resp = await http.GetAsync("http://127.0.0.1:5088/diangexitong/api/ping");
    if (!resp.IsSuccessStatusCode)
    {
        throw new InvalidOperationException($"ping http={(int)resp.StatusCode}");
    }
});

Console.WriteLine();
if (failures.Count == 0)
{
    Console.WriteLine("ALL PASS (7/7)");
    return 0;
}

Console.WriteLine($"FAIL ({failures.Count}/7)");
foreach (var f in failures)
{
    Console.WriteLine($" - {f}");
}

return 1;

async Task Step(string name, Func<Task> action)
{
    Console.Write($"[{name}] ");
    try
    {
        await action();
        Console.WriteLine("OK");
    }
    catch (Exception ex)
    {
        Console.WriteLine("FAIL");
        Console.WriteLine($"   {ex.Message}");
        failures.Add($"{name}: {ex.Message}");
    }
}
