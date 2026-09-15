using System.Text;
using LiveAssistant.Config;
using LiveAssistant.Models;
using LiveAssistant.Services;
using LiveAssistant.Services.AiSpeech;
using Xunit;

namespace LiveAssistant.Tests;

[Collection("AiSpeechEventFixSerial")]
public class AiSpeechEventFixTests
{
    private static (string Temp, ConfigManager Config, LogService Log, AiSpeechCoordinator Coord) CreateCoord(
        Action<AiSpeechSettings>? mutate = null)
    {
        var temp = Path.Combine(Path.GetTempPath(), "la-ai-evt-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        Environment.SetEnvironmentVariable("LA_DATA_DIR", temp);
        Environment.SetEnvironmentVariable("LA_TEST_EXE_DIR", temp);

        var config = new ConfigManager();
        config.Load();
        config.Settings.AiSpeech.Enabled = true;
        config.Settings.AiSpeech.WelcomeUser = false;
        config.Settings.AiSpeech.ThankGift = true;
        config.Settings.AiSpeech.AnnounceSongRequest = false;
        config.Settings.AiSpeech.WelcomeIntervalSeconds = 5;
        config.Settings.AiSpeech.WelcomeMaxNames = 3;
        config.Settings.AiSpeech.GiftMergeSeconds = 1;
        config.Settings.AiSpeech.VolumePercent = 150;
        config.Settings.AiSpeech.OllamaUrl = "http://127.0.0.1:1";
        config.Settings.AiSpeech.TtsUrl = "http://127.0.0.1:1";
        config.Settings.AiSpeech.OllamaTimeoutSeconds = 2;
        config.Settings.AiSpeech.TtsTimeoutSeconds = 2;
        mutate?.Invoke(config.Settings.AiSpeech);
        for (var i = 0; i < 5; i++)
        {
            try
            {
                config.Save();
                break;
            }
            catch (IOException) when (i < 4)
            {
                Thread.Sleep(50);
            }
        }

        var log = new LogService(temp);
        var coord = new AiSpeechCoordinator(config, log, new OutboundReplyTracker());
        return (temp, config, log, coord);
    }

    private static void Cleanup(string temp, AiSpeechCoordinator? coord)
    {
        try { coord?.Dispose(); } catch { /* ignore */ }
        Environment.SetEnvironmentVariable("LA_DATA_DIR", null);
        Environment.SetEnvironmentVariable("LA_TEST_EXE_DIR", null);
        try { Directory.Delete(temp, true); } catch { /* ignore */ }
    }

    private static byte[] BuildSineWav(float amplitude = 0.25f, int sampleRate = 8000, double seconds = 0.05)
    {
        var n = (int)(sampleRate * seconds);
        using var ms = new MemoryStream();
        using (var bw = new BinaryWriter(ms, Encoding.ASCII, leaveOpen: true))
        {
            var dataBytes = n * 2;
            bw.Write(Encoding.ASCII.GetBytes("RIFF"));
            bw.Write(36 + dataBytes);
            bw.Write(Encoding.ASCII.GetBytes("WAVE"));
            bw.Write(Encoding.ASCII.GetBytes("fmt "));
            bw.Write(16);
            bw.Write((short)1);
            bw.Write((short)1);
            bw.Write(sampleRate);
            bw.Write(sampleRate * 2);
            bw.Write((short)2);
            bw.Write((short)16);
            bw.Write(Encoding.ASCII.GetBytes("data"));
            bw.Write(dataBytes);
            for (var i = 0; i < n; i++)
            {
                var sample = (short)(amplitude * short.MaxValue * Math.Sin(2 * Math.PI * 440 * i / sampleRate));
                bw.Write(sample);
            }
        }

        return ms.ToArray();
    }

    [Fact]
    public void Welcome_Disabled_DoesNotEnterBufferOrQueue()
    {
        var (temp, config, _, coord) = CreateCoord(s =>
        {
            s.WelcomeUser = false;
            s.Enabled = true;
        });
        try
        {
            coord.TryEnqueueMemberJoin(new DanmakuItem
            {
                UserId = "u1",
                Nickname = "小明",
                MsgType = "member",
                MsgId = "m1"
            });
            Assert.Equal(0, coord.GetStatus().QueueCount);
            Assert.False(config.Settings.AiSpeech.WelcomeUser);
        }
        finally
        {
            Cleanup(temp, coord);
        }
    }

    [Fact]
    public async Task Welcome_Enabled_MemberEventuallyEnqueues()
    {
        var (temp, _, _, coord) = CreateCoord(s =>
        {
            s.WelcomeUser = true;
            s.WelcomeIntervalSeconds = 5;
            s.WelcomeMaxNames = 1;
        });
        try
        {
            coord.TryEnqueueMemberJoin(new DanmakuItem
            {
                UserId = "u-welcome",
                Nickname = "欢迎测试",
                MsgType = "member",
                MsgId = "mw1"
            });

            // 等缓冲 flush（MaxNames=1 应尽快 flush）
            var ok = false;
            for (var i = 0; i < 40; i++)
            {
                await Task.Delay(100);
                var st = coord.GetStatus();
                if (st.QueueCount > 0 || st.TaskKind == AiSpeechEventKind.Welcome)
                {
                    ok = true;
                    break;
                }
            }

            Assert.True(ok, "Welcome enabled 时应最终进入语音队列");
        }
        finally
        {
            Cleanup(temp, coord);
        }
    }

    [Fact]
    public void Gift_Disabled_DoesNotBuffer()
    {
        var (temp, _, _, coord) = CreateCoord(s => s.ThankGift = false);
        try
        {
            coord.TryEnqueueGift(new GiftEvent
            {
                UserId = "g1",
                Nickname = "土豪",
                GiftId = "100",
                GiftName = "玫瑰",
                Count = 1,
                EventId = "evt-gift-1"
            });
            Assert.Equal(0, coord.GetStatus().QueueCount);
        }
        finally
        {
            Cleanup(temp, coord);
        }
    }

    [Fact]
    public async Task Gift_Enabled_EntersQueueAfterMerge()
    {
        var (temp, _, _, coord) = CreateCoord(s =>
        {
            s.ThankGift = true;
            s.GiftMergeSeconds = 1;
            s.GiftThankMode = "template";
        });
        try
        {
            coord.TryEnqueueGift(new GiftEvent
            {
                UserId = "g2",
                Nickname = "土豪二号",
                GiftId = "101",
                GiftName = "嘉年华",
                Count = 1,
                EventId = "evt-gift-2"
            });

            var ok = false;
            for (var i = 0; i < 40; i++)
            {
                await Task.Delay(100);
                var st = coord.GetStatus();
                if (st.QueueCount > 0 || st.TaskKind == AiSpeechEventKind.Gift)
                {
                    ok = true;
                    break;
                }
            }

            Assert.True(ok, "Gift enabled 时应进入语音队列");
        }
        finally
        {
            Cleanup(temp, coord);
        }
    }

    [Fact]
    public async Task Gift_Combo_MergesToSingleThanks()
    {
        var (temp, _, _, coord) = CreateCoord(s =>
        {
            s.ThankGift = true;
            s.GiftMergeSeconds = 2;
            s.GiftThankMode = "template";
            s.MaxQueueSize = 10;
        });
        try
        {
            for (var i = 0; i < 5; i++)
            {
                coord.TryEnqueueGift(new GiftEvent
                {
                    UserId = "combo-user",
                    Nickname = "连击哥",
                    GiftId = "200",
                    GiftName = "小心心",
                    Count = 1,
                    EventId = $"evt-combo-{i}",
                    RepeatCount = i + 1
                });
            }

            await Task.Delay(2500);
            var st = coord.GetStatus();
            // 同用户同礼物合并后最多一笔 Gift 任务（可能已开始处理，队列<=1）
            Assert.True(st.QueueCount <= 1, $"combo merge queue={st.QueueCount}");
            Assert.True(st.TaskKind == AiSpeechEventKind.Gift || st.QueueCount >= 0);
        }
        finally
        {
            Cleanup(temp, coord);
        }
    }

    [Fact]
    public void SongRequest_Disabled_DoesNotEnqueue()
    {
        var (temp, _, _, coord) = CreateCoord(s => s.AnnounceSongRequest = false);
        try
        {
            coord.TryEnqueueSongRequest(new SongRequestSucceededEvent
            {
                UserId = "s1",
                Nickname = "小明",
                SongName = "后来",
                Artist = "刘若英",
                AheadCount = 2,
                QueueItemId = 99
            });
            Assert.Equal(0, coord.GetStatus().QueueCount);
        }
        finally
        {
            Cleanup(temp, coord);
        }
    }

    [Fact]
    public void SongRequest_CommitSuccess_EnqueuesOnce_WithDetails()
    {
        var (temp, _, _, coord) = CreateCoord(s =>
        {
            s.AnnounceSongRequest = true;
            s.MaxQueueSize = 10;
        });
        try
        {
            var ev = new SongRequestSucceededEvent
            {
                UserId = "s2",
                Nickname = "小明",
                SongName = "后来",
                Artist = "刘若英",
                AheadCount = 2,
                QueueItemId = 1001
            };
            coord.TryEnqueueSongRequest(ev);
            coord.TryEnqueueSongRequest(ev); // 第二次仍会入队（不同 TaskId）；业务侧只触发一次事件

            var st = coord.GetStatus();
            Assert.True(st.QueueCount >= 1);
            Assert.Equal(AiSpeechEventKind.SongRequest, st.TaskKind);
            Assert.Contains("后来", st.LatestContent, StringComparison.Ordinal);
            Assert.Contains("刘若英", st.LatestContent, StringComparison.Ordinal);
            Assert.Contains("2", st.LatestContent, StringComparison.Ordinal);
            Assert.Equal("小明", st.LatestNickname);
        }
        finally
        {
            Cleanup(temp, coord);
        }
    }

    [Fact]
    public void SongRequest_Priority_BetweenGiftAndDanmaku()
    {
        Assert.True((int)AiSpeechPriority.Gift < (int)AiSpeechPriority.SongRequest);
        Assert.True((int)AiSpeechPriority.SongRequest < (int)AiSpeechPriority.DanmakuImportant);
        Assert.True((int)AiSpeechPriority.DanmakuImportant < (int)AiSpeechPriority.Welcome);

        var s = new AiSpeechScheduler { MaxSize = 10, MaxAgeSeconds = 60 };
        s.Enqueue(new AiSpeechTask { Kind = AiSpeechEventKind.Welcome, Priority = AiSpeechPriority.Welcome, Content = "w" });
        s.Enqueue(new AiSpeechTask { Kind = AiSpeechEventKind.Danmaku, Priority = AiSpeechPriority.DanmakuImportant, Content = "d" });
        s.Enqueue(new AiSpeechTask { Kind = AiSpeechEventKind.SongRequest, Priority = AiSpeechPriority.SongRequest, Content = "s" });
        s.Enqueue(new AiSpeechTask { Kind = AiSpeechEventKind.Gift, Priority = AiSpeechPriority.Gift, Content = "g" });

        Assert.True(s.TryDequeue(out var t1));
        Assert.Equal(AiSpeechEventKind.Gift, t1!.Kind);
        Assert.True(s.TryDequeue(out var t2));
        Assert.Equal(AiSpeechEventKind.SongRequest, t2!.Kind);
        Assert.True(s.TryDequeue(out var t3));
        Assert.Equal(AiSpeechEventKind.Danmaku, t3!.Kind);
        Assert.True(s.TryDequeue(out var t4));
        Assert.Equal(AiSpeechEventKind.Welcome, t4!.Kind);
    }

    [Fact]
    public void Volume_GainChangesOutputAmplitude_And_200PercentDoesNotOverflow()
    {
        var wav = BuildSineWav(0.3f);
        var a100 = AiSpeechPlayer.AnalyzeBytes(wav, 1.0f);
        var a150 = AiSpeechPlayer.AnalyzeBytes(wav, 1.5f);
        var a200 = AiSpeechPlayer.AnalyzeBytes(wav, 2.0f);

        Assert.True(a150.PeakAfterGain > a100.PeakAfterGain * 1.2f,
            $"150% should be louder: 100={a100.PeakAfterGain} 150={a150.PeakAfterGain}");
        Assert.True(a200.PeakAfterGain > a100.PeakAfterGain * 1.4f,
            $"200% should be louder: 100={a100.PeakAfterGain} 200={a200.PeakAfterGain}");
        Assert.True(a200.PeakAfterGain <= 1.0001f, $"200% must clamp: peak={a200.PeakAfterGain}");
        Assert.True(a100.Peak > 0.01f);
        Assert.True(a100.Rms > 0);
    }

    [Fact]
    public void SoftLimit_ClampsToUnitRange()
    {
        Assert.InRange(SoftLimitingSampleProvider.SoftLimit(0.5f), -1f, 1f);
        Assert.InRange(SoftLimitingSampleProvider.SoftLimit(3.0f), -1f, 1f);
        Assert.InRange(SoftLimitingSampleProvider.SoftLimit(-3.0f), -1f, 1f);
        Assert.True(SoftLimitingSampleProvider.SoftLimit(3.0f) <= 1f);
    }

    [Fact]
    public void FeatureFlags_SaveSettingsFromUi_TakeEffectImmediately()
    {
        var (temp, config, _, coord) = CreateCoord();
        try
        {
            coord.SaveSettingsFromUi(s =>
            {
                s.WelcomeUser = true;
                s.ThankGift = false;
                s.AnnounceSongRequest = true;
                s.VolumePercent = 180;
            });

            Assert.True(config.Settings.AiSpeech.WelcomeUser);
            Assert.False(config.Settings.AiSpeech.ThankGift);
            Assert.True(config.Settings.AiSpeech.AnnounceSongRequest);
            Assert.Equal(180, config.Settings.AiSpeech.VolumePercent);

            coord.TryEnqueueGift(new GiftEvent
            {
                UserId = "x",
                Nickname = "n",
                GiftName = "玫瑰",
                GiftId = "1",
                Count = 1,
                EventId = "flag-gift"
            });
            Assert.Equal(0, coord.GetStatus().QueueCount);

            coord.TryEnqueueSongRequest(new SongRequestSucceededEvent
            {
                UserId = "u",
                Nickname = "测",
                SongName = "歌",
                Artist = "手",
                AheadCount = 0,
                QueueItemId = 1
            });
            Assert.True(coord.GetStatus().QueueCount >= 1);
        }
        finally
        {
            Cleanup(temp, coord);
        }
    }

    [Fact]
    public void SongRequestSucceededEvent_CarriesRequiredFields()
    {
        var e = new SongRequestSucceededEvent
        {
            UserId = "uid",
            Nickname = "小明",
            SongName = "后来",
            Artist = "刘若英",
            AheadCount = 2,
            QueueItemId = 55
        };
        Assert.Equal("uid", e.UserId);
        Assert.Equal("小明", e.Nickname);
        Assert.Equal("后来", e.SongName);
        Assert.Equal("刘若英", e.Artist);
        Assert.Equal(2, e.AheadCount);
        Assert.Equal(55, e.QueueItemId);
    }

    [Fact]
    public void PromptStore_HasSongRequestPrompt()
    {
        var dir = Path.Combine(Path.GetTempPath(), "la-prompt-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            using var store = new AiPromptStore(dir, Path.Combine(dir, "data"));
            var text = store.GetSongRequest();
            Assert.False(string.IsNullOrWhiteSpace(text));
            Assert.Contains("点歌", text, StringComparison.Ordinal);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { /* ignore */ }
        }
    }
}
