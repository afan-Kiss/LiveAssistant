using LiveAssistant.Models;
using LiveAssistant.Services;
using LiveAssistant.Services.AiSpeech;
using Xunit;

namespace LiveAssistant.Tests;

public sealed class InteractionFilterTests
{
    [Fact]
    public void LoginUserId_FiltersBotEcho_EvenWhenNicknameDiffers()
    {
        var tracker = new OutboundReplyTracker(TimeSpan.FromMinutes(2));
        var filter = new ChatAudienceFilter(tracker);
        filter.UpdateDouyinIdentity("login-uid-1", "机器人账号", "room1");

        var item = new DanmakuItem
        {
            MsgId = "echo-1",
            UserId = "login-uid-1",
            Nickname = "显示昵称不同",
            Content = "@观众 你好呀",
            MsgType = "chat",
            RoomKey = "room1"
        };

        Assert.True(filter.ShouldExclude(item, out var reason));
        Assert.Equal("login_user_id", reason);
    }

    [Fact]
    public void OutboundMsgId_IsFiltered()
    {
        var tracker = new OutboundReplyTracker(TimeSpan.FromMinutes(2));
        tracker.Track("local-1", "感谢礼物", platformMessageId: "plat-100");
        var filter = new ChatAudienceFilter(tracker);
        filter.UpdateDouyinIdentity("bot-uid", "机器人", "room1");

        var item = new DanmakuItem
        {
            MsgId = "plat-100",
            UserId = "someone",
            Nickname = "路人",
            Content = "感谢礼物",
            MsgType = "chat",
            RoomKey = "room1"
        };

        Assert.True(filter.ShouldExclude(item, out var reason));
        Assert.Equal("outbound_msg_id", reason);
    }

    [Fact]
    public void RealAudienceSameContent_IsNotFiltered()
    {
        var tracker = new OutboundReplyTracker(TimeSpan.FromMinutes(2));
        tracker.Track("local-1", "好的");
        var filter = new ChatAudienceFilter(tracker);
        filter.UpdateDouyinIdentity("bot-uid", "机器人账号", "room1");

        var item = new DanmakuItem
        {
            MsgId = "audience-1",
            UserId = "audience-uid",
            Nickname = "真实观众",
            Content = "好的",
            MsgType = "chat",
            RoomKey = "room1"
        };

        Assert.False(filter.ShouldExclude(item, out _));
    }

    [Fact]
    public void HostUserId_IsFilteredFromWordCloudPath()
    {
        var tracker = new OutboundReplyTracker();
        var filter = new ChatAudienceFilter(tracker);
        filter.UpdateRoomOwner("owner-uid", "主播昵称");

        var item = new DanmakuItem
        {
            MsgId = "host-1",
            UserId = "owner-uid",
            Nickname = "主播昵称",
            Content = "大家好",
            MsgType = "chat",
            RoomKey = "room1"
        };

        Assert.True(filter.ShouldExclude(item, out var reason));
        Assert.Equal("room_owner_user_id", reason);
    }

    [Fact]
    public void BotMessage_DoesNotEnterMovieStream()
    {
        var dir = Path.Combine(Path.GetTempPath(), "la-filter-" + Guid.NewGuid().ToString("N"));
        var db = new LiveAssistant.Database.AppDatabase(dir);
        var config = new LiveAssistant.Config.ConfigManager();
        config.Load();
        config.Settings.MovieInteraction.Enabled = true;
        var log = new LogService(dir);
        var tracker = new OutboundReplyTracker();
        var filter = new ChatAudienceFilter(tracker);
        filter.UpdateDouyinIdentity("bot-uid", "机器人", "room1");
        var svc = new MovieInteractionService(config, db, log, audienceFilter: filter);

        svc.OnDanmaku(new DanmakuItem
        {
            MsgId = "bot-dm-1",
            UserId = "bot-uid",
            Nickname = "机器人",
            Content = "感谢礼物",
            MsgType = "chat",
            RoomKey = "room1"
        });

        Assert.Equal(0, svc.Repository.GetStreamMaxSeq());
        svc.Dispose();
    }

    [Fact]
    public void ScoreCommand_DoesNotEnterMovieStream()
    {
        var dir = Path.Combine(Path.GetTempPath(), "la-filter-" + Guid.NewGuid().ToString("N"));
        var db = new LiveAssistant.Database.AppDatabase(dir);
        var config = new LiveAssistant.Config.ConfigManager();
        config.Load();
        config.Settings.MovieInteraction.Enabled = true;
        var log = new LogService(dir);
        var tracker = new OutboundReplyTracker();
        var filter = new ChatAudienceFilter(tracker);
        filter.UpdateDouyinIdentity("bot-uid", "机器人", "room1");
        var svc = new MovieInteractionService(config, db, log, audienceFilter: filter);

        svc.OnDanmaku(new DanmakuItem
        {
            MsgId = "score-cmd-1",
            UserId = "u-aud",
            Nickname = "观众甲",
            Content = "哪吒 好看",
            MsgType = "chat",
            RoomKey = "room1"
        });

        Assert.Equal(0, svc.Repository.GetStreamMaxSeq());
        svc.Dispose();
    }

