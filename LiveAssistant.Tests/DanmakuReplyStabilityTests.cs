using System.Net;
using System.Text;
using System.Text.Json;
using LiveAssistant.Config;
using LiveAssistant.Services;
using LiveAssistant.Utils;
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
    public void DanmakuDeduplicator_BlocksRepeatedChat_ButConfirmIsInteractiveCommand()
    {
        var deduper = new DanmakuDeduplicator(contentWindow: TimeSpan.FromMinutes(1));
        Assert.True(deduper.TryAdmitUserContent("u1", "666"));
        Assert.False(deduper.TryAdmitUserContent("u1", "666"));
        Assert.True(SongRequestConfirmParser.IsConfirm("确定"));
        Assert.True(SongRequestConfirmParser.IsConfirm("确定"));
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

    [Fact]
    public async Task ReplyQueue_FakeSuccessFromSidecar_DoesNotRetry()
    {
        var settings = new ReplySettings
        {
            MaxPerSecond = 20,
            MaxRetries = 3,
            RetryDelayMs = 20
        };
        var idem = new ReplyIdempotencyStore();
        var log = new LogService(_dir);
        var handler = new FakeSuccessMentionHandler();
        var douyin = new DouyinService(
            new DouyinSettings { BaseUrl = "http://127.0.0.1:17888/" },
            log,
            new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:17888/") });

        using var queue = new ReplyQueue(douyin, log, settings, idem);
        queue.EnqueueMention("rid", "user-x", "点歌成功《测试》，前面还有0首");
        await Task.Delay(300);

        Assert.Equal(1, Volatile.Read(ref handler.Attempts));
        Assert.Equal(1, idem.Count);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* ignore */ }
    }

    private sealed class FakeSuccessMentionHandler : HttpMessageHandler
    {
        public int Attempts;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Attempts);
            var json = JsonSerializer.Serialize(new { ok = false, message = "假成功：返回内容与发送内容不一致" });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
        }
    }
}
