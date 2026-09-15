using LiveAssistant.Utils;
using Xunit;

namespace LiveAssistant.Tests;

public sealed class SongEditionHelperTests
{
    [Theory]
    [InlineData("泡沫", "G.E.M.邓紫棋", "泡沫", "G.E.M.邓紫棋、DJ Wave", false)]
    [InlineData("死了都要爱", "信乐团", "死了都要爱 (DJ版)", "信乐团", false)]
    [InlineData("死了都要爱", "信乐团", "死了都要爱DJ版", "高高在这呢", false)]
    [InlineData("死了都要爱", "信乐团", "死了都要爱", "信乐团", true)]
    [InlineData("泡沫 DJ版", "DJ Wave", "泡沫 DJ版", "DJ Wave", true)]
    [InlineData("晴天", "周杰伦", "晴天", "周杰伦", true)]
    public void IsCompatibleAlternate_RejectsUnexpectedDjRemix(
        string requestedName,
        string requestedArtist,
        string candidateName,
        string candidateArtist,
        bool expected)
    {
        var ok = SongEditionHelper.IsCompatibleAlternate(
            requestedName, requestedArtist, candidateName, candidateArtist);
        Assert.Equal(expected, ok);
    }

    [Fact]
    public void LooksLikeDerivativeEdition_DetectsDjCollaborator()
    {
        Assert.True(SongEditionHelper.LooksLikeDerivativeEdition("泡沫", "G.E.M.邓紫棋、DJ Wave"));
        Assert.False(SongEditionHelper.LooksLikeDerivativeEdition("泡沫", "G.E.M.邓紫棋"));
    }
}
