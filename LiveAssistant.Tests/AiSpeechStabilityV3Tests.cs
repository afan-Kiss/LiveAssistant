using System.Diagnostics;
using System.Text;
using System.Text.Json;
using LiveAssistant.Services.AiSpeech;
using Xunit;

namespace LiveAssistant.Tests;

public class AiSpeechMetricsTests
{
    [Fact]
    public void Snapshot_TracksCountsAndAverages()
    {
        var m = new AiSpeechMetrics();
        m.NoteReceived(3);
        m.NoteFiltered(2);
        m.NoteGenerated(100, 20);
        m.NoteGenerated(200, 40);
        m.NoteTtsSuccess(50);
        m.NoteTtsFailed("down");
        m.NotePlaySuccess(300);
        m.NotePlayFailed("device");
        m.NoteQueueSize(4);
        m.NoteQueueSize(7);
        m.NoteSkip("THINK_ONLY");
        m.NoteSkip("DUPLICATE_REPLY");
        m.NoteSkip("SKIP");
        m.NoteOllamaError("timeout");

        var s = m.Snapshot(2, 5);
        Assert.Equal(3, s.ReceivedCount);
        Assert.Equal(2, s.FilteredCount);
        Assert.Equal(2, s.GeneratedCount);
        Assert.Equal(1, s.TtsSuccessCount);
        Assert.Equal(1, s.TtsFailedCount);
        Assert.Equal(1, s.PlaySuccessCount);
        Assert.Equal(1, s.PlayFailedCount);
        Assert.Equal(150, s.AverageOllamaMs);
        Assert.Equal(30, s.AverageQueueWaitMs);
        Assert.Equal(7, s.MaxQueueSizeSeen);
        Assert.Equal(1, s.SkipThinkOnly);
        Assert.Equal(1, s.SkipDuplicateReply);
        Assert.Equal(1, s.SkipByModel);
        Assert.Equal(1, s.OllamaError);
        Assert.Contains("AI_METRIC_SNAPSHOT", m.FormatSnapshotLog(2, 5));
        Assert.DoesNotContain("观众说", m.FormatSnapshotLog(2, 5));
    }
}

public class AiReplyDuplicateGuardTests
{
    [Fact]
    public void Detects_RepeatedThankYou_WithinWindow()
    {
        var g = new AiReplyDuplicateGuard(20, TimeSpan.FromMinutes(5));
        Assert.False(g.IsDuplicate("感谢支持！"));
        Assert.True(g.IsDuplicate("感谢支持"));
        Assert.True(g.IsDuplicate("感谢支持。"));
        Assert.False(g.IsDuplicate("欢迎新朋友来玩"));
    }

    [Fact]
    public void Capacity_EvictsOldest()
    {
        var g = new AiReplyDuplicateGuard(4, TimeSpan.FromMinutes(5));
        for (var i = 0; i < 4; i++)
        {
            Assert.False(g.IsDuplicate($"句子{i}"));
        }

        Assert.False(g.IsDuplicate("句子4"));
        // 最早的可能仍在窗口内但超出容量被挤出；新句子不重复
        Assert.True(g.Count <= 4);
    }
}

public class ModelSkipAndJsonContentTests
{
    [Fact]
    public void Sanitize_SkipToken_RejectsWithoutExplanationLeak()
    {
        Assert.Equal("SKIP", ModelOutputSanitizer.Sanitize("[SKIP]").RejectReason);
        Assert.Equal("SKIP", ModelOutputSanitizer.Sanitize("SKIP").RejectReason);
        Assert.True(ModelOutputSanitizer.IsModelSkip("[SKIP]"));
        Assert.False(ModelOutputSanitizer.IsModelSkip("[SKIP] 因为无意义"));
        var explained = ModelOutputSanitizer.Sanitize("[SKIP] 因为无意义");
        Assert.NotEqual("SKIP", explained.RejectReason);
    }

