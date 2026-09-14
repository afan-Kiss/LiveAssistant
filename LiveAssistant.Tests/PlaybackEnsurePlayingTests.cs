using LiveAssistant.Config;
using LiveAssistant.Database;
using LiveAssistant.Models;
using LiveAssistant.Services;
using Xunit;

namespace LiveAssistant.Tests;

public sealed class PlaybackEnsurePlayingTests : IDisposable
{
    private readonly string _dir;

    public PlaybackEnsurePlayingTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "la-ensure-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    [Fact]
    public async Task EnsurePlaying_DoesNotAdvanceWhileNowPlayingExists()
    {
        var db = new AppDatabase(_dir);
        var queue = new QueueService(db);
        var config = new ConfigManager();
        config.Load();
        config.Settings.Playback.Mode = PlaybackMode.RequestOnly;
        var log = new LogService(_dir);

        var played = new List<string>();
        using var commands = new PlaybackCommandQueue(
            config,
            queue,
            new KugouService(config.Settings.Kugou, log),
            new RandomPlaylistService(config, db),
            new PlaybackService(log),
            new ReplyService(config),
            new SystemMessageService(20),
            log,
            playAsync: (track, _, _) =>
            {
                played.Add(track.SongName);
                return Task.FromResult(true);
            });

        queue.Add(new QueueItem { UserId = "1", Nickname = "A", SongName = "正在播", Hash = "h1" });
        queue.DequeueNext();
        queue.Add(new QueueItem { UserId = "2", Nickname = "B", SongName = "等待中", Hash = "h2" });

        await commands.EnqueueEnsurePlayingAsync();
        await Task.Delay(200);

        Assert.Empty(played);
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
}
