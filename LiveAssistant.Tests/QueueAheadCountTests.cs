using Xunit;
using LiveAssistant.Database;
using LiveAssistant.Models;
using LiveAssistant.Services;

namespace LiveAssistant.Tests;

public sealed class QueueAheadCountTests : IDisposable
{
    private readonly string _dataDir;
    private readonly QueueService _queue;

    public QueueAheadCountTests()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), "la_queue_" + Guid.NewGuid().ToString("N"));
        _queue = new QueueService(new AppDatabase(_dataDir));
    }

    [Fact]
    public void ThirdItem_HasTwoSongsAhead()
    {
        _queue.Add(new QueueItem { UserId = "1", Nickname = "A", SongName = "S1", Artist = "" });
        var a = _queue.DequeueNext()!;

        var b = _queue.Add(new QueueItem { UserId = "2", Nickname = "B", SongName = "S2", Artist = "" });
        var c = _queue.AddWithPriority(new QueueItem { UserId = "3", Nickname = "C", SongName = "S3", Artist = "" }, 0);

        Assert.Equal(0, _queue.GetAheadCount(a.Id));
        Assert.Equal(1, _queue.GetAheadCount(b.Id));
        Assert.Equal(2, _queue.GetAheadCount(c.Id));
    }

    [Fact]
    public void RandomNowPlaying_DoesNotCountAsAhead()
    {
        _queue.BeginPlaying(new QueueItem
        {
            Nickname = "随机",
            SongName = "随机曲",
            IsRandom = true
        });
        var request = _queue.Add(new QueueItem { UserId = "2", Nickname = "B", SongName = "S2", Artist = "" });

        Assert.Equal(0, _queue.GetAheadCount(request.Id));
    }

    [Fact]
    public void VipInsert_RecalculatesAheadCount()
    {
        _queue.Add(new QueueItem { UserId = "1", Nickname = "A", SongName = "S1", Artist = "" });
        _queue.DequeueNext();
        var b = _queue.Add(new QueueItem { UserId = "2", Nickname = "B", SongName = "S2", Artist = "" });
        var vip = _queue.AddWithPriority(new QueueItem { UserId = "3", Nickname = "VIP", SongName = "SV", Artist = "" }, 100);

        Assert.Equal(1, _queue.GetAheadCount(vip.Id));
        Assert.Equal(2, _queue.GetAheadCount(b.Id));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dataDir, true);
        }
        catch
        {
            // ignore
        }
    }
}
