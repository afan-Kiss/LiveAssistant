using LiveAssistant.Models;
using LiveAssistant.Utils;
using Xunit;

namespace LiveAssistant.Tests;

public sealed class KugouSessionCookieTests
{
    [Fact]
    public void BuildHeader_IncludesLoginFieldsAndExtra()
    {
        var header = KugouSessionCookie.BuildHeader(new KugouSidecarSession
        {
            Dfid = "df123",
            Token = "tok",
            UserId = "uid",
            VipType = "0",
            Extra = new Dictionary<string, string> { ["t1"] = "abc" }
        });

        Assert.Contains("dfid=df123", header);
        Assert.Contains("token=tok", header);
        Assert.Contains("userid=uid", header);
        Assert.Contains("vip_type=0", header);
        Assert.Contains("t1=abc", header);
    }

    [Fact]
    public void BuildHeader_ReturnsEmpty_WhenMissingTokenOrUserId()
    {
        Assert.Equal("", KugouSessionCookie.BuildHeader(new KugouSidecarSession { UserId = "1" }));
        Assert.Equal("", KugouSessionCookie.BuildHeader(new KugouSidecarSession { Token = "1" }));
    }
}
