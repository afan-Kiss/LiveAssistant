using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LiveAssistant.Services.AiSpeech;

public sealed class OllamaClient : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient _http;
    private readonly bool _ownsClient;

    public OllamaClient(string baseUrl, TimeSpan timeout, HttpClient? httpClient = null)
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

    public async Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linked.CancelAfter(Timeout);
        using var resp = await _http.GetAsync($"{BaseUrl}/api/tags", linked.Token);
        resp.EnsureSuccessStatusCode();
        await using var stream = await resp.Content.ReadAsStreamAsync(linked.Token);
        var doc = await JsonSerializer.DeserializeAsync<OllamaTagsResponse>(stream, JsonOptions, linked.Token);
        return doc?.Models?
                   .Select(m => m.Name ?? "")
                   .Where(n => !string.IsNullOrWhiteSpace(n))
                   .Distinct(StringComparer.OrdinalIgnoreCase)
                   .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                   .ToList()
               ?? (IReadOnlyList<string>)Array.Empty<string>();
    }

    public async Task<bool> HealthAsync(CancellationToken ct = default)
    {
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
            linked.CancelAfter(TimeSpan.FromSeconds(Math.Min(8, Timeout.TotalSeconds)));
            using var resp = await _http.GetAsync($"{BaseUrl}/api/tags", linked.Token);
            return resp.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    public async Task<OllamaGenerateResult> GenerateAsync(
        string model,
        string systemPrompt,
        string userPrompt,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(model))
        {
            return OllamaGenerateResult.Fail("未选择 Ollama 模型");
        }

        var payload = new
        {
            model,
            stream = false,
            options = new { temperature = 0.7, num_predict = 120 },
            messages = new object[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = userPrompt }
            }
        };

        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
            linked.CancelAfter(Timeout);
            using var content = JsonContent.Create(payload);
            using var resp = await _http.PostAsync($"{BaseUrl}/api/chat", content, linked.Token);
            var body = await resp.Content.ReadAsStringAsync(linked.Token);
            if (!resp.IsSuccessStatusCode)
            {
                var resource = IsResourceError(resp.StatusCode, body);
                return OllamaGenerateResult.Fail(
                    $"HTTP {(int)resp.StatusCode}: {Trim(body, 200)}",
                    (int)resp.StatusCode,
                    resource);
            }

            using var doc = JsonDocument.Parse(body);
            var text = "";
            if (doc.RootElement.TryGetProperty("message", out var message)
                && message.TryGetProperty("content", out var contentEl))
            {
                text = contentEl.GetString() ?? "";
            }

            if (string.IsNullOrWhiteSpace(text)
                && doc.RootElement.TryGetProperty("response", out var responseEl))
            {
                text = responseEl.GetString() ?? "";
            }

            if (string.IsNullOrWhiteSpace(text))
            {
                return OllamaGenerateResult.Fail("Ollama 返回空内容", (int)resp.StatusCode);
            }

            return OllamaGenerateResult.Ok(text.Trim());
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return OllamaGenerateResult.Fail("Ollama 请求超时");
        }
        catch (Exception ex)
        {
            return OllamaGenerateResult.Fail($"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private static bool IsResourceError(System.Net.HttpStatusCode code, string body)
    {
        if (code == System.Net.HttpStatusCode.InternalServerError
            || code == System.Net.HttpStatusCode.ServiceUnavailable
            || (int)code == 500)
        {
            var lower = body.ToLowerInvariant();
            if (lower.Contains("memory") || lower.Contains("vram") || lower.Contains("cuda")
                || lower.Contains("out of memory") || lower.Contains("resource")
                || lower.Contains("oom") || lower.Contains("allocate")
                || lower.Contains("terminated") || lower.Contains("insufficient"))
            {
                return true;
            }
        }

        return false;
    }

    private static string NormalizeBase(string url)
    {
        url = (url ?? "").Trim().TrimEnd('/');
        if (string.IsNullOrWhiteSpace(url))
        {
            return "http://127.0.0.1:11434";
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

    private sealed class OllamaTagsResponse
    {
        [JsonPropertyName("models")]
        public List<OllamaModelInfo>? Models { get; set; }
    }

    private sealed class OllamaModelInfo
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }
    }
}

public sealed class OllamaGenerateResult
{
    public bool Success { get; init; }
    public string Text { get; init; } = "";
    public string? Error { get; init; }
    public int? StatusCode { get; init; }
    public bool ResourceError { get; init; }

    public static OllamaGenerateResult Ok(string text)
        => new() { Success = true, Text = text };

    public static OllamaGenerateResult Fail(string error, int? status = null, bool resource = false)
        => new() { Success = false, Error = error, StatusCode = status, ResourceError = resource };
}
