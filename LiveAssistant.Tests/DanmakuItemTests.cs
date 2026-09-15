using System.Globalization;
using LiveAssistant.Models;
using Xunit;

namespace LiveAssistant.Tests;

public sealed class DanmakuItemTests
{
    [Fact]
    public void ParseTimestamp_ParsesSidecarFormat()
    {
        var ts = DanmakuItem.ParseTimestamp("2026-9-15 10:34:20");
        Assert.Equal(new DateTime(2026, 9, 15, 10, 34, 20), ts);
    }

    [Fact]
    public void DisplayLine_IncludesTimestampPrefix()
    {
        var item = new DanmakuItem
        {
            Nickname = "观众A",
            Content = "你好",
            Timestamp = new DateTime(2026, 9, 15, 10, 34, 20)
        };

        Assert.Equal("[10:34:20] 😊 观众A：你好", item.DisplayLine);
    }

    [Fact]
    public void ParseTimestamp_ParsesUnixMilliseconds()
    {
        var expected = new DateTime(2026, 9, 15, 10, 34, 20);
        var unixMs = new DateTimeOffset(expected).ToUnixTimeMilliseconds();
        var ts = DanmakuItem.ParseTimestamp(unixMs.ToString(CultureInfo.InvariantCulture));
        Assert.Equal(expected, ts);
    }
}
