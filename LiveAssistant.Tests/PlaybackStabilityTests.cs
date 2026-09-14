using System.Net;
using System.Text;
using System.Text.Json;
using LiveAssistant.Config;
using LiveAssistant.Database;
using LiveAssistant.Models;
using LiveAssistant.Services;
using Xunit;

namespace LiveAssistant.Tests;

public sealed class PlaybackStabilityTests : IDisposable
{
    private readonly string _dir;

    public PlaybackStabilityTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "la_play_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    [Fact]
    public async Task ResolveFreshTrack_PrefersHash_IgnoresStaleUrlPath()
    {
        var handler = new FakeKugouHandler(
            urlByHash: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["HASH123"] = "https://fresh.example/play.mp3"
            },
            searchFirst: ("晴天", "HASH_OTHER", "https://search.example/other.mp3"));

        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://kugou.test/") };
        var kugou = new KugouService(new KugouSettings { BaseUrl = "http://kugou.test/" }, new LogService(_dir), http);

        var track = await kugou.ResolveFreshTrackAsync(
            hash: "HASH123",
            songName: "晴天",
            artist: "周杰伦",
            songId: "1");

        Assert.NotNull(track);
        Assert.Equal("https://fresh.example/play.mp3", track!.PlayUrl);
        Assert.Equal("HASH123", track.Hash);
        Assert.Equal(1, handler.SongUrlCalls);
        Assert.Equal(0, handler.SearchCalls);
    }

    [Fact]
    public async Task ResolveFreshTrack_HashFail_FallsBackToSearch()
    {
        var handler = new FakeKugouHandler(
            urlByHash: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            searchFirst: ("晴天", "HASH_NEW", "https://search.example/new.mp3"));

        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://kugou.test/") };
        var kugou = new KugouService(new KugouSettings { BaseUrl = "http://kugou.test/" }, new LogService(_dir), http);

        var track = await kugou.ResolveFreshTrackAsync("EXPIRED", "晴天");

        Assert.NotNull(track);
        Assert.Equal("https://search.example/new.mp3", track!.PlayUrl);
        Assert.True(handler.SongUrlCalls >= 1);
        Assert.Equal(1, handler.SearchCalls);
    }

    [Fact]
    public async Task PlayFailure_FinishesPlaying_AndAdvancesToNext()
    {
        var db = new AppDatabase(_dir);
        var queue = new QueueService(db);
        var config = new ConfigManager();
        config.Load();
        config.Settings.Playback.Mode = PlaybackMode.RequestOnly;
        config.Settings.Playback.AutoSkipOnError = false; // 即使关闭也必须切歌
        config.Settings.RandomPlaylist.Items.Clear();

        var log = new LogService(_dir);
        var kugou = new KugouService(config.Settings.Kugou, log);
        var played = new List<string>();

        var bad = queue.Add(new QueueItem
        {
            UserId = "1",
            Nickname = "A",
            SongName = "坏歌",
            Hash = "bad-hash",
            PlayUrl = "https://stale.example/old.mp3"
        });
        var good = queue.Add(new QueueItem
        {
            UserId = "2",
            Nickname = "B",
            SongName = "好歌",
            Hash = "good-hash",
            PlayUrl = "https://stale.example/also-old.mp3"
        });

        using var commands = new PlaybackCommandQueue(
            config,
            queue,
            kugou,
            new RandomPlaylistService(config, db),
            new PlaybackService(log),
            new ReplyService(config),
            new SystemMessageService(20),
            log,
            resolveFresh: (item, _) =>
            {
                if (item.SongName == "坏歌")
                {
                    return Task.FromResult<TrackInfo?>(null);
                }

                return Task.FromResult<TrackInfo?>(new TrackInfo
                {
                    SongName = item.SongName,
                    Hash = item.Hash,
                    PlayUrl = "https://fresh.example/" + item.Hash + ".mp3",
                    Requester = item.Nickname
                });
            },
            playAsync: (track, _, _) =>
            {
                played.Add(track.SongName);
                return Task.FromResult(true);
            });

        await commands.EnqueueEnsurePlayingAsync();
        Assert.True(await WaitUntil(() => played.Contains("好歌"), TimeSpan.FromSeconds(3)));

        Assert.DoesNotContain("坏歌", played);
        Assert.Contains("好歌", played);
        Assert.Null(queue.NowPlaying?.SongName == "坏歌" ? queue.NowPlaying : null);

        // 坏歌不得仍卡在 playing
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT status FROM queue_items WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", bad.Id);
        var badStatus = Convert.ToString(cmd.ExecuteScalar());
        Assert.Equal("finished", badStatus);

        cmd.Parameters.Clear();
        cmd.Parameters.AddWithValue("$id", good.Id);
        var goodStatus = Convert.ToString(cmd.ExecuteScalar());
        Assert.Equal("playing", goodStatus);

        db.Dispose();
    }

    [Fact]
    public async Task PlayUsesFreshUrl_NotQueuedStalePlayUrl()
    {
        var db = new AppDatabase(Path.Combine(_dir, "fresh"));
        Directory.CreateDirectory(Path.Combine(_dir, "fresh"));
        var queue = new QueueService(db);
        var config = new ConfigManager();
        config.Load();
        config.Settings.Playback.Mode = PlaybackMode.RequestOnly;
        config.Settings.RandomPlaylist.Items.Clear();

        var log = new LogService(_dir);
        string? playedUrl = null;

        queue.Add(new QueueItem
        {
            UserId = "1",
            Nickname = "A",
            SongName = "晴天",
            Hash = "H1",
            PlayUrl = "https://stale.example/expired.mp3"
        });

        using var commands = new PlaybackCommandQueue(
            config,
            queue,
            new KugouService(config.Settings.Kugou, log),
            new RandomPlaylistService(config, db),
            new PlaybackService(log),
            new ReplyService(config),
            new SystemMessageService(20),
            log,
            resolveFresh: (item, _) =>
            {
                Assert.Equal("https://stale.example/expired.mp3", item.PlayUrl);
                return Task.FromResult<TrackInfo?>(new TrackInfo
                {
                    SongName = item.SongName,
                    Hash = item.Hash,
                    PlayUrl = "https://fresh.example/H1.mp3"
                });
            },
            playAsync: (track, _, _) =>
            {
                playedUrl = track.PlayUrl;
                return Task.FromResult(true);
            });

        await commands.EnqueueEnsurePlayingAsync();
        Assert.True(await WaitUntil(() => playedUrl != null, TimeSpan.FromSeconds(3)));
        Assert.Equal("https://fresh.example/H1.mp3", playedUrl);
        db.Dispose();
    }

    [Fact]
    public void QueueRecoverPlaying_ResetsToWaiting()
    {
        var data = Path.Combine(_dir, "recover");
        Directory.CreateDirectory(data);
        var db = new AppDatabase(data);
        var queue = new QueueService(db);
        var item = queue.Add(new QueueItem
        {
            UserId = "u",
            Nickname = "n",
            SongName = "歌",
            Hash = "h1"
        });
        queue.DequeueNext();
        Assert.Equal(QueueItemStatus.Playing, queue.NowPlaying?.Status);

        db.Dispose();

        // 模拟重启
        var db2 = new AppDatabase(data);
        var recovered = db2.RecoverPlayingQueueItems();
        Assert.True(recovered >= 1);

        var queue2 = new QueueService(db2);
        Assert.Null(queue2.NowPlaying);
        Assert.Contains(queue2.Waiting, x => x.Id == item.Id && x.Hash == "h1" && x.SongName == "歌");
        db2.Dispose();
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* ignore */ }
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

    private sealed class FakeKugouHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, string> _urlByHash;
        private readonly (string Name, string Hash, string Url)? _searchFirst;

        public int SongUrlCalls { get; private set; }
        public int SearchCalls { get; private set; }

        public FakeKugouHandler(
            Dictionary<string, string> urlByHash,
            (string Name, string Hash, string Url)? searchFirst)
        {
            _urlByHash = urlByHash;
            _searchFirst = searchFirst;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? "";
            var body = request.Content == null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);

            if (path.Contains("song/url", StringComparison.OrdinalIgnoreCase))
            {
                SongUrlCalls++;
                using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
                var hash = doc.RootElement.TryGetProperty("hash", out var h) ? h.GetString() ?? "" : "";
                if (_urlByHash.TryGetValue(hash, out var url))
                {
                    return Json(new
                    {
                        code = 0,
                        data = new { url, 歌曲名称 = "晴天", 歌手名称 = "周杰伦", id = "1" }
                    });
                }

                return Json(new { code = 1, msg = "expired", data = (object?)null });
            }

            if (path.Contains("search", StringComparison.OrdinalIgnoreCase))
            {
                SearchCalls++;
                if (_searchFirst == null)
                {
                    return Json(new { code = 0, data = new { songs = Array.Empty<object>() } });
                }

                var s = _searchFirst.Value;
                // ResolveTrackAsync will call song/url with this hash next
                _urlByHash[s.Hash] = s.Url;
                return Json(new
                {
                    code = 0,
                    data = new
                    {
                        歌单 = new[]
                        {
                            new Dictionary<string, object?>
                            {
                                ["歌曲名称"] = s.Name,
                                ["hash"] = s.Hash,
                                ["歌手名称"] = "周杰伦",
                                ["歌曲长度"] = 100
                            }
                        }
                    }
                });
            }

            return Json(new { code = 1, msg = "unknown" });
        }

        private static HttpResponseMessage Json(object obj)
        {
            var json = JsonSerializer.Serialize(obj);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
        }
    }
}
