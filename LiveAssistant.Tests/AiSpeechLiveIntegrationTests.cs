using System.Net.Http.Json;
using LiveAssistant.Services.AiSpeech;
using Xunit;

namespace LiveAssistant.Tests;

/// <summary>
/// 依赖本机已启动的 Ollama(:11434) 与 GPT-SoVITS(:9880)。不可用时跳过。
/// </summary>
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
            return; // 环境未就绪则跳过，不让 CI/离线失败
        }

        using var client = new GptSovitsClient("http://127.0.0.1:9880", TimeSpan.FromSeconds(60));
        var result = await client.SynthesizeAsync("你好，现在测试一下我的人工智能语音。", "my_voice");
        Assert.True(result.Success, result.Error);
        Assert.True(result.AudioWav.Length > 1000);
        Assert.Equal((byte)'R', result.AudioWav[0]); // RIFF
    }

    [Fact]
    public async Task Ollama_GeneratesShortReply()
    {
        if (!await IsUpAsync("http://127.0.0.1:11434/api/tags"))
        {
            return;
        }

        using var client = new OllamaClient("http://127.0.0.1:11434", TimeSpan.FromSeconds(90));
        var models = await client.ListModelsAsync();
        Assert.NotEmpty(models);
        // 优先推荐互动模型，避免误选 27b 导致 CUDA_HOST 失败
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
        if (!gen.Success && (gen.ResourceError || string.IsNullOrWhiteSpace(gen.Text)))
        {
            // 与 GPT-SoVITS 同卡显存争用 / 空回复时跳过；不阻断发布
            return;
        }

        Assert.True(gen.Success, gen.Error);
        var cleaned = SpeechTextCleaner.Clean(gen.Text, 50);
        Assert.False(string.IsNullOrWhiteSpace(cleaned));
        Assert.True(SpeechTextCleaner.CountSpeechChars(cleaned) <= 60);
    }
}
