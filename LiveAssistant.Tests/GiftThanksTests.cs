using LiveAssistant.Config;
using LiveAssistant.Database;
using LiveAssistant.Models;
using LiveAssistant.Services;
using Xunit;

namespace LiveAssistant.Tests;

public sealed class GiftThanksTests : IDisposable
{
    private readonly string _dir;

    public GiftThanksTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "la-gift-thanks-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    [Fact]
    public void HandleGiftEvent_SendsThanksMention()
    {
        var db = new AppDatabase(_dir);
        var config = new ConfigManager();
        config.Load();
        config.ReplyTemplates["giftThanks"] = "感谢送出 {gift}×{count}";
        var log = new LogService(_dir);
        var sent = new List<(string userId, string content)>();
        var replyQueue = new ReplyQueue(
            new DouyinService(config.Settings.Douyin, log),
            log,
            config.Settings.Reply,
            sendMention: (_, userId, content, _) =>
            {
                sent.Add((userId, content));
                return Task.FromResult(true);
            });

        var gifts = new GiftService(
            config,
            new DouyinService(config.Settings.Douyin, log),
            new GiftRepository(db),
            new UserRepository(db),
            new UserLevelService(config, new UserRepository(db)),
            new GiftRuleRepository(db),
            log,
            new SystemMessageService(20),
            new ReplyService(config),
            replyQueue);
        gifts.Start("room1");

        var ok = gifts.HandleGiftEvent(new GiftEvent
        {
            UserId = "u1",
            Nickname = "粉丝",
            GiftName = "小心心",
            GiftId = "g1",
            Count = 2,
            Value = 2,
            DiamondCount = 1,
            EventId = "evt-1",
            Time = DateTime.Now
        });

        Assert.True(ok);
        for (var i = 0; i < 20 && sent.Count == 0; i++)
        {
            Thread.Sleep(50);
        }

        Assert.Single(sent);
        Assert.Equal("u1", sent[0].userId);
        Assert.Contains("小心心", sent[0].content);
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
