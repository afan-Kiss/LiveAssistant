using LiveAssistant.Config;
using LiveAssistant.Models;
using LiveAssistant.Services;
using LiveAssistant.Services.AiSpeech;
using Xunit;

namespace LiveAssistant.Tests;

public class AiSpeechCoordinatorSmokeTests
{
    [Fact]
    public async Task Enqueue_TwentyMessages_NeverExceedsMaxQueue()
    {
        var temp = Path.Combine(Path.GetTempPath(), "la-ai-speech-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        Environment.SetEnvironmentVariable("LA_DATA_DIR", temp);
        Environment.SetEnvironmentVariable("LA_TEST_EXE_DIR", temp);

        try
        {
            var config = new ConfigManager();
            config.Load();
            config.Settings.AiSpeech.Enabled = true;
            config.Settings.AiSpeech.MaxQueueSize = 5;
            config.Settings.AiSpeech.MinIntervalSeconds = 3;
            config.Settings.AiSpeech.Model = "qwen3:8b";
            // 指向不可达端口，确保不会真正打满 Ollama/TTS；只验证排队策略
            config.Settings.AiSpeech.OllamaUrl = "http://127.0.0.1:1";
            config.Settings.AiSpeech.TtsUrl = "http://127.0.0.1:1";
            config.Settings.AiSpeech.OllamaTimeoutSeconds = 2;
            config.Settings.AiSpeech.TtsTimeoutSeconds = 2;
            config.Save();

            var log = new LogService(temp);
            var tracker = new OutboundReplyTracker();
            using var coordinator = new AiSpeechCoordinator(config, log, tracker);

            for (var i = 0; i < 20; i++)
            {
                coordinator.TryEnqueueDanmaku(new DanmakuItem
                {
                    MsgId = "m" + i,
                    UserId = "u" + i,
                    Nickname = "用户" + i,
                    Content = $"主播你好吗这是第{i}条有意义的弹幕内容",
                    MsgType = "chat",
                    Timestamp = DateTime.Now
                }, null, null);
            }

            await Task.Delay(300);
            var status = coordinator.GetStatus();
            Assert.True(status.QueueCount <= 5, $"queue={status.QueueCount}");
        }
        finally
        {
            Environment.SetEnvironmentVariable("LA_DATA_DIR", null);
            Environment.SetEnvironmentVariable("LA_TEST_EXE_DIR", null);
            try { Directory.Delete(temp, true); } catch { /* ignore */ }
        }
    }

    [Fact]
    public async Task SelfMessage_IsSkipped()
    {
        var temp = Path.Combine(Path.GetTempPath(), "la-ai-speech-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        Environment.SetEnvironmentVariable("LA_DATA_DIR", temp);
        Environment.SetEnvironmentVariable("LA_TEST_EXE_DIR", temp);

        try
        {
            var config = new ConfigManager();
            config.Load();
            config.Settings.AiSpeech.Enabled = true;
            config.Settings.AiSpeech.OllamaUrl = "http://127.0.0.1:1";
            config.Settings.AiSpeech.TtsUrl = "http://127.0.0.1:1";
            config.Save();

            var log = new LogService(temp);
            var tracker = new OutboundReplyTracker();
            using var coordinator = new AiSpeechCoordinator(config, log, tracker);

            coordinator.TryEnqueueDanmaku(new DanmakuItem
            {
                MsgId = "self1",
                UserId = "host",
                Nickname = "主播昵称",
                Content = "大家好今天一起聊聊",
                MsgType = "chat"
            }, "主播昵称", "主播昵称");

            await Task.Delay(200);
            Assert.Equal(0, coordinator.GetStatus().QueueCount);
        }
        finally
        {
            Environment.SetEnvironmentVariable("LA_DATA_DIR", null);
            Environment.SetEnvironmentVariable("LA_TEST_EXE_DIR", null);
            try { Directory.Delete(temp, true); } catch { /* ignore */ }
        }
    }

    [Fact]
    public async Task TestVoice_WhenTtsDown_DoesNotThrow()
    {
        var temp = Path.Combine(Path.GetTempPath(), "la-ai-speech-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        Environment.SetEnvironmentVariable("LA_DATA_DIR", temp);
        Environment.SetEnvironmentVariable("LA_TEST_EXE_DIR", temp);

        try
        {
            var config = new ConfigManager();
            config.Load();
            config.Settings.AiSpeech.TtsUrl = "http://127.0.0.1:1";
            config.Settings.AiSpeech.TtsTimeoutSeconds = 2;
            config.Save();

            var log = new LogService(temp);
            using var coordinator = new AiSpeechCoordinator(config, log, new OutboundReplyTracker());
            var result = await coordinator.TestVoiceAsync();
            Assert.False(result.Success);
            Assert.False(string.IsNullOrWhiteSpace(result.Error));
        }
        finally
        {
            Environment.SetEnvironmentVariable("LA_DATA_DIR", null);
            Environment.SetEnvironmentVariable("LA_TEST_EXE_DIR", null);
            try { Directory.Delete(temp, true); } catch { /* ignore */ }
        }
    }
}
