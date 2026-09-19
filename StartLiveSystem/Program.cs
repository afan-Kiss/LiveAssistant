using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace StartLiveSystem;

internal static class Program
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(3) };
    private static string _logPrefix = "LIVE_BOOT";
    private static string _baseDir = AppContext.BaseDirectory;

    private static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        try
        {
            var cfgPath = ResolveLauncherJsonPath(args);
            Boot($"loading config: {cfgPath}");
            var cfg = LoadConfig(cfgPath);
            _logPrefix = string.IsNullOrWhiteSpace(cfg.BootLogPrefix) ? "LIVE_BOOT" : cfg.BootLogPrefix.Trim();
            _baseDir = Path.GetDirectoryName(cfgPath) ?? AppContext.BaseDirectory;

            CheckEnvironment(cfg);
            ResolveAndPersistAbsolutePaths(cfg, cfgPath);

            if (!EnsureCdpComponent(cfg.DouyinCdp).GetAwaiter().GetResult())
            {
                return 2;
            }

            if (!EnsureComponent("kugou api", cfg.KugouApi, StartKugouAsync).GetAwaiter().GetResult())
            {
                return 3;
            }

            if (!EnsureComponent("maoyan overlay", cfg.Maoyan, StartMaoyanAsync).GetAwaiter().GetResult())
            {
                return 4;
            }

            if (!EnsureLiveAssistant(cfg.LiveAssistant))
            {
                return 5;
            }

            Boot("all components ready");
            return 0;
        }
        catch (Exception ex)
        {
            BootFail("launcher", ex.Message, "", -1);
            Console.Error.WriteLine(ex);
            return 1;
        }
    }

    private static async Task<bool> EnsureCdpComponent(ComponentConfig component)
    {
        if (!component.Enabled)
        {
            Boot("skip cdp (disabled)");
            return true;
        }

        Boot("checking cdp");
        var preferred = PreferCdpExe(component);
        Boot($"cdp preferred exe={preferred}");

        EnsureCdpBuiltIfStale(preferred);

        var (healthy, body) = await TryGetAsync(component.HealthUrl).ConfigureAwait(false);
        var version = ExtractJsonString(body, "version") ?? "";
        var canSend = ExtractJsonBool(body, "can_send");
        var loginOk = ExtractJsonBool(body, "login_ok");
        Boot($"cdp health healthy={healthy} version={version} login_ok={loginOk?.ToString() ?? "-"} can_send={canSend?.ToString() ?? "-"}");

        var minVersion = string.IsNullOrWhiteSpace(component.MinVersion) ? "1.0.0" : component.MinVersion.Trim();
        var versionOk = healthy && IsVersionAtLeast(version, minVersion);
        var fieldsOk = canSend.HasValue; // 旧 1.1.0 没有 can_send
        var processOk = IsProcessRunning(component.ProcessNames);

        if (healthy && versionOk && fieldsOk && processOk)
        {
            // 确认监听端口上的 exe 就是 preferred（或同目录最新）
            var listener = GetListenerExe(component.Port);
            if (!string.IsNullOrWhiteSpace(listener)
                && !string.IsNullOrWhiteSpace(preferred)
                && File.Exists(preferred)
                && !SameExePath(listener, preferred)
                && File.GetLastWriteTimeUtc(preferred) > File.GetLastWriteTimeUtc(listener).AddSeconds(2))
            {
                Boot($"cdp listener is stale path={listener}; replacing with {preferred}");
                StopKnownCdpOnPort(component);
            }
            else
            {
                Boot($"cdp already running version={version} listener={listener ?? "-"}");
                if (component.WaitLogin)
                {
                    await WaitForLoginAsync(component).ConfigureAwait(false);
                }
                return true;
            }
        }

        if (healthy && (!versionOk || !fieldsOk))
        {
            Boot($"cdp outdated or missing capability fields version={version}; replacing");
            StopKnownCdpOnPort(component);
            await Task.Delay(800).ConfigureAwait(false);
            if (IsPortOpen(component.Port))
            {
                BootFail("cdp", $"port {component.Port} still occupied after stop", preferred, -1);
                return false;
            }
        }
        else if (IsPortOpen(component.Port) && !healthy)
        {
            Boot($"cdp port {component.Port} occupied but unhealthy; trying known-process stop");
            StopKnownCdpOnPort(component);
            await Task.Delay(800).ConfigureAwait(false);
            if (IsPortOpen(component.Port))
            {
                BootFail("cdp", $"port {component.Port} occupied by unknown process", "", -1);
                return false;
            }
        }

        Boot("starting cdp");
        var (ok, path, exitCode) = await StartDouyinCdpAsync(component, "cdp").ConfigureAwait(false);
        if (!ok)
        {
            BootFail("cdp", "start failed", path, exitCode);
            return false;
        }

        Boot($"started cdp path={path}");
        if (!await WaitUntilCdpReadyAsync(component, component.WaitReadySeconds, minVersion).ConfigureAwait(false))
        {
            BootFail("cdp", $"health/version check failed within {component.WaitReadySeconds}s (need >={minVersion} with can_send)", path, -1);
            return false;
        }

        Boot("cdp health ok");
        if (component.WaitLogin)
        {
            await WaitForLoginAsync(component).ConfigureAwait(false);
        }

        return true;
    }

    private static async Task<bool> WaitUntilCdpReadyAsync(ComponentConfig c, int seconds, string minVersion)
    {
        var deadline = DateTime.UtcNow.AddSeconds(Math.Max(5, seconds));
        while (DateTime.UtcNow < deadline)
        {
            var (ok, body) = await TryGetAsync(c.HealthUrl).ConfigureAwait(false);
            var version = ExtractJsonString(body, "version") ?? "";
            var canSend = ExtractJsonBool(body, "can_send");
            if (ok && IsVersionAtLeast(version, minVersion) && canSend.HasValue)
            {
                Boot($"cdp ready version={version} can_send={canSend}");
                return true;
            }

            await Task.Delay(1000).ConfigureAwait(false);
        }

        return false;
    }

    private static string PreferCdpExe(ComponentConfig c)
    {
        var sourceBin = Path.Combine(ResolveSourceRoot(), "抖音cdp弹幕", "bin", "cdp-danmaku.exe");
        if (File.Exists(sourceBin))
        {
            return Path.GetFullPath(sourceBin);
        }

        var resolved = ResolveExe(c);
        return string.IsNullOrWhiteSpace(resolved) ? sourceBin : Path.GetFullPath(resolved);
    }

    private static void EnsureCdpBuiltIfStale(string exePath)
    {
        var sourceDir = Path.Combine(ResolveSourceRoot(), "抖音cdp弹幕");
        if (!Directory.Exists(sourceDir) || !File.Exists(Path.Combine(sourceDir, "go.mod")))
        {
            Boot("cdp source missing; skip auto-build");
            return;
        }

        var newestSource = Directory.EnumerateFiles(sourceDir, "*.go", SearchOption.AllDirectories)
            .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}.git{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .Select(File.GetLastWriteTimeUtc)
            .DefaultIfEmpty(DateTime.MinValue)
            .Max();

        var needBuild = !File.Exists(exePath) || File.GetLastWriteTimeUtc(exePath) < newestSource.AddSeconds(-2);
        if (!needBuild)
        {
            Boot($"cdp exe up-to-date path={exePath}");
            return;
        }

        var go = ResolveGoExe();
        if (string.IsNullOrWhiteSpace(go))
        {
            Boot("go not found; cannot auto-build cdp");
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(exePath)!);
        Boot($"building cdp source={sourceDir} out={exePath}");
        var psi = new ProcessStartInfo
        {
            FileName = go,
            Arguments = $"build -ldflags \"-H windowsgui\" -o \"{exePath}\" ./cmd/native",
            WorkingDirectory = sourceDir,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        using var p = Process.Start(psi);
        if (p == null)
        {
            Boot("cdp build process failed to start");
            return;
        }

        var stdout = p.StandardOutput.ReadToEnd();
        var stderr = p.StandardError.ReadToEnd();
        p.WaitForExit(180_000);
        if (p.ExitCode != 0)
        {
            Boot($"cdp build failed exit={p.ExitCode} err={Truncate(stderr, 300)}");
            return;
        }

        Boot($"cdp build ok size={(File.Exists(exePath) ? new FileInfo(exePath).Length : 0)} out={stdout.Trim()}");
        TryMirrorCdpToSidecars(exePath);
    }

    private static void TryMirrorCdpToSidecars(string exePath)
    {
        try
        {
            var targets = new[]
            {
                Path.Combine(_baseDir, "sidecars", "douyin-cdp", "cdp-danmaku.exe"),
                Path.Combine(_baseDir, "sidecars", "cdp-danmaku.exe"),
                Path.Combine(_baseDir, "publish", "LiveAssistant-one", "cdp-danmaku.exe")
            };
            foreach (var t in targets)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(t)!);
                File.Copy(exePath, t, overwrite: true);
            }

            Boot("cdp mirrored to sidecars/publish");
        }
        catch (Exception ex)
        {
            Boot($"cdp mirror skipped: {ex.Message}");
        }
    }

    private static void StopKnownCdpOnPort(ComponentConfig c)
    {
        var port = c.Port > 0 ? c.Port : 17891;
        try
        {
            foreach (var p in Process.GetProcesses())
            {
                try
                {
                    var name = p.ProcessName;
                    var known = c.ProcessNames.Any(n =>
                        string.Equals(name, n, StringComparison.OrdinalIgnoreCase)
                        || name.Contains(n, StringComparison.OrdinalIgnoreCase));
                    if (!known)
                    {
                        continue;
                    }

                    string? exe = null;
                    try { exe = p.MainModule?.FileName; } catch { /* ignore */ }
                    Boot($"stopping known cdp pid={p.Id} name={name} exe={exe ?? "-"}");
                    p.Kill(entireProcessTree: true);
                }
                catch
                {
                    // ignore
                }
                finally
                {
                    p.Dispose();
                }
            }
        }
        catch (Exception ex)
        {
            Boot($"stop cdp error: {ex.Message}");
        }

        // 二次确认：仅当 listener 名称仍属白名单才杀
        var listener = GetListenerExe(port);
        if (!string.IsNullOrWhiteSpace(listener))
        {
            var file = Path.GetFileNameWithoutExtension(listener);
            var known = c.ProcessNames.Any(n =>
                string.Equals(file, n, StringComparison.OrdinalIgnoreCase)
                || file.Contains(n, StringComparison.OrdinalIgnoreCase));
            if (known)
            {
                try
                {
                    foreach (var p in Process.GetProcessesByName(file))
                    {
                        try { p.Kill(entireProcessTree: true); } catch { /* ignore */ }
                        finally { p.Dispose(); }
                    }
                }
                catch { /* ignore */ }
            }
            else
            {
                Boot($"refuse kill unknown listener exe={listener}");
            }
        }
    }

    private static string? GetListenerExe(int port)
    {
        if (port <= 0)
        {
            return null;
        }

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = "/c netstat -ano -p tcp",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            using var p = Process.Start(psi);
            if (p == null)
            {
                return null;
            }

            var output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(8000);
            var needle = $":{port} ";
            foreach (var raw in output.Split('\n'))
            {
                var line = raw.Trim();
                if (!line.Contains(needle, StringComparison.Ordinal)
                    || !line.Contains("LISTENING", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 0 || !int.TryParse(parts[^1], out var pid) || pid <= 0)
                {
                    continue;
                }

                try
                {
                    using var proc = Process.GetProcessById(pid);
                    try
                    {
                        return proc.MainModule?.FileName;
                    }
                    catch
                    {
                        // MainModule may be denied; fall through
                    }
                }
                catch
                {
                    // ignore
                }
            }
        }
        catch
        {
            // ignore
        }

        return null;
    }

    private static bool SameExePath(string? a, string? b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b))
        {
            return false;
        }

        try
        {
            var fa = new FileInfo(a);
            var fb = new FileInfo(b);
            if (!fa.Exists || !fb.Exists)
            {
                return false;
            }

            if (string.Equals(fa.FullName, fb.FullName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            // 编码/短路径导致字符串不一致时，用大小+mtime 近似判断同一产物
            return fa.Length == fb.Length
                   && Math.Abs((fa.LastWriteTimeUtc - fb.LastWriteTimeUtc).TotalSeconds) < 3;
        }
        catch
        {
            return false;
        }
    }

    private static string? ResolveGoExe()
    {
        if (CommandExists("go"))
        {
            return "go";
        }

        var candidates = new[]
        {
            @"C:\Go\bin\go.exe",
            @"C:\Program Files\Go\bin\go.exe"
        };
        return candidates.FirstOrDefault(File.Exists);
    }

    private static bool IsVersionAtLeast(string actual, string minimum)
    {
        if (string.IsNullOrWhiteSpace(actual))
        {
            return false;
        }

        static int[] Parse(string v)
        {
            var parts = v.Split(['.', '-'], StringSplitOptions.RemoveEmptyEntries);
            var nums = new int[3];
            for (var i = 0; i < Math.Min(3, parts.Length); i++)
            {
                int.TryParse(new string(parts[i].TakeWhile(char.IsDigit).ToArray()), out nums[i]);
            }

            return nums;
        }

        var a = Parse(actual);
        var m = Parse(minimum);
        for (var i = 0; i < 3; i++)
        {
            if (a[i] != m[i])
            {
                return a[i] > m[i];
            }
        }

        return true;
    }

    private static string? ExtractJsonString(string? json, string field)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (TryGetPropertyRecursive(doc.RootElement, field, out var el)
                && el.ValueKind == JsonValueKind.String)
            {
                return el.GetString();
            }
        }
        catch
        {
            // ignore
        }

        return null;
    }

    private static bool? ExtractJsonBool(string? json, string field)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (TryGetPropertyRecursive(doc.RootElement, field, out var el)
                && (el.ValueKind == JsonValueKind.True || el.ValueKind == JsonValueKind.False))
            {
                return el.GetBoolean();
            }
        }
        catch
        {
            // ignore
        }

        return null;
    }

    private static bool TryGetPropertyRecursive(JsonElement el, string name, out JsonElement found)
    {
        if (el.ValueKind == JsonValueKind.Object)
        {
            if (el.TryGetProperty(name, out found))
            {
                return true;
            }

            foreach (var prop in el.EnumerateObject())
            {
                if (TryGetPropertyRecursive(prop.Value, name, out found))
                {
                    return true;
                }
            }
        }
        else if (el.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in el.EnumerateArray())
            {
                if (TryGetPropertyRecursive(item, name, out found))
                {
                    return true;
                }
            }
        }

        found = default;
        return false;
    }

    private static string Truncate(string? text, int max)
    {
        text ??= "";
        return text.Length <= max ? text : text[..max] + "…";
    }

    private static void ResolveAndPersistAbsolutePaths(LauncherConfig cfg, string cfgPath)
    {
        var sourceRoot = ResolveSourceRoot();
        cfg.SourceRoot = sourceRoot;

        var cdpSource = Path.Combine(sourceRoot, "抖音cdp弹幕");
        var cdpExe = PreferCdpExe(cfg.DouyinCdp);
        cfg.DouyinCdp.SourceDir = cdpSource;
        cfg.DouyinCdp.Exe = cdpExe;
        cfg.DouyinCdp.MinVersion = string.IsNullOrWhiteSpace(cfg.DouyinCdp.MinVersion) ? "1.0.0" : cfg.DouyinCdp.MinVersion;
        cfg.DouyinCdp.GitCommit = TryReadGitHead(cdpSource);
        if (File.Exists(cdpExe))
        {
            cfg.DouyinCdp.BuildTime = File.GetLastWriteTime(cdpExe).ToString("o");
        }

        var kugouPreferred = Path.Combine(sourceRoot, "酷狗协议", "KgDesktop", "build", "bin", "酷狗api_v1.5.exe");
        var kugouExe = "";
        if (File.Exists(kugouPreferred)
            && File.Exists(Path.Combine(Path.GetDirectoryName(kugouPreferred)!, "kgapijs", "app.js")))
        {
            kugouExe = kugouPreferred;
        }
        else
        {
            kugouExe = ResolveExe(cfg.KugouApi);
        }

        if (string.IsNullOrWhiteSpace(kugouExe))
        {
            var sidecarKg = Path.Combine(_baseDir, "sidecars", "酷狗api_v1.5.exe");
            if (File.Exists(sidecarKg))
            {
                kugouExe = sidecarKg;
            }
        }

        cfg.KugouApi.SourceDir = Path.Combine(sourceRoot, "酷狗协议");
        cfg.KugouApi.Exe = string.IsNullOrWhiteSpace(kugouExe) ? cfg.KugouApi.Exe : Path.GetFullPath(kugouExe);
        if (!string.IsNullOrWhiteSpace(cfg.KugouApi.Exe) && File.Exists(cfg.KugouApi.Exe))
        {
            cfg.KugouApi.BuildTime = File.GetLastWriteTime(cfg.KugouApi.Exe).ToString("o");
        }

        var maoyanExe = ResolveExe(cfg.Maoyan);
        if (string.IsNullOrWhiteSpace(maoyanExe))
        {
            maoyanExe = ResolveNewestMaoyanOverlayExe() ?? "";
        }

        cfg.Maoyan.SourceDir = Path.Combine(sourceRoot, "抖音直播24小时无人直播");
        cfg.Maoyan.Exe = string.IsNullOrWhiteSpace(maoyanExe) ? cfg.Maoyan.Exe : Path.GetFullPath(maoyanExe);
        cfg.Maoyan.StartBat = Path.Combine(cfg.Maoyan.SourceDir, "start.bat");
        cfg.Maoyan.DevWorkingDirectory = cfg.Maoyan.SourceDir;
        if (!string.IsNullOrWhiteSpace(cfg.Maoyan.Exe) && File.Exists(cfg.Maoyan.Exe))
        {
            cfg.Maoyan.BuildTime = File.GetLastWriteTime(cfg.Maoyan.Exe).ToString("o");
        }

        var laExe = ResolveExe(cfg.LiveAssistant);
        cfg.LiveAssistant.SourceDir = Path.Combine(sourceRoot, "抖音弹幕点歌系统");
        cfg.LiveAssistant.Exe = string.IsNullOrWhiteSpace(laExe) ? cfg.LiveAssistant.Exe : Path.GetFullPath(laExe);
        cfg.LiveAssistant.GitCommit = TryReadGitHead(cfg.LiveAssistant.SourceDir);
        cfg.LiveAssistant.DevWorkingDirectory = cfg.LiveAssistant.SourceDir;
        if (!string.IsNullOrWhiteSpace(cfg.LiveAssistant.Exe) && File.Exists(cfg.LiveAssistant.Exe))
        {
            cfg.LiveAssistant.BuildTime = File.GetLastWriteTime(cfg.LiveAssistant.Exe).ToString("o");
        }

        cfg.DouyinCdp.DevWorkingDirectory = cdpSource;
        cfg.DouyinCdp.CandidateExes = new List<string>
        {
            cdpExe,
            Path.Combine(_baseDir, "sidecars", "douyin-cdp", "cdp-danmaku.exe"),
            Path.Combine(_baseDir, "sidecars", "cdp-danmaku.exe"),
            Path.Combine(_baseDir, "publish", "LiveAssistant-one", "cdp-danmaku.exe")
        };

        try
        {
            var json = JsonSerializer.Serialize(cfg, new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = null,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
            });
            // 保持 DouyinCDP 字段名
            json = json.Replace("\"DouyinCdp\"", "\"DouyinCDP\"", StringComparison.Ordinal);
            File.WriteAllText(cfgPath, json);
            Boot($"launcher.json updated: {cfgPath}");
        }
        catch (Exception ex)
        {
            Boot($"launcher.json write skipped: {ex.Message}");
        }
    }

    private static string TryReadGitHead(string repoDir)
    {
        try
        {
            var head = Path.Combine(repoDir, ".git", "HEAD");
            if (!File.Exists(head))
            {
                return "";
            }

            var text = File.ReadAllText(head).Trim();
            if (text.StartsWith("ref:", StringComparison.Ordinal))
            {
                var refPath = Path.Combine(repoDir, ".git", text[5..].Trim().Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(refPath))
                {
                    return File.ReadAllText(refPath).Trim();
                }
            }

            return text;
        }
        catch
        {
            return "";
        }
    }

    private static async Task<bool> EnsureComponent(
        string label,
        ComponentConfig component,
        Func<ComponentConfig, string, Task<(bool ok, string path, int exitCode)>> starter,
        bool waitLogin = false)
    {
        if (!component.Enabled)
        {
            Boot($"skip {label} (disabled)");
            return true;
        }

        Boot($"checking {label}");

        var requireProcess = component.ProcessNames is { Count: > 0 };
        var processUp = requireProcess && IsProcessRunning(component.ProcessNames);
        var healthy = await IsHealthyAsync(component).ConfigureAwait(false);

        // 有明确进程名时（如 MaoyanOverlay），不能只凭端口健康就当成已启动，
        // 避免误复用无关软件（例如单独的猫眼 API）。
        if (healthy && (!requireProcess || processUp))
        {
            Boot($"{label} already running");
            if (waitLogin)
            {
                await WaitForLoginAsync(component).ConfigureAwait(false);
            }
            return true;
        }

        if (processUp && await WaitUntilHealthyAsync(component, Math.Min(30, component.WaitReadySeconds)).ConfigureAwait(false))
        {
            Boot($"{label} already running (process reuse)");
            if (waitLogin)
            {
                await WaitForLoginAsync(component).ConfigureAwait(false);
            }
            return true;
        }

        if (component.Port > 0
            && IsPortOpen(component.Port)
            && !healthy
            && !requireProcess)
        {
            BootFail(label, $"port {component.Port} occupied but health check failed", "", -1);
            return false;
        }

        if (requireProcess && healthy && !processUp)
        {
            Boot($"{label} port/health occupied by other process; still starting {string.Join(',', component.ProcessNames)}");
        }

        Boot($"starting {label}");
        var (ok, path, exitCode) = await starter(component, label).ConfigureAwait(false);
        if (!ok)
        {
            BootFail(label, "start failed", path, exitCode);
            return false;
        }

        Boot($"started {label} path={path}");
        if (!await WaitUntilReadyAsync(component, component.WaitReadySeconds).ConfigureAwait(false))
        {
            BootFail(label, $"health/process check failed within {component.WaitReadySeconds}s", path, -1);
            return false;
        }

        Boot($"{label} health ok");
        if (waitLogin)
        {
            await WaitForLoginAsync(component).ConfigureAwait(false);
        }

        return true;
    }

    private static bool EnsureLiveAssistant(ComponentConfig component)
    {
        if (!component.Enabled)
        {
            Boot("skip liveassistant (disabled)");
            return true;
        }

        Boot("checking liveassistant");
        if (IsProcessRunning(component.ProcessNames))
        {
            Boot("liveassistant already running");
            return true;
        }

        Boot("starting liveassistant");
        var mode = ResolveMode();
        string? path = null;
        ProcessStartInfo? psi = null;

        if (mode != "dev")
        {
            path = ResolveExe(component);
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
            {
                psi = new ProcessStartInfo
                {
                    FileName = path,
                    WorkingDirectory = Path.GetDirectoryName(path) ?? _baseDir,
                    UseShellExecute = true
                };
            }
        }

        if (psi == null)
        {
            var workDir = FirstExistingDir(component.DevWorkingDirectory, _baseDir);
            if (string.IsNullOrWhiteSpace(component.DevCommand))
            {
                BootFail("liveassistant", "no exe and no devCommand", workDir ?? "", -1);
                return false;
            }

            if (!CommandExists("dotnet"))
            {
                BootFail("liveassistant", ".NET SDK/runtime missing for dotnet run", workDir ?? "", -1);
                return false;
            }

            path = workDir;
            psi = new ProcessStartInfo
            {
                FileName = component.DevCommand,
                Arguments = component.DevArgs ?? "",
                WorkingDirectory = workDir!,
                UseShellExecute = true
            };
            Boot("using development mode for liveassistant");
        }

        try
        {
            Process.Start(psi);
            Boot($"started liveassistant path={path}");
            Thread.Sleep(1500);
            if (!IsProcessRunning(component.ProcessNames) && mode == "dev")
            {
                // dotnet run may take longer to spawn LiveAssistant.exe
                Thread.Sleep(3000);
            }
            return true;
        }
        catch (Exception ex)
        {
            BootFail("liveassistant", ex.Message, path ?? "", -1);
            return false;
        }
    }

    private static Task<(bool ok, string path, int exitCode)> StartDouyinCdpAsync(ComponentConfig c, string label)
    {
        var exe = PreferCdpExe(c);
        EnsureCdpBuiltIfStale(exe);
        if (!string.IsNullOrWhiteSpace(exe) && File.Exists(exe))
        {
            TryMirrorCdpToSidecars(exe);
            return Task.FromResult(StartDetached(exe, "", Path.GetDirectoryName(exe)!));
        }

        var workDir = FirstExistingDir(c.DevWorkingDirectory, Path.Combine(ResolveSourceRoot(), "抖音cdp弹幕"));
        var go = ResolveGoExe();
        if (!string.IsNullOrWhiteSpace(go) && Directory.Exists(workDir))
        {
            Boot("cdp exe missing after build attempt; falling back to go run ./cmd/native");
            return Task.FromResult(StartDetached(go, "run ./cmd/native", workDir));
        }

        BootFail(label, "cdp-danmaku.exe not found and cannot build", exe, -1);
        return Task.FromResult((false, exe, -1));
    }

    private static async Task<(bool ok, string path, int exitCode)> StartKugouAsync(ComponentConfig c, string label)
    {
        await Task.CompletedTask.ConfigureAwait(false);
        var exe = ResolveExe(c);
        if (string.IsNullOrWhiteSpace(exe) || !File.Exists(exe))
        {
            BootFail(label, "kugou exe not found", "", -1);
            return (false, "", -1);
        }

        if (!string.IsNullOrWhiteSpace(c.RequireSibling))
        {
            var sibling = Path.Combine(Path.GetDirectoryName(exe)!, c.RequireSibling);
            if (!Directory.Exists(sibling) || !File.Exists(Path.Combine(sibling, "app.js")))
            {
                BootFail(label, $"missing sibling folder {c.RequireSibling}\\app.js", exe, -1);
                return (false, exe, -1);
            }
        }

        return StartDetached(exe, "", Path.GetDirectoryName(exe)!);
    }

    private static async Task<(bool ok, string path, int exitCode)> StartMaoyanAsync(ComponentConfig c, string label)
    {
        await Task.CompletedTask.ConfigureAwait(false);

        // 正式版：抖音直播24小时无人直播 / MaoyanOverlay（含词云球的 Electron Overlay）
        var mode = ResolveMode();
        var exe = ResolveExe(c);
        if (string.IsNullOrWhiteSpace(exe))
        {
            exe = ResolveNewestMaoyanOverlayExe() ?? "";
        }

        if (mode != "dev" && !string.IsNullOrWhiteSpace(exe) && File.Exists(exe))
        {
            return StartDetached(exe, "", Path.GetDirectoryName(exe)!);
        }

        var workDir = FirstExistingDir(
            c.DevWorkingDirectory,
            Path.Combine(ResolveSourceRoot(), "抖音直播24小时无人直播"));

        if (mode == "dev" || string.IsNullOrWhiteSpace(exe) || !File.Exists(exe))
        {
            if (!CommandExists("node"))
            {
                BootFail(label, "Node.js missing (needed for MaoyanOverlay npm start)", workDir ?? "", -1);
                return (false, workDir ?? "", -1);
            }

            var bat = ResolveFirstExisting(c.StartBat, c.CandidateBats);
            if (!string.IsNullOrWhiteSpace(bat) && File.Exists(bat))
            {
                Boot("using start.bat for MaoyanOverlay");
                return StartDetached("cmd.exe", $"/c \"\"{bat}\"\"", Path.GetDirectoryName(bat)!);
            }

            if (Directory.Exists(workDir) && File.Exists(Path.Combine(workDir, "package.json")))
            {
                Boot("using development mode for MaoyanOverlay (npm start)");
                return StartDetached("npm", string.IsNullOrWhiteSpace(c.DevArgs) ? "start" : c.DevArgs!, workDir);
            }
        }

        if (!string.IsNullOrWhiteSpace(exe) && File.Exists(exe))
        {
            return StartDetached(exe, "", Path.GetDirectoryName(exe)!);
        }

        BootFail(label, "MaoyanOverlay.exe not found under 抖音直播24小时无人直播\\dist", "", -1);
        return (false, "", -1);
    }

    private static string? ResolveNewestMaoyanOverlayExe()
    {
        var dist = Path.Combine(ResolveSourceRoot(), "抖音直播24小时无人直播", "dist");
        if (!Directory.Exists(dist))
        {
            return null;
        }

        try
        {
            return Directory.EnumerateFiles(dist, "MaoyanOverlay*.exe")
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

    private static (bool ok, string path, int exitCode) StartDetached(string fileName, string args, string workDir)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = args,
                WorkingDirectory = workDir,
                UseShellExecute = true
            };
            var p = Process.Start(psi);
            return (p != null, Path.Combine(workDir, Path.GetFileName(fileName)), p?.Id ?? 0);
        }
        catch (Exception ex)
        {
            BootFail("process", ex.Message, fileName, -1);
            return (false, fileName, -1);
        }
    }

    private static async Task WaitForLoginAsync(ComponentConfig c)
    {
        if (string.IsNullOrWhiteSpace(c.HealthUrl))
        {
            return;
        }

        // 最多探测约 12 秒；未登录不退出启动器，继续拉起后续依赖。
        for (var i = 0; i < 6; i++)
        {
            var (ok, body) = await TryGetAsync(c.HealthUrl).ConfigureAwait(false);
            if (ok && HasLoginOk(body, c.LoginOkField))
            {
                Boot("cdp login_ok");
                return;
            }

            Boot("等待抖音扫码登录 (login_ok=false)，不退出，继续后续启动");
            await Task.Delay(2000).ConfigureAwait(false);
        }

        Boot("cdp not logged in yet; continue without exit");
    }

    private static bool HasLoginOk(string? json, string? field)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return false;
        }

        var key = string.IsNullOrWhiteSpace(field) ? "login_ok" : field;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (TryGetBool(doc.RootElement, key, out var v))
            {
                return v;
            }

            if (doc.RootElement.TryGetProperty("data", out var data) && TryGetBool(data, key, out v))
            {
                return v;
            }
        }
        catch
        {
            // ignore parse errors
        }

        return false;
    }

    private static bool TryGetBool(JsonElement el, string name, out bool value)
    {
        value = false;
        if (!el.TryGetProperty(name, out var p))
        {
            return false;
        }

        if (p.ValueKind == JsonValueKind.True)
        {
            value = true;
            return true;
        }

        if (p.ValueKind == JsonValueKind.False)
        {
            value = false;
            return true;
        }

        return false;
    }

    private static async Task<bool> WaitUntilReadyAsync(ComponentConfig c, int seconds)
    {
        var until = DateTime.UtcNow.AddSeconds(Math.Max(1, seconds));
        var requireProcess = c.ProcessNames is { Count: > 0 };
        while (DateTime.UtcNow < until)
        {
            var processOk = !requireProcess || IsProcessRunning(c.ProcessNames);
            var healthOk = await IsHealthyAsync(c).ConfigureAwait(false);
            if (processOk && healthOk)
            {
                return true;
            }

            // Overlay 窗口已起来、内置票房 API 稍慢时也算阶段性成功，继续等到 health
            if (processOk && requireProcess && string.Equals(
                    c.ProcessNames[0], "MaoyanOverlay", StringComparison.OrdinalIgnoreCase))
            {
                Boot("maoyan overlay window process up; waiting ticket api health");
            }

            await Task.Delay(1000).ConfigureAwait(false);
        }

        var finalProcess = !requireProcess || IsProcessRunning(c.ProcessNames);
        return finalProcess && await IsHealthyAsync(c).ConfigureAwait(false);
    }

    private static async Task<bool> WaitUntilHealthyAsync(ComponentConfig c, int seconds)
    {
        return await WaitUntilReadyAsync(c, seconds).ConfigureAwait(false);
    }

    private static async Task<bool> IsHealthyAsync(ComponentConfig c)
    {
        if (string.IsNullOrWhiteSpace(c.HealthUrl))
        {
            return c.Port > 0 && IsPortOpen(c.Port);
        }

        var (ok, _) = await TryGetAsync(c.HealthUrl).ConfigureAwait(false);
        return ok;
    }

    private static async Task<(bool ok, string? body)> TryGetAsync(string url)
    {
        try
        {
            using var resp = await Http.GetAsync(url).ConfigureAwait(false);
            var body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            return (resp.IsSuccessStatusCode, body);
        }
        catch
        {
            return (false, null);
        }
    }

    private static bool IsPortOpen(int port)
    {
        try
        {
            using var client = new TcpClient();
            var task = client.ConnectAsync("127.0.0.1", port);
            return task.Wait(400) && client.Connected;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsProcessRunning(IReadOnlyList<string>? names)
    {
        if (names == null || names.Count == 0)
        {
            return false;
        }

        Process[] procs;
        try
        {
            procs = Process.GetProcesses();
        }
        catch
        {
            return false;
        }

        foreach (var p in procs)
        {
            try
            {
                var name = p.ProcessName;
                foreach (var n in names)
                {
                    if (string.Equals(name, n, StringComparison.OrdinalIgnoreCase)
                        || name.Contains(n, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
            }
            catch
            {
                // ignore access denied
            }
            finally
            {
                p.Dispose();
            }
        }

        return false;
    }

    private static void CheckEnvironment(LauncherConfig cfg)
    {
        Boot("checking environment");
        var env = cfg.Environment;

        if (env.RequireDotnet)
        {
            var hasRuntime = Directory.Exists(@"C:\Program Files\dotnet\shared\Microsoft.WindowsDesktop.App")
                             || CommandExists("dotnet");
            if (!hasRuntime)
            {
                throw new InvalidOperationException(".NET 8 Windows Desktop runtime missing. Install .NET 8 Desktop Runtime.");
            }

            Boot("dotnet ok");
        }

        if (env.RequireNode)
        {
            if (!CommandExists("node"))
            {
                throw new InvalidOperationException("Node.js missing. Install Node LTS for maoyan.");
            }

            Boot($"node ok ({RunCapture("node", "-v")})");
        }

        if (env.RequireChrome)
        {
            var chrome = FindChrome();
            if (string.IsNullOrWhiteSpace(chrome))
            {
                throw new InvalidOperationException("Google Chrome not found. CDP and maoyan require Chrome.");
            }

            var ver = FileVersionInfo.GetVersionInfo(chrome).ProductVersion ?? "?";
            Boot($"chrome ok path={chrome} version={ver}");
            if (TryParseMajor(ver, out var major) && major < env.ChromeMinMajor)
            {
                Boot($"WARNING chrome {ver} is older than recommended major {env.ChromeMinMajor}; please upgrade Chrome manually");
            }
            else if (TryParseMajor(ver, out major) && major < 140)
            {
                Boot($"WARNING chrome {ver} is relatively old; consider upgrading to latest stable (do not auto-upgrade)");
            }
        }

        if (env.RequireGo && !CommandExists("go") && !File.Exists(@"C:\go\bin\go.exe"))
        {
            throw new InvalidOperationException("Go toolchain missing (required for CDP source builds).");
        }

        if (env.RequirePython && !CommandExists("python") && !CommandExists("py"))
        {
            throw new InvalidOperationException("Python missing.");
        }
    }

    private static string? FindChrome()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var candidates = new[]
        {
            Path.Combine(local, @"Google\Chrome\Bin\chrome.exe"),
            Path.Combine(local, @"Google\Chrome\Application\chrome.exe"),
            @"C:\Program Files\Google\Chrome\Application\chrome.exe",
            @"C:\Program Files (x86)\Google\Chrome\Application\chrome.exe"
        };
        return candidates.FirstOrDefault(File.Exists);
    }

    private static bool TryParseMajor(string version, out int major)
    {
        major = 0;
        var m = Regex.Match(version, @"^(\d+)");
        return m.Success && int.TryParse(m.Groups[1].Value, out major);
    }

    private static string ResolveExe(ComponentConfig c)
    {
        if (!string.IsNullOrWhiteSpace(c.Exe))
        {
            var configured = ResolvePath(c.Exe);
            if (File.Exists(configured))
            {
                return configured;
            }
        }

        foreach (var candidate in c.CandidateExes)
        {
            var path = ResolvePath(candidate);
            if (File.Exists(path))
            {
                return path;
            }
        }

        return ScanForFile(c.ScanNames) ?? "";
    }

    private static string? ResolveFirstExisting(string? primary, IReadOnlyList<string>? candidates)
    {
        if (!string.IsNullOrWhiteSpace(primary))
        {
            var p = ResolvePath(primary);
            if (File.Exists(p))
            {
                return p;
            }
        }

        foreach (var c in candidates ?? Enumerable.Empty<string>())
        {
            var p = ResolvePath(c);
            if (File.Exists(p))
            {
                return p;
            }
        }

        return null;
    }

    private static string? ScanForFile(IReadOnlyList<string>? names)
    {
        if (names == null || names.Count == 0)
        {
            return null;
        }

        var sourceRoot = ResolveSourceRoot();
        var roots = new List<string>
        {
            _baseDir,
            Path.Combine(_baseDir, "sidecars"),
            Path.Combine(_baseDir, "publish"),
            Path.Combine(sourceRoot, "抖音弹幕点歌系统"),
            Path.Combine(sourceRoot, "抖音cdp弹幕"),
            Path.Combine(sourceRoot, "酷狗协议"),
            Path.Combine(sourceRoot, "抖音直播24小时无人直播"),
            Path.Combine(sourceRoot, "LiveAssistant")
        };

        foreach (var root in roots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!Directory.Exists(root))
            {
                continue;
            }

            foreach (var name in names)
            {
                try
                {
                    var hit = Directory.EnumerateFiles(root, name, SearchOption.AllDirectories)
                        .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}node_modules{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
                        .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}.git{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
                        .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
                        .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}TestResults{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
                        .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}backup{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
                        .OrderByDescending(p => p.Contains($"{Path.DirectorySeparatorChar}sidecars{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
                        .ThenByDescending(p => p.Contains($"{Path.DirectorySeparatorChar}publish{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
                        .ThenByDescending(p => p.Contains($"{Path.DirectorySeparatorChar}dist{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
                        .ThenByDescending(p => p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
                        .FirstOrDefault();
                    if (!string.IsNullOrWhiteSpace(hit))
                    {
                        return hit;
                    }
                }
                catch
                {
                    // ignore access issues while scanning
                }
            }
        }

        return null;
    }

    private static string ResolvePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return "";
        }

        var p = path.Trim().Replace('/', Path.DirectorySeparatorChar);
        if (!Path.IsPathRooted(p))
        {
            p = Path.Combine(_baseDir, p);
        }

        try
        {
            return Path.GetFullPath(p);
        }
        catch
        {
            return p;
        }
    }

    private static string ResolveSourceRoot()
    {
        // Prefer configured sourceRoot via static after load; fallback siblings of repo.
        var fromEnv = Environment.GetEnvironmentVariable("LIVE_SOURCE_ROOT");
        if (!string.IsNullOrWhiteSpace(fromEnv) && Directory.Exists(fromEnv))
        {
            return fromEnv;
        }

        var parent = Directory.GetParent(_baseDir)?.FullName;
        if (!string.IsNullOrWhiteSpace(parent) && Directory.Exists(Path.Combine(parent, "抖音cdp弹幕")))
        {
            return parent;
        }

        var grand = Directory.GetParent(_baseDir)?.Parent?.FullName;
        if (!string.IsNullOrWhiteSpace(grand) && Directory.Exists(Path.Combine(grand, "抖音cdp弹幕")))
        {
            return grand;
        }

        const string fallback = @"E:\我的源码目录";
        return Directory.Exists(fallback) ? fallback : _baseDir;
    }

    private static string? FirstExistingDir(params string?[] candidates)
    {
        foreach (var c in candidates)
        {
            if (string.IsNullOrWhiteSpace(c))
            {
                continue;
            }

            var p = ResolvePath(c);
            if (Directory.Exists(p))
            {
                return p;
            }
        }

        return null;
    }

    private static string ResolveMode()
    {
        var mode = (Environment.GetEnvironmentVariable("LIVE_BOOT_MODE") ?? "auto").Trim().ToLowerInvariant();
        return mode is "dev" or "prod" or "auto" ? mode : "auto";
    }

    private static bool CommandExists(string name)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "where.exe",
                Arguments = name,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var p = Process.Start(psi);
            if (p == null)
            {
                return false;
            }

            p.WaitForExit(3000);
            return p.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static string RunCapture(string file, string args)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = file,
                Arguments = args,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var p = Process.Start(psi);
            if (p == null)
            {
                return "";
            }

            var o = p.StandardOutput.ReadToEnd().Trim();
            p.WaitForExit(3000);
            return o;
        }
        catch
        {
            return "";
        }
    }

    private static string ResolveLauncherJsonPath(string[] args)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i] is "--config" or "-c" && File.Exists(args[i + 1]))
            {
                return Path.GetFullPath(args[i + 1]);
            }
        }

        var candidates = new List<string>
        {
            Path.Combine(AppContext.BaseDirectory, "launcher.json"),
            Path.Combine(Directory.GetCurrentDirectory(), "launcher.json")
        };

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 6 && dir != null; i++)
        {
            candidates.Add(Path.Combine(dir.FullName, "launcher.json"));
            dir = dir.Parent;
        }

        foreach (var c in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (File.Exists(c))
            {
                return c;
            }
        }

        throw new FileNotFoundException("launcher.json not found. Place it next to StartLiveSystem.exe or pass --config.");
    }

    private static LauncherConfig LoadConfig(string path)
    {
        var json = File.ReadAllText(path, Encoding.UTF8);
        var cfg = JsonSerializer.Deserialize<LauncherConfig>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        }) ?? new LauncherConfig();

        if (!string.IsNullOrWhiteSpace(cfg.SourceRoot) && Directory.Exists(cfg.SourceRoot))
        {
            Environment.SetEnvironmentVariable("LIVE_SOURCE_ROOT", cfg.SourceRoot);
        }

        if (!string.IsNullOrWhiteSpace(cfg.Mode))
        {
            Environment.SetEnvironmentVariable("LIVE_BOOT_MODE", cfg.Mode);
        }

        // Apply sourceRoot into relative candidate resolution via _baseDir already set to config dir.
        // Also rewrite empty DevWorkingDirectory defaults.
        cfg.DouyinCdp.DevWorkingDirectory = string.IsNullOrWhiteSpace(cfg.DouyinCdp.DevWorkingDirectory)
            ? Path.Combine(ResolveSourceRoot(), "抖音cdp弹幕")
            : cfg.DouyinCdp.DevWorkingDirectory;
        cfg.Maoyan.DevWorkingDirectory = string.IsNullOrWhiteSpace(cfg.Maoyan.DevWorkingDirectory)
            ? Path.Combine(ResolveSourceRoot(), "抖音直播24小时无人直播")
            : cfg.Maoyan.DevWorkingDirectory;
        cfg.LiveAssistant.DevWorkingDirectory = string.IsNullOrWhiteSpace(cfg.LiveAssistant.DevWorkingDirectory)
            ? _baseDir
            : cfg.LiveAssistant.DevWorkingDirectory;

        return cfg;
    }

    private static void Boot(string message) => Console.WriteLine($"[BOOT]{Environment.NewLine}{message}");

    private static void BootFail(string component, string error, string path, int exitCode)
        => Console.WriteLine($"[BOOT]{Environment.NewLine}FAIL component={component} error={error} path={path} exitCode={exitCode}");
}

