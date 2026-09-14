using LiveAssistant.Config;
using LiveAssistant.Services;
using Xunit;

namespace LiveAssistant.Tests;

public sealed class DanmakuReplyStabilityTests : IDisposable
{
    private readonly string _dir;

    public DanmakuReplyStabilityTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "la_dm_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    [Fact]
    public void DanmakuDeduplicator_DropsSameMsgId_WithinTtl()
    {
        var deduper = new DanmakuDeduplicator(_dir, TimeSpan.FromMinutes(30));
        Assert.True(deduper.TryAdmit("m1"));
        Assert.False(deduper.TryAdmit("m1"));
        Assert.True(deduper.TryAdmit("m2"));
    }

    [Fact]
    public void DanmakuDeduplicator_SurvivesRestart_ViaFile()
    {
        var a = new DanmakuDeduplicator(_dir, TimeSpan.FromMinutes(30));
        Assert.True(a.TryAdmit("persist-1"));

        var b = new DanmakuDeduplicator(_dir, TimeSpan.FromMinutes(30));
        Assert.True(b.Contains("persist-1"));
        Assert.False(b.TryAdmit("persist-1"));
    }

    [Fact]
    public void DanmakuDeduplicator_EmptyMsgId_AlwaysAdmitted()
    {
        var deduper = new DanmakuDeduplicator(ttl: TimeSpan.FromMinutes(30));
        Assert.True(deduper.TryAdmit(""));
        Assert.True(deduper.TryAdmit(""));
        Assert.True(deduper.TryAdmit(null));
    }

    [Fact]
    public void ReplyIdempotencyStore_SameReplyId_SucceedsOnce()
    {
        var store = new ReplyIdempotencyStore(TimeSpan.FromMinutes(30));
        Assert.False(store.HasSucceeded("r1"));
        store.MarkSucceeded("r1");
        Assert.True(store.HasSucceeded("r1"));
    }

    [Fact]
    public async Task ReplyQueue_SameReplyId_NotSentTwice_AfterSuccess()
    {
        var sends = 0;
        var settings = new ReplySettings
        {
            MaxPerSecond = 20,
            MaxRetries = 3,
            RetryDelayMs = 10
        };
        var idem = new ReplyIdempotencyStore();
        var log = new LogService(_dir);
        var douyin = new DouyinService(new DouyinSettings { BaseUrl = "http://127.0.0.1:9" }, log);

        using var queue = new ReplyQueue(
            douyin,
            log,
            settings,
            idem,
            (_, _, _, _) =>
            {
                Interlocked.Increment(ref sends);
                return Task.FromResult(true);
            });

        const string replyId = "fixed-reply-id-001";
        queue.EnqueueMentionWithReplyId(replyId, "rid", "u1", "@测试 你好");
        await Task.Delay(150);
        Assert.Equal(1, Volatile.Read(ref sends));
        Assert.True(idem.HasSucceeded(replyId));

        // 模拟成功后网络层又把同一 reply_id 重入队
        queue.EnqueueMentionWithReplyId(replyId, "rid", "u1", "@测试 你好");
        await Task.Delay(150);
        Assert.Equal(1, Volatile.Read(ref sends));
    }

    [Fact]
    public async Task ReplyQueue_RetryKeepsSameReplyId_AndMarksSuccessOnce()
    {
        var attempts = 0;
        var settings = new ReplySettings
        {
            MaxPerSecond = 20,
            MaxRetries = 3,
            RetryDelayMs = 20
        };
        var idem = new ReplyIdempotencyStore();
        var log = new LogService(_dir);
        var douyin = new DouyinService(new DouyinSettings { BaseUrl = "http://127.0.0.1:9" }, log);

        using var queue = new ReplyQueue(
            douyin,
            log,
            settings,
            idem,
            (_, _, _, _) =>
            {
                var n = Interlocked.Increment(ref attempts);
                // 第一次失败，第二次成功 —— 验证重试用同一条链路且成功后只记一次
                return Task.FromResult(n >= 2);
            });

        queue.EnqueueMention("rid", "user-x", "内容");
        await Task.Delay(500);

        Assert.True(Volatile.Read(ref attempts) >= 2);
        Assert.Equal(1, idem.Count);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* ignore */ }
    }
}
