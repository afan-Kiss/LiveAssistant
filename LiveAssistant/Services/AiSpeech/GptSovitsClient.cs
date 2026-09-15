using System.Net.Http.Json;
using System.Text.Json;

namespace LiveAssistant.Services.AiSpeech;

public sealed class GptSovitsClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly bool _ownsClient;

    public GptSovitsClient(string baseUrl, TimeSpan timeout, HttpClient? httpClient = null)
    {
        _ownsClient = httpClient == null;
        _http = httpClient ?? new HttpClient { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
        BaseUrl = NormalizeBase(baseUrl);
        Timeout = timeout;
    }

    public string BaseUrl { get; private set; }
    public TimeSpan Timeout { get; private set; }

    public void Configure(string baseUrl, TimeSpan timeout)
    {
        BaseUrl = NormalizeBase(baseUrl);
        Timeout = timeout;
    }

    public async Task<GptSovitsHealth> HealthAsync(CancellationToken ct = default)
    {
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
            linked.CancelAfter(TimeSpan.FromSeconds(Math.Min(8, Timeout.TotalSeconds)));
            using var resp = await _http.GetAsync($"{BaseUrl}/health", linked.Token);
            if (!resp.IsSuccessStatusCode)
            {
                return new GptSovitsHealth { Ok = false, Error = $"HTTP {(int)resp.StatusCode}" };
            }

            var json = await resp.Content.ReadAsStringAsync(linked.Token);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            return new GptSovitsHealth
            {
                Ok = true,
                TtsReady = ReadBool(root, "tts_ready"),
                VoiceReady = ReadBool(root, "voice_ready"),
                Voice = ReadString(root, "voice"),
                Ollama = ReadBool(root, "ollama"),
                RawJson = json
            };
        }
        catch (Exception ex)
        {
            return new GptSovitsHealth { Ok = false, Error = $"{ex.GetType().Name}: {ex.Message}" };
        }
    }

    public Task<GptSovitsSynthesizeResult> SynthesizeAsync(
        string text,
        string voice)
        => SynthesizeAsync(text, voice, 1.0, null, CancellationToken.None);

    public Task<GptSovitsSynthesizeResult> SynthesizeAsync(
        string text,
        string voice,
        CancellationToken ct)
        => SynthesizeAsync(text, voice, 1.0, null, ct);

    public async Task<GptSovitsSynthesizeResult> SynthesizeAsync(
        string text,
        string voice,
        double speedFactor,
        string? referWav = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return GptSovitsSynthesizeResult.Fail("TTS 文本为空");
        }

        speedFactor = speedFactor <= 0 || double.IsNaN(speedFactor) || double.IsInfinity(speedFactor)
            ? 1.0
            : Math.Clamp(speedFactor, 0.5, 2.0);

        var payload = new Dictionary<string, object?>
        {
            ["text"] = text,
            ["voice"] = string.IsNullOrWhiteSpace(voice) ? "my_voice" : voice.Trim(),
            ["speed_factor"] = speedFactor
        };
        if (!string.IsNullOrWhiteSpace(referWav))
        {
            payload["refer_wav"] = referWav.Trim();
        }

        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
            linked.CancelAfter(Timeout);
            using var content = JsonContent.Create(payload);
            using var resp = await _http.PostAsync($"{BaseUrl}/tts", content, linked.Token);
            if (!resp.IsSuccessStatusCode)
            {
                var errBody = await resp.Content.ReadAsStringAsync(linked.Token);
                return GptSovitsSynthesizeResult.Fail(
                    $"HTTP {(int)resp.StatusCode}: {Trim(errBody, 200)}",
                    (int)resp.StatusCode);
            }

            var bytes = await resp.Content.ReadAsByteArrayAsync(linked.Token);
            if (bytes.Length < 44)
            {
                return GptSovitsSynthesizeResult.Fail("TTS 返回音频过短或无效", (int)resp.StatusCode);
            }

            return GptSovitsSynthesizeResult.Ok(bytes);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return GptSovitsSynthesizeResult.Fail("TTS 请求超时");
        }
        catch (Exception ex)
        {
            return GptSovitsSynthesizeResult.Fail($"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private static bool ReadBool(JsonElement root, string name)
        => root.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.True;

    private static string ReadString(JsonElement root, string name)
        => root.TryGetProperty(name, out var el) ? el.GetString() ?? "" : "";

    private static string NormalizeBase(string url)
    {
        url = (url ?? "").Trim().TrimEnd('/');
        if (string.IsNullOrWhiteSpace(url))
        {
            return "http://127.0.0.1:9880";
        }

        if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            && !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            url = "http://" + url;
        }

        return url.TrimEnd('/');
    }

    private static string Trim(string s, int max)
        => string.IsNullOrEmpty(s) ? "" : s.Length <= max ? s : s[..max] + "…";

    public void Dispose()
    {
        if (_ownsClient)
        {
            _http.Dispose();
        }
    }
}

public sealed class GptSovitsHealth
{
    public bool Ok { get; init; }
    public bool TtsReady { get; init; }
    public bool VoiceReady { get; init; }
    public string Voice { get; init; } = "";
    public bool Ollama { get; init; }
    public string? Error { get; init; }
    public string? RawJson { get; init; }
}

public sealed class GptSovitsSynthesizeResult
{
    public bool Success { get; init; }
    public byte[] AudioWav { get; init; } = Array.Empty<byte>();
    public string? Error { get; init; }
    public int? StatusCode { get; init; }

    public static GptSovitsSynthesizeResult Ok(byte[] wav)
        => new() { Success = true, AudioWav = wav };

    public static GptSovitsSynthesizeResult Fail(string error, int? status = null)
        => new() { Success = false, Error = error, StatusCode = status };
}
