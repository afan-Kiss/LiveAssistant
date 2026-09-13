using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace LiveAssistant.Utils;

public static class HttpJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public static HttpClient CreateClient(string baseUrl, string? token = null, string tokenHeader = "X-API-Token")
    {
        var client = new HttpClient
        {
            BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/"),
            Timeout = TimeSpan.FromSeconds(30)
        };
        if (!string.IsNullOrWhiteSpace(token))
        {
            client.DefaultRequestHeaders.Add(tokenHeader, token);
        }
        return client;
    }

    public static async Task<T?> PostAsync<T>(HttpClient client, string path, object? body, CancellationToken ct = default)
    {
        var json = JsonSerializer.Serialize(body ?? new { }, Options);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        using var response = await client.PostAsync(path.TrimStart('/'), content, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        return JsonSerializer.Deserialize<T>(text, Options);
    }

    public static async Task<T?> GetAsync<T>(HttpClient client, string path, CancellationToken ct = default)
    {
        using var response = await client.GetAsync(path.TrimStart('/'), ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        return JsonSerializer.Deserialize<T>(text, Options);
    }
}
