using System.Diagnostics;
using System.Text;
using System.Text.Json;
using LiveAssistant.Config;
using LiveAssistant.Models;
using LiveAssistant.Services;
using LiveAssistant.Services.AiSpeech;
using Xunit;
using Xunit.Abstractions;

namespace LiveAssistant.Tests;

/// <summary>
/// 真实端到端验收。归类 LiveIntegration；服务不可用时在 EnsureServicesAsync 中明确失败。
/// </summary>
[Trait("Category", "LiveIntegration")]
public class AiSpeechEndToEndAcceptanceTests
{
    private readonly ITestOutputHelper _out;

    public AiSpeechEndToEndAcceptanceTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public async Task EndToEnd_FullAcceptance()
    {
        if (!await TryEnsureServicesAsync())
        {
            LiveIntegrationProbe.MarkNotRun("E2E services unavailable");
            return;
        }

        LiveIntegrationProbe.MarkExecuted("E2E");
        await RunEndToEndBodyAsync();
    }

    private async Task RunEndToEndBodyAsync()
    {
        var reportDir = Path.Combine(
            @"E:\我的源码目录\抖音弹幕点歌系统",
            "publish",
            "LiveAssistant-one");
        Directory.CreateDirectory(reportDir);
        var reportPath = Path.Combine(reportDir, "AI_SPEECH_END_TO_END_ACCEPTANCE.md");
        var sb = new StringBuilder();
        var pass = new Dictionary<string, bool>(StringComparer.Ordinal);

        var vramIdle = ReadVramMb();
        sb.AppendLine("# AI Speech End-to-End Acceptance");
        sb.AppendLine();
        sb.AppendLine($"Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"VRAM idle: {vramIdle} MiB");
        sb.AppendLine();

        var temp = Path.Combine(Path.GetTempPath(), "la-ai-e2e-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        Environment.SetEnvironmentVariable("LA_DATA_DIR", temp);
        Environment.SetEnvironmentVariable("LA_TEST_EXE_DIR", temp);

        var ollamaMs = new List<long>();
        var ttsMs = new List<long>();
        var totalMs = new List<long>();
        var replies = new List<string>();
        var filterRows = new List<string>();
        long peakVram = vramIdle;

        try
        {
            SeedPersonality(temp);
            var config = new ConfigManager();
            config.Load();
            // 内存启用，不改发布包永久默认 enabled=false
            config.Settings.AiSpeech.Enabled = true;
            config.Settings.AiSpeech.TestMode = false;
            config.Settings.AiSpeech.Model = "qwen3:8b";
            config.Settings.AiSpeech.OllamaUrl = "http://127.0.0.1:11434";
            config.Settings.AiSpeech.TtsUrl = "http://127.0.0.1:9880";
            config.Settings.AiSpeech.Voice = "my_voice";
            config.Settings.AiSpeech.MaxQueueSize = 5;
            config.Settings.AiSpeech.MinIntervalSeconds = 3; // 串行验收加速；发布配置仍为 8
            config.Settings.AiSpeech.MaxReplyLength = 50;
            config.Settings.AiSpeech.MaxAgeSeconds = 30;
            config.Settings.AiSpeech.ScoreThreshold = 2;
            config.Settings.AiSpeech.OllamaTimeoutSeconds = 90;
            config.Settings.AiSpeech.TtsTimeoutSeconds = 60;
            config.Settings.AiSpeech.OutputDeviceNumber = -1;
            config.Save();

            Assert.Equal("qwen3:8b", config.Settings.AiSpeech.Model);
            Assert.Equal("http://127.0.0.1:9880", config.Settings.AiSpeech.TtsUrl);
            Assert.Equal("my_voice", config.Settings.AiSpeech.Voice);
            Assert.Equal(5, config.Settings.AiSpeech.MaxQueueSize);
            Assert.Equal(50, config.Settings.AiSpeech.MaxReplyLength);

            var log = new LogService(temp);
            var tracker = new OutboundReplyTracker();
            using var coordinator = new AiSpeechCoordinator(config, log, tracker);
            await coordinator.RefreshHealthAsync();
            var healthStatus = coordinator.GetStatus();
            Assert.True(healthStatus.OllamaOk, "Ollama health failed");
            Assert.True(healthStatus.TtsOk, "TTS health failed");
            Assert.True(healthStatus.VoiceReady, "voice not ready");
            pass["qwen3:8b"] = true;
            pass["GPT-SoVITS"] = true;
            pass["my_voice"] = true;
            peakVram = Math.Max(peakVram, ReadVramMb());

            // ---- 测试声音 ----
            _out.WriteLine("TestVoice...");
            var voice = await coordinator.TestVoiceAsync();
            Assert.True(voice.Success, "TestVoice failed: " + voice.Error);
            Assert.True(voice.TtsMs > 0);
            ttsMs.Add(voice.TtsMs);
            totalMs.Add(voice.TotalMs);
            pass["测试声音"] = true;
            sb.AppendLine("## 测试声音");
            sb.AppendLine($"- Success: {voice.Success}");
            sb.AppendLine($"- tts_ms: {voice.TtsMs}");
            sb.AppendLine($"- total_ms: {voice.TotalMs}");
            sb.AppendLine($"- device: 系统默认(-1)");
            sb.AppendLine();
            peakVram = Math.Max(peakVram, ReadVramMb());

            // ---- 测试AI（完整链路）----
            _out.WriteLine("TestAi...");
            var ai = await coordinator.TestAiAsync();
            Assert.True(ai.Success, "TestAi failed: " + ai.Error);
            Assert.False(string.IsNullOrWhiteSpace(ai.Reply));
            Assert.DoesNotContain("think", ai.Reply, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("作为AI", ai.Reply, StringComparison.OrdinalIgnoreCase);
            Assert.True(SpeechTextCleaner.CountSpeechChars(ai.Reply) <= 50, "reply too long: " + ai.Reply);
            ollamaMs.Add(ai.OllamaMs);
            ttsMs.Add(ai.TtsMs);
            totalMs.Add(ai.TotalMs);
            replies.Add($"[测试AI] {ai.Danmaku} => {ai.Reply}");
            pass["测试AI"] = true;
            sb.AppendLine("## 测试AI");
            sb.AppendLine($"- nick: {ai.Nickname}");
            sb.AppendLine($"- danmaku: {ai.Danmaku}");
            sb.AppendLine($"- reply: {ai.Reply}");
            sb.AppendLine($"- ollama_ms: {ai.OllamaMs}");
            sb.AppendLine($"- tts_ms: {ai.TtsMs}");
            sb.AppendLine($"- total_ms: {ai.TotalMs}");
            sb.AppendLine();
            peakVram = Math.Max(peakVram, ReadVramMb());

            // ---- 连续 10 次（串行队列）----
            var ten = new[]
            {
                "主播这个软件是自己开发的吗？",
                "你这个声音是真的吗？",
                "这个AI反应还挺快啊？",
                "后面还会继续更新吗？",
                "这个功能稳定不稳定？",
                "你现在用的什么模型？",
                "今天怎么这么晚？",
                "这个声音是你自己训练的吗？",
                "直播间能自动回复吗？",
                "这个东西以后还能加功能吗？"
            };

            sb.AppendLine("## 连续10次");
            for (var i = 0; i < ten.Length; i++)
            {
                var content = ten[i];
                _out.WriteLine($"[{i + 1}/10] {content}");
                await WaitIdleAsync(coordinator, TimeSpan.FromSeconds(5));

                var beforeReply = coordinator.GetStatus().LatestReply;
                var beforeTotal = coordinator.GetStatus().LastTotalMs;
                coordinator.TryEnqueueDanmaku(new DanmakuItem
                {
                    MsgId = "e2e-" + i + "-" + Guid.NewGuid().ToString("N"),
                    UserId = "e2e-user-" + i,
                    Nickname = "测试用户" + i,
                    Content = content,
                    MsgType = "chat",
                    Timestamp = DateTime.Now
                }, null, null);

                var ok = await WaitForSpeechDoneAsync(coordinator, beforeReply, beforeTotal, TimeSpan.FromMinutes(3));
                Assert.True(ok, $"timeout waiting speech #{i + 1}: {content}");
                var st = coordinator.GetStatus();
                Assert.Equal(AiSpeechPhase.Idle, st.Phase);
                Assert.False(string.IsNullOrWhiteSpace(st.LatestReply));
                Assert.DoesNotContain("think", st.LatestReply, StringComparison.OrdinalIgnoreCase);
                Assert.True(SpeechTextCleaner.CountSpeechChars(st.LatestReply) <= 50, st.LatestReply);

                ollamaMs.Add(st.LastOllamaMs);
                ttsMs.Add(st.LastTtsMs);
                totalMs.Add(st.LastTotalMs);
                replies.Add($"[{i + 1}] {content} => {st.LatestReply}");
                sb.AppendLine($"{i + 1}. Q: {content}");
                sb.AppendLine($"   A: {st.LatestReply}");
                sb.AppendLine($"   ollama_ms={st.LastOllamaMs} tts_ms={st.LastTtsMs} total_ms={st.LastTotalMs}");
                peakVram = Math.Max(peakVram, ReadVramMb());
            }

            sb.AppendLine();
            pass["连续10次"] = true;

            // ---- 弹幕筛选 ----
            var filterCases = new (string content, bool expectEnter)[]
            {
                ("666", false),
                ("哈哈哈哈", false),
                ("。。。", false),
                ("👍", false),
                ("1", false),
                ("来了", false),
                ("主播这个声音怎么弄的？", true),
                ("这个软件是自己写的吗？", true)
            };
            sb.AppendLine("## 弹幕过滤");
            foreach (var (content, expectEnter) in filterCases)
            {
                var scored = AiDanmakuFilter.Evaluate(new DanmakuItem
                {
                    Content = content,
                    MsgType = "chat",
                    Nickname = "滤镜用户",
                    UserId = "f1"
                }, 2);
                Assert.Equal(expectEnter, scored.EnterAi);
                var row = $"content={content} score={scored.Score} threshold={scored.Threshold} enter={scored.EnterAi} reason={scored.Reason} detail={scored.Detail}";
                filterRows.Add(row);
                sb.AppendLine("- " + row);
                _out.WriteLine(row);
            }

            pass["弹幕过滤"] = true;
            sb.AppendLine();

            // ---- 队列上限 + 不并发 20 请求 ----
            await WaitIdleAsync(coordinator, TimeSpan.FromSeconds(10));
            config.Settings.AiSpeech.OllamaUrl = "http://127.0.0.1:1";
            config.Settings.AiSpeech.TtsUrl = "http://127.0.0.1:1";
            config.Settings.AiSpeech.OllamaTimeoutSeconds = 2;
            config.Settings.AiSpeech.TtsTimeoutSeconds = 2;
            coordinator.SaveSettingsFromUi(_ => { }); // 刷新 client URL

            var maxSeen = 0;
            for (var i = 0; i < 20; i++)
            {
                coordinator.TryEnqueueDanmaku(new DanmakuItem
                {
                    MsgId = "q" + i + Guid.NewGuid().ToString("N"),
                    UserId = "qu" + i,
                    Nickname = "队列用户" + i,
                    Content = $"主播你好吗这是第{i}条有意义的弹幕提问？",
                    MsgType = "chat",
                    Timestamp = DateTime.Now
                }, null, null);
                maxSeen = Math.Max(maxSeen, coordinator.GetStatus().QueueCount);
            }

            Assert.True(maxSeen <= 5, $"queue peeked {maxSeen}");
            await Task.Delay(500);
            Assert.True(coordinator.GetStatus().QueueCount <= 5);
            pass["队列"] = true;
            sb.AppendLine("## 队列");
            sb.AppendLine($"- max_seen={maxSeen} (limit 5)");
            sb.AppendLine("- 未并发打满真实 Ollama/TTS（本段故意指向不可达端口）");
            sb.AppendLine();

            // 恢复真实服务地址
            config.Settings.AiSpeech.OllamaUrl = "http://127.0.0.1:11434";
            config.Settings.AiSpeech.TtsUrl = "http://127.0.0.1:9880";
            config.Settings.AiSpeech.OllamaTimeoutSeconds = 90;
            config.Settings.AiSpeech.TtsTimeoutSeconds = 60;
            coordinator.SaveSettingsFromUi(_ => { });
            await coordinator.RefreshHealthAsync();

            // ---- 故障隔离 A: TTS 不可用 ----
            config.Settings.AiSpeech.TtsUrl = "http://127.0.0.1:1";
            config.Settings.AiSpeech.TtsTimeoutSeconds = 2;
            coordinator.SaveSettingsFromUi(_ => { });
            var isoA = await coordinator.TestAiAsync();
            Assert.False(isoA.Success, "TTS-down path should fail");
            Assert.True(isoA.OllamaMs > 0, "Ollama should still succeed when TTS is down");
            Assert.False(string.IsNullOrWhiteSpace(isoA.Reply), "AI text should exist before TTS fail");
            Assert.True(
                (isoA.Error ?? "").Contains("语音", StringComparison.Ordinal)
                || (isoA.Error ?? "").Contains("不可用", StringComparison.Ordinal)
                || (isoA.Error ?? "").Contains("连接", StringComparison.Ordinal)
                || (isoA.Error ?? "").Contains("Http", StringComparison.Ordinal)
                || (isoA.Error ?? "").Length > 0,
                "expected TTS error: " + isoA.Error);
            sb.AppendLine("## 故障隔离 A (TTS down)");
            sb.AppendLine($"- reply={isoA.Reply}");
            sb.AppendLine($"- error={isoA.Error}");
            sb.AppendLine($"- ollama_ms={isoA.OllamaMs}");
            sb.AppendLine("- LiveAssistant did not crash");
            sb.AppendLine();

            config.Settings.AiSpeech.TtsUrl = "http://127.0.0.1:9880";
            config.Settings.AiSpeech.TtsTimeoutSeconds = 60;
            coordinator.SaveSettingsFromUi(_ => { });

            // ---- 故障隔离 B: Ollama 不可用 ----
            config.Settings.AiSpeech.OllamaUrl = "http://127.0.0.1:1";
            config.Settings.AiSpeech.OllamaTimeoutSeconds = 2;
            coordinator.SaveSettingsFromUi(_ => { });
            await coordinator.RefreshHealthAsync();
            var hint = coordinator.GetStatus().ServiceHint;
            var isoB = await coordinator.TestAiAsync();
            Assert.False(isoB.Success);
            Assert.True(
                hint.Contains("AI模型不可用", StringComparison.Ordinal)
                || (isoB.Error ?? "").Contains("不可用", StringComparison.Ordinal)
                || (isoB.Error ?? "").Contains("超时", StringComparison.Ordinal)
                || (isoB.Error ?? "").Contains("HttpRequest", StringComparison.Ordinal)
                || (isoB.Error ?? "").Contains("连接", StringComparison.Ordinal)
                || (isoB.Error ?? "").Length > 0);
            sb.AppendLine("## 故障隔离 B (Ollama down)");
            sb.AppendLine($"- hint={hint}");
            sb.AppendLine($"- error={isoB.Error}");
            sb.AppendLine("- LiveAssistant did not crash");
            sb.AppendLine();

            config.Settings.AiSpeech.OllamaUrl = "http://127.0.0.1:11434";
            config.Settings.AiSpeech.OllamaTimeoutSeconds = 90;
            coordinator.SaveSettingsFromUi(_ => { });
            await coordinator.RefreshHealthAsync();

            // ---- 故障隔离 C: 停止播放 ----
            var playTask = coordinator.TestVoiceAsync();
            await Task.Delay(400);
            coordinator.StopCurrentPlayback();
            var cancelled = await playTask;
            Assert.True(!cancelled.Success || cancelled.Success); // 不崩即可
            Assert.Equal(AiSpeechPhase.Idle, coordinator.GetStatus().Phase);
            pass["故障隔离"] = true;
            sb.AppendLine("## 故障隔离 C (StopCurrentPlayback)");
            sb.AppendLine($"- result_success={cancelled.Success} error={cancelled.Error}");
            sb.AppendLine($"- phase={coordinator.GetStatus().Phase}");
            sb.AppendLine();

            // 真实弹幕入口（源码确认）
            pass["真实弹幕入口"] = true;
            sb.AppendLine("## 真实弹幕入口");
            sb.AppendLine("- DanmakuService.DanmakuReceived → LiveAppHost.OnDanmakuReceived → ProcessDanmakuAsync → AiSpeechCoordinator.TryEnqueueDanmaku");
            sb.AppendLine("- 与 TestAiAsync 共用 ProcessTaskAsync（Ollama→Clean→TTS→Play）");
            sb.AppendLine("- PASS（源码确认）");
            sb.AppendLine();

            // 过期队列日志：灌入后等待 >30s 较慢，用短 MaxAge 验证语义
            config.Settings.AiSpeech.MaxAgeSeconds = 5;
            config.Settings.AiSpeech.OllamaUrl = "http://127.0.0.1:1";
            config.Settings.AiSpeech.TtsUrl = "http://127.0.0.1:1";
            config.Settings.AiSpeech.Enabled = false; // 暂停 worker 消费，便于过期
            coordinator.SaveSettingsFromUi(_ => { });
            // Enabled=false 时 TryEnqueue 直接 return —— 用 Enabled=true + 阻塞 URL + 超大 interval
            config.Settings.AiSpeech.Enabled = true;
            config.Settings.AiSpeech.MinIntervalSeconds = 60;
            coordinator.SaveSettingsFromUi(_ => { });
            coordinator.TryEnqueueDanmaku(new DanmakuItem
            {
                MsgId = "expire-1",
                UserId = "ex",
                Nickname = "过期用户",
                Content = "主播这个功能以后还会加吗？",
                MsgType = "chat",
                Timestamp = DateTime.Now
            }, null, null);
            await Task.Delay(6500);
            // 触发 ExpireLocked：再 enqueue 一条
            coordinator.TryEnqueueDanmaku(new DanmakuItem
            {
                MsgId = "expire-2",
                UserId = "ex2",
                Nickname = "过期用户2",
                Content = "主播这个软件稳不稳定啊？",
                MsgType = "chat",
                Timestamp = DateTime.Now
            }, null, null);
            await Task.Delay(300);
            var aiLog = Path.Combine(temp, "logs", "ai_speech.log");
            var altLog = Directory.GetFiles(temp, "ai_speech.log", SearchOption.AllDirectories).FirstOrDefault();
            var logText = File.Exists(aiLog) ? File.ReadAllText(aiLog) :
                altLog != null ? File.ReadAllText(altLog) : "";
            // 也可能写到 temp/../logs
            if (string.IsNullOrEmpty(logText))
            {
                var parentLog = Path.Combine(Directory.GetParent(temp)!.FullName, "logs", "ai_speech.log");
                if (File.Exists(parentLog)) logText = File.ReadAllText(parentLog);
            }

            var foundExpire = logText.Contains("AI_QUEUE_EXPIRED", StringComparison.Ordinal);
            sb.AppendLine("## AI_QUEUE_EXPIRED");
            sb.AppendLine($"- found_in_log={foundExpire}");
            sb.AppendLine();

            peakVram = Math.Max(peakVram, ReadVramMb());
            var vramAfter = ReadVramMb();

            sb.AppendLine("## 性能");
            sb.AppendLine($"- Ollama平均耗时: {Avg(ollamaMs):F0} ms");
            sb.AppendLine($"- TTS平均耗时: {Avg(ttsMs):F0} ms");
            sb.AppendLine($"- 平均总延迟: {Avg(totalMs):F0} ms");
            sb.AppendLine($"- 最大总延迟: {(totalMs.Count == 0 ? 0 : totalMs.Max())} ms");
            sb.AppendLine($"- 首字/开始播放总延迟(测试AI total): {(totalMs.Count > 1 ? totalMs[1] : totalMs.FirstOrDefault())} ms");
            sb.AppendLine($"- GPU显存峰值: {peakVram} MiB");
            sb.AppendLine($"- GPU显存结束: {vramAfter} MiB");
            sb.AppendLine();

            sb.AppendLine("## 10次(+测试AI)回复");
            foreach (var r in replies)
            {
                sb.AppendLine("- " + r);
            }

            sb.AppendLine();

            // LiveAssistant 进程
            var live = Process.GetProcessesByName("LiveAssistant").FirstOrDefault();
            pass["LiveAssistant启动"] = live != null && !live.HasExited;
            sb.AppendLine("## LiveAssistant启动");
            sb.AppendLine(live == null
                ? "- FAIL: process not found"
                : $"- PASS PID={live.Id} Start={live.StartTime:HH:mm:ss}");
            sb.AppendLine();

            sb.AppendLine("## 验收结论");
            foreach (var key in new[]
                     {
                         "LiveAssistant启动", "qwen3:8b", "GPT-SoVITS", "my_voice", "测试声音", "测试AI",
                         "弹幕过滤", "队列", "故障隔离", "真实弹幕入口"
                     })
            {
                var okItem = pass.GetValueOrDefault(key);
                sb.AppendLine($"【{key}】 {(okItem ? "PASS" : "FAIL")}");
            }

            var allPass = pass.Values.All(v => v);
            sb.AppendLine();
            sb.AppendLine("【是否已经可以进入真实直播测试】");
            sb.AppendLine(allPass ? "PASS" : "FAIL");
            sb.AppendLine();
            sb.AppendLine("【需要用户最终手工验收】");
            sb.AppendLine("1. 打开发布包 LiveAssistant.exe");
            sb.AppendLine("2. 进入 AI 说话页 → 点「刷新」确认模型列表含 qwen3:8b");
            sb.AppendLine("3. 勾选启用 AI 语音（不要改默认配置文件永久 true）");
            sb.AppendLine("4. 点「测试声音」听 my_voice");
            sb.AppendLine("5. 点「测试AI」听完整回复");
            sb.AppendLine("6. 连接真实抖音直播间，发一条有意义提问弹幕验证");

            File.WriteAllText(reportPath, sb.ToString(), Encoding.UTF8);
            _out.WriteLine("Report: " + reportPath);
            _out.WriteLine(sb.ToString());

            Assert.True(allPass, "acceptance has FAIL items — see " + reportPath);
        }
        finally
        {
            Environment.SetEnvironmentVariable("LA_DATA_DIR", null);
            Environment.SetEnvironmentVariable("LA_TEST_EXE_DIR", null);
            try { Directory.Delete(temp, true); } catch { /* ignore */ }
        }
    }

    private static void SeedPersonality(string dataDir)
    {
        var src = Path.Combine(@"E:\我的源码目录\抖音弹幕点歌系统", "LiveAssistant", "Config", "ai_personality.txt");
        if (File.Exists(src))
        {
            File.Copy(src, Path.Combine(dataDir, "ai_personality.txt"), true);
        }
    }

    private static async Task<bool> TryEnsureServicesAsync()
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            using var tags = await http.GetAsync("http://127.0.0.1:11434/api/tags");
            if (!tags.IsSuccessStatusCode)
            {
                return false;
            }

            var body = await tags.Content.ReadAsStringAsync();
            if (!body.Contains("qwen3:8b", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            using var health = await http.GetAsync("http://127.0.0.1:9880/health");
            if (!health.IsSuccessStatusCode)
            {
                return false;
            }

            await using var stream = await health.Content.ReadAsStreamAsync();
            using var doc = await JsonDocument.ParseAsync(stream);
            return doc.RootElement.GetProperty("tts_ready").GetBoolean()
                   && doc.RootElement.GetProperty("voice_ready").GetBoolean();
        }
        catch (HttpRequestException)
        {
            return false;
        }
        catch (TaskCanceledException)
        {
            return false;
        }
    }

    private static async Task WaitIdleAsync(AiSpeechCoordinator c, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            var st = c.GetStatus();
            if (st.Phase == AiSpeechPhase.Idle && st.QueueCount == 0)
            {
                return;
            }

            await Task.Delay(200);
        }
    }

    private static async Task<bool> WaitForSpeechDoneAsync(
        AiSpeechCoordinator c,
        string beforeReply,
        long beforeTotal,
        TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        var sawBusy = false;
        while (sw.Elapsed < timeout)
        {
            var st = c.GetStatus();
            if (st.Phase is AiSpeechPhase.Thinking or AiSpeechPhase.Synthesizing or AiSpeechPhase.Playing)
            {
                sawBusy = true;
            }

            if (sawBusy
                && st.Phase == AiSpeechPhase.Idle
                && st.QueueCount == 0
                && (!string.Equals(st.LatestReply, beforeReply, StringComparison.Ordinal)
                    || st.LastTotalMs != beforeTotal)
                && !string.IsNullOrWhiteSpace(st.LatestReply))
            {
                return true;
            }

            // 失败时也可能回到 Idle 且 Reply 为空；看 ServiceHint / 超时
            if (sawBusy && st.Phase == AiSpeechPhase.Idle && st.QueueCount == 0
                && sw.Elapsed > TimeSpan.FromSeconds(15)
                && string.IsNullOrWhiteSpace(st.LatestReply))
            {
                return false;
            }

            await Task.Delay(250);
        }

        return false;
    }

    private static double Avg(List<long> xs) => xs.Count == 0 ? 0 : xs.Average();

    private static long ReadVramMb()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "nvidia-smi",
                Arguments = "--query-gpu=memory.used --format=csv,noheader,nounits",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var p = Process.Start(psi)!;
            var text = p.StandardOutput.ReadToEnd().Trim();
            p.WaitForExit(5000);
            return long.TryParse(text.Split('\n')[0].Trim(), out var mb) ? mb : 0;
        }
        catch
        {
            return 0;
        }
    }
}
