using LiveAssistant.Config;
using LiveAssistant.Services;
using Xunit;

namespace LiveAssistant.Tests;

public sealed class ReplyMergeTests
{
    [Fact]
    public void FormatForUser_SingleSong_ShowsAheadCount()
    {
        var entries = new List<SongRequestReplyEntry>
        {
            new() { UserId = "u1", Nickname = "C", SongName = "歌曲", AheadCount = 2 }
        };

        var text = SongRequestReplyFormatter.FormatForUser(entries);
        Assert.Contains("点歌成功《歌曲》", text);
        Assert.Contains("前面还有2首", text);
        Assert.DoesNotContain("@", text);
        Assert.DoesNotContain("\n", text);
    }

    [Fact]
    public void FormatForUser_MultipleSongsSameUser_MergesSongs()
    {
        var entries = new List<SongRequestReplyEntry>
        {
            new() { UserId = "u1", Nickname = "A", SongName = "歌1", AheadCount = 1 },
            new() { UserId = "u1", Nickname = "A", SongName = "歌2", AheadCount = 2 }
        };

        var text = SongRequestReplyFormatter.FormatForUser(entries);
        Assert.Contains("《歌1》", text);
        Assert.Contains("《歌2》", text);
        Assert.Contains("前面还有2首", text);
    }

    [Fact]
    public async Task FlushSongRequestBatch_SendsSeparateMentionPerUser()
    {
        var sends = new List<(string UserId, string Content)>();
        var settings = new ReplySettings { MaxPerSecond = 20, MaxRetries = 1, RetryDelayMs = 10, SongRequestBatchWindowMs = 50 };
        var log = new LogService(Path.Combine(Path.GetTempPath(), "la-reply-" + Guid.NewGuid().ToString("N")));
        var douyin = new DouyinService(new DouyinSettings { BaseUrl = "http://127.0.0.1:9" }, log);

        using var queue = new ReplyQueue(
            douyin,
            log,
            settings,
            sendMention: (webRid, userId, content, _) =>
            {
                sends.Add((userId, content));
                return Task.FromResult(true);
            });

        queue.EnqueueSongRequestReply("rid", "u1", "A", "歌1", 1);
        queue.EnqueueSongRequestReply("rid", "u2", "B", "歌2", 0);
        queue.FlushSongRequestBatch();

        for (var i = 0; i < 20 && sends.Count < 2; i++)
        {
            await Task.Delay(50);
        }

        Assert.Equal(2, sends.Count);
        Assert.Contains(sends, s => s.UserId == "u1" && s.Content.Contains("歌1"));
        Assert.Contains(sends, s => s.UserId == "u2" && s.Content.Contains("歌2"));
    }
}
