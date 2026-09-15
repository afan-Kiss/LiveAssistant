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
    public async Task GetPlayUrl_WhenSessionDroppedThenLoginRestored_RetriesSuccessfully()
    {
        var urlCalls = 0;
        var loginCalls = 0;
        var handler = new StubHandler(req =>
        {
            var path = req.RequestUri?.AbsolutePath ?? "";
            if (path.Contains("login/status", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref loginCalls);
                return Json(new { code = 0, data = new { logged_in = true, nickname = "test", vip_label = "概念版VIP" } });
            }

            if (path.Contains("song/url", StringComparison.Ordinal))
            {
                var n = Interlocked.Increment(ref urlCalls);
                if (n == 1)
                {
                    return Json(new { code = 40101, msg = "登录已掉线，请重新扫码登录" });
                }

                return Json(new
                {
                    code = 0,
                    data = new { url = "http://fs/yp/f_ok.mp3", is_preview = false }
                });
            }

            return Json(new { code = 1, msg = "unknown" });
        });

        var svc = CreateService(handler);
        await svc.RefreshLoginStatusAsync();
        var url = await svc.GetPlayUrlAsync("abc123", "测试", CancellationToken.None);

        Assert.NotNull(url);
        Assert.Contains("/yp/f_", url!.Url);
        Assert.True(urlCalls >= 2);
        Assert.True(loginCalls >= 2);
        Assert.True(svc.LoginSnapshot.LoggedIn);
    }

    [Fact]
    public async Task RefreshLoginStatus_WhenInvalidatedMidFlight_RefetchesInsteadOfEmptySnapshot()
    {
        var loginCalls = 0;
        var gate = new ManualResetEventSlim(false);
        var entered = new ManualResetEventSlim(false);
        var handler = new StubHandler(req =>
        {
            var path = req.RequestUri?.AbsolutePath ?? "";
            if (path.Contains("login/status", StringComparison.Ordinal))
            {
                var n = Interlocked.Increment(ref loginCalls);
                if (n == 1)
                {
                    entered.Set();
                    gate.Wait(TimeSpan.FromSeconds(5));
                }

                return Json(new { code = 0, data = new { logged_in = true, nickname = "recovered", userid = "u1" } });
            }

            return Json(new { code = 1, msg = "unknown" });
        });

        var svc = CreateService(handler);
        var refreshTask = svc.RefreshLoginStatusAsync(forceRefresh: true);
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));

        // 模拟取链 session_expired：bump generation + 清空 snapshot
        var invalidate = typeof(KugouService).GetMethod(
            "InvalidateLoginCache",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(invalidate);
        invalidate!.Invoke(svc, null);
        gate.Set();

        var snap = await refreshTask;
        Assert.True(snap.LoggedIn);
        Assert.Equal("recovered", snap.Nickname);
        Assert.True(loginCalls >= 2);
    }

    [Fact]
    public async Task TryAutoClaimVip_OnFailure_BacksOffInsteadOfSpamming()
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
                Interlocked.Increment(ref claimCalls);
                return Json(new { code = 1, msg = "领取试用会员失败: status=0 error_code=51002 http=502" });
            }

            return Json(new { code = 1, msg = "unknown" });
        });

        var svc = CreateService(handler);
        await svc.RefreshLoginStatusAsync();
        Assert.Null(await svc.TryAutoClaimVipAsync());
        Assert.Null(await svc.TryAutoClaimVipAsync());
        Assert.Null(await svc.TryAutoClaimVipAsync());
        Assert.Equal(1, claimCalls);
    }

    [Fact]
    public async Task GetPlayUrl_WhenVipMaskedAsStaleSession_KeepsLoginAndUsesAlternate()
    {
        var urlCalls = 0;
        var handler = new StubHandler(req =>
        {
            var path = req.RequestUri?.AbsolutePath ?? "";
            if (path.Contains("login/status", StringComparison.Ordinal))
            {
                return Json(new { code = 0, data = new { logged_in = true, nickname = "test", vip_label = "超级VIP" } });
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
                            new { hash = "vip-hash", 歌曲名称 = "死了都要爱", 歌手名称 = "信乐团", 歌曲ID = "1" },
                            new { hash = "free-hash", 歌曲名称 = "死了都要爱", 歌手名称 = "信乐团", 歌曲ID = "2" }
                        }
                    }
                });
            }

            if (path.Contains("song/url", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref urlCalls);
                var body = req.Content?.ReadAsStringAsync().Result ?? "";
                if (body.Contains("vip-hash", StringComparison.Ordinal))
                {
                    return Json(new { code = 502, msg = "当前登录态无效，旧版扫码残留，请先退出后再扫码登录" });
                }

                if (body.Contains("free-hash", StringComparison.Ordinal))
                {
                    return Json(new
                    {
                        code = 0,
                        data = new { url = "http://fs/yp/f_free.mp3", is_preview = false, song_name = "死了都要爱", author_name = "信乐团" }
                    });
                }

                return Json(new { code = 1, msg = "unknown hash" });
            }

            return Json(new { code = 1, msg = "unknown" });
        });

        var svc = CreateService(handler);
        await svc.RefreshLoginStatusAsync();
        var url = await svc.GetPlayUrlAsync(
            new KugouSongContext { Hash = "vip-hash", Keyword = "死了都要爱", Artist = "信乐团" },
            tryAlternates: true,
            CancellationToken.None);

        Assert.NotNull(url);
        Assert.Contains("/yp/f_free", url!.Url);
        Assert.True(svc.LoginSnapshot.LoggedIn);
        Assert.True(KugouService.IsPrivilegeMaskedAsStaleSession(
            502, "当前登录态无效，旧版扫码残留，请先退出后再扫码登录"));
        Assert.False(KugouService.IsPrivilegeMaskedAsStaleSession(40101, "登录已掉线，请重新扫码登录"));
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
    public async Task PickFallbackRandomTrack_RotatesProbeHashesWhenRecentlyPlayed()
    {
        var urlCalls = 0;
        var handler = new StubHandler(req =>
        {
            var path = req.RequestUri?.AbsolutePath ?? "";
            if (path.Contains("song/url", StringComparison.Ordinal))
            {
                urlCalls++;
                var body = req.Content?.ReadAsStringAsync().Result ?? "";
                var hash = body.Contains("f15843ca55658254f674508ec64b5b63", StringComparison.Ordinal)
                    ? "f15843ca55658254f674508ec64b5b63"
                    : "69f342d52afb4ea64301a22d119d3ac0";
                return Json(new
                {
                    code = 0,
                    data = new
                    {
                        url = $"http://fs/{hash}.mp3",
                        hash,
                        is_preview = false,
                        song_name = hash == "69f342d52afb4ea64301a22d119d3ac0" ? "备用A" : "备用B"
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
        var recent = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool WasRecent(string? _, string? hash) => !string.IsNullOrWhiteSpace(hash) && recent.Contains(hash);

        var first = await svc.PickFallbackRandomTrackAsync(WasRecent, CancellationToken.None);
        Assert.NotNull(first);
        recent.Add(first!.Hash);

        var second = await svc.PickFallbackRandomTrackAsync(WasRecent, CancellationToken.None);
        Assert.NotNull(second);
        Assert.NotEqual(first.Hash, second!.Hash);
        Assert.True(urlCalls >= 2);
    }

    [Fact]
    public async Task PickRandomTrack_UsesSearchFallbackWhenEverydayUnavailable()
    {
        var searchCalls = 0;
        var handler = new StubHandler(req =>
        {
            var path = req.RequestUri?.AbsolutePath ?? "";
            if (path.Contains("everyday/recommend", StringComparison.Ordinal))
            {
                return Json(new { code = 502, msg = "kgapijs unavailable" });
            }

            if (path.Contains("search", StringComparison.Ordinal))
            {
                searchCalls++;
                return Json(new
                {
                    code = 0,
                    data = new
                    {
                        歌单 = new[]
                        {
                            new { hash = "search1", 歌曲名称 = "搜索歌一", 歌手名称 = "歌手A", 歌曲ID = "201" },
                            new { hash = "search2", 歌曲名称 = "搜索歌二", 歌手名称 = "歌手B", 歌曲ID = "202" }
                        }
                    }
                });
            }

            if (path.Contains("song/url", StringComparison.Ordinal))
            {
                var body = req.Content?.ReadAsStringAsync().Result ?? "";
                var hash = body.Contains("search2", StringComparison.Ordinal) ? "search2" : "search1";
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

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.True(searchCalls >= 1);
        var picked = new[] { first!.SongName, second!.SongName };
        Assert.Contains("搜索歌一", picked);
        Assert.Contains("搜索歌二", picked);
    }

    [Fact]
    public async Task PickRandomTrack_ShufflesDailyRecommendAndRefreshesWhenExhausted()
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
        var picked = new[] { first!.SongName, second!.SongName, third!.SongName };
        Assert.Contains("歌一", picked);
        Assert.Contains("歌二", picked);
        Assert.Equal(2, everydayCalls);
        Assert.True(urlCalls >= 2);
    }

    [Fact]
    public async Task ResolveCandidate_FallsBackToSameArtistAlternateHash()
    {
        var urlHashes = new List<string>();
        var handler = new StubHandler(req =>
        {
            var path = req.RequestUri?.AbsolutePath ?? "";
            if (path.Contains("login/status", StringComparison.Ordinal))
            {
                return Json(new { code = 0, data = new { logged_in = true, nickname = "test" } });
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
                            new { hash = "gem_hash", 歌曲名称 = "泡沫", 歌手名称 = "G.E.M.邓紫棋", 歌曲ID = "1001" },
                            new { hash = "gem_alt", 歌曲名称 = "泡沫", 歌手名称 = "G.E.M.邓紫棋", 歌曲ID = "1002" }
                        }
                    }
                });
            }

            if (path.Contains("song/url", StringComparison.Ordinal))
            {
                var body = req.Content?.ReadAsStringAsync().Result ?? "";
                if (body.Contains("gem_alt", StringComparison.Ordinal))
                {
                    urlHashes.Add("gem_alt");
                    return Json(new
                    {
                        code = 0,
                        data = new { url = "http://fs/gem_alt.mp3", is_preview = false, 歌曲名称 = "泡沫", 歌手名称 = "G.E.M.邓紫棋" }
                    });
                }

                urlHashes.Add("gem_hash");
                return Json(new { code = 1, msg = "session expired", data = (object?)null });
            }

            return Json(new { code = 1, msg = "unknown" });
        });

        var svc = CreateService(handler);
        var track = await svc.ResolveCandidateAsync(new Models.SongSearchCandidate
        {
            Hash = "gem_hash",
            SongName = "泡沫",
            Artist = "G.E.M.邓紫棋",
            SongId = "1001",
            AlbumAudioId = 1001
        });

        Assert.NotNull(track);
        Assert.Equal("http://fs/gem_alt.mp3", track!.PlayUrl);
        Assert.Contains("gem_alt", urlHashes);
    }

    [Fact]
    public async Task ResolveFreshTrack_Request_DoesNotSwitchToAlternateVersion()
    {
        var urlHashes = new List<string>();
        var handler = new StubHandler(req =>
        {
            var path = req.RequestUri?.AbsolutePath ?? "";
            if (path.Contains("login/status", StringComparison.Ordinal))
            {
                return Json(new { code = 0, data = new { logged_in = true, nickname = "test" } });
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
                            new { hash = "gem_hash", 歌曲名称 = "泡沫", 歌手名称 = "G.E.M.邓紫棋", 歌曲ID = "1001" },
                            new { hash = "cover_hash", 歌曲名称 = "泡沫", 歌手名称 = "翻唱歌手", 歌曲ID = "2002" }
                        }
                    }
                });
            }

            if (path.Contains("song/url", StringComparison.Ordinal))
            {
                var body = req.Content?.ReadAsStringAsync().Result ?? "";
                if (body.Contains("cover_hash", StringComparison.Ordinal))
                {
                    urlHashes.Add("cover_hash");
                    return Json(new
                    {
                        code = 0,
                        data = new { url = "http://fs/cover.mp3", is_preview = false, 歌曲名称 = "泡沫", 歌手名称 = "翻唱歌手" }
                    });
                }

                urlHashes.Add("gem_hash");
                return Json(new { code = 1, msg = "preview_only", data = (object?)null });
            }

            return Json(new { code = 1, msg = "unknown" });
        });

        var svc = CreateService(handler);
        var track = await svc.ResolveFreshTrackAsync(
            "gem_hash",
            "泡沫",
            "G.E.M.邓紫棋",
            "1001",
            albumAudioId: 1001,
            isRandom: false);

        Assert.Null(track);
        Assert.DoesNotContain("cover_hash", urlHashes);
    }

    [Fact]
    public async Task Search_NormalizesSongIdToAlbumAudioId()
    {
        var handler = new StubHandler(req =>
        {
            if ((req.RequestUri?.AbsolutePath ?? "").Contains("search", StringComparison.Ordinal))
            {
                return Json(new
                {
                    code = 0,
                    data = new
                    {
                        歌单 = new[]
                        {
                            new { hash = "abc", 歌曲名称 = "泡沫", 歌手名称 = "G.E.M.邓紫棋", 歌曲ID = "55667788" }
                        }
                    }
                });
            }

            return Json(new { code = 1, msg = "unknown" });
        });

        var svc = CreateService(handler);
        var songs = await svc.SearchAsync("泡沫", ct: CancellationToken.None);
        Assert.Single(songs);
        Assert.Equal(55667788, songs[0].AlbumAudioId);
        Assert.Equal("55667788", songs[0].SongId);
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
