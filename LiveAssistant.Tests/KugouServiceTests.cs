using System.Net;
using System.Text;
using System.Text.Json;
using LiveAssistant.Config;
using LiveAssistant.Services;
using Xunit;

namespace LiveAssistant.Tests;

public sealed class KugouServiceTests
{
    [Fact]
    public async Task GetPlayUrl_WhenLoggedIn_RejectsPreviewAndRetriesAfterRefresh()
    {
        var calls = 0;
        var handler = new StubHandler(req =>
        {
            var path = req.RequestUri?.AbsolutePath ?? "";
            if (path.Contains("login/status", StringComparison.Ordinal))
            {
                return Json(new { code = 0, data = new { logged_in = true, nickname = "test", vip_label = "概念版VIP" } });
            }

            if (path.Contains("song/url", StringComparison.Ordinal))
            {
                calls++;
                var preview = calls == 1;
                return Json(new
                {
                    code = 0,
                    data = new { url = preview ? "http://fs/yp/p_test.mp3" : "http://fs/yp/f_test.mp3", is_preview = preview }
                });
            }

            return Json(new { code = 1, msg = "unknown" });
        });

        var svc = CreateService(handler);
        var url = await svc.GetPlayUrlAsync("abc123", "测试", CancellationToken.None);

        Assert.NotNull(url);
        Assert.False(url!.IsPreview);
        Assert.Contains("/yp/f_", url.Url);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task TryAutoClaimVip_OnlyOncePerDay()
    {
        var claimCalls = 0;
        var handler = new StubHandler(req =>
        {
            var path = req.RequestUri?.AbsolutePath ?? "";
            if (path.Contains("login/status", StringComparison.Ordinal))
            {
                return Json(new { code = 0, data = new { logged_in = true, nickname = "test" } });
            }

            if (path.Contains("vip/claim", StringComparison.Ordinal))
            {
                claimCalls++;
                return Json(new
                {
                    code = 0,
                    data = new { claimed = true, upgraded = true, message = "已领取", vip_label = "概念版VIP" }
                });
            }

            return Json(new { code = 1, msg = "unknown" });
        });

        var svc = CreateService(handler);
        await svc.RefreshLoginStatusAsync();
        var first = await svc.TryAutoClaimVipAsync();
        var second = await svc.TryAutoClaimVipAsync();

        Assert.NotNull(first);
        Assert.Null(second);
        Assert.Equal(1, claimCalls);
    }

    [Fact]
    public async Task GetPlayUrl_WhenAutoFails_FallsBackToPreview_WhenNotLoggedIn()
    {
        var handler = new StubHandler(req =>
        {
            var path = req.RequestUri?.AbsolutePath ?? "";
            if (path.Contains("login/status", StringComparison.Ordinal))
            {
                return Json(new { code = 0, data = new { logged_in = false } });
            }

            if (path.Contains("song/url", StringComparison.Ordinal))
            {
                var body = req.Content?.ReadAsStringAsync().GetAwaiter().GetResult() ?? "";
                if (body.Contains("\"mode\":\"preview\"", StringComparison.Ordinal))
                {
                    return Json(new
                    {
                        code = 0,
                        data = new { url = "http://fs/yp/p_paomo.mp3", is_preview = true }
                    });
                }

                return Json(new { code = 502, msg = "got preview url for full request" });
            }

            return Json(new { code = 1, msg = "unknown" });
        });

        var svc = CreateService(handler);
        var url = await svc.GetPlayUrlAsync("paomo-hash", "泡沫", CancellationToken.None);

        Assert.NotNull(url);
        Assert.True(url!.IsPreview);
        Assert.Contains("p_paomo", url.Url);
    }

    [Fact]
    public async Task GetPlayUrl_WhenLoggedInAndFullRequired_DoesNotFallBackToPreview()
    {
        var handler = new StubHandler(req =>
        {
            var path = req.RequestUri?.AbsolutePath ?? "";
            if (path.Contains("login/status", StringComparison.Ordinal))
            {
                return Json(new { code = 0, data = new { logged_in = true, nickname = "test" } });
            }

            if (path.Contains("song/url", StringComparison.Ordinal))
            {
                return Json(new { code = 502, msg = "got preview url for full request" });
            }

            return Json(new { code = 1, msg = "unknown" });
        });

        var svc = CreateService(handler);
        var url = await svc.GetPlayUrlAsync("paomo-hash", "泡沫", CancellationToken.None);

        Assert.Null(url);
    }

    [Fact]
    public async Task GetPlayUrl_TriesFullModeAfterAuto()
    {
        var modes = new List<string>();
        var handler = new StubHandler(req =>
        {
            var path = req.RequestUri?.AbsolutePath ?? "";
            if (path.Contains("login/status", StringComparison.Ordinal))
            {
                return Json(new { code = 0, data = new { logged_in = true, nickname = "test" } });
            }

            if (path.Contains("song/url", StringComparison.Ordinal))
            {
                var body = req.Content?.ReadAsStringAsync().GetAwaiter().GetResult() ?? "";
                if (body.Contains("\"mode\":\"auto\"", StringComparison.Ordinal))
                {
                    modes.Add("auto");
                    return Json(new { code = 502, msg = "auto failed" });
                }

                if (body.Contains("\"mode\":\"full\"", StringComparison.Ordinal))
                {
                    modes.Add("full");
                    return Json(new
                    {
                        code = 0,
                        data = new { url = "http://fs/yp/f_paomo.mp3", is_preview = false }
                    });
                }
            }

            return Json(new { code = 1, msg = "unknown" });
        });

        var svc = CreateService(handler);
        var url = await svc.GetPlayUrlAsync("paomo-hash", "泡沫", CancellationToken.None);

        Assert.NotNull(url);
        Assert.False(url!.IsPreview);
        Assert.Contains("auto", modes);
        Assert.Contains("full", modes);
    }

    [Fact]
    public async Task GetPlayUrl_AlternateResult_StillFallsBackToPreview_WhenNotLoggedIn()
    {
        var primaryHash = "primary-hash";
        var altHash = "alt-hash";
        var handler = new StubHandler(req =>
        {
            var path = req.RequestUri?.AbsolutePath ?? "";
            if (path.Contains("login/status", StringComparison.Ordinal))
            {
                return Json(new { code = 0, data = new { logged_in = false } });
            }

            if (path.Contains("search", StringComparison.Ordinal))
            {
                return Json(new
                {
                    code = 0,
                    data = new
                    {
                        歌单 = new[]
                        {
                            new { hash = primaryHash, 歌曲名称 = "泡沫", 歌手名称 = "A", id = "1" },
                            new { hash = altHash, 歌曲名称 = "泡沫", 歌手名称 = "B", id = "2" }
                        }
                    }
                });
            }

            if (path.Contains("song/url", StringComparison.Ordinal))
            {
                var body = req.Content?.ReadAsStringAsync().GetAwaiter().GetResult() ?? "";
                if (body.Contains(altHash, StringComparison.Ordinal)
                    && body.Contains("\"mode\":\"preview\"", StringComparison.Ordinal))
                {
                    return Json(new
                    {
                        code = 0,
                        data = new { url = "http://fs/yp/p_alt.mp3", is_preview = true }
                    });
                }

                return Json(new { code = 502, msg = "got preview url for full request" });
            }

            return Json(new { code = 1, msg = "unknown" });
        });

        var svc = CreateService(handler);
        var url = await svc.GetPlayUrlAsync(primaryHash, "泡沫", CancellationToken.None);

        Assert.NotNull(url);
        Assert.Contains("p_alt", url!.Url);
    }

    [Fact]
    public async Task GetPlayUrl_WhenNotLoggedIn_AllowsPreview()
    {
        var handler = new StubHandler(req =>
        {
            var path = req.RequestUri?.AbsolutePath ?? "";
            if (path.Contains("login/status", StringComparison.Ordinal))
            {
                return Json(new { code = 0, data = new { logged_in = false } });
            }

            if (path.Contains("song/url", StringComparison.Ordinal))
            {
                return Json(new
                {
                    code = 0,
                    data = new { url = "http://fs/yp/p_test.mp3", is_preview = true }
                });
            }

            return Json(new { code = 1, msg = "unknown" });
        });

        var svc = CreateService(handler);
        var url = await svc.GetPlayUrlAsync("abc123", "测试", CancellationToken.None);

        Assert.NotNull(url);
        Assert.True(url!.IsPreview);
    }

    private static KugouService CreateService(HttpMessageHandler handler)
    {
        var client = new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:17888/") };
        var log = new LogService(Path.Combine(Path.GetTempPath(), "la-kugou-test-" + Guid.NewGuid().ToString("N")));
        var settings = new KugouSettings { RequireFullPlayback = true };
        return new KugouService(settings, log, client);
    }

    private static HttpResponseMessage Json(object body)
    {
        var json = JsonSerializer.Serialize(body);
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler;

        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> handler)
        {
            _handler = handler;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(_handler(request));
    }
}
