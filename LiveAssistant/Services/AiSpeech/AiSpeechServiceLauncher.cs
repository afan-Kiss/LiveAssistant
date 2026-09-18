using System.Diagnostics;

namespace LiveAssistant.Services.AiSpeech;

/// <summary>
/// 用户主动「一键启动」时拉起缺失的 AI 依赖。
/// 不在软件启动时偷偷启动；仅按钮/显式调用触发。
/// </summary>
public sealed class AiSpeechServiceLauncher
{
    private readonly Action<string>? _log;
    private static readonly object StartGate = new();
    private static DateTime _lastOllamaStartUtc = DateTime.MinValue;
    private static DateTime _lastTtsStartUtc = DateTime.MinValue;

    public AiSpeechServiceLauncher(Action<string>? log = null)
    {
        _log = log;
    }

    public async Task<AiSpeechLaunchResult> StartMissingAsync(
        AiSpeechHealthReport current,
        string? ollamaExePath,
        string? ttsStartScriptPath,
        string? ttsWorkingDirectory,
        string? condaActivateBat,
        string? condaEnvName,
        CancellationToken ct = default)
    {
        var result = new AiSpeechLaunchResult();
        var steps = new List<string>();

        try
        {
            if (!current.OllamaAvailable)
            {
                var ollama = TryStartOllama(ollamaExePath);
                result.OllamaStarted = ollama.Started;
                result.OllamaPath = ollama.Path;
                result.OllamaError = ollama.Error;
                steps.Add(ollama.Started
                    ? $"已启动 Ollama：{ollama.Path}"
                    : $"Ollama 启动失败：{ollama.Error}");
                _log?.Invoke(
                    $"AI_LAUNCH ollama started={(ollama.Started ? 1 : 0)} path={ollama.Path ?? "-"} err={ollama.Error ?? "-"}");
            }
            else
            {
                steps.Add("Ollama 已在运行，跳过");
                result.OllamaAlreadyRunning = true;
            }

            if (!(current.TtsAvailable && current.TtsReady))
            {
                var tts = TryStartTts(ttsStartScriptPath, ttsWorkingDirectory, condaActivateBat, condaEnvName);
                result.TtsStarted = tts.Started;
                result.TtsPath = tts.Path;
                result.TtsError = tts.Error;
                steps.Add(tts.Started
                    ? $"已启动 GPT-SoVITS：{tts.Path}"
                    : $"GPT-SoVITS 启动失败：{tts.Error}");
                _log?.Invoke(
                    $"AI_LAUNCH tts started={(tts.Started ? 1 : 0)} path={tts.Path ?? "-"} err={tts.Error ?? "-"}");
            }
            else
            {
                steps.Add("GPT-SoVITS 已在运行，跳过");
                result.TtsAlreadyRunning = true;
            }

            if (current.OllamaAvailable && !current.ModelAvailable)
            {
                steps.Add($"模型未安装：{current.ModelConfigured}（请手动 ollama pull，本按钮不自动下载大模型）");
                result.ModelMissingHint = current.ModelConfigured;
            }

            // 给进程一点启动时间
            if (result.OllamaStarted || result.TtsStarted)
            {
                await Task.Delay(TimeSpan.FromSeconds(2), ct);
            }

            var ollamaOk = current.OllamaAvailable || result.OllamaStarted || result.OllamaAlreadyRunning;
            var ttsOk = (current.TtsAvailable && current.TtsReady)
                        || result.TtsStarted
                        || result.TtsAlreadyRunning;
            result.Success = ollamaOk && ttsOk;
            if (!string.IsNullOrWhiteSpace(result.ModelMissingHint))
            {
                steps.Add("服务已尝试启动，但配置模型仍需安装");
            }

            result.Message = string.Join("；", steps);
            return result;
        }
        catch (OperationCanceledException)
        {
            result.Success = false;
            result.Message = "启动已取消";
            return result;
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.Message = $"一键启动异常：{ex.Message}";
            _log?.Invoke($"AI_LAUNCH error={ex.GetType().Name}: {ex.Message}");
            return result;
        }
    }

