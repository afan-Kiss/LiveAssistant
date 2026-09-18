using System.Net;
using System.Text;
using System.Text.Json;
using LiveAssistant.Config;
using LiveAssistant.Database;
using LiveAssistant.Models;
using LiveAssistant.Services;
using Xunit;

namespace LiveAssistant.Tests;

public sealed class SongRequestConfirmFlowTests : IDisposable
{
    private readonly string _dir;

    public SongRequestConfirmFlowTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "la-sr-confirm-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    [Fact]
    public async Task SingleResult_AlwaysAsksConfirm_EvenIfSettingDisabled()
    {
        var sent = new List<string>();
        var ctx = CreateContext(
            SongRequestPolicyMode.Free,
            requireConfirm: false,
            onMention: content =>
            {
                sent.Add(content);
                return true;
            });
        var user = new DanmakuItem { UserId = "u1", Nickname = "观众A", Content = "点歌 泡沫" };

        await ctx.SongRequest.HandleDanmakuAsync(user, "room1");
        Assert.Equal(0, ctx.Queue.WaitingCount);
        Assert.True(await WaitUntil(() => sent.Count >= 1, TimeSpan.FromSeconds(2)));
        Assert.Contains("是否确定点歌", sent[0], StringComparison.Ordinal);

        await ctx.SongRequest.HandleDanmakuAsync(
            new DanmakuItem { UserId = "u1", Nickname = "观众A", Content = "确定" }, "room1");
        Assert.True(await WaitUntil(() => ctx.Queue.WaitingCount >= 1, TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task OrphanConfirm_WithoutSession_RepliesHint()
    {
        var sent = new List<string>();
        var ctx = CreateContext(
            SongRequestPolicyMode.Free,
            requireConfirm: true,
            onMention: content =>
            {
                sent.Add(content);
                return true;
            });

        await ctx.SongRequest.HandleDanmakuAsync(
            new DanmakuItem { UserId = "u1", Nickname = "观众", Content = "确定" }, "room1");
        Assert.Equal(0, ctx.Queue.WaitingCount);
        Assert.True(await WaitUntil(() => sent.Count >= 1, TimeSpan.FromSeconds(2)));
        Assert.Contains("没有待确认的点歌", sent[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task OrphanConfirm_RateLimited_DoesNotSpam()
    {
        var sent = new List<string>();
        var ctx = CreateContext(
            SongRequestPolicyMode.Free,
            requireConfirm: true,
            onMention: content =>
            {
                sent.Add(content);
                return true;
            });

        var confirm = new DanmakuItem { UserId = "u1", Nickname = "观众", Content = "确定" };
        await ctx.SongRequest.HandleDanmakuAsync(confirm, "room1");
        await ctx.SongRequest.HandleDanmakuAsync(confirm, "room1");
        await ctx.SongRequest.HandleDanmakuAsync(confirm, "room1");
        Assert.True(await WaitUntil(() => sent.Count >= 1, TimeSpan.FromSeconds(2)));
        Assert.Equal(1, sent.Count);
    }

    [Fact]
    public async Task UnrelatedChat_DuringConfirm_DoesNotResendPrompt_AndReturnsNotConsumed()
    {
        var sent = new List<string>();
        var ctx = CreateContext(
            SongRequestPolicyMode.Free,
            requireConfirm: true,
            onMention: content =>
            {
                sent.Add(content);
                return true;
            });

        Assert.True(await ctx.SongRequest.HandleDanmakuAsync(
            new DanmakuItem { UserId = "u1", Nickname = "观众", Content = "点歌 泡沫" }, "room1"));
        Assert.True(await WaitUntil(() => sent.Count >= 1, TimeSpan.FromSeconds(2)));
        var before = sent.Count;

        // 闲聊：不消费、不清会话、不重发询问
        Assert.False(await ctx.SongRequest.HandleDanmakuAsync(
            new DanmakuItem { UserId = "u1", Nickname = "观众", Content = "主播你好" }, "room1"));
        await Task.Delay(300);
        Assert.Equal(before, sent.Count);
        Assert.Equal(0, ctx.Queue.WaitingCount);

        // 之后仍可确认入队
        Assert.True(await ctx.SongRequest.HandleDanmakuAsync(
            new DanmakuItem { UserId = "u1", Nickname = "观众", Content = "确定" }, "room1"));
        Assert.True(await WaitUntil(() => ctx.Queue.WaitingCount == 1, TimeSpan.FromSeconds(3)));
    }

    [Fact]
    public async Task UnrelatedChat_DuringConfirm_DoesNotResendPrompt()
    {
        var sent = new List<string>();
        var ctx = CreateContext(
            SongRequestPolicyMode.Free,
            requireConfirm: true,
            onMention: content =>
            {
                sent.Add(content);
                return true;
            });

        await ctx.SongRequest.HandleDanmakuAsync(
            new DanmakuItem { UserId = "u1", Nickname = "观众", Content = "点歌 泡沫" }, "room1");
        Assert.True(await WaitUntil(() => sent.Count >= 1, TimeSpan.FromSeconds(2)));
        var before = sent.Count;

        await ctx.SongRequest.HandleDanmakuAsync(
            new DanmakuItem { UserId = "u1", Nickname = "观众", Content = "哈哈哈" }, "room1");
        await Task.Delay(300);
        Assert.Equal(before, sent.Count);
        Assert.Equal(0, ctx.Queue.WaitingCount);
    }

    [Fact]
    public async Task ResolveFailure_KeepsSession_AllowsRetryConfirm()
    {
        var sent = new List<string>();
        var ctx = CreateContext(
            SongRequestPolicyMode.Free,
            resolveUrl: false,
            requireConfirm: true,
            onMention: content =>
            {
                sent.Add(content);
                return true;
            });

        await ctx.SongRequest.HandleDanmakuAsync(
            new DanmakuItem { UserId = "u1", Nickname = "观众", Content = "点歌 泡沫" }, "room1");
        await ctx.SongRequest.HandleDanmakuAsync(
            new DanmakuItem { UserId = "u1", Nickname = "观众", Content = "确定" }, "room1");
        Assert.True(await WaitUntil(
            () => sent.Any(s => s.Contains("播放地址获取失败", StringComparison.Ordinal)),
            TimeSpan.FromSeconds(2)));
        Assert.Equal(0, ctx.Queue.WaitingCount);

        // 取链失败后会话仍在：再发确定不应回「没有待确认」
        var before = sent.Count;
        await ctx.SongRequest.HandleDanmakuAsync(
            new DanmakuItem { UserId = "u1", Nickname = "观众", Content = "确定" }, "room1");
        Assert.True(await WaitUntil(() => sent.Count > before, TimeSpan.FromSeconds(2)));
        Assert.DoesNotContain(sent.Skip(before), s => s.Contains("没有待确认", StringComparison.Ordinal));
        Assert.Contains(sent.Skip(before), s => s.Contains("播放地址获取失败", StringComparison.Ordinal));
    }

    [Fact]
    public async Task FreeMode_DoesNotEnqueueUntilUserReplies确定()
    {
        var ctx = CreateContext(SongRequestPolicyMode.Free, requireConfirm: true);
        var user = new DanmakuItem { UserId = "u1", Nickname = "观众A", Content = "点歌 泡沫" };

        await ctx.SongRequest.HandleDanmakuAsync(user, "room1");
        Assert.Equal(0, ctx.Queue.WaitingCount);

        await ctx.SongRequest.HandleDanmakuAsync(
            new DanmakuItem { UserId = "u1", Nickname = "观众A", Content = "确定" }, "room1");
        Assert.Equal(1, ctx.Queue.WaitingCount);
    }

    [Fact]
    public async Task PointsMode_DoesNotEnqueueUntilUserReplies确定()
    {
        var ctx = CreateContext(SongRequestPolicyMode.Points, userPoints: 100, pointsCost: 10, requireConfirm: true);
        var user = new DanmakuItem { UserId = "u1", Nickname = "观众B", Content = "点歌 泡沫" };

        await ctx.SongRequest.HandleDanmakuAsync(user, "room1");
        Assert.Equal(0, ctx.Queue.WaitingCount);

        await ctx.SongRequest.HandleDanmakuAsync(
            new DanmakuItem { UserId = "u1", Nickname = "观众B", Content = "确定" }, "room1");
        Assert.Equal(1, ctx.Queue.WaitingCount);
        Assert.Equal(90, ctx.Users.GetUser("u1")!.Points);
    }

    [Fact]
    public async Task DoubleConfirm_OnlyEnqueuesOnce_AndDeductsOnce()
    {
        var ctx = CreateContext(SongRequestPolicyMode.Points, userPoints: 100, pointsCost: 10, requireConfirm: true);
        await ctx.SongRequest.HandleDanmakuAsync(
            new DanmakuItem { UserId = "u1", Nickname = "观众", Content = "点歌 泡沫" }, "room1");

        var confirm = new DanmakuItem { UserId = "u1", Nickname = "观众", Content = "确定" };
        await ctx.SongRequest.HandleDanmakuAsync(confirm, "room1");
        await ctx.SongRequest.HandleDanmakuAsync(confirm, "room1");

        Assert.Equal(1, ctx.Queue.WaitingCount);
        Assert.Equal(90, ctx.Users.GetUser("u1")!.Points);
    }

    [Fact]
    public async Task MultiArtist_AutoPicksFirst_AsksConfirmOnly()
    {
        var sent = new List<string>();
        var ctx = CreateContext(
            SongRequestPolicyMode.Free,
            requireConfirm: true,
            searchResultCount: 3,
            onMention: content =>
            {
                sent.Add(content);
                return true;
            });

        await ctx.SongRequest.HandleDanmakuAsync(
            new DanmakuItem { UserId = "u1", Nickname = "观众", Content = "点歌 光年之外" }, "room1");
        Assert.Equal(0, ctx.Queue.WaitingCount);
        Assert.True(await WaitUntil(() => sent.Count >= 1, TimeSpan.FromSeconds(2)));
        Assert.Contains("是否确定点歌", sent[0], StringComparison.Ordinal);
        Assert.Contains("G.E.M.邓紫棋", sent[0], StringComparison.Ordinal);
        Assert.DoesNotContain("请回复歌手名", sent[0], StringComparison.Ordinal);

        await ctx.SongRequest.HandleDanmakuAsync(
            new DanmakuItem { UserId = "u1", Nickname = "观众", Content = "确定" }, "room1");
        Assert.True(await WaitUntil(() => ctx.Queue.WaitingCount >= 1, TimeSpan.FromSeconds(2)));
        var queued = ctx.Queue.Waiting.First();
        Assert.Equal("光年之外", queued.SongName);
        Assert.Equal("G.E.M.邓紫棋", queued.Artist);
        Assert.Equal("hash0", queued.Hash);
    }

    private static async Task<bool> WaitUntil(Func<bool> cond, TimeSpan timeout)
    {
        var start = DateTime.UtcNow;
        while (DateTime.UtcNow - start < timeout)
        {
            if (cond())
            {
                return true;
            }

            await Task.Delay(40);
        }

        return cond();
    }

    [Fact]
    public async Task ResolveFailure_DoesNotEnqueueOrDeductPoints()
    {
        var ctx = CreateContext(SongRequestPolicyMode.Points, userPoints: 100, pointsCost: 10, resolveUrl: false, requireConfirm: true);
        await ctx.SongRequest.HandleDanmakuAsync(
            new DanmakuItem { UserId = "u1", Nickname = "观众", Content = "点歌 泡沫" }, "room1");
        await ctx.SongRequest.HandleDanmakuAsync(
            new DanmakuItem { UserId = "u1", Nickname = "观众", Content = "确定" }, "room1");

        Assert.Equal(0, ctx.Queue.WaitingCount);
        Assert.Equal(100, ctx.Users.GetUser("u1")!.Points);
    }

    private TestContext CreateContext(
        SongRequestPolicyMode mode,
        int userPoints = 0,
        int pointsCost = 10,
        bool resolveUrl = true,
        int searchResultCount = 1,
        bool requireConfirm = false,
        Func<string, bool>? onMention = null)
    {
        var db = new AppDatabase(_dir);
        var config = new ConfigManager();
        config.Load();
        config.Settings.SongRequestPolicy.Mode = mode;
        config.Settings.SongRequestPolicy.RequireConfirm = requireConfirm;
        config.Settings.SongRequestPolicy.PointsCost = pointsCost;
        config.ReplyTemplates["songRequestConfirm"] = "是否确定点歌《{song}》- {artist}？回复 确定 开始点歌";
        config.ReplyTemplates["songRequestConfirmPoints"] =
            "是否确定点歌《{song}》- {artist}？需要 {cost} 积分，回复 确定 开始点歌";
        config.ReplyTemplates["songRequestChooseArtist"] = "找到多首《{song}》，请回复歌手名：{artists}";
        config.ReplyTemplates["songRequestArtistNotFound"] = "没有找到该歌手，请从以下歌手中选择：{artists}";
        config.ReplyTemplates["songResolveFailed"] = "《{song}》播放地址获取失败，请稍后重试";

        var log = new LogService(_dir);
        var handler = new KugouStubHandler(resolveUrl, searchResultCount);
        var kugou = new KugouService(config.Settings.Kugou, log, new HttpClient(handler)
        {
            BaseAddress = new Uri("http://127.0.0.1:17888/")
        });
        var users = new UserRepository(db);
        users.EnsureUser("u1", "测试");
        if (userPoints > 0)
        {
            users.AddPoints("u1", "测试", userPoints);
        }

        var queue = new QueueService(db);
        var permission = new SongRequestPermissionService(
            config, users, queue,
            new SongBlacklistService(new SongBlacklistRepository(db)),
            new LevelPermissionRepository(db),
            new UserLevelService(config, users));

        var replyQueue = new ReplyQueue(
            new DouyinService(config.Settings.Douyin, log),
            log,
            config.Settings.Reply,
            sendMention: (_, _, content, _, _) => Task.FromResult(new MentionSendResult { Ok = onMention?.Invoke(content) ?? true }));

        var songRequest = new SongRequestService(
            config, kugou, queue, permission, new ReplyService(config),
            replyQueue, new SystemMessageService(20), log);

        return new TestContext(queue, users, songRequest);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, true);
        }
        catch
        {
            // ignore
        }
    }

    private sealed record TestContext(QueueService Queue, UserRepository Users, SongRequestService SongRequest);

    private sealed class KugouStubHandler : HttpMessageHandler
    {
        private readonly bool _resolveUrl;
        private readonly int _searchResultCount;

        public KugouStubHandler(bool resolveUrl = true, int searchResultCount = 1)
        {
            _resolveUrl = resolveUrl;
            _searchResultCount = searchResultCount;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? "";
            if (path.Contains("search", StringComparison.Ordinal))
            {
                object[] playlist;
                if (_searchResultCount <= 1)
                {
                    playlist =
                    [
                        new
                        {
                            hash = "hash1",
                            歌曲名称 = "泡沫",
                            歌手名称 = "G.E.M.邓紫棋",
                            id = "1"
                        }
                    ];
                }
                else
                {
                    var artists = new[] { "G.E.M.邓紫棋", "月亮失了约zz", "DJ初檬" };
                    playlist = artists
                        .Take(_searchResultCount)
                        .Select((artist, index) => new
                        {
                            hash = $"hash{index}",
                            歌曲名称 = "光年之外",
                            歌手名称 = artist,
                            id = (index + 1).ToString()
                        })
                        .Cast<object>()
                        .ToArray();
                }

                return Task.FromResult(Json(new
                {
                    code = 0,
                    data = new { 歌单 = playlist }
                }));
            }

            if (path.Contains("song/url", StringComparison.Ordinal))
            {
                if (!_resolveUrl)
                {
                    return Task.FromResult(Json(new { code = 502, msg = "url unavailable" }));
                }

                return Task.FromResult(Json(new
                {
                    code = 0,
                    data = new { url = "http://fs/yp/f_paomo.mp3", is_preview = false }
                }));
            }

            if (path.Contains("login/status", StringComparison.Ordinal))
            {
                return Task.FromResult(Json(new { code = 0, data = new { logged_in = true, nickname = "test" } }));
            }

            return Task.FromResult(Json(new { code = 1, msg = "unknown" }));
        }

        private static HttpResponseMessage Json(object body)
        {
            var json = JsonSerializer.Serialize(body);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
        }
    }
}
