using LiveAssistant.Services.AiSpeech;
using Xunit;

namespace LiveAssistant.Tests;

public class ModelOutputSanitizerTests
{
    [Fact]
    public void Sanitize_KeepsFinalAnswer_StripsThink()
    {
        var raw = """
            <think>
            用户问的是软件来源，我应该简短回答。
            </think>
            对，这个软件基本都是我自己慢慢做出来的。
            """;
        var r = ModelOutputSanitizer.Sanitize(raw);
        Assert.True(r.Ok);
        Assert.DoesNotContain("think", r.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("用户问的是", r.Text);
        Assert.Contains("自己慢慢做出来", r.Text);
    }

    [Fact]
    public void Sanitize_ThinkOnly_Rejects()
    {
        var raw = """
            <think>
            这是一大段分析，没有最终口语答案。
            </think>
            """;
        var r = ModelOutputSanitizer.Sanitize(raw);
        Assert.False(r.Ok);
        Assert.Equal("THINK_ONLY", r.RejectReason);
    }

    [Fact]
    public void Sanitize_Empty_Rejects()
    {
        var r = ModelOutputSanitizer.Sanitize("   ");
        Assert.False(r.Ok);
        Assert.Equal("EMPTY", r.RejectReason);
    }

    [Fact]
    public void Sanitize_AnalysisBlock_Stripped()
    {
        var raw = "<analysis>内部分析</analysis>嗯，这个声音是我自己训练的。";
        var r = ModelOutputSanitizer.Sanitize(raw);
        Assert.True(r.Ok);
        Assert.DoesNotContain("内部分析", r.Text);
        Assert.Contains("自己训练", r.Text);
    }
}

public class SpeechNameCleanerTests
{
    [Fact]
    public void Clean_StripsEmojiAndKeepsName()
    {
        var name = SpeechNameCleaner.Clean("✨✨✨小明666💗");
        Assert.Contains("小明", name);
        Assert.DoesNotContain("✨", name);
    }

    [Fact]
    public void Clean_AllEmoji_ReturnsEmpty()
    {
        Assert.Equal("", SpeechNameCleaner.Clean("👍👍👍"));
    }
}

public class AiSpeechSchedulerTests
{
    [Fact]
    public void Dequeues_HigherPriorityFirst()
    {
        var s = new AiSpeechScheduler { MaxSize = 10, MaxAgeSeconds = 60 };
        s.Enqueue(new AiSpeechTask
        {
            Kind = AiSpeechEventKind.Like,
            Priority = AiSpeechPriority.Like,
            Content = "like"
        });
        s.Enqueue(new AiSpeechTask
        {
            Kind = AiSpeechEventKind.Gift,
            Priority = AiSpeechPriority.Gift,
            Content = "gift"
        });
        s.Enqueue(new AiSpeechTask
        {
            Kind = AiSpeechEventKind.Danmaku,
            Priority = AiSpeechPriority.DanmakuImportant,
            Content = "danmaku"
        });

        Assert.True(s.TryDequeue(out var t1));
        Assert.Equal(AiSpeechEventKind.Gift, t1!.Kind);
        Assert.True(s.TryDequeue(out var t2));
        Assert.Equal(AiSpeechEventKind.Danmaku, t2!.Kind);
        Assert.True(s.TryDequeue(out var t3));
        Assert.Equal(AiSpeechEventKind.Like, t3!.Kind);
    }

    [Fact]
    public void MaxSize_DropsLowestPriority()
    {
        var s = new AiSpeechScheduler { MaxSize = 2, MaxAgeSeconds = 60 };
        s.Enqueue(new AiSpeechTask { Priority = AiSpeechPriority.Like, Content = "a" });
        s.Enqueue(new AiSpeechTask { Priority = AiSpeechPriority.Gift, Content = "b" });
        s.Enqueue(new AiSpeechTask { Priority = AiSpeechPriority.Welcome, Content = "c" });
        Assert.Equal(2, s.Count);
        Assert.True(s.TryDequeue(out var top));
        Assert.Equal(AiSpeechPriority.Gift, top!.Priority);
    }
}

public class UserConversationContextTests
{
    [Fact]
    public void Remembers_PerUserId_NotNickname()
    {
        var ctx = new UserConversationContext(8, 4, 15);
        ctx.AddUserMessage("uid-1", "这个声音是AI吗？");
        ctx.AddHostReply("uid-1", "对，是用我的声音训练出来的。");
        ctx.AddUserMessage("uid-2", "无关用户");

        var msgs = ctx.GetMessages("uid-1");
        Assert.Equal(2, msgs.Count);
        Assert.Contains(msgs, m => m.Content.Contains("声音是AI"));
        Assert.Contains(msgs, m => m.Content.Contains("训练出来"));
        Assert.DoesNotContain(msgs, m => m.Content.Contains("无关"));
    }
}

public class GiftMergeBufferTests
{
    [Fact]
    public void Merges_SameUserSameGift()
    {
        using var buf = new GiftMergeBuffer { MergeSeconds = 2 };
        buf.Add("u1", "小明", "小心心", 1);
        buf.Add("u1", "小明", "小心心", 1);
        buf.Add("u1", "小明", "小心心", 2);
        Thread.Sleep(2200);
        var ready = buf.DrainReady();
        Assert.Single(ready);
        Assert.Equal(4, ready[0].Count);
        Assert.Equal("小心心", ready[0].GiftName);
    }
}

public class WelcomeBatchBufferTests
{
    [Fact]
    public void Batches_Nicknames_MaxThree()
    {
        using var buf = new WelcomeBatchBuffer { IntervalSeconds = 30, MaxNames = 3 };
        WelcomeBatchBuffer.WelcomeBatch? got = null;
        buf.Flushed += b => got = b;
        buf.Add("1", "张三");
        buf.Add("2", "李四");
        buf.Add("3", "王五");
        buf.Add("4", "赵六");
        buf.FlushNow();
        Assert.NotNull(got);
        Assert.True(got!.Value.Nicknames.Count <= 3);
        Assert.Contains("张三", got.Value.Nicknames);
    }
}

public class EmotionPresetsTests
{
    [Fact]
    public void Unconfigured_Emotions_ReportFalse()
    {
        // 默认仓库通常只有自然参考，其它情绪未配置时应为 false
        Assert.False(EmotionPresets.IsConfigured(EmotionPresets.Happy));
        Assert.False(EmotionPresets.IsConfigured(EmotionPresets.Excited));
    }

    [Fact]
    public void AutoPick_Gift_IsExcited()
    {
        Assert.Equal(EmotionPresets.Excited, EmotionPresets.AutoPick(AiSpeechEventKind.Gift));
    }
}
