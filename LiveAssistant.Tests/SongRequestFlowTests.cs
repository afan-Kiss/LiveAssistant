using LiveAssistant.Models;
using LiveAssistant.Utils;
using Xunit;

namespace LiveAssistant.Tests;

public sealed class SongRequestFlowTests
{
    [Fact]
    public void ArtistNameMatcher_MatchesExactAndPartial()
    {
        var candidates = new List<SongSearchCandidate>
        {
            new() { Artist = "G.E.M.邓紫棋", SongName = "泡沫" },
            new() { Artist = "田馥甄", SongName = "泡沫" }
        };

        Assert.Equal("G.E.M.邓紫棋", ArtistNameMatcher.Match("G.E.M.邓紫棋", candidates)!.Artist);
        Assert.Equal("田馥甄", ArtistNameMatcher.Match("田馥甄", candidates)!.Artist);
        Assert.Equal("田馥甄", ArtistNameMatcher.Match("馥甄", candidates)!.Artist);
        Assert.Null(ArtistNameMatcher.Match("周杰伦", candidates));
    }

    [Fact]
    public void ArtistNameMatcher_RejectsAmbiguousPartialMatch()
    {
        var candidates = new List<SongSearchCandidate>
        {
            new() { Artist = "王力宏", SongName = "花田错" },
            new() { Artist = "王力宏/卢巧音", SongName = "花田错" }
        };

        Assert.Null(ArtistNameMatcher.Match("王力宏", candidates));
        Assert.Equal("王力宏", ArtistNameMatcher.Match("王力宏", new List<SongSearchCandidate>
        {
            new() { Artist = "王力宏", SongName = "花田错" }
        })!.Artist);
    }

    [Fact]
    public void ArtistNameMatcher_RejectsSingleCharPartial()
    {
        var candidates = new List<SongSearchCandidate>
        {
            new() { Artist = "G.E.M.邓紫棋", SongName = "泡沫" },
            new() { Artist = "田馥甄", SongName = "泡沫" }
        };

        Assert.Null(ArtistNameMatcher.Match("G", candidates));
    }

    [Fact]
    public void ConfirmParser_AcceptsCommonAnswers()
    {
        Assert.True(SongRequestConfirmParser.IsConfirm("确定"));
        Assert.True(SongRequestConfirmParser.IsConfirm("确认"));
        Assert.True(SongRequestConfirmParser.IsConfirm("好的"));
        Assert.True(SongRequestConfirmParser.IsCancel("取消"));
        Assert.False(SongRequestConfirmParser.IsConfirm("点歌 泡沫"));
        Assert.False(SongRequestConfirmParser.IsConfirm("点"));
        Assert.False(SongRequestConfirmParser.IsConfirm("好"));
        Assert.False(SongRequestConfirmParser.IsConfirm("要"));
        Assert.False(SongRequestConfirmParser.IsConfirm("是"));
    }

    [Fact]
    public void SessionStore_ExpiresEntries()
    {
        var store = new LiveAssistant.Services.SongRequestSessionStore(TimeSpan.FromMilliseconds(1));
        store.Set(new SongRequestSession
        {
            WebRid = "room1",
            UserId = "u1",
            Nickname = "A",
            Keyword = "泡沫",
            Step = SongRequestSessionStep.Confirm,
            Selected = new SongSearchCandidate { SongName = "泡沫", Artist = "邓紫棋" }
        });
        Thread.Sleep(20);
        Assert.Null(store.Get("room1", "u1"));
    }
}
