using LiveAssistant.Config;
using LiveAssistant.Database;
using LiveAssistant.Models;
using LiveAssistant.Services;
using LiveAssistant.Utils;
using Xunit;

namespace LiveAssistant.Tests;

public sealed class SkipSongTests : IDisposable
{
    private readonly string _dir;

    public SkipSongTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "la-skip-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    [Fact]
    public void SkipSongParser_AcceptsCommonCommands()
    {
        Assert.True(SkipSongParser.TryParse("切歌"));
        Assert.True(SkipSongParser.TryParse("下一首"));
        Assert.True(SkipSongParser.TryParse("跳过"));
        Assert.False(SkipSongParser.TryParse("帮我切歌"));
    }

    [Fact]
    public void FreeMode_SkipDoesNotDeductPoints()
    {
        var (svc, users, queue, sent) = CreateService(SongRequestPolicyMode.Free, userPoints: 50);
        queue.BeginPlaying(new QueueItem { SongName = "测试曲", Nickname = "随机", IsRandom = true });

        var handled = svc.TryHandle(new DanmakuItem
        {
            UserId = "u1",
            Nickname = "观众A",
            Content = "切歌"
        }, "room1");

        Assert.True(handled);
        WaitForReply(sent);
        Assert.Equal(50, users.GetUser("u1")!.Points);
        Assert.Contains("已切歌", sent[0]);
        Assert.DoesNotContain("积分", sent[0]);
    }

    [Fact]
    public void PointsMode_SkipDeductsConfiguredCost()
    {
        var (svc, users, queue, sent) = CreateService(SongRequestPolicyMode.Points, userPoints: 50, skipCost: 20);
        queue.BeginPlaying(new QueueItem { SongName = "测试曲", Nickname = "观众B" });

        svc.TryHandle(new DanmakuItem
        {
            UserId = "u1",
            Nickname = "观众B",
            Content = "切歌"
        }, "room1");

        WaitForReply(sent);
        Assert.Equal(30, users.GetUser("u1")!.Points);
        Assert.Contains("20", sent[0]);
    }

    [Fact]
    public void PointsMode_InsufficientPoints_RejectsSkip()
    {
        var (svc, users, queue, sent) = CreateService(SongRequestPolicyMode.Points, userPoints: 5, skipCost: 20);
        queue.BeginPlaying(new QueueItem { SongName = "测试曲", Nickname = "观众C" });

        svc.TryHandle(new DanmakuItem
        {
            UserId = "u1",
            Nickname = "观众C",
            Content = "切歌"
        }, "room1");

        WaitForReply(sent);
        Assert.Equal(5, users.GetUser("u1")!.Points);
        Assert.Contains("20", sent[0]);
    }

    [Fact]
    public void NothingPlaying_RejectsSkip()
    {
        var (svc, users, _, sent) = CreateService(SongRequestPolicyMode.Points, userPoints: 50, skipCost: 20);

        svc.TryHandle(new DanmakuItem
        {
            UserId = "u1",
            Nickname = "观众D",
            Content = "切歌"
        }, "room1");

        WaitForReply(sent);
        Assert.Equal(50, users.GetUser("u1")!.Points);
        Assert.Contains("没有", sent[0]);
    }

    private (SkipSongService svc, UserRepository users, QueueService queue, List<string> sent) CreateService(
        SongRequestPolicyMode mode,
        int userPoints,
        int skipCost = 20)
    {
        var db = new AppDatabase(_dir);
        var users = new UserRepository(db);
        users.EnsureUser("u1", "测试观众");
        users.AddPoints("u1", "测试观众", userPoints);

        var config = new ConfigManager();
        config.Load();
        config.Settings.SongRequestPolicy.Mode = mode;
        config.Settings.SongRequestPolicy.SkipPointsCost = skipCost;
        config.ReplyTemplates["skipSongSuccess"] = "已切歌";
        config.ReplyTemplates["skipSongSuccessPaid"] = "已切歌，消耗{cost}积分";
        config.ReplyTemplates["skipSongInsufficientPoints"] = "切歌需要 {cost} 积分，你当前只有 {score} 积分";
        config.ReplyTemplates["skipSongNothingPlaying"] = "当前没有可切的歌曲";

        var log = new LogService(_dir);
        var queue = new QueueService(db);
        var playback = new PlaybackService(log);
        var kugou = new KugouService(config.Settings.Kugou, log);
        var random = new RandomPlaylistService(config, db);
        var system = new SystemMessageService(50);
        var commands = new PlaybackCommandQueue(config, queue, kugou, random, playback, new ReplyService(config), system, log);
        var engine = new PlaybackEngine(config, commands, system);
        var sent = new List<string>();
        var replyQueue = new ReplyQueue(
            new DouyinService(config.Settings.Douyin, log),
            log,
            config.Settings.Reply,
            sendMention: (_, _, content, _, _) =>
            {
                sent.Add(content);
                return Task.FromResult(new MentionSendResult { Ok = true });
            });

        var svc = new SkipSongService(
            config, users, playback, queue, engine, new ReplyService(config), replyQueue, system, log);
        return (svc, users, queue, sent);
    }

    private static void WaitForReply(List<string> sent)
    {
        for (var i = 0; i < 20 && sent.Count == 0; i++)
        {
            Thread.Sleep(50);
        }

        Assert.Single(sent);
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
