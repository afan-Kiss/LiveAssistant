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

            if (!EnsureComponent("cdp", cfg.DouyinCdp, StartDouyinCdpAsync, waitLogin: cfg.DouyinCdp.WaitLogin).GetAwaiter().GetResult())
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

        if (await IsHealthyAsync(component).ConfigureAwait(false))
        {
            Boot($"{label} already running");
            if (waitLogin)
            {
                await WaitForLoginAsync(component).ConfigureAwait(false);
            }
            return true;
        }

        if (component.Port > 0 && IsPortOpen(component.Port) && !await IsHealthyAsync(component).ConfigureAwait(false))
        {
            BootFail(label, $"port {component.Port} occupied but health check failed", "", -1);
            return false;
        }

        if (IsProcessRunning(component.ProcessNames) && await WaitUntilHealthyAsync(component, Math.Min(15, component.WaitReadySeconds)).ConfigureAwait(false))
        {
            Boot($"{label} already running (process reuse)");
            if (waitLogin)
            {
                await WaitForLoginAsync(component).ConfigureAwait(false);
            }
            return true;
        }

        Boot($"starting {label}");
        var (ok, path, exitCode) = await starter(component, label).ConfigureAwait(false);
        if (!ok)
        {
            BootFail(label, "start failed", path, exitCode);
            return false;
        }

        Boot($"started {label} path={path}");
        if (!await WaitUntilHealthyAsync(component, component.WaitReadySeconds).ConfigureAwait(false))
        {
            BootFail(label, $"health check failed within {component.WaitReadySeconds}s", path, -1);
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
        var mode = ResolveMode();
        var exe = ResolveExe(c);
        if (mode != "dev" && !string.IsNullOrWhiteSpace(exe) && File.Exists(exe))
        {
            return Task.FromResult(StartDetached(exe, "", Path.GetDirectoryName(exe)!));
        }

        var workDir = FirstExistingDir(c.DevWorkingDirectory, Path.Combine(ResolveSourceRoot(), "抖音cdp弹幕"));
        if (mode == "dev" || string.IsNullOrWhiteSpace(exe) || !File.Exists(exe))
        {
            if (!CommandExists("go") && !File.Exists(@"C:\go\bin\go.exe"))
            {
                return Task.FromResult((false, workDir ?? "", -1));
            }

            if (Directory.Exists(workDir))
            {
                Boot("using development mode for cdp (go run)");
                var go = CommandExists("go") ? "go" : @"C:\go\bin\go.exe";
                return Task.FromResult(StartDetached(go, string.IsNullOrWhiteSpace(c.DevArgs) ? "run ./cmd/server" : c.DevArgs!, workDir));
            }
        }

        if (!string.IsNullOrWhiteSpace(exe) && File.Exists(exe))
        {
            return Task.FromResult(StartDetached(exe, "", Path.GetDirectoryName(exe)!));
        }

        BootFail(label, "cdp-danmaku.exe not found; place under ./sidecars/ or build 抖音cdp弹幕", "", -1);
        return Task.FromResult((false, "", -1));
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
        if (!CommandExists("node"))
        {
            BootFail(label, "Node.js missing", "", -1);
            return (false, "", -1);
        }

        var entryJs = ResolveFirstExisting(null, c.CandidateEntryJs);
        if (!string.IsNullOrWhiteSpace(entryJs) && File.Exists(entryJs))
        {
            var dir = Path.GetDirectoryName(entryJs)!;
            if (!Directory.Exists(Path.Combine(dir, "node_modules")))
            {
                BootFail(label, "node_modules missing", dir, -1);
                return (false, dir, -1);
            }

            return StartDetached("node", "index.js", dir);
        }

        var bat = ResolveFirstExisting(c.StartBat, c.CandidateBats);
        if (!string.IsNullOrWhiteSpace(bat) && File.Exists(bat))
        {
            return StartDetached("cmd.exe", $"/c \"\"{bat}\"\"", Path.GetDirectoryName(bat)!);
        }

        var mode = ResolveMode();
        var workDir = FirstExistingDir(c.DevWorkingDirectory, Path.Combine(ResolveSourceRoot(), "猫眼票房助手"));
        if ((mode == "dev" || mode == "auto") && Directory.Exists(workDir) && File.Exists(Path.Combine(workDir, "index.js")))
        {
            if (!Directory.Exists(Path.Combine(workDir, "node_modules")))
            {
                BootFail(label, "node_modules missing", workDir, -1);
                return (false, workDir, -1);
            }

            Boot("using development/source mode for maoyan");
            return StartDetached("node", "index.js", workDir);
        }

        BootFail(label, "maoyan start entry not found (expected 猫眼票房助手)", "", -1);
        return (false, "", -1);
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

    private static async Task<bool> WaitUntilHealthyAsync(ComponentConfig c, int seconds)
    {
        var until = DateTime.UtcNow.AddSeconds(Math.Max(1, seconds));
        while (DateTime.UtcNow < until)
        {
            if (await IsHealthyAsync(c).ConfigureAwait(false))
            {
                return true;
            }

            await Task.Delay(1000).ConfigureAwait(false);
        }

        return await IsHealthyAsync(c).ConfigureAwait(false);
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
            Path.Combine(sourceRoot, "猫眼票房助手"),
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
            ? Path.Combine(ResolveSourceRoot(), "猫眼票房助手")
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