    [Fact]
    public void SongRequest_DoesNotEnterMovieStream()
    {
        var dir = Path.Combine(Path.GetTempPath(), "la-filter-" + Guid.NewGuid().ToString("N"));
        var db = new LiveAssistant.Database.AppDatabase(dir);
        var config = new LiveAssistant.Config.ConfigManager();
        config.Load();
        config.Settings.MovieInteraction.Enabled = true;
        var log = new LogService(dir);
        var svc = new MovieInteractionService(config, db, log);

        svc.OnDanmaku(new DanmakuItem
        {
            MsgId = "song-1",
            UserId = "u-aud",
            Nickname = "观众乙",
            Content = "点歌 晴天",
            MsgType = "chat",
            RoomKey = "room1"
        });

        Assert.Equal(0, svc.Repository.GetStreamMaxSeq());
        svc.Dispose();
    }

    [Fact]
    public void PointsQuery_DoesNotEnterMovieStream()
    {
        var dir = Path.Combine(Path.GetTempPath(), "la-filter-" + Guid.NewGuid().ToString("N"));
        var db = new LiveAssistant.Database.AppDatabase(dir);
        var config = new LiveAssistant.Config.ConfigManager();
        config.Load();
        config.Settings.MovieInteraction.Enabled = true;
        var log = new LogService(dir);
        var svc = new MovieInteractionService(config, db, log);

        svc.OnDanmaku(new DanmakuItem
        {
            MsgId = "points-1",
            UserId = "u-aud",
            Nickname = "观众丙",
            Content = "查我",
            MsgType = "chat",
            RoomKey = "room1"
        });

        Assert.Equal(0, svc.Repository.GetStreamMaxSeq());
        svc.Dispose();
    }

    [Fact]
    public void BanCommand_DoesNotEnterMovieStream()
    {
        var dir = Path.Combine(Path.GetTempPath(), "la-filter-" + Guid.NewGuid().ToString("N"));
        var db = new LiveAssistant.Database.AppDatabase(dir);
        var config = new LiveAssistant.Config.ConfigManager();
        config.Load();
        config.Settings.MovieInteraction.Enabled = true;
        var log = new LogService(dir);
        var svc = new MovieInteractionService(config, db, log);

        svc.OnDanmaku(new DanmakuItem
        {
            MsgId = "ban-1",
            UserId = "u-aud",
            Nickname = "观众丁",
            Content = "禁言 某人",
            MsgType = "chat",
            RoomKey = "room1"
        });

        Assert.Equal(0, svc.Repository.GetStreamMaxSeq());
        svc.Dispose();
    }

    [Theory]
    [InlineData("bot-uid", ChatMessageKind.BotMessage)]
    [InlineData("owner-uid", ChatMessageKind.StreamerMessage)]
    public void Classify_MapsFilterReasons(string userId, ChatMessageKind expectedKind)
    {
        var tracker = new OutboundReplyTracker();
        var filter = new ChatAudienceFilter(tracker);
        filter.UpdateDouyinIdentity("bot-uid", "机器人", "room1");
        filter.UpdateRoomOwner("owner-uid", "主播");

        var item = new DanmakuItem
        {
            MsgId = "cls-1",
            UserId = userId,
            Nickname = "昵称",
            Content = "普通聊天",
            MsgType = "chat",
            RoomKey = "room1"
        };

        var cls = filter.Classify(item);
        Assert.Equal(expectedKind, cls.Kind);
    }

    [Fact]
    public void Classify_NormalChat_ForRealAudience()
    {
        var filter = new ChatAudienceFilter(new OutboundReplyTracker());
        filter.UpdateDouyinIdentity("bot-uid", "机器人", "room1");

        var cls = filter.Classify(new DanmakuItem
        {
            MsgId = "n1",
            UserId = "aud-1",
            Nickname = "观众",
            Content = "这个电影不错",
            MsgType = "chat",
            RoomKey = "room1"
        });

        Assert.Equal(ChatMessageKind.NormalChat, cls.Kind);
    }

    [Theory]
    [InlineData("欢迎小明来到直播间 ❤️")]
    [InlineData("欢迎 小明 进入直播间")]
    public void WelcomeTemplate_Echo_IsFilteredAsBot(string content)
    {
        var filter = new ChatAudienceFilter(new OutboundReplyTracker());
        filter.UpdateDouyinIdentity("bot-uid", "机器人", "room1");

        // 无 user_id/昵称时，依赖欢迎模板启发式识别机器人回显
        Assert.True(filter.ShouldExclude(new DanmakuItem
        {
            MsgId = "w1",
            UserId = "",
            Nickname = "",
            Content = content,
            MsgType = "chat",
            RoomKey = "room1"
        }, out var reason));
        Assert.Equal("bot_template", reason);
    }

