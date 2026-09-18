using LiveAssistant.Config;
using LiveAssistant.Database;
using LiveAssistant.Models;
using LiveAssistant.Services;
using Xunit;

namespace LiveAssistant.Tests;

public sealed class PointsQueryTests : IDisposable
{
    private readonly string _dir;

    public PointsQueryTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "la-points-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    [Fact]
    public void TryHandle_RepliesWithUserPoints()
    {
        var db = new AppDatabase(_dir);
        var ledger = new PointsLedgerRepository(db);
        var users = new UserRepository(db, ledger);
        users.EnsureUser("u1", "测试观众");
        users.AddPoints("u1", "测试观众", 88);
        users.SetLevel("u1", 3);

        var config = new ConfigManager();
        config.Load();
        config.ReplyTemplates["pointsQuery"] = "@{name} 积分 {score}，等级 Lv{level} 明细:{details}";

        var reply = new ReplyService(config);
        var sent = new List<string>();
        var queue = new ReplyQueue(
            new DouyinService(config.Settings.Douyin, new LogService(_dir)),
            new LogService(_dir),
            config.Settings.Reply,
            sendMention: (_, _, content, _, _) =>
            {
                sent.Add(content);
                return Task.FromResult(new MentionSendResult { Ok = true });
            });

        var svc = new PointsQueryService(users, ledger);
        var handled = svc.TryHandle(new DanmakuItem
        {
            UserId = "u1",
            Nickname = "测试观众",
            Content = "查积分"
        }, "room1", reply, queue);

        Assert.True(handled);
        for (var i = 0; i < 20 && sent.Count == 0; i++)
        {
            Thread.Sleep(50);
        }

        Assert.Single(sent);
        Assert.Contains("88", sent[0]);
        Assert.Contains("Lv3", sent[0]);
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
