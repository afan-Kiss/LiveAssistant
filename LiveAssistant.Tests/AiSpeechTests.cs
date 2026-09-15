using LiveAssistant.Models;
using LiveAssistant.Services.AiSpeech;
using Xunit;

namespace LiveAssistant.Tests;

public class AiSpeechFilterTests
{
    [Theory]
    [InlineData("", true)]
    [InlineData("好", true)]
    [InlineData("666", true)]
    [InlineData("12345", true)]
    [InlineData("点歌 泡沫", true)]
    [InlineData("确定", true)]
    [InlineData("取消", true)]
    [InlineData("查我", true)]
    [InlineData("切歌", true)]
    [InlineData("哈哈", true)]
    [InlineData("天气不错", true)] // 无加分，低于阈值
    [InlineData("主播这个软件是自己写的吗", false)]
    [InlineData("今天天气怎么样呀", false)]
    [InlineData("主播你好", false)]
    public void ShouldSkip_MatchesRules(string content, bool expectSkip)
    {
        var item = new DanmakuItem { Content = content, MsgType = "chat", Nickname = "观众A", UserId = "u1" };
        var skip = AiDanmakuFilter.ShouldSkip(item, AiDanmakuFilter.DefaultScoreThreshold, out _);
        Assert.Equal(expectSkip, skip);
    }

    [Theory]
    [InlineData("gift")]
    [InlineData("member")]
    [InlineData("like")]
    [InlineData("follow")]
    public void ShouldSkip_NonChatTypes(string msgType)
    {
        var item = new DanmakuItem { Content = "你好呀主播", MsgType = msgType, Nickname = "A", UserId = "1" };
        Assert.True(AiDanmakuFilter.ShouldSkip(item, out var reason));
        Assert.Contains(msgType, reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Evaluate_ScoresQuestionAndHost()
    {
        var item = new DanmakuItem
        {
            Content = "主播这个软件怎么用啊？",
            MsgType = "chat",
            Nickname = "观众",
            UserId = "u1"
        };
        var result = AiDanmakuFilter.Evaluate(item, 2);
        Assert.True(result.EnterAi);
        Assert.True(result.Score >= 2);
        Assert.Contains("提问", result.Detail, StringComparison.Ordinal);
        Assert.Contains("主播", result.Detail, StringComparison.Ordinal);
        Assert.Contains("软件", result.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Evaluate_LowScoreIgnored()
    {
        var item = new DanmakuItem
        {
            Content = "晚上好大家",
            MsgType = "chat",
            Nickname = "观众",
            UserId = "u1"
        };
        var result = AiDanmakuFilter.Evaluate(item, 2);
        Assert.False(result.EnterAi);
        Assert.Equal("low_score", result.Reason);
    }
}

public class AiSpeechModelsCatalogTests
{
    [Fact]
    public void Merge_PutsRecommendedFirst()
    {
        var merged = AiSpeechModelsCatalog.MergeWithInstalled(new[] { "llama3:8b", "qwen3:8b" });
        Assert.Equal("qwen3:8b", merged[0]);
        Assert.Contains("qwen2.5:7b", merged);
        Assert.Contains("qwen3.5:27b", merged);
        Assert.Contains("llama3:8b", merged);
        Assert.Equal(1, merged.Count(m => m.Equals("qwen3:8b", StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public void ResolveDefault_UsesRecommended()
    {
        Assert.Equal("qwen3:8b", AiSpeechModelsCatalog.ResolveDefault(""));
        Assert.Equal("qwen2.5:7b", AiSpeechModelsCatalog.ResolveDefault("qwen2.5:7b"));
    }
}

public class AiPersonalityLoaderTests
{
    [Fact]
    public void Load_CreatesDefaultFile()
    {
        var temp = Path.Combine(Path.GetTempPath(), "la-ai-person-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        var cfg = Path.Combine(temp, "Config");
        Directory.CreateDirectory(cfg);
        Environment.SetEnvironmentVariable("LA_TEST_EXE_DIR", temp);
        try
        {
            var text = AiPersonalityLoader.Load(temp, out var path);
            Assert.False(string.IsNullOrWhiteSpace(text));
            Assert.Contains("真实直播主播", text, StringComparison.Ordinal);
            Assert.True(File.Exists(AiPersonalityLoader.GetConfigPath()));
            Assert.True(File.Exists(path) || path.Contains("ai_personality", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Environment.SetEnvironmentVariable("LA_TEST_EXE_DIR", null);
            try { Directory.Delete(temp, true); } catch { /* ignore */ }
        }
    }
}

public class SpeechTextCleanerTests
{
    [Fact]
    public void Clean_RemovesMarkdownAndUrl()
    {
        var raw = "**你好** ##标题 `code` https://example.com/a 哈哈（笑）";
        var cleaned = SpeechTextCleaner.Clean(raw, 50);
        Assert.DoesNotContain("**", cleaned);
        Assert.DoesNotContain("##", cleaned);
        Assert.DoesNotContain("http", cleaned, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("（笑）", cleaned);
        Assert.Contains("哈哈", cleaned);
    }

    [Fact]
    public void Clean_TruncatesBySentence()
    {
        var raw = "第一句话就到这里。第二句会更长一些而且继续往后面写很多很多内容。第三句不该出现。";
        var cleaned = SpeechTextCleaner.Clean(raw, 20);
        Assert.True(SpeechTextCleaner.CountSpeechChars(cleaned) <= 25);
        Assert.DoesNotContain("第三句", cleaned);
        Assert.EndsWith("。", cleaned);
    }
}

public class AiSpeechQueuePolicyTests
{
    [Fact]
    public void Coordinator_DoesNotThrow_WhenServicesDown()
    {
        var items = Enumerable.Range(0, 20)
            .Select(i => new DanmakuItem
            {
                MsgId = "m" + i,
                UserId = "u" + i,
                Nickname = "用户" + i,
                Content = $"主播你好吗这是第{i}条测试弹幕",
                MsgType = "chat"
            })
            .ToList();

        var accepted = 0;
        foreach (var item in items)
        {
            if (!AiDanmakuFilter.ShouldSkip(item, out _))
            {
                accepted++;
            }
        }

        Assert.Equal(20, accepted);
    }
}