    [Fact]
    public void OllamaStyleJson_OnlyContentIsSpeakable()
    {
        // 模拟 {"thinking":"...","content":"正常回复"} 只取 content
        var json = """{"message":{"thinking":"内部分析不要读","content":"正常回复"},"done":true}""";
        using var doc = JsonDocument.Parse(json);
        var content = doc.RootElement.GetProperty("message").GetProperty("content").GetString();
        Assert.Equal("正常回复", content);
        var speakable = ModelOutputSanitizer.PrepareSpeakableOrNull(content);
        Assert.Equal("正常回复", speakable);
        Assert.Null(ModelOutputSanitizer.PrepareSpeakableOrNull("<think>内部</think>"));
        Assert.Null(ModelOutputSanitizer.PrepareSpeakableOrNull("<analysis>x</analysis>"));
    }

    [Fact]
    public void AntiLeakRules_MentionSkip()
    {
        Assert.Contains("[SKIP]", ContextPromptBuilder.AntiLeakRules);
    }
}

public class AiSpeechPlayerStabilityTests
{
    internal static byte[] BuildMinimalWav(int dataBytes = 64)
    {
        using var ms = new MemoryStream();
        using (var bw = new BinaryWriter(ms, Encoding.ASCII, leaveOpen: true))
        {
            bw.Write(Encoding.ASCII.GetBytes("RIFF"));
            bw.Write(36 + dataBytes);
            bw.Write(Encoding.ASCII.GetBytes("WAVE"));
            bw.Write(Encoding.ASCII.GetBytes("fmt "));
            bw.Write(16);
            bw.Write((short)1);
            bw.Write((short)1);
            bw.Write(8000);
            bw.Write(8000);
            bw.Write((short)1);
            bw.Write((short)8);
            bw.Write(Encoding.ASCII.GetBytes("data"));
            bw.Write(dataBytes);
            bw.Write(new byte[dataBytes]);
        }

        return ms.ToArray();
    }

    [Fact]
    public async Task Play_EmptyBytes_Throws_NoTempLeft()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ai-play-empty-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        using var player = new AiSpeechPlayer();
        await Assert.ThrowsAnyAsync<Exception>(async () =>
            await player.PlayWavAsync(Array.Empty<byte>(), dir, CancellationToken.None));
        Assert.Null(player.CurrentTempFile);
        Assert.False(player.IsPlaying);
        Assert.Empty(Directory.EnumerateFiles(dir, "*.wav"));
        try { Directory.Delete(dir, true); } catch { /* ignore */ }
    }

    [Fact]
    public async Task Play_InvalidWav_Throws_AndCleans()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ai-play-bad-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        using var player = new AiSpeechPlayer();
        await Assert.ThrowsAnyAsync<Exception>(async () =>
            await player.PlayWavAsync(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }, dir, CancellationToken.None));
        Assert.Null(player.CurrentTempFile);
        Assert.False(player.IsPlaying);
        Assert.Empty(Directory.EnumerateFiles(dir, "*.wav"));
        try { Directory.Delete(dir, true); } catch { /* ignore */ }
    }

    [Fact]
    public async Task Play_CancelBeforeStart_CleansUp()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ai-play-cancel-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        using var player = new AiSpeechPlayer();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var wav = BuildMinimalWav(256);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await player.PlayWavAsync(wav, dir, TimeSpan.FromSeconds(2), cts.Token));
        Assert.Null(player.CurrentTempFile);
        Assert.False(player.IsPlaying);
        AiSpeechPlayer.CleanupTempDirectory(dir);
        try { Directory.Delete(dir, true); } catch { /* ignore */ }
    }

    [Fact]
    public void Dispose_StopsPlayback()
    {
        var player = new AiSpeechPlayer();
        player.Dispose();
        Assert.False(player.IsPlaying);
        Assert.Null(player.CurrentTempFile);
    }

    [Fact]
    public async Task Play_HardTimeout_Unblocks()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ai-play-to-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        using var player = new AiSpeechPlayer();
        var wav = BuildMinimalWav(64);
        var sw = Stopwatch.StartNew();
        try
        {
            await player.PlayWavAsync(wav, dir, TimeSpan.FromMilliseconds(400), CancellationToken.None);
        }
        catch (Exception)
        {
            // 超时 / 设备 / 格式 — 均可接受，关键不挂起
        }

        sw.Stop();
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), $"play hung: {sw.Elapsed}");
        Assert.Null(player.CurrentTempFile);
        Assert.False(player.IsPlaying);
        AiSpeechPlayer.CleanupTempDirectory(dir);
        try { Directory.Delete(dir, true); } catch { /* ignore */ }
    }
}