    [Fact]
    public void Classify_Command_ForScoreText()
    {
        var filter = new ChatAudienceFilter(new OutboundReplyTracker());
        var cls = filter.Classify(new DanmakuItem
        {
            MsgId = "c1",
            UserId = "aud-1",
            Nickname = "观众",
            Content = "哪吒 不好看",
            MsgType = "chat",
            RoomKey = "room1"
        });

        Assert.Equal(ChatMessageKind.Command, cls.Kind);
        Assert.Equal("interaction_command", cls.Reason);
    }

    [Fact]
    public void AudienceMessage_EntersMovieStream()
    {
        var dir = Path.Combine(Path.GetTempPath(), "la-filter-" + Guid.NewGuid().ToString("N"));
        var db = new LiveAssistant.Database.AppDatabase(dir);
        var config = new LiveAssistant.Config.ConfigManager();
        config.Load();
        config.Settings.MovieInteraction.Enabled = true;
        var log = new LogService(dir);
        var tracker = new OutboundReplyTracker();
        var filter = new ChatAudienceFilter(tracker);
        filter.UpdateDouyinIdentity("bot-uid", "机器人", "room1");
        var svc = new MovieInteractionService(config, db, log, audienceFilter: filter);

        svc.OnDanmaku(new DanmakuItem
        {
            MsgId = "aud-1",
            UserId = "u-aud",
            Nickname = "观众甲",
            Content = "这部电影真不错",
            MsgType = "chat",
            RoomKey = "room1"
        });

        Assert.Equal(1, svc.Repository.GetStreamMaxSeq());
        svc.Dispose();
    }

    [Fact]
    public async Task BotMessage_DoesNotTriggerAiReply()
    {
        var temp = Path.Combine(Path.GetTempPath(), "la-ai-filter-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        Environment.SetEnvironmentVariable("LA_DATA_DIR", temp);
        Environment.SetEnvironmentVariable("LA_TEST_EXE_DIR", temp);

        try
        {
            var config = new LiveAssistant.Config.ConfigManager();
            config.Load();
            config.Settings.AiSpeech.Enabled = true;
            config.Settings.AiSpeech.ReplyDanmaku = true;
            config.Settings.AiSpeech.OllamaUrl = "http://127.0.0.1:1";
            config.Settings.AiSpeech.TtsUrl = "http://127.0.0.1:1";

            var log = new LogService(temp);
            var tracker = new OutboundReplyTracker();
            var filter = new ChatAudienceFilter(tracker);
            filter.UpdateDouyinIdentity("host-uid", "主播昵称", "room1");
            using var coordinator = new AiSpeechCoordinator(config, log, tracker, filter);

            coordinator.TryEnqueueDanmaku(new DanmakuItem
            {
                MsgId = "self-uid-1",
                UserId = "host-uid",
                Nickname = "任意昵称",
                Content = "大家好今天一起聊聊电影吧",
                MsgType = "chat",
                RoomKey = "room1"
            }, "主播昵称", "主播昵称");

            await Task.Delay(200);
            Assert.Equal(0, coordinator.GetStatus().QueueCount);
        }
        finally
        {
            Environment.SetEnvironmentVariable("LA_DATA_DIR", null);
            Environment.SetEnvironmentVariable("LA_TEST_EXE_DIR", null);
            try { Directory.Delete(temp, true); } catch { /* ignore */ }
        }
    }

    [Fact]
    public async Task MemberEvent_EnqueuesWelcomeTts()
    {
        var temp = Path.Combine(Path.GetTempPath(), "la-welcome-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        Environment.SetEnvironmentVariable("LA_DATA_DIR", temp);
        Environment.SetEnvironmentVariable("LA_TEST_EXE_DIR", temp);

        try
        {
            var config = new LiveAssistant.Config.ConfigManager();
            config.Load();
            config.Settings.AiSpeech.Enabled = true;
            config.Settings.AiSpeech.WelcomeUser = true;
            config.Settings.AiSpeech.WelcomeIntervalSeconds = 5;
            config.Settings.AiSpeech.WelcomeMaxNames = 1;
            config.Settings.AiSpeech.OllamaUrl = "http://127.0.0.1:1";
            config.Settings.AiSpeech.TtsUrl = "http://127.0.0.1:1";
            config.Save();

            var log = new LogService(temp);
            var tracker = new OutboundReplyTracker();
            using var coordinator = new AiSpeechCoordinator(config, log, tracker);

            coordinator.TryEnqueueMemberJoin(new DanmakuItem
            {
                MsgId = "m1",
                UserId = "u-member",
                Nickname = "新观众",
                MsgType = "member",
                RoomKey = "room1"
            });

            var ok = false;
            for (var i = 0; i < 40; i++)
            {
                await Task.Delay(100);
                var st = coordinator.GetStatus();
                if (st.QueueCount > 0 || st.TaskKind == AiSpeechEventKind.Welcome)
                {
                    ok = true;
                    break;
                }
            }

            Assert.True(ok, "member 事件应进入欢迎 TTS 队列");
        }
        finally
        {
            Environment.SetEnvironmentVariable("LA_DATA_DIR", null);
            Environment.SetEnvironmentVariable("LA_TEST_EXE_DIR", null);
            try { Directory.Delete(temp, true); } catch { /* ignore */ }
        }
    }
}
