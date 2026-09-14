using System.Net.Http.Json;
using System.Text.Json;
using NAudio.Wave;

var baseUrl = args.Length > 0 ? args[0].TrimEnd('/') : "http://127.0.0.1:17888";
using var http = new HttpClient { BaseAddress = new Uri(baseUrl + "/"), Timeout = TimeSpan.FromSeconds(45) };

Console.WriteLine($"[1] health -> {baseUrl}/health");
Console.WriteLine(await http.GetStringAsync("health"));

Console.WriteLine("[2] login/status");
Console.WriteLine(await http.GetStringAsync("api/v1/login/status"));

Console.WriteLine("[3] search 泡沫");
using var searchResp = await http.PostAsJsonAsync("api/v1/search", new { keyword = "泡沫", page = 1, pagesize = 3 });
var searchJson = await searchResp.Content.ReadAsStringAsync();
Console.WriteLine($"search http={(int)searchResp.StatusCode}");
using var searchDoc = JsonDocument.Parse(searchJson);
var root = searchDoc.RootElement;
if (root.GetProperty("code").GetInt32() != 0)
{
    Console.WriteLine(searchJson);
    return 2;
}

var list = root.GetProperty("data").GetProperty("歌单");
if (list.GetArrayLength() == 0)
{
    Console.WriteLine("search empty");
    return 3;
}

var first = list[0];
var hash = first.GetProperty("hash").GetString() ?? "";
var name = first.TryGetProperty("歌曲名称", out var n) ? n.GetString() : "?";
var artist = first.TryGetProperty("歌手名称", out var a) ? a.GetString() : "?";
Console.WriteLine($"picked {name} - {artist} hash={hash}");

string? playUrl = null;
string usedMode = "";
foreach (var mode in new[] { "auto", "full", "preview" })
{
    Console.WriteLine($"[4] song/url mode={mode}");
    try
    {
        using var urlResp = await http.PostAsJsonAsync("api/v1/song/url", new { hash, mode, quality = "auto" });
        var urlJson = await urlResp.Content.ReadAsStringAsync();
        Console.WriteLine($"http={(int)urlResp.StatusCode} body={(urlJson.Length <= 300 ? urlJson : urlJson[..300] + "...")}");
        if (!urlResp.IsSuccessStatusCode) continue;
        using var urlDoc = JsonDocument.Parse(urlJson);
        if (urlDoc.RootElement.GetProperty("code").GetInt32() != 0) continue;
        if (urlDoc.RootElement.GetProperty("data").TryGetProperty("url", out var u))
        {
            playUrl = u.GetString();
            if (!string.IsNullOrWhiteSpace(playUrl))
            {
                usedMode = mode;
                Console.WriteLine($"got url via mode={mode}");
                break;
            }
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"mode={mode} exception: {ex.Message}");
    }
}

if (string.IsNullOrWhiteSpace(playUrl))
{
    Console.WriteLine("FAIL no play url");
    return 4;
}

Console.WriteLine($"[5] NAudio play 4 seconds (mode={usedMode})");
using var reader = new MediaFoundationReader(playUrl);
using var output = new WaveOutEvent { Volume = 0.7f };
output.Init(reader);
output.Play();
await Task.Delay(4000);
output.Stop();
Console.WriteLine($"OK played duration={reader.TotalTime.TotalSeconds:F1}s");
return 0;