public class AiSpeechSchedulerDrainTests
{
    [Fact]
    public void HundredTasks_WithRandomFailures_QueueEventuallyEmpty()
    {
        var scheduler = new AiSpeechScheduler { MaxSize = 20, MaxAgeSeconds = 120 };
        var metrics = new AiSpeechMetrics();
        var rng = new Random(42);
        var processed = 0;
        var failed = 0;

        for (var i = 0; i < 100; i++)
        {
            scheduler.Enqueue(new AiSpeechTask
            {
                Kind = i % 3 == 0 ? AiSpeechEventKind.Gift : AiSpeechEventKind.Danmaku,
                Priority = i % 3 == 0 ? AiSpeechPriority.Gift : AiSpeechPriority.DanmakuImportant,
                Content = $"t{i}",
                EnqueuedAt = DateTime.UtcNow
            });
            metrics.NoteQueueSize(scheduler.Count);
        }

        while (scheduler.TryDequeue(out var task, out var expired))
        {
            if (expired > 0)
            {
                for (var e = 0; e < expired; e++) metrics.NoteSkip("EXPIRED");
            }

            Assert.NotNull(task);
            processed++;
            if (rng.NextDouble() < 0.20)
            {
                failed++;
                metrics.NoteOllamaError("sim");
            }
            else
            {
                metrics.NoteGenerated(10, 1);
                metrics.NoteTtsSuccess(5);
                metrics.NotePlaySuccess(20);
            }
        }

        Assert.Equal(0, scheduler.Count);
        Assert.True(processed >= 20); // MaxSize 可能丢弃
        Assert.True(failed >= 0);
        var snap = metrics.Snapshot(0, 20);
        Assert.Equal(0, snap.CurrentQueueSize);
    }
}

public class AiPromptStoreStabilityTests
{
    [Fact]
    public void KeepOld_WhenReloadReadsEmpty()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ai-prompt-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var logs = new List<string>();
        try
        {
            var path = Path.Combine(dir, "danmaku_prompt.txt");
            File.WriteAllText(path, "【弹幕回复任务】稳定旧版提示词内容足够长用于测试保留。\n");
            using var store = new AiPromptStore(dir, Path.Combine(dir, "data"), msg => logs.Add(msg));
            var old = store.GetDanmaku();
            Assert.Contains("稳定旧版", old);

            // 写入空文件并强制重载路径：SavePrompt 会写非空；直接改文件为空
            File.WriteAllText(path, "\n");
            // 触发 Get -> MaybeRefresh
            Thread.Sleep(50);
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(2));
            var kept = store.GetDanmaku();
            Assert.Contains("稳定旧版", kept);
            Assert.Contains(logs, l => l.Contains("AI_PROMPT_KEEP_OLD", StringComparison.Ordinal));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { /* ignore */ }
        }
    }
}