    public static string? ResolveOllamaExe(string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured.Trim()))
        {
            return Path.GetFullPath(configured.Trim());
        }

        foreach (var c in CandidateOllamaExes())
        {
            if (File.Exists(c))
            {
                return c;
            }
        }

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "where.exe",
                Arguments = "ollama",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            };
            using var p = Process.Start(psi);
            if (p != null)
            {
                var output = p.StandardOutput.ReadToEnd();
                p.WaitForExit(3000);
                var line = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                    .FirstOrDefault(x => x.EndsWith("ollama.exe", StringComparison.OrdinalIgnoreCase));
                if (!string.IsNullOrWhiteSpace(line) && File.Exists(line.Trim()))
                {
                    return line.Trim();
                }
            }
        }
        catch
        {
            // ignore
        }

        return null;
    }

    public static string? ResolveTtsStartScript(string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured.Trim()))
        {
            return Path.GetFullPath(configured.Trim());
        }

        foreach (var c in CandidateTtsScripts())
        {
            if (File.Exists(c))
            {
                return c;
            }
        }

        return null;
    }

    private (bool Started, string? Path, string? Error) TryStartOllama(string? configured)
    {
        lock (StartGate)
        {
            if (DateTime.UtcNow - _lastOllamaStartUtc < TimeSpan.FromSeconds(15))
            {
                return (false, null, "刚尝试启动过 Ollama，请稍候");
            }

            var exe = ResolveOllamaExe(configured);
            if (exe == null)
            {
                return (false, null, "未找到 ollama.exe（可在配置中填写 OllamaExePath）");
            }

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = exe,
                    Arguments = "serve",
                    WorkingDirectory = Path.GetDirectoryName(exe) ?? "",
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                Process.Start(psi);
                _lastOllamaStartUtc = DateTime.UtcNow;
                return (true, exe, null);
            }
            catch (Exception ex)
            {
                return (false, exe, ex.Message);
            }
        }
    }

    private (bool Started, string? Path, string? Error) TryStartTts(
        string? configuredScript,
        string? workingDirectory,
        string? condaActivateBat,
        string? condaEnvName)
    {
        lock (StartGate)
        {
            if (DateTime.UtcNow - _lastTtsStartUtc < TimeSpan.FromSeconds(20))
            {
                return (false, null, "刚尝试启动过 GPT-SoVITS，请稍候（模型加载较慢）");
            }

            // 优先：静默直接起 python service（避免 bat 里的 pause）
            var workDir = ResolveTtsWorkDir(workingDirectory, configuredScript);
            var activate = ResolveCondaActivate(condaActivateBat);
            var envName = string.IsNullOrWhiteSpace(condaEnvName) ? "GPTSoVits" : condaEnvName.Trim();

            if (!string.IsNullOrWhiteSpace(workDir)
                && File.Exists(Path.Combine(workDir, "service", "main.py"))
                && !string.IsNullOrWhiteSpace(activate)
                && File.Exists(activate))
            {
                try
                {
                    Directory.CreateDirectory(Path.Combine(workDir, "logs"));
                    var logFile = Path.Combine(workDir, "logs", "startup_console.log");
                    var args =
                        $"/c cd /d \"{workDir}\" && call \"{activate}\" {envName} && set TQDM_DISABLE=1 && python service\\main.py >> \"{logFile}\" 2>&1";

                    var psi = new ProcessStartInfo
                    {
                        FileName = "cmd.exe",
                        Arguments = args,
                        WorkingDirectory = workDir,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };
                    Process.Start(psi);
                    _lastTtsStartUtc = DateTime.UtcNow;
                    return (true, Path.Combine(workDir, "service", "main.py"), null);
                }
                catch (Exception ex)
                {
                    return (false, workDir, ex.Message);
                }
            }

            var script = ResolveTtsStartScript(configuredScript);
            if (script == null)
            {
                return (false, null,
                    "未找到 GPT-SoVITS 启动脚本（默认 E:\\AI\\GPT-SoVITS\\启动-GPT-SoVITS服务.bat）");
            }

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = $"/c \"{script}\"",
                    WorkingDirectory = Path.GetDirectoryName(script) ?? "",
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                Process.Start(psi);
                _lastTtsStartUtc = DateTime.UtcNow;
                return (true, script, null);
            }
            catch (Exception ex)
            {
                return (false, script, ex.Message);
            }
        }
    }

    private static string? ResolveTtsWorkDir(string? configured, string? scriptPath)
    {
        if (!string.IsNullOrWhiteSpace(configured) && Directory.Exists(configured.Trim()))
        {
            return Path.GetFullPath(configured.Trim());
        }

        foreach (var d in new[]
                 {
                     @"E:\AI\GPT-SoVITS",
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "GPT-SoVITS")
                 })
        {
            if (Directory.Exists(d) && File.Exists(Path.Combine(d, "service", "main.py")))
            {
                return d;
            }
        }

        var script = ResolveTtsStartScript(scriptPath);
        if (script != null)
        {
            var dir = Path.GetDirectoryName(script);
            if (!string.IsNullOrWhiteSpace(dir)
                && File.Exists(Path.Combine(dir, "service", "main.py")))
            {
                return dir;
            }
        }

        return null;
    }

    private static string? ResolveCondaActivate(string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured.Trim()))
        {
            return Path.GetFullPath(configured.Trim());
        }

        foreach (var p in new[]
                 {
                     @"E:\AI\miniconda3\Scripts\activate.bat",
                     @"C:\ProgramData\miniconda3\Scripts\activate.bat",
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                         "miniconda3", "Scripts", "activate.bat"),
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                         "anaconda3", "Scripts", "activate.bat")
                 })
        {
            if (File.Exists(p))
            {
                return p;
            }
        }

        return null;
    }

    private static IEnumerable<string> CandidateOllamaExes()
    {
        yield return @"E:\TelegramAISpeaker\ollama\ollama.exe";
        yield return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs", "Ollama", "ollama.exe");
        yield return @"C:\Program Files\Ollama\ollama.exe";
        yield return @"E:\AI\ollama\ollama.exe";
    }

    private static IEnumerable<string> CandidateTtsScripts()
    {
        yield return @"E:\AI\GPT-SoVITS\启动-GPT-SoVITS服务.bat";
        yield return @"E:\AI\GPT-SoVITS\app\start-api.bat";
    }
}

public sealed class AiSpeechLaunchResult
{
    public bool Success { get; set; }
    public string Message { get; set; } = "";
    public bool OllamaStarted { get; set; }
    public bool OllamaAlreadyRunning { get; set; }
    public string? OllamaPath { get; set; }
    public string? OllamaError { get; set; }
    public bool TtsStarted { get; set; }
    public bool TtsAlreadyRunning { get; set; }
    public string? TtsPath { get; set; }
    public string? TtsError { get; set; }
    public string? ModelMissingHint { get; set; }
}
