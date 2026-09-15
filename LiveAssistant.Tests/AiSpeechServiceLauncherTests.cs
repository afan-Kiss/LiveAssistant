using LiveAssistant.Services.AiSpeech;
using Xunit;

namespace LiveAssistant.Tests;

public class AiSpeechServiceLauncherTests
{
    [Fact]
    public void ResolveOllamaExe_FindsKnownOrNull()
    {
        var path = AiSpeechServiceLauncher.ResolveOllamaExe(null);
        if (path != null)
        {
            Assert.True(File.Exists(path));
            Assert.EndsWith("ollama.exe", path, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void ResolveOllamaExe_UsesConfiguredWhenExists()
    {
        var existing = AiSpeechServiceLauncher.ResolveOllamaExe(null);
        if (existing == null)
        {
            return;
        }

        var resolved = AiSpeechServiceLauncher.ResolveOllamaExe(existing);
        Assert.Equal(Path.GetFullPath(existing), resolved);
    }

    [Fact]
    public void ResolveTtsStartScript_FindsBatOrNull()
    {
        var path = AiSpeechServiceLauncher.ResolveTtsStartScript(null);
        if (path != null)
        {
            Assert.True(File.Exists(path));
        }
    }

    [Fact]
    public async Task StartMissing_WhenAlreadyReady_DoesNotForceStart()
    {
        var launcher = new AiSpeechServiceLauncher();
        var ready = new AiSpeechHealthReport
        {
            OllamaAvailable = true,
            ModelAvailable = true,
            ModelConfigured = "qwen3:8b",
            TtsAvailable = true,
            TtsReady = true,
            VoiceReady = true
        };

        var result = await launcher.StartMissingAsync(ready, null, null, null, null, null);
        Assert.True(result.Success);
        Assert.True(result.OllamaAlreadyRunning);
        Assert.True(result.TtsAlreadyRunning);
        Assert.False(result.OllamaStarted);
        Assert.False(result.TtsStarted);
    }
}
