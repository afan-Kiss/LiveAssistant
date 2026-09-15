using System.Text.Json;
using LiveAssistant.Models;
using LiveAssistant.Services;
using LiveAssistant.Utils;
using Xunit;

namespace LiveAssistant.Tests;

public sealed class PipelineParseTests
{
    [Fact]
    public void SkipSongParser_AcceptsCommonCommands()
    {
        Assert.True(SkipSongParser.TryParse("切歌"));
        Assert.True(SkipSongParser.TryParse("下一首"));
        Assert.False(SkipSongParser.TryParse("切歌 泡沫"));
    }

    [Fact]
    public void PointsQueryParser_AcceptsCommonFormats()
    {
        Assert.True(PointsQueryParser.TryParse("查积分"));
        Assert.True(PointsQueryParser.TryParse("我的积分"));
        Assert.True(PointsQueryParser.TryParse("积分查询"));
        Assert.False(PointsQueryParser.TryParse("查积分 张三"));
        Assert.False(PointsQueryParser.TryParse("点歌 泡沫"));
    }

    [Fact]
    public void SongNameParser_AcceptsCommonFormats()
    {
        Assert.True(SongNameParser.TryParse("点歌 泡沫", out var a));
        Assert.Equal("泡沫", a);
        Assert.True(SongNameParser.TryParse("点歌泡沫", out var noSpace));
        Assert.Equal("泡沫", noSpace);
        Assert.True(SongNameParser.TryParse("点歌双截棍", out var noSpace2));
        Assert.Equal("双截棍", noSpace2);
        Assert.True(SongNameParser.TryParse("点歌:晴天", out var b));
        Assert.Equal("晴天", b);
        Assert.True(SongNameParser.TryParse("点歌：后来", out var c));
        Assert.Equal("后来", c);
        Assert.False(SongNameParser.TryParse("点歌", out _));
        Assert.False(SongNameParser.TryParse("我想点歌 泡沫", out _));
        Assert.False(SongNameParser.TryParse("点歌成功《泡沫》", out _));
        Assert.True(SongNameParser.IsBotReply("点歌成功《泡沫》"));
        Assert.True(SongNameParser.IsBotReply("@1155 是否确定点歌《社会摇》- 萧全？回复 确定 开始点歌"));
        Assert.True(SongNameParser.IsBotReply("@一只小青蛙 你当前有 99999 积分，等级 Lv4"));
    }

    [Fact]
    public void LookupUser_ParsesSidecarArray()
    {
        using var doc = JsonDocument.Parse("""[{"user_id":"u1","nickname":"粉粉"}]""");
        var user = DouyinService.ParseLookupUser(doc.RootElement);
        Assert.NotNull(user);
        Assert.Equal("u1", user!.UserId);
        Assert.Equal("粉粉", user.Nickname);
    }

    [Fact]
    public void KugouSearch_BindsChineseSongList()
    {
        const string json = """{"code":0,"data":{"歌单":[{"hash":"abc","歌曲名称":"泡沫","歌手名称":"G.E.M.","id":"1"}]}}""";
        var result = JsonSerializer.Deserialize<KugouResponse<KugouSearchData>>(json, HttpJson.Options);
        Assert.NotNull(result);
        Assert.Equal(0, result!.Code);
        Assert.Equal("泡沫", result.Data!.Songs![0].SongName);
        Assert.Equal("abc", result.Data.Songs[0].Hash);
    }
}
