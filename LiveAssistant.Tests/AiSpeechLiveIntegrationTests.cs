using LiveAssistant.Services.AiSpeech;
using Xunit;

namespace LiveAssistant.Tests;

/// <summary>
/// 依赖本机 Ollama(:11434) 与 GPT-SoVITS(:9880)。
/// Category=LiveIntegration：默认单元测试用 Category!=LiveIntegration 排除。
/// health 不可用 → NOT RUN（return + Probe）；health 正常但业务失败 → FAIL。
/// </summary>
[Trait("Category", "LiveIntegration")]
public class AiSpeechLiveIntegrationTests
{
    private static async Task<bool> IsUpAsync(string url)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            using var resp = await http.GetAsync(url);
            return resp.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    [Fact]
    public async Task Tts_ReturnsWav()
    {
        if (!await IsUpAsync("http://127.0.0.1:9880/health"))
        {
            LiveIntegrationProbe.MarkNotRun("TTS health unavailable");
            return;
        }

        using var client = new GptSovitsClient("http://127.0.0.1:9880", TimeSpan.FromSeconds(60));
        var result = await client.SynthesizeAsync("你好，现在测试一下我的人工智能语音。", "my_voice");
        Assert.True(result.Success, result.Error ?? "TTS 合成失败（health 可达）");
        Assert.True(result.AudioWav.Length > 1000);
        Assert.Equal((byte)'R', result.AudioWav[0]);
        LiveIntegrationProbe.MarkExecuted("TTS");
    }

    [Fact]
    public async Task Ollama_GeneratesShortReply()
    {
        if (!await IsUpAsync("http://127.0.0.1:11434/api/tags"))
        {
            LiveIntegrationProbe.MarkNotRun("Ollama tags unavailable");
            return;
        }

        using var client = new OllamaClient("http://127.0.0.1:11434", TimeSpan.FromSeconds(90));
        var models = await client.ListModelsAsync();
        Assert.NotEmpty(models);
        var model = AiSpeechModelsCatalog.ResolveDefault(
            models.FirstOrDefault(m => m.Equals(AiSpeechModelsCatalog.DefaultModel, StringComparison.OrdinalIgnoreCase)),
            models.ToList());
        if (!models.Any(m => m.Equals(model, StringComparison.OrdinalIgnoreCase)))
        {
            model = models.FirstOrDefault(m => m.Contains("qwen", StringComparison.OrdinalIgnoreCase)) ?? models[0];
        }

        var gen = await client.GenerateAsync(
            model,
            AiSpeechCoordinator.DefaultSystemPrompt,
            "观众昵称：测试用户\n观众说：主播这个功能是你自己做的吗？");
        Assert.True(gen.Success, gen.Error ?? "Ollama health 可达但生成失败");
        var cleaned = SpeechTextCleaner.Clean(gen.Text, 50);
        Assert.False(string.IsNullOrWhiteSpace(cleaned));
        Assert.True(SpeechTextCleaner.CountSpeechChars(cleaned) <= 60);
        LiveIntegrationProbe.MarkExecuted("Ollama");
    }
}

/// <summary>供报告区分 Live NOT RUN / EXECUTED。</summary>
internal static class LiveIntegrationProbe
{
    private static readonly object Gate = new();
    private static readonly List<string> NotRun = new();
    private static readonly List<string> Executed = new();

    public static void MarkNotRun(string reason)
    {
        lock (Gate) { NotRun.Add(reason); }
        Console.WriteLine("[LIVE NOT RUN] " + reason);
    }

    public static void MarkExecuted(string name)
    {
        lock (Gate) { Executed.Add(name); }
        Console.WriteLine("[LIVE EXECUTED] " + name);
    }

    public static string Snapshot()
    {
        lock (Gate)
        {
            return $"executed=[{string.Join(",", Executed)}] notRun=[{string.Join(",", NotRun)}]";
        }
    }
}
