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
    public void LoginPageUrl_UsesServerLoginRoute()
    {
        var log = new LogService(Path.Combine(Path.GetTempPath(), "la-kugou-test-" + Guid.NewGuid().ToString("N")));
        var svc = new KugouService(new KugouSettings { BaseUrl = "http://127.0.0.1:17888" }, log);
        Assert.Equal("http://127.0.0.1:17888/login", svc.LoginPageUrl);

        var svcTrailing = new KugouService(new KugouSettings { BaseUrl = "http://127.0.0.1:17888/" }, log);
        Assert.Equal("http://127.0.0.1:17888/login", svcTrailing.LoginPageUrl);
        Assert.DoesNotContain("/api/v1/login/page", svc.LoginPageUrl, StringComparison.Ordinal);
    }

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
    public async Task RefreshLoginStatus_ConcurrentCalls_OnlyOneSidecarRequest()
    {
        var loginCalls = 0;
        var handler = new StubHandler(req =>
        {
            var path = req.RequestUri?.AbsolutePath ?? "";
            if (path.Contains("login/status", StringComparison.Ordinal))
            {
                var call = Interlocked.Increment(ref loginCalls);
                if (call == 1)
                {
                    Thread.Sleep(300);
                }

                return Json(new { code = 0, data = new { logged_in = true, nickname = "test" } });
            }

            return Json(new { code = 1, msg = "unknown" });
        });

        var svc = CreateService(handler);
        var tasks = Enumerable.Range(0, 6)
            .Select(_ => svc.RefreshLoginStatusAsync())
            .ToArray();
        var results = await Task.WhenAll(tasks);

        Assert.Equal(1, loginCalls);
        Assert.All(results, r => Assert.True(r.LoggedIn));
    }

    [Fact]
    public async Task TryAutoClaimVip_ConcurrentCalls_OnlyOneSidecarRequest()
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
                var call = Interlocked.Increment(ref claimCalls);
                if (call == 1)
                {
                    Thread.Sleep(300);
                }

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

        var tasks = Enumerable.Range(0, 6)
            .Select(_ => svc.TryAutoClaimVipAsync())
            .ToArray();
        await Task.WhenAll(tasks);

        Assert.Equal(1, claimCalls);
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

        var svc = CreateService(handler, requireFullPlayback: false);
        var url = await svc.GetPlayUrlAsync("paomo-hash", "泡沫", CancellationToken.None);

        Assert.NotNull(url);
        Assert.True(url!.IsPreview);
        Assert.Contains("p_paomo", url.Url);
    }

    [Fact]
    public async Task GetPlayUrl_WhenNotLoggedInAndFullRequired_ReturnsNull()
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

        var svc = CreateService(handler, requireFullPlayback: true);
        var url = await svc.GetPlayUrlAsync("abc123", "测试", CancellationToken.None);

        Assert.Null(url);
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
    public async Task GetPlayUrl_TriesFullModeBeforeAuto()
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
                if (body.Contains("\"mode\":\"full\"", StringComparison.Ordinal))
                {
                    modes.Add("full");
                    return Json(new
                    {
                        code = 0,
                        data = new { url = "http://fs/yp/f_paomo.mp3", is_preview = false }
                    });
                }

                if (body.Contains("\"mode\":\"auto\"", StringComparison.Ordinal))
                {
                    modes.Add("auto");
                    return Json(new { code = 502, msg = "auto failed" });
                }
            }

            return Json(new { code = 1, msg = "unknown" });
        });

        var svc = CreateService(handler);
        var url = await svc.GetPlayUrlAsync("paomo-hash", "泡沫", CancellationToken.None);

        Assert.NotNull(url);
        Assert.False(url!.IsPreview);
        Assert.Equal(new[] { "full" }, modes);
    }

    [Fact]
    public async Task GetPlayUrl_FallsBackToAutoWhenFullFails()
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
                if (body.Contains("\"mode\":\"full\"", StringComparison.Ordinal))
                {
                    modes.Add("full");
                    return Json(new { code = 502, msg = "full failed" });
                }

                if (body.Contains("\"mode\":\"auto\"", StringComparison.Ordinal))
                {
                    modes.Add("auto");
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
        Assert.Equal(new[] { "full", "auto" }, modes);
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

        var svc = CreateService(handler, requireFullPlayback: false);
        var url = await svc.GetPlayUrlAsync(primaryHash, "泡沫", CancellationToken.None);

        Assert.NotNull(url);
        Assert.Contains("p_alt", url!.Url);
    }

    [Fact]
    public async Task GetPlayUrl_WhenFullRequired_RejectsPreviewUrlEvenIfFlagFalse()
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
                return Json(new
                {
                    code = 0,
                    data = new { url = "http://fs/yp/p_silent.mp3", is_preview = false }
                });
            }

            return Json(new { code = 1, msg = "unknown" });
        });

        var svc = CreateService(handler);
        var url = await svc.GetPlayUrlAsync("abc123", "测试", CancellationToken.None);

        Assert.Null(url);
    }

    [Fact]
    public async Task GetPlayUrl_WhenSessionDropped_ClearsLoginAndReturnsNull()
    {
        var loggedIn = true;
        var handler = new StubHandler(req =>
        {
            var path = req.RequestUri?.AbsolutePath ?? "";
            if (path.Contains("login/status", StringComparison.Ordinal))
            {
                return Json(new { code = 0, data = new { logged_in = loggedIn, nickname = "test" } });
            }

            if (path.Contains("song/url", StringComparison.Ordinal))
            {
                loggedIn = false;
                return Json(new { code = 40101, msg = "登录已掉线，请重新扫码登录" });
            }

            return Json(new { code = 1, msg = "unknown" });
        });

        var svc = CreateService(handler);
        await svc.RefreshLoginStatusAsync();
        var url = await svc.GetPlayUrlAsync("abc123", "测试", CancellationToken.None);

        Assert.Null(url);
        Assert.False(svc.LoginSnapshot.LoggedIn);
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

        var svc = CreateService(handler, requireFullPlayback: false);
        var url = await svc.GetPlayUrlAsync("abc123", "测试", CancellationToken.None);

        Assert.NotNull(url);
        Assert.True(url!.IsPreview);
    }

    [Fact]
    public async Task PickRandomTrack_SkipsPreviewOnlyTracksAndContinues()
    {
        var handler = new StubHandler(req =>
        {
            var path = req.RequestUri?.AbsolutePath ?? "";
            if (path.Contains("everyday/recommend", StringComparison.Ordinal))
            {
                return Json(new
                {
                    code = 0,
                    data = new
                    {
                        歌单 = new[]
                        {
                            new { hash = "previewhash", 歌曲名称 = "试听歌", 歌手名称 = "歌手A", 歌曲ID = "201" },
                            new { hash = "fullhash", 歌曲名称 = "完整歌", 歌手名称 = "歌手B", 歌曲ID = "202" }
                        }
                    }
                });
            }

            if (path.Contains("song/url", StringComparison.Ordinal))
            {
                var body = req.Content?.ReadAsStringAsync().Result ?? "";
                var preview = body.Contains("previewhash", StringComparison.Ordinal);
                return Json(new
                {
                    code = 0,
                    data = new
                    {
                        url = preview ? "http://fs/yp/p_test.mp3" : "http://fs/yp/f_test.mp3",
                        is_preview = preview
                    }
                });
            }

            if (path.Contains("login/status", StringComparison.Ordinal))
            {
                return Json(new { code = 0, data = new { logged_in = true, nickname = "test" } });
            }

            return Json(new { code = 1, msg = "unknown" });
        });

        var svc = CreateService(handler);
        var track = await svc.PickRandomTrackAsync((_, _) => false, CancellationToken.None);

        Assert.NotNull(track);
        Assert.Equal("完整歌", track!.SongName);
        Assert.False(track.IsPreview);
    }

    [Fact]
    public async Task FetchEverydayRecommend_WithoutSession_SkipsUnauthenticatedMoe()
    {
        var moeCalls = 0;
        var handler = new StubHandler(req =>
        {
            var host = req.RequestUri?.Host ?? "";
            if (host == "127.0.0.1" && req.RequestUri?.Port is 16521 or 3000)
            {
                moeCalls++;
            }

            if ((req.RequestUri?.AbsolutePath ?? "").Contains("login/status", StringComparison.Ordinal))
            {
                return Json(new { code = 0, data = new { logged_in = true, nickname = "test", userid = "1" } });
            }

            return Json(new { code = 1, msg = "not found" });
        });

        var dataDir = Path.Combine(Path.GetTempPath(), "la-kugou-no-session-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dataDir);
        var svc = CreateService(handler, dataDirectory: dataDir);
        var songs = await svc.FetchEverydayRecommendAsync(CancellationToken.None);

        Assert.Empty(songs);
        Assert.Equal(0, moeCalls);
    }

    [Fact]
    public async Task PickRandomTrack_UsesDailyRecommendInOrderAndRefreshesWhenExhausted()
    {
        var everydayCalls = 0;
        var urlCalls = 0;
        var handler = new StubHandler(req =>
        {
            var path = req.RequestUri?.AbsolutePath ?? "";
            if (path.Contains("everyday/recommend", StringComparison.Ordinal))
            {
                everydayCalls++;
                return Json(new
                {
                    code = 0,
                    data = new
                    {
                        歌单 = new[]
                        {
                            new { hash = "hash1", 歌曲名称 = "歌一", 歌手名称 = "歌手A", 歌曲ID = "101" },
                            new { hash = "hash2", 歌曲名称 = "歌二", 歌手名称 = "歌手B", 歌曲ID = "102" }
                        }
                    }
                });
            }

            if (path.Contains("song/url", StringComparison.Ordinal))
            {
                urlCalls++;
                var body = req.Content?.ReadAsStringAsync().Result ?? "";
                var hash = body.Contains("hash2", StringComparison.Ordinal) ? "hash2" : "hash1";
                return Json(new
                {
                    code = 0,
                    data = new { url = $"http://fs/{hash}.mp3", hash, is_preview = false, time_length = 200 }
                });
            }

            if (path.Contains("login/status", StringComparison.Ordinal))
            {
                return Json(new { code = 0, data = new { logged_in = true, nickname = "test" } });
            }

            return Json(new { code = 1, msg = "unknown" });
        });

        var svc = CreateService(handler);
        var first = await svc.PickRandomTrackAsync((_, _) => false, CancellationToken.None);
        var second = await svc.PickRandomTrackAsync((_, _) => false, CancellationToken.None);
        var third = await svc.PickRandomTrackAsync((_, _) => false, CancellationToken.None);

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.NotNull(third);
        Assert.Equal("歌一", first!.SongName);
        Assert.Equal("歌二", second!.SongName);
        Assert.Equal("歌一", third!.SongName);
        Assert.Equal(2, everydayCalls);
        Assert.True(urlCalls >= 3);
    }

    private static KugouService CreateService(
        HttpMessageHandler handler,
        bool requireFullPlayback = true,
        string? dataDirectory = null)
    {
        var client = new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:17888/") };
        var log = new LogService(Path.Combine(Path.GetTempPath(), "la-kugou-test-" + Guid.NewGuid().ToString("N")));
        var settings = new KugouSettings { RequireFullPlayback = requireFullPlayback };
        return new KugouService(settings, log, client, dataDirectory);
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
