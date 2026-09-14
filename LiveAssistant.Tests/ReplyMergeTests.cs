using Xunit;
using LiveAssistant.Services;

namespace LiveAssistant.Tests;

public sealed class ReplyMergeTests
{
    [Fact]
    public void MergedReply_ContainsAllSongRequests()
    {
        var entries = new List<SongRequestReplyEntry>
        {
            new() { Nickname = "A", SongName = "歌1" },
            new() { Nickname = "B", SongName = "歌2" },
            new() { Nickname = "C", SongName = "歌3" }
        };

        var text = SongRequestReplyFormatter.FormatMerged(entries, 3);

        Assert.Contains("🎵 点歌:", text);
        Assert.Contains("A-歌1", text);
        Assert.Contains("B-歌2", text);
        Assert.Contains("C-歌3", text);
        Assert.Contains("当前排队3首", text);
    }

    [Fact]
    public void SingleReply_ShowsAheadCount()
    {
        var entries = new List<SongRequestReplyEntry>
        {
            new() { Nickname = "C", SongName = "歌曲" }
        };

        var text = SongRequestReplyFormatter.FormatMerged(entries, 3);
        Assert.Contains("@C 点歌成功《歌曲》", text);
        Assert.Contains("前面还有2首", text);
    }
}
