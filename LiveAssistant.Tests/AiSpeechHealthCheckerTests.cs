using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using LiveAssistant.Services.AiSpeech;
using Xunit;

namespace LiveAssistant.Tests;

public class AiSpeechHealthCheckerTests
{
    [Fact]
    public async Task CheckOnce_WhenServicesDown_ReportsUnavailable_WithoutThrowing()
    {
        using var ollamaHttp = new HttpClient(new FailHandler()) { BaseAddress = new Uri("http://127.0.0.1:9/") };
        using var ttsHttp = new HttpClient(new FailHandler()) { BaseAddress = new Uri("http://127.0.0.1:9/") };
        using var ollama = new OllamaClient("http://127.0.0.1:9", TimeSpan.FromSeconds(2), ollamaHttp);
        using var tts = new GptSovitsClient("http://127.0.0.1:9", TimeSpan.FromSeconds(2), ttsHttp);
        var logs = new List<string>();
        var checker = new AiSpeechHealthChecker(
            ollama,
            tts,
            () => "http://127.0.0.1:9",
            () => 2,
            () => "http://127.0.0.1:9",
            () => 2,
            () => "qwen3:8b",
            () => "my_voice",
            msg => logs.Add(msg));

        var report = await checker.CheckOnceAsync("unit", CancellationToken.None);
        Assert.False(report.OllamaAvailable);
        Assert.False(report.ModelAvailable);
        Assert.False(report.TtsAvailable);
        Assert.False(report.FullyReady);
        Assert.Contains("Ollama", report.ServiceHint, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(logs, l => l.Contains("AI_HEALTH_CHECK", StringComparison.Ordinal));
        Assert.Contains(report.SummaryLines, s => s.Contains("Ollama未连接", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CheckOnce_WhenOllamaUpModelMissing_ListsInstalled()
    {
        var handler = new ScriptedHandler(req =>
        {
            if (req.RequestUri!.AbsolutePath.Contains("/api/tags", StringComparison.Ordinal))
            {
                return JsonResponse("""{"models":[{"name":"llama3.2:3b"},{"name":"qwen2.5:7b"}]}""");
            }

            if (req.RequestUri!.AbsolutePath.Contains("/health", StringComparison.Ordinal))
            {
                return JsonResponse("""{"tts_ready":true,"voice_ready":true,"voice":"my_voice"}""");
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        using var http = new HttpClient(handler);
        using var ollama = new OllamaClient("http://127.0.0.1:11434", TimeSpan.FromSeconds(5), http);
        using var tts = new GptSovitsClient("http://127.0.0.1:9880", TimeSpan.FromSeconds(5), http);
        var checker = new AiSpeechHealthChecker(
            ollama, tts,
            () => "http://127.0.0.1:11434", () => 5,
            () => "http://127.0.0.1:9880", () => 5,
            () => "qwen3:8b", () => "my_voice");

        var report = await checker.CheckOnceAsync("model_missing", CancellationToken.None);
        Assert.True(report.OllamaAvailable);
        Assert.False(report.ModelAvailable);
        Assert.True(report.TtsAvailable && report.TtsReady && report.VoiceReady);
        Assert.Contains("qwen3:8b", report.ServiceHint);
        Assert.Contains(report.InstalledModels, m => m.Contains("qwen2.5", StringComparison.Ordinal));
        Assert.Contains(report.SummaryLines, s => s.Contains("模型不存在", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CheckOnce_WhenAllReady_FullyReady()
    {
        var handler = new ScriptedHandler(req =>
        {
            if (req.RequestUri!.AbsolutePath.Contains("/api/tags", StringComparison.Ordinal))
            {
                return JsonResponse("""{"models":[{"name":"qwen3:8b"}]}""");
            }

            return JsonResponse("""{"tts_ready":true,"voice_ready":true,"voice":"my_voice"}""");
        });
        using var http = new HttpClient(handler);
        using var ollama = new OllamaClient("http://127.0.0.1:11434", TimeSpan.FromSeconds(5), http);
        using var tts = new GptSovitsClient("http://127.0.0.1:9880", TimeSpan.FromSeconds(5), http);
        var logs = new List<string>();
        var checker = new AiSpeechHealthChecker(
            ollama, tts,
            () => "http://127.0.0.1:11434", () => 5,
            () => "http://127.0.0.1:9880", () => 5,
            () => "qwen3:8b", () => "my_voice",
            msg => logs.Add(msg));

        var report = await checker.CheckOnceAsync("startup", CancellationToken.None, countAsStartupAttempt: true);
        Assert.True(report.FullyReady);
        Assert.Contains(logs, l => l.Contains("OLLAMA_AVAILABLE", StringComparison.Ordinal));
        Assert.Contains(logs, l => l.Contains("AI_STARTUP_CHECK", StringComparison.Ordinal));
        Assert.Contains(report.SummaryLines, s => s.Contains("可以发言", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CheckOnce_WhenVoiceNotReady_ReportsVoiceMissing()
    {
        var handler = new ScriptedHandler(req =>
        {
            if (req.RequestUri!.AbsolutePath.Contains("/api/tags", StringComparison.Ordinal))
            {
                return JsonResponse("""{"models":[{"name":"qwen3:8b"}]}""");
            }

            return JsonResponse("""{"tts_ready":true,"voice_ready":false,"voice":""}""");
        });
        using var http = new HttpClient(handler);
        using var ollama = new OllamaClient("http://127.0.0.1:11434", TimeSpan.FromSeconds(5), http);
        using var tts = new GptSovitsClient("http://127.0.0.1:9880", TimeSpan.FromSeconds(5), http);
        var checker = new AiSpeechHealthChecker(
            ollama, tts,
            () => "http://127.0.0.1:11434", () => 5,
            () => "http://127.0.0.1:9880", () => 5,
            () => "qwen3:8b", () => "my_voice");

        var report = await checker.CheckOnceAsync("voice", CancellationToken.None);
        Assert.True(report.OllamaAvailable && report.ModelAvailable);
        Assert.True(report.TtsAvailable && report.TtsReady);
        Assert.False(report.VoiceReady);
        Assert.Contains("声音模型未加载", report.ServiceHint);
    }

    private static HttpResponseMessage JsonResponse(string json)
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

    private sealed class FailHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new HttpRequestException("connection refused");
    }

    private sealed class ScriptedHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _fn;
        public ScriptedHandler(Func<HttpRequestMessage, HttpResponseMessage> fn) => _fn = fn;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(_fn(request));
    }
}