internal sealed class LauncherConfig
{
    public string Mode { get; set; } = "auto";
    public string SourceRoot { get; set; } = "";
    public string BootLogPrefix { get; set; } = "LIVE_BOOT";

    [JsonPropertyName("DouyinCDP")]
    public ComponentConfig DouyinCdp { get; set; } = new();

    public ComponentConfig KugouApi { get; set; } = new();
    public ComponentConfig Maoyan { get; set; } = new();
    public ComponentConfig LiveAssistant { get; set; } = new();
    public EnvironmentConfig Environment { get; set; } = new();
}

internal sealed class ComponentConfig
{
    public bool Enabled { get; set; } = true;
    public string Exe { get; set; } = "";
    public string SourceDir { get; set; } = "";
    public string GitCommit { get; set; } = "";
    public string BuildTime { get; set; } = "";
    public string MinVersion { get; set; } = "";
    public string DevCommand { get; set; } = "";
    public string DevArgs { get; set; } = "";
    public string DevWorkingDirectory { get; set; } = "";
    public string HealthUrl { get; set; } = "";
    public int Port { get; set; }
    public List<string> ProcessNames { get; set; } = new();
    public int WaitReadySeconds { get; set; } = 60;
    public bool WaitLogin { get; set; }
    public string LoginOkField { get; set; } = "login_ok";
    public List<string> CandidateExes { get; set; } = new();
    public List<string> CandidateBats { get; set; } = new();
    public List<string> CandidateEntryJs { get; set; } = new();
    public List<string> ScanNames { get; set; } = new();
    public string RequireSibling { get; set; } = "";
    public string StartBat { get; set; } = "";
}

internal sealed class EnvironmentConfig
{
    public bool RequireDotnet { get; set; } = true;
    public bool RequireNode { get; set; } = true;
    public bool RequireChrome { get; set; } = true;
    public bool RequireGo { get; set; }
    public bool RequirePython { get; set; }
    public int ChromeMinMajor { get; set; } = 120;
}