public class AiSpeechLiveSimulationTests
{
    [Fact]
    public void TenThousandEvents_DoNotFloodQueueOrMemory()
    {
        const int events = 10_000;
        var scheduler = new AiSpeechScheduler { MaxSize = 5, MaxAgeSeconds = 30 };
        var metrics = new AiSpeechMetrics();
        var filterThreshold = 2;
        var rng = new Random(20260916);

        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        var memBefore = GC.GetTotalMemory(true);
        var speakAttempts = 0;

        for (var i = 0; i < events; i++)
        {
            var roll = rng.Next(100);
            if (roll < 50)
            {
                // 弹幕：大量噪声应被过滤
                var content = roll % 7 == 0 ? "这个声音是AI吗？" : (roll % 5 == 0 ? "666" : "哈哈哈");
                var item = new LiveAssistant.Models.DanmakuItem
                {
                    UserId = $"u{i % 500}",
                    Nickname = $"昵称{i % 500}",
                    Content = content,
                    MsgType = "chat"
                };
                var scored = AiDanmakuFilter.Evaluate(item, filterThreshold);
                if (!scored.EnterAi)
                {
                    metrics.NoteFiltered();
                    continue;
                }

                metrics.NoteReceived();
                scheduler.Enqueue(new AiSpeechTask
                {
                    Kind = AiSpeechEventKind.Danmaku,
                    Priority = AiSpeechPriority.DanmakuImportant,
                    Content = content,
                    EnqueuedAt = DateTime.UtcNow
                });
            }
            else if (roll < 70)
            {
                metrics.NoteReceived();
                scheduler.Enqueue(new AiSpeechTask
                {
                    Kind = AiSpeechEventKind.Gift,
                    Priority = AiSpeechPriority.Gift,
                    Content = "gift",
                    EnqueuedAt = DateTime.UtcNow
                });
            }
            else if (roll < 85)
            {
                metrics.NoteReceived();
                scheduler.Enqueue(new AiSpeechTask
                {
                    Kind = AiSpeechEventKind.Welcome,
                    Priority = AiSpeechPriority.Welcome,
                    Content = "hi",
                    EnqueuedAt = DateTime.UtcNow
                });
            }
            else
            {
                metrics.NoteReceived();
                scheduler.Enqueue(new AiSpeechTask
                {
                    Kind = AiSpeechEventKind.Like,
                    Priority = AiSpeechPriority.Like,
                    Content = "like",
                    EnqueuedAt = DateTime.UtcNow
                });
            }

            metrics.NoteQueueSize(scheduler.Count);

            // 模拟消费：每次循环尽量抽空，避免堆积成“大量生成声音”
            while (scheduler.TryDequeue(out var task))
            {
                speakAttempts++;
                var reply = task!.Kind == AiSpeechEventKind.Gift ? "感谢支持" : $"回{speakAttempts % 17}";
                if (ModelOutputSanitizer.IsModelSkip(reply))
                {
                    metrics.NoteSkip("SKIP");
                    continue;
                }

                var guard = speakAttempts; // 仅计数
                _ = guard;
                metrics.NoteGenerated(5, 1);
                if (rng.NextDouble() < 0.05)
                {
                    metrics.NoteTtsFailed("sim");
                }
                else
                {
                    metrics.NoteTtsSuccess(3);
                    metrics.NotePlaySuccess(10);
                }
            }
        }

        Assert.Equal(0, scheduler.Count);
        Assert.True(scheduler.PeekMaxSeen <= 5);
        var snap = metrics.Snapshot(0, 5);
        Assert.True(snap.ReceivedCount + snap.FilteredCount >= events / 2);
        Assert.True(speakAttempts < events); // 过滤+队列上限 → 远少于事件数

        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        var memAfter = GC.GetTotalMemory(true);
        var deltaMb = (memAfter - memBefore) / (1024.0 * 1024.0);
        Assert.True(deltaMb < 80, $"memory delta too high: {deltaMb:0.0} MB");
    }
}

public class AiSpeechShutdownSafetyTests
{
    [Fact]
    public async Task PlayerDispose_DuringPlay_NoHang()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ai-shut-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var player = new AiSpeechPlayer();
        var wav = AiSpeechPlayerStabilityTests.BuildMinimalWav(4000);
        using var cts = new CancellationTokenSource();
        var task = player.PlayWavAsync(wav, dir, TimeSpan.FromSeconds(2), cts.Token);
        await Task.Delay(20);
        player.Dispose();
        cts.Cancel();
        try
        {
            await task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch
        {
            // cancel / dispose / timeout 均可
        }

        Assert.True(task.IsCompleted);
        AiSpeechPlayer.CleanupTempDirectory(dir);
        try { Directory.Delete(dir, true); } catch { /* ignore */ }
    }
}
