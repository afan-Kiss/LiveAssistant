using System.Diagnostics;
using System.Text;
using System.Text.Json;
using LiveAssistant.Config;
using LiveAssistant.Models;
using LiveAssistant.Services.AiSpeech;
using NAudio.Wave;

namespace LiveAssistant.Services;

/// <summary>
/// 换机一键部署：诊断 + 从 Deploy/MachineSetup 安装包安装组件，失败返回具体原因。
/// </summary>
public sealed class MachineSetupService
{
    public const string KitFolderName = "Deploy\\MachineSetup";
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private readonly ConfigManager _config;
    private readonly LogService _log;
    private readonly PlaybackService _playback;
    private readonly AiSpeechCoordinator _aiSpeech;
    private readonly object _installLock = new();
    private volatile bool _installing;

    public MachineSetupService(
        ConfigManager config,
        LogService log,
        PlaybackService playback,
        AiSpeechCoordinator aiSpeech)
    {
        _config = config;
        _log = log;
        _playback = playback;
        _aiSpeech = aiSpeech;
    }

    public string KitRoot => Path.Combine(AppPaths.ExeDirectory, "Deploy", "MachineSetup");

    public MachineSetupStatus GetStatus()
    {
        EnsureKitScaffold();
        var components = new List<MachineSetupComponentStatus>
        {
            CheckVbCable(),
            CheckDouyinSidecar(),
            CheckKugouSidecar(),
            CheckKgapiJs(),
            CheckKuaishouSidecar(),
            CheckJavaRuntime(),
            CheckOllama(),
            CheckOllamaModel(),
            CheckGptSovits(),
            CheckAudioRoute(),
            CheckConfigData()
        };

        var ok = components.Count(c => c.State == "ok");
        var bad = components.Count(c => c.State is "missing" or "broken");
        var reboot = components.Count(c => c.State == "needs_reboot");

        return new MachineSetupStatus
        {
            KitRoot = KitRoot,
            KitReady = Directory.Exists(KitRoot),
            Summary = $"就绪 {ok}/{components.Count}" +
                      (bad > 0 ? $"，待处理 {bad}" : "") +
                      (reboot > 0 ? $"，需重启 {reboot}" : ""),
            Components = components,
            PackHints = BuildPackHints()
        };
    }

    public MachineSetupInstallResult Install(string? componentId)
    {
        if (_installing)
        {
            return new MachineSetupInstallResult
            {
                Ok = false,
                Summary = "已有安装任务进行中，请稍后再试",
                Steps =
                {
                    new MachineSetupStepResult
                    {
                        ComponentId = componentId ?? "all",
                        Name = "安装锁",
                        Success = false,
                        FailureReason = "并发安装被拒绝"
                    }
                }
            };
        }

        lock (_installLock)
        {
            _installing = true;
            try
            {
                return InstallCore(componentId);
            }
            finally
            {
                _installing = false;
            }
        }
    }

    private MachineSetupInstallResult InstallCore(string? componentId)
    {
        EnsureKitScaffold();
        var id = string.IsNullOrWhiteSpace(componentId) || componentId == "all"
            ? null
            : componentId.Trim();

        var steps = new List<MachineSetupStepResult>();
        var targets = id == null
            ? new[] { "sidecars", "vbcable", "kuaishou", "ollama", "ollama-model", "gpt-sovits", "audio-route" }
            : new[] { id };

        foreach (var target in targets)
        {
            try
            {
                steps.Add(target switch
                {
                    "sidecars" or "douyin-sidecar" or "kugou-sidecar" or "kgapijs"
                        => InstallSidecarsFromKit(target),
                    "vbcable" => InstallVbCable(),
                    "kuaishou" or "kuaishou-sidecar" or "java"
                        => InstallKuaishouKit(target),
                    "ollama" => InstallOllama(),
                    "ollama-model" or "ai-model" => PullOllamaModel(),
                    "gpt-sovits" or "tts" or "ai-tts" => InstallGptSovits(),
                    "ai-speech" => InstallAiSpeechBundle(),
                    "audio-route" => ApplyAudioRouteToCable(),
                    "config-data" => EnsureConfigDataStep(),
                    _ => new MachineSetupStepResult
                    {
                        ComponentId = target,
                        Name = target,
                        Success = false,
                        FailureReason =
                            $"未知组件 id：{target}。可用：vbcable / sidecars / kuaishou / ollama / ollama-model / gpt-sovits / ai-speech / audio-route / config-data"
                    }
                });
            }
            catch (Exception ex)
            {
                _log.Error("machine-setup", $"安装 {target} 异常", ex);
                steps.Add(new MachineSetupStepResult
                {
                    ComponentId = target,
                    Name = target,
                    Success = false,
                    FailureReason = $"{ex.GetType().Name}: {ex.Message}",
                    Message = "安装过程抛出异常"
                });
            }
        }

        // 刷新侧车解析（不整表 Reload，避免冲掉未落盘状态）
        _config.Settings.Douyin.DouyinExePath =
            SidecarLocator.ResolveDouyin(_config.Settings.Douyin.DouyinExePath);
        _config.Settings.Kugou.KugouExePath =
            SidecarLocator.ResolveKugou(_config.Settings.Kugou.KugouExePath);
        _config.Save();
        var status = GetStatus();
        var failed = steps.Where(s => !s.Success && !s.Skipped).ToList();
        return new MachineSetupInstallResult
        {
            Ok = failed.Count == 0,
            Summary = failed.Count == 0
                ? $"完成 {steps.Count} 步" + (steps.Any(s => s.Message.Contains("重启", StringComparison.Ordinal)) ? "（可能需要重启电脑）" : "")
                : $"失败 {failed.Count} 步：" + string.Join("；", failed.Select(f => f.FailureReason ?? f.Message)),
            Steps = steps,
            StatusAfter = status
        };
    }

    private MachineSetupStepResult InstallSidecarsFromKit(string filterId)
    {
        var srcRoot = Path.Combine(KitRoot, "sidecars");
        if (!Directory.Exists(srcRoot))
        {
            return Fail("sidecars", "侧车文件包",
                $"安装包目录不存在：{srcRoot}",
                "换机前请把抖音弹幕助手、酷狗api、kgapijs 拷进 Deploy/MachineSetup/sidecars/");
        }

        var copied = new List<string>();
        var errors = new List<string>();

        void TryCopyFile(string fileName, bool required)
        {
            if (filterId is not ("sidecars" or "all") &&
                filterId == "douyin-sidecar" && !fileName.Contains("抖音") && !fileName.Contains("douyin", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (filterId == "kugou-sidecar" && !SidecarLocator.IsKugouApiExe(fileName))
            {
                return;
            }

            var src = FindInKit(srcRoot, fileName);
            if (src == null)
            {
                // 宽松：扫目录
                src = Directory.EnumerateFiles(srcRoot, "*.exe", SearchOption.AllDirectories)
                    .FirstOrDefault(f => string.Equals(Path.GetFileName(f), fileName, StringComparison.OrdinalIgnoreCase)
                                         || (fileName.Contains("抖音") && SidecarLocator.IsDouyinApiExe(Path.GetFileName(f)))
                                         || (fileName.Contains("酷狗") && SidecarLocator.IsKugouApiExe(Path.GetFileName(f))));
            }

            if (src == null)
            {
                if (required)
                {
                    errors.Add($"缺少文件：{fileName}（应放在 {srcRoot}）");
                }

                return;
            }

            var dest = Path.Combine(AppPaths.ExeDirectory, Path.GetFileName(src));
            try
            {
                File.Copy(src, dest, overwrite: true);
                copied.Add(Path.GetFileName(dest));
            }
            catch (Exception ex)
            {
                errors.Add($"复制 {Path.GetFileName(src)} → {dest} 失败：{ex.Message}（是否被占用/无权限？）");
            }
        }

        if (filterId is "sidecars" or "douyin-sidecar")
        {
            TryCopyFile(SidecarLocator.PreferredDouyinFileName, required: filterId != "sidecars");
            // 宽松匹配抖音
            foreach (var f in Directory.EnumerateFiles(srcRoot, "*.exe", SearchOption.AllDirectories))
            {
                if (SidecarLocator.IsDouyinApiExe(Path.GetFileName(f)))
                {
                    var dest = Path.Combine(AppPaths.ExeDirectory, SidecarLocator.PreferredDouyinFileName);
                    try
                    {
                        File.Copy(f, dest, true);
                        copied.Add(SidecarLocator.PreferredDouyinFileName);
                    }
                    catch (Exception ex)
                    {
                        errors.Add($"复制抖音侧车失败：{ex.Message}");
                    }
                }
            }
        }

        if (filterId is "sidecars" or "kugou-sidecar")
        {
            foreach (var f in Directory.EnumerateFiles(srcRoot, "*.exe", SearchOption.AllDirectories))
            {
                if (!SidecarLocator.IsKugouApiExe(Path.GetFileName(f)))
                {
                    continue;
                }

                var dest = Path.Combine(AppPaths.ExeDirectory, SidecarLocator.PreferredKugouFileName);
                try
                {
                    File.Copy(f, dest, true);
                    copied.Add(SidecarLocator.PreferredKugouFileName);
                }
                catch (Exception ex)
                {
                    errors.Add($"复制酷狗侧车失败：{ex.Message}");
                }
            }
        }

        if (filterId is "sidecars" or "kgapijs")
        {
            var jsSrc = FindKgapiJsInKit(srcRoot);
            if (jsSrc == null)
            {
                errors.Add($"缺少 kgapijs 目录（需含 app.js），应放在 {Path.Combine(srcRoot, "kgapijs")}");
            }
            else
            {
                var jsDest = Path.Combine(AppPaths.ExeDirectory, SidecarLocator.KugouJsFolderName);
                try
                {
                    CopyDirectory(jsSrc, jsDest);
                    if (!SidecarLocator.IsKgapiJsReady(jsDest))
                    {
                        errors.Add($"已复制 kgapijs 但缺少 app.js：{jsDest}");
                    }
                    else
                    {
                        copied.Add("kgapijs/");
                    }
                }
                catch (Exception ex)
                {
                    errors.Add($"复制 kgapijs 失败：{ex.Message}");
                }
            }
        }

        if (copied.Count == 0 && errors.Count == 0)
        {
            return new MachineSetupStepResult
            {
                ComponentId = filterId,
                Name = "侧车文件",
                Skipped = true,
                Success = true,
                Message = "安装包中未找到可复制的侧车文件",
                FailureReason = null
            };
        }

        if (errors.Count > 0)
        {
            return Fail(filterId, "侧车文件",
                string.Join(" | ", errors),
                copied.Count > 0 ? $"已复制：{string.Join(", ", copied)}" : "无文件被复制");
        }

        // 写回配置路径
        var dy = SidecarLocator.ResolveDouyin("", AppPaths.ExeDirectory);
        var kg = SidecarLocator.ResolveKugou("", AppPaths.ExeDirectory);
        if (!string.IsNullOrWhiteSpace(dy))
        {
            _config.Settings.Douyin.DouyinExePath = dy;
        }

        if (!string.IsNullOrWhiteSpace(kg))
        {
            _config.Settings.Kugou.KugouExePath = kg;
        }

        _config.Save();

        return new MachineSetupStepResult
        {
            ComponentId = "sidecars",
            Name = "侧车文件",
            Success = true,
            Message = "已复制：" + string.Join(", ", copied.Distinct())
        };
    }

    private MachineSetupStepResult InstallVbCable()
    {
        if (IsVbCableInstalled())
        {
            return new MachineSetupStepResult
            {
                ComponentId = "vbcable",
                Name = "VB-CABLE",
                Success = true,
                Skipped = true,
                Message = "已检测到 CABLE 设备，跳过安装"
            };
        }

        var setup = FindVbCableSetup();
        if (setup == null)
        {
            return Fail("vbcable", "VB-CABLE",
                $"未找到安装包。请将 VBCABLE_Setup_x64.exe（或 Driver Pack 解压后的 Setup）放到：{Path.Combine(KitRoot, "VBCable")}",
                "下载：https://vb-audio.com/Cable/ ，解压后拷贝 Setup 到 Deploy/MachineSetup/VBCable/");
        }

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = setup,
                // VB-CABLE：-i 安装，-h 隐藏界面；需管理员
                Arguments = "-i -h",
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = Path.GetDirectoryName(setup) ?? KitRoot
            };

            using var proc = Process.Start(psi);
            if (proc == null)
            {
                return Fail("vbcable", "VB-CABLE",
                    "无法启动安装程序（Process.Start 返回 null）",
                    "请右键安装包「以管理员身份运行」手动安装，然后重启电脑");
            }

            var exited = proc.WaitForExit(120_000);
            if (!exited)
            {
                return Fail("vbcable", "VB-CABLE",
                    "安装程序超过 120 秒未退出",
                    $"安装包：{setup}。可手动运行并查看是否卡在 UAC/重启提示");
            }

            var code = proc.ExitCode;
            if (code != 0 && code != 1)
            {
                // 部分版本成功也会非 0；若仍检测不到设备则判失败
                if (!IsVbCableInstalled())
                {
                    return Fail("vbcable", "VB-CABLE",
                        $"安装进程退出码={code}，且仍未检测到 CABLE 设备",
                        "请重启电脑后再诊断；或手动以管理员运行 Setup。UAC 取消也会导致失败");
                }
            }

            if (!IsVbCableInstalled())
            {
                return new MachineSetupStepResult
                {
                    ComponentId = "vbcable",
                    Name = "VB-CABLE",
                    Success = true,
                    Message = "安装程序已执行，但尚未检测到设备——通常需要重启电脑后再点「重新诊断」",
                    FailureReason = null,
                    LogTail = $"setup={setup}; exitCode={code}"
                };
            }

            return new MachineSetupStepResult
            {
                ComponentId = "vbcable",
                Name = "VB-CABLE",
                Success = true,
                Message = "安装成功，已检测到 CABLE 设备"
            };
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return Fail("vbcable", "VB-CABLE",
                "用户取消了 UAC 管理员授权（错误 1223）",
                "换机安装驱动必须允许管理员权限，请重试并在弹窗点「是」");
        }
        catch (Exception ex)
        {
            return Fail("vbcable", "VB-CABLE",
                $"{ex.GetType().Name}: {ex.Message}",
                $"安装包路径：{setup}");
        }
    }

    private MachineSetupStepResult InstallKuaishouKit(string filterId)
    {
        var stepsMsg = new List<string>();
        var errors = new List<string>();
        var ksRoot = Path.Combine(KitRoot, "Kuaishou");

        if (filterId is "kuaishou" or "kuaishou-sidecar" or "java")
        {
            // JAR
            if (filterId is not "java")
            {
                var jarSrc = Directory.Exists(ksRoot)
                    ? Directory.EnumerateFiles(ksRoot, "ks-ui-server*.jar", SearchOption.AllDirectories).FirstOrDefault()
                      ?? Directory.EnumerateFiles(ksRoot, "*.jar", SearchOption.AllDirectories).FirstOrDefault()
                    : null;
                if (jarSrc == null)
                {
                    errors.Add($"缺少 ks-ui-server.jar，请放到 {ksRoot}");
                }
                else
                {
                    var destDir = Path.Combine(AppPaths.ExeDirectory, "sidecars", "kuaishou");
                    Directory.CreateDirectory(destDir);
                    var dest = Path.Combine(destDir, "ks-ui-server.jar");
                    try
                    {
                        File.Copy(jarSrc, dest, true);
                        stepsMsg.Add($"已复制 {dest}");
                        _config.Settings.Kuaishou.BaseUrl = "http://127.0.0.1:18900";
                        _config.Save();
                    }
                    catch (Exception ex)
                    {
                        errors.Add($"复制快手 jar 失败：{ex.Message}");
                    }
                }
            }

            // 便携 JRE（可选）
            if (filterId is "kuaishou" or "java")
            {
                var jreSrc = Path.Combine(ksRoot, "jre");
                if (Directory.Exists(jreSrc) && File.Exists(Path.Combine(jreSrc, "bin", "java.exe")))
                {
                    var jreDest = Path.Combine(AppPaths.ExeDirectory, "sidecars", "kuaishou", "jre");
                    try
                    {
                        CopyDirectory(jreSrc, jreDest);
                        stepsMsg.Add("已复制便携 JRE");
                    }
                    catch (Exception ex)
                    {
                        errors.Add($"复制 JRE 失败：{ex.Message}");
                    }
                }
                else if (!IsJavaAvailable(out _))
                {
                    errors.Add($"未检测到系统 Java，且安装包无便携 JRE。请安装 JDK17，或把 jre 目录放到 {jreSrc}");
                }
            }
        }

        if (errors.Count > 0)
        {
            return Fail("kuaishou", "快手侧车", string.Join(" | ", errors),
                string.Join("；", stepsMsg));
        }

        if (stepsMsg.Count == 0)
        {
            return new MachineSetupStepResult
            {
                ComponentId = "kuaishou",
                Name = "快手侧车",
                Skipped = true,
                Success = true,
                Message = "安装包无快手文件可安装（若本机已有 Java+jar 可忽略）"
            };
        }

        return new MachineSetupStepResult
        {
            ComponentId = "kuaishou",
            Name = "快手侧车",
            Success = true,
            Message = string.Join("；", stepsMsg)
        };
    }

    private MachineSetupStepResult ApplyAudioRouteToCable()
    {
        if (!IsVbCableInstalled())
        {
            return Fail("audio-route", "音频路由",
                "未检测到 VB-CABLE 设备，无法自动选输出",
                "请先安装 VB-CABLE 并重启，再执行本步骤；或手动在「播放控制」选择 CABLE Input");
        }

        var devices = AudioOutputDevices.ListDevices();
        var cable = devices.FirstOrDefault(d =>
            d.DeviceNumber >= 0 &&
            d.Name.Contains("cable", StringComparison.OrdinalIgnoreCase) &&
            d.Name.Contains("input", StringComparison.OrdinalIgnoreCase))
            ?? devices.FirstOrDefault(d =>
                d.DeviceNumber >= 0 &&
                d.Name.Contains("cable", StringComparison.OrdinalIgnoreCase));

        if (cable == null)
        {
            return Fail("audio-route", "音频路由",
                "设备列表中找不到名称含 CABLE 的播放设备",
                $"当前设备：{string.Join(", ", devices.Select(d => d.Name))}");
        }

        _config.Settings.Playback.OutputDeviceNumber = cable.DeviceNumber;
        _config.Settings.Playback.OutputDeviceName = cable.Name;
        // 双通道：快手 AI + 歌曲走 CABLE；抖音 AI 保持系统默认，避免抖音口播进快手
        _config.Settings.AiSpeech.OutputDeviceNumber = -1;
        _config.Settings.AiSpeech.OutputDeviceName = "";
        _config.Settings.AiSpeech.KuaishouOutputDeviceNumber = cable.DeviceNumber;
        _config.Settings.AiSpeech.KuaishouOutputDeviceName = cable.Name;
        _playback.SetDeviceNumber(cable.DeviceNumber);
        _aiSpeech.ApplyDeviceFromSettings("douyin");
        _config.Save();

        return new MachineSetupStepResult
        {
            ComponentId = "audio-route",
            Name = "音频路由",
            Success = true,
            Message = $"歌曲/快手AI → {cable.Name}；抖音AI → 系统默认（防串台）"
        };
    }

    private MachineSetupStepResult EnsureConfigDataStep()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.ConfigDirectory);
            Directory.CreateDirectory(AppPaths.ResolveDataDirectory());
            Directory.CreateDirectory(AppPaths.LogsDirectory);
            if (!File.Exists(Path.Combine(AppPaths.ConfigDirectory, "appsettings.json"))
                && File.Exists(Path.Combine(AppPaths.ResolveDataDirectory(), "appsettings.json")) == false)
            {
                _config.Save();
            }

            return new MachineSetupStepResult
            {
                ComponentId = "config-data",
                Name = "配置与数据目录",
                Success = true,
                Message = $"Config={AppPaths.ConfigDirectory}；Data={AppPaths.ResolveDataDirectory()}"
            };
        }
        catch (Exception ex)
        {
            return Fail("config-data", "配置与数据目录", ex.Message, "检查磁盘权限与杀毒软件拦截");
        }
    }

    // ---------- checks ----------

    private MachineSetupComponentStatus CheckVbCable()
    {
        var installed = IsVbCableInstalled();
        var setup = FindVbCableSetup();
        return new MachineSetupComponentStatus
        {
            Id = "vbcable",
            Name = "VB-CABLE 虚拟声卡",
            Category = "音频",
            State = installed ? "ok" : (setup != null ? "missing" : "missing"),
            CanInstall = !installed && setup != null,
            Detail = installed
                ? "已检测到 CABLE 播放设备"
                : setup != null
                    ? $"未安装，但找到安装包：{setup}"
                    : $"未安装，且安装包缺失（应放：{Path.Combine(KitRoot, "VBCable")}）",
            FixHint = installed ? "" : "一键安装需管理员权限；装完建议重启电脑",
            DetectedPath = setup
        };
    }

    private MachineSetupComponentStatus CheckDouyinSidecar()
    {
        var path = SidecarLocator.ResolveDouyin(_config.Settings.Douyin.DouyinExePath);
        var ok = !string.IsNullOrWhiteSpace(path) && File.Exists(path);
        return new MachineSetupComponentStatus
        {
            Id = "douyin-sidecar",
            Name = "抖音弹幕 API",
            Category = "侧车",
            State = ok ? "ok" : "missing",
            CanInstall = Directory.Exists(Path.Combine(KitRoot, "sidecars")),
            Detail = ok ? path : $"缺少 {SidecarLocator.PreferredDouyinFileName}",
            FixHint = ok ? "" : "放入 Deploy/MachineSetup/sidecars/ 后点「安装侧车」",
            DetectedPath = ok ? path : null
        };
    }

    private MachineSetupComponentStatus CheckKugouSidecar()
    {
        var path = SidecarLocator.ResolveKugou(_config.Settings.Kugou.KugouExePath);
        var ok = !string.IsNullOrWhiteSpace(path) && File.Exists(path);
        return new MachineSetupComponentStatus
        {
            Id = "kugou-sidecar",
            Name = "酷狗 API",
            Category = "侧车",
            State = ok ? "ok" : "missing",
            CanInstall = Directory.Exists(Path.Combine(KitRoot, "sidecars")),
            Detail = ok ? path : $"缺少 {SidecarLocator.PreferredKugouFileName}",
            FixHint = ok ? "" : "放入 Deploy/MachineSetup/sidecars/ 后点「安装侧车」",
            DetectedPath = ok ? path : null
        };
    }

    private MachineSetupComponentStatus CheckKgapiJs()
    {
        var dir = SidecarLocator.FindKgapiJsDirectory(AppPaths.ExeDirectory);
        var ok = !string.IsNullOrWhiteSpace(dir);
        return new MachineSetupComponentStatus
        {
            Id = "kgapijs",
            Name = "酷狗 kgapijs",
            Category = "侧车",
            State = ok ? "ok" : "missing",
            CanInstall = true,
            Detail = ok ? dir! : "缺少 kgapijs/app.js（每日推荐等功能需要）",
            FixHint = ok ? "" : "随酷狗侧车一并拷贝到 Deploy/MachineSetup/sidecars/kgapijs/",
            DetectedPath = dir
        };
    }

    private MachineSetupComponentStatus CheckKuaishouSidecar()
    {
        var jar = Path.Combine(AppPaths.ExeDirectory, "sidecars", "kuaishou", "ks-ui-server.jar");
        var kitJar = Directory.Exists(Path.Combine(KitRoot, "Kuaishou"));
        var exists = File.Exists(jar);
        return new MachineSetupComponentStatus
        {
            Id = "kuaishou-sidecar",
            Name = "快手侧车 jar",
            Category = "侧车",
            State = exists ? "ok" : "missing",
            CanInstall = kitJar,
            Detail = exists ? jar : "缺少 sidecars/kuaishou/ks-ui-server.jar（可用本机已运行的 :18900 代替）",
            FixHint = "把 ks-ui-server.jar 放到 Deploy/MachineSetup/Kuaishou/",
            DetectedPath = exists ? jar : null
        };
    }

    private MachineSetupComponentStatus CheckJavaRuntime()
    {
        var ok = IsJavaAvailable(out var detail);
        var portable = Path.Combine(AppPaths.ExeDirectory, "sidecars", "kuaishou", "jre", "bin", "java.exe");
        if (File.Exists(portable))
        {
            ok = true;
            detail = portable;
        }

        return new MachineSetupComponentStatus
        {
            Id = "java",
            Name = "Java 运行时",
            Category = "运行环境",
            State = ok ? "ok" : "missing",
            CanInstall = Directory.Exists(Path.Combine(KitRoot, "Kuaishou", "jre")),
            Detail = ok ? detail : "未找到 java.exe（PATH 或便携 JRE）",
            FixHint = "安装 Temurin JDK 17，或把便携 jre 放到 Deploy/MachineSetup/Kuaishou/jre/"
        };
    }

    private MachineSetupComponentStatus CheckOllama()
    {
        var exe = AiSpeechServiceLauncher.ResolveOllamaExe(_config.Settings.AiSpeech.OllamaExePath);
        var kitSetup = FindOllamaSetup();
        var kitPortable = FindPortableOllamaDir();
        var running = ProbeHttpOk(_config.Settings.AiSpeech.OllamaUrl?.TrimEnd('/') + "/api/tags");
        string state;
        if (!string.IsNullOrWhiteSpace(exe) || running)
        {
            state = "ok";
        }
        else
        {
            state = "missing";
        }

        return new MachineSetupComponentStatus
        {
            Id = "ollama",
            Name = "Ollama（AI 大模型引擎）",
            Category = "AI语音",
            State = state,
            CanInstall = kitSetup != null || kitPortable != null,
            Detail = running
                ? $"服务已运行：{_config.Settings.AiSpeech.OllamaUrl}"
                : !string.IsNullOrWhiteSpace(exe)
                    ? $"已找到：{exe}（服务可能未启动）"
                    : kitSetup != null || kitPortable != null
                        ? "未安装，但安装包内有 Ollama"
                        : $"未找到 ollama.exe。请把 OllamaSetup.exe 或便携目录放到 {Path.Combine(KitRoot, "AiSpeech", "Ollama")}",
            FixHint = "一键安装会跑 Setup（需管理员）或复制便携版，并写入 OllamaExePath",
            DetectedPath = exe ?? kitSetup ?? kitPortable
        };
    }

    private MachineSetupComponentStatus CheckOllamaModel()
    {
        var model = string.IsNullOrWhiteSpace(_config.Settings.AiSpeech.Model)
            ? "qwen3:8b"
            : _config.Settings.AiSpeech.Model.Trim();
        var hasModel = ProbeOllamaHasModel(model, out var detail);
        var ollamaOk = !string.IsNullOrWhiteSpace(
            AiSpeechServiceLauncher.ResolveOllamaExe(_config.Settings.AiSpeech.OllamaExePath))
            || ProbeHttpOk(_config.Settings.AiSpeech.OllamaUrl?.TrimEnd('/') + "/api/tags");

        return new MachineSetupComponentStatus
        {
            Id = "ollama-model",
            Name = $"Ollama 模型（{model}）",
            Category = "AI语音",
            State = hasModel ? "ok" : "missing",
            CanInstall = ollamaOk,
            Detail = hasModel ? detail : (ollamaOk ? $"未检测到模型 {model}" : "需先安装/启动 Ollama"),
            FixHint = ollamaOk
                ? $"将执行：ollama pull {model}（体积大，需联网，可能数分钟～数十分钟）"
                : "先安装 Ollama"
        };
    }

    private MachineSetupComponentStatus CheckGptSovits()
    {
        var work = _config.Settings.AiSpeech.TtsWorkingDirectory?.Trim() ?? "";
        var script = AiSpeechServiceLauncher.ResolveTtsStartScript(_config.Settings.AiSpeech.TtsStartScriptPath);
        var mainPy = !string.IsNullOrWhiteSpace(work) && File.Exists(Path.Combine(work, "service", "main.py"));
        var kit = FindGptSovitsKitDir();
        var ttsUp = ProbeHttpOk(_config.Settings.AiSpeech.TtsUrl?.TrimEnd('/') + "/");
        // 宽松探测：9880 常见 health
        if (!ttsUp)
        {
            ttsUp = ProbeHttpOk(_config.Settings.AiSpeech.TtsUrl?.TrimEnd('/') + "/docs")
                    || ProbeHttpOk(_config.Settings.AiSpeech.TtsUrl?.TrimEnd('/') + "/health");
        }

        var ok = mainPy || !string.IsNullOrWhiteSpace(script) || ttsUp;
        return new MachineSetupComponentStatus
        {
            Id = "gpt-sovits",
            Name = "GPT-SoVITS（AI 音色 TTS）",
            Category = "AI语音",
            State = ok ? "ok" : "missing",
            CanInstall = kit != null,
            Detail = ttsUp
                ? $"TTS 服务已响应：{_config.Settings.AiSpeech.TtsUrl}"
                : mainPy
                    ? $"工程目录：{work}"
                    : !string.IsNullOrWhiteSpace(script)
                        ? $"启动脚本：{script}"
                        : kit != null
                            ? $"未配置，但安装包有工程：{kit}"
                            : $"缺少 GPT-SoVITS。请把完整工程放到 {Path.Combine(KitRoot, "AiSpeech", "GPT-SoVITS")}（需含 service/main.py）",
            FixHint = "换机需带上 conda 环境 GPTSoVits；一键安装只复制工程并写配置路径，不自动装 conda",
            DetectedPath = mainPy ? work : script ?? kit
        };
    }

    private MachineSetupStepResult InstallOllama()
    {
        var existing = AiSpeechServiceLauncher.ResolveOllamaExe(_config.Settings.AiSpeech.OllamaExePath);
        if (!string.IsNullOrWhiteSpace(existing) && File.Exists(existing))
        {
            _config.Settings.AiSpeech.OllamaExePath = existing;
            _config.Save();
            return new MachineSetupStepResult
            {
                ComponentId = "ollama",
                Name = "Ollama",
                Success = true,
                Skipped = true,
                Message = $"已存在：{existing}"
            };
        }

        var portable = FindPortableOllamaDir();
        if (portable != null)
        {
            var dest = Path.Combine(AppPaths.ExeDirectory, "sidecars", "ollama");
            try
            {
                CopyDirectory(portable, dest);
                var exe = Path.Combine(dest, "ollama.exe");
                if (!File.Exists(exe))
                {
                    exe = Directory.EnumerateFiles(dest, "ollama.exe", SearchOption.AllDirectories).FirstOrDefault() ?? "";
                }

                if (!File.Exists(exe))
                {
                    return Fail("ollama", "Ollama",
                        $"已复制便携目录但未找到 ollama.exe：{dest}",
                        "请确认安装包 AiSpeech/Ollama 内含 ollama.exe");
                }

                _config.Settings.AiSpeech.OllamaExePath = exe;
                _config.Save();
                TryStartOllamaServe(exe);
                return new MachineSetupStepResult
                {
                    ComponentId = "ollama",
                    Name = "Ollama",
                    Success = true,
                    Message = $"已复制便携版并写入配置：{exe}"
                };
            }
            catch (Exception ex)
            {
                return Fail("ollama", "Ollama", $"复制便携版失败：{ex.Message}", portable);
            }
        }

        var setup = FindOllamaSetup();
        if (setup == null)
        {
            return Fail("ollama", "Ollama",
                $"未找到安装包。请将 OllamaSetup.exe 放到：{Path.Combine(KitRoot, "AiSpeech", "Ollama")}",
                "下载：https://ollama.com/download ；或使用便携目录（含 ollama.exe）");
        }

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = setup,
                Arguments = "/VERYSILENT /NORESTART",
                UseShellExecute = true,
                Verb = "runas"
            };
            using var proc = Process.Start(psi);
            if (proc == null)
            {
                return Fail("ollama", "Ollama", "无法启动 OllamaSetup（返回 null）", setup);
            }

            if (!proc.WaitForExit(300_000))
            {
                return Fail("ollama", "Ollama", "安装超过 5 分钟未结束", setup);
            }

            var exe2 = AiSpeechServiceLauncher.ResolveOllamaExe("");
            if (string.IsNullOrWhiteSpace(exe2))
            {
                // 常见路径
                var localApp = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Programs", "Ollama", "ollama.exe");
                if (File.Exists(localApp))
                {
                    exe2 = localApp;
                }
            }

            if (string.IsNullOrWhiteSpace(exe2) || !File.Exists(exe2))
            {
                return Fail("ollama", "Ollama",
                    $"安装程序 exit={proc.ExitCode}，但仍未找到 ollama.exe",
                    "请注销/重启后重试诊断，或手动安装后填写 AiSpeech.OllamaExePath");
            }

            _config.Settings.AiSpeech.OllamaExePath = exe2;
            _config.Save();
            TryStartOllamaServe(exe2);
            return new MachineSetupStepResult
            {
                ComponentId = "ollama",
                Name = "Ollama",
                Success = true,
                Message = $"安装完成：{exe2}"
            };
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return Fail("ollama", "Ollama", "用户取消了 UAC 管理员授权（1223）", "请允许管理员安装");
        }
        catch (Exception ex)
        {
            return Fail("ollama", "Ollama", $"{ex.GetType().Name}: {ex.Message}", setup);
        }
    }

    private MachineSetupStepResult PullOllamaModel()
    {
        var model = string.IsNullOrWhiteSpace(_config.Settings.AiSpeech.Model)
            ? "qwen3:8b"
            : _config.Settings.AiSpeech.Model.Trim();

        if (ProbeOllamaHasModel(model, out var already))
        {
            return new MachineSetupStepResult
            {
                ComponentId = "ollama-model",
                Name = "Ollama 模型",
                Success = true,
                Skipped = true,
                Message = already
            };
        }

        var exe = AiSpeechServiceLauncher.ResolveOllamaExe(_config.Settings.AiSpeech.OllamaExePath);
        if (string.IsNullOrWhiteSpace(exe))
        {
            return Fail("ollama-model", "Ollama 模型",
                "未找到 ollama.exe，无法 pull 模型",
                "请先执行「安装 Ollama」");
        }

        TryStartOllamaServe(exe);
        // 等服务起来
        var ready = false;
        for (var i = 0; i < 20; i++)
        {
            Thread.Sleep(500);
            if (ProbeHttpOk(_config.Settings.AiSpeech.OllamaUrl?.TrimEnd('/') + "/api/tags"))
            {
                ready = true;
                break;
            }
        }

        if (!ready)
        {
            return Fail("ollama-model", "Ollama 模型",
                "Ollama 服务未在 10 秒内就绪，无法 pull",
                $"请手动运行「{exe} serve」后再装模型");
        }

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = $"pull {model}",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            if (proc == null)
            {
                return Fail("ollama-model", "Ollama 模型", "无法启动 ollama pull", model);
            }

            var stdout = new StringBuilder();
            var stderr = new StringBuilder();
            proc.OutputDataReceived += (_, e) => { if (e.Data != null) stdout.AppendLine(e.Data); };
            proc.ErrorDataReceived += (_, e) => { if (e.Data != null) stderr.AppendLine(e.Data); };
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();

            if (!proc.WaitForExit((int)TimeSpan.FromMinutes(45).TotalMilliseconds))
            {
                try { proc.Kill(true); } catch { /* ignore */ }
                return Fail("ollama-model", "Ollama 模型",
                    $"pull {model} 超过 45 分钟未完成（可能网络慢）",
                    "可手动执行：ollama pull " + model);
            }

            var tail = (stdout.ToString() + "\n" + stderr.ToString()).Trim();
            if (tail.Length > 800)
            {
                tail = tail[^800..];
            }

            if (proc.ExitCode != 0 && !ProbeOllamaHasModel(model, out _))
            {
                return Fail("ollama-model", "Ollama 模型",
                    $"ollama pull 退出码={proc.ExitCode}",
                    string.IsNullOrWhiteSpace(tail) ? "请检查网络与磁盘空间" : tail);
            }

            if (!ProbeOllamaHasModel(model, out var okDetail))
            {
                return Fail("ollama-model", "Ollama 模型",
                    $"pull 结束但仍未在 /api/tags 中看到 {model}",
                    tail);
            }

            return new MachineSetupStepResult
            {
                ComponentId = "ollama-model",
                Name = "Ollama 模型",
                Success = true,
                Message = okDetail,
                LogTail = tail
            };
        }
        catch (Exception ex)
        {
            return Fail("ollama-model", "Ollama 模型", $"{ex.GetType().Name}: {ex.Message}", model);
        }
    }

    private MachineSetupStepResult InstallGptSovits()
    {
        var kit = FindGptSovitsKitDir();
        if (kit == null)
        {
            return Fail("gpt-sovits", "GPT-SoVITS",
                $"安装包缺少工程目录：{Path.Combine(KitRoot, "AiSpeech", "GPT-SoVITS")}",
                "请把含 service/main.py 的完整 GPT-SoVITS 工程拷入该目录；conda 环境需在新机自备");
        }

        var dest = Path.Combine(AppPaths.ExeDirectory, "sidecars", "GPT-SoVITS");
        try
        {
            CopyDirectory(kit, dest);
            if (!File.Exists(Path.Combine(dest, "service", "main.py")))
            {
                return Fail("gpt-sovits", "GPT-SoVITS",
                    $"复制后仍缺少 service/main.py：{dest}",
                    "检查安装包目录结构是否完整");
            }

            _config.Settings.AiSpeech.TtsWorkingDirectory = dest;
            var bat = Directory.EnumerateFiles(dest, "*.bat", SearchOption.TopDirectoryOnly)
                .FirstOrDefault(f =>
                {
                    var n = Path.GetFileName(f);
                    return n.Contains("start", StringComparison.OrdinalIgnoreCase)
                           || n.Contains("api", StringComparison.OrdinalIgnoreCase)
                           || n.Contains("sovits", StringComparison.OrdinalIgnoreCase);
                });
            if (!string.IsNullOrWhiteSpace(bat))
            {
                _config.Settings.AiSpeech.TtsStartScriptPath = bat;
            }

            // 尝试探测 conda
            var activate = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "anaconda3", "Scripts", "activate.bat");
            if (!File.Exists(activate))
            {
                activate = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    "miniconda3", "Scripts", "activate.bat");
            }

            if (File.Exists(activate) && string.IsNullOrWhiteSpace(_config.Settings.AiSpeech.CondaActivateBat))
            {
                _config.Settings.AiSpeech.CondaActivateBat = activate;
            }

            if (string.IsNullOrWhiteSpace(_config.Settings.AiSpeech.CondaEnvName))
            {
                _config.Settings.AiSpeech.CondaEnvName = "GPTSoVits";
            }

            _config.Save();

            var condaOk = !string.IsNullOrWhiteSpace(_config.Settings.AiSpeech.CondaActivateBat)
                          && File.Exists(_config.Settings.AiSpeech.CondaActivateBat);
            return new MachineSetupStepResult
            {
                ComponentId = "gpt-sovits",
                Name = "GPT-SoVITS",
                Success = true,
                Message = condaOk
                    ? $"工程已复制到 {dest}，并写入 TTS 工作目录"
                    : $"工程已复制到 {dest}；未找到 conda activate.bat，请在新机安装 Anaconda/Miniconda 并创建环境 GPTSoVits，再在主界面 AI 设置里填写路径"
            };
        }
        catch (Exception ex)
        {
            return Fail("gpt-sovits", "GPT-SoVITS", $"复制失败：{ex.Message}", kit);
        }
    }

    private MachineSetupStepResult InstallAiSpeechBundle()
    {
        var steps = new List<MachineSetupStepResult>
        {
            InstallOllama(),
            PullOllamaModel(),
            InstallGptSovits()
        };
        var failed = steps.Where(s => !s.Success && !s.Skipped).ToList();
        return new MachineSetupStepResult
        {
            ComponentId = "ai-speech",
            Name = "AI 语音整包",
            Success = failed.Count == 0,
            Message = string.Join("；", steps.Select(s => $"{s.Name}:{(s.Skipped ? "跳过" : s.Success ? "成功" : "失败")}")),
            FailureReason = failed.Count == 0
                ? null
                : string.Join(" | ", failed.Select(f => f.FailureReason ?? f.Message)),
            LogTail = string.Join("\n", steps.Select(s => $"[{s.ComponentId}] {s.Message} {s.FailureReason}"))
        };
    }

    private MachineSetupComponentStatus CheckAudioRoute()
    {
        var name = _config.Settings.Playback.OutputDeviceName ?? "";
        var cableReady = IsVbCableInstalled();
        var (ksName, _) = _config.Settings.AiSpeech.ResolveOutputDevice("kuaishou");
        var routed = name.Contains("cable", StringComparison.OrdinalIgnoreCase)
                     || (ksName ?? "").Contains("cable", StringComparison.OrdinalIgnoreCase);
        string state;
        if (!cableReady)
        {
            state = "missing";
        }
        else if (routed)
        {
            state = "ok";
        }
        else
        {
            state = "broken";
        }

        var (dyName, _) = _config.Settings.AiSpeech.ResolveOutputDevice("douyin");
        return new MachineSetupComponentStatus
        {
            Id = "audio-route",
            Name = "音频输出路由",
            Category = "音频",
            State = state,
            CanInstall = cableReady,
            Detail = routed
                ? $"歌曲={_config.Settings.Playback.OutputDeviceName}；抖音AI={dyName}；快手AI={ksName}"
                : cableReady
                    ? "已装 CABLE，但歌曲/快手AI 尚未指向 CABLE Input"
                    : "需先安装 VB-CABLE",
            FixHint = "一键安装：歌曲+快手AI→CABLE，抖音AI→系统默认；快手「系统声音」选 CABLE Input"
        };
    }

    private MachineSetupComponentStatus CheckConfigData()
    {
        var cfg = AppPaths.ConfigDirectory;
        var data = AppPaths.ResolveDataDirectory();
        var ok = Directory.Exists(cfg) && Directory.Exists(data);
        return new MachineSetupComponentStatus
        {
            Id = "config-data",
            Name = "配置与数据目录",
            Category = "数据",
            State = ok ? "ok" : "missing",
            CanInstall = true,
            Detail = $"Config={cfg}；Data={data}",
            FixHint = "换机请连同 data/ 与 Config/ 一起拷贝，保留积分与策略"
        };
    }

    // ---------- helpers ----------

    private static MachineSetupStepResult Fail(string id, string name, string reason, string? hint = null)
        => new()
        {
            ComponentId = id,
            Name = name,
            Success = false,
            Message = hint ?? "",
            FailureReason = reason
        };

    private void EnsureKitScaffold()
    {
        try
        {
            Directory.CreateDirectory(Path.Combine(KitRoot, "VBCable"));
            Directory.CreateDirectory(Path.Combine(KitRoot, "sidecars"));
            Directory.CreateDirectory(Path.Combine(KitRoot, "Kuaishou"));
            Directory.CreateDirectory(Path.Combine(KitRoot, "AiSpeech", "Ollama"));
            Directory.CreateDirectory(Path.Combine(KitRoot, "AiSpeech", "GPT-SoVITS"));
            var readme = Path.Combine(KitRoot, "README.txt");
            File.WriteAllText(readme, BuildReadme(), Encoding.UTF8);

            var manifestPath = Path.Combine(KitRoot, "manifest.json");
            if (!File.Exists(manifestPath))
            {
                var manifest = new MachineSetupManifest
                {
                    Version = 1,
                    Description = "换机安装包清单：把下列文件放入对应子目录后，在后台点一键安装",
                    Components =
                    {
                        new MachineSetupManifestItem
                        {
                            Id = "vbcable",
                            Name = "VB-CABLE",
                            RelativePath = "VBCable/VBCABLE_Setup_x64.exe",
                            Kind = "installer_exe",
                            InstallArgs = "-i -h",
                            Required = true
                        },
                        new MachineSetupManifestItem
                        {
                            Id = "douyin-sidecar",
                            Name = "抖音弹幕助手",
                            RelativePath = "sidecars/抖音直播弹幕助手.exe",
                            Kind = "copy_file",
                            TargetRelativePath = "抖音直播弹幕助手.exe",
                            Required = true
                        },
                        new MachineSetupManifestItem
                        {
                            Id = "kugou-sidecar",
                            Name = "酷狗API",
                            RelativePath = "sidecars/酷狗api_v1.5.exe",
                            Kind = "copy_file",
                            TargetRelativePath = "酷狗api_v1.5.exe",
                            Required = true
                        },
                        new MachineSetupManifestItem
                        {
                            Id = "kgapijs",
                            Name = "kgapijs",
                            RelativePath = "sidecars/kgapijs",
                            Kind = "copy_dir",
                            TargetRelativePath = "kgapijs",
                            Required = true
                        },
                        new MachineSetupManifestItem
                        {
                            Id = "kuaishou",
                            Name = "快手侧车",
                            RelativePath = "Kuaishou/ks-ui-server.jar",
                            Kind = "copy_file",
                            TargetRelativePath = "sidecars/kuaishou/ks-ui-server.jar",
                            Required = false
                        }
                    }
                };
                File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest, JsonOpts), Encoding.UTF8);
            }
        }
        catch (Exception ex)
        {
            _log.Warn($"创建换机安装包目录失败: {ex.Message}");
        }
    }

    private static string BuildReadme() => """
换机安装包目录（Deploy/MachineSetup）
=====================================

打包旧电脑时，请把下列文件放进本目录对应子文件夹，拷到新电脑点歌系统旁，
然后打开管理后台 → 系统设置 → 换机部署 → 一键安装。

1) VBCable/
   - VBCABLE_Setup_x64.exe（https://vb-audio.com/Cable/）

2) sidecars/
   - 抖音直播弹幕助手.exe
   - 酷狗api_v1.5.exe
   - kgapijs/ 整个文件夹（内含 app.js）

3) Kuaishou/（可选）
   - ks-ui-server.jar
   - jre/ 便携 Java（可选）

4) AiSpeech/（AI 语音，可选但推荐）
   - Ollama/OllamaSetup.exe  或  Ollama/ 便携目录（含 ollama.exe）
   - GPT-SoVITS/ 完整工程（必须含 service/main.py）
   说明：模型用「一键 pull」按 Config 里 aiSpeech.model 下载（需联网）；
         conda 环境 GPTSoVits 需在新机自行安装，安装程序不会替你装 Anaconda。

另外请单独备份：
   - 点歌系统 Config/ 与 data/
   - 抖音/酷狗登录态、快手 Cookie
   - （可选）Ollama 模型缓存目录，可避免重新 pull：
     %USERPROFILE%\.ollama\models

VB-CABLE / Ollama 安装需要管理员权限；CABLE 装完建议重启。
""";

    private List<string> BuildPackHints() => new()
    {
        $"安装包根目录：{KitRoot}",
        "必带：VBCable、抖音弹幕助手、酷狗 api + kgapijs、Config/data",
        "AI：OllamaSetup 或便携 ollama + GPT-SoVITS 工程；模型可联网 pull 或拷 .ollama/models",
        "AI：新机需有 conda 环境 GPTSoVits（安装包不自带 Anaconda）",
        "快手：ks-ui-server.jar + Java17",
        "登录态换机通常要重登"
    };

    private static bool IsVbCableInstalled()
    {
        try
        {
            for (var i = 0; i < WaveOut.DeviceCount; i++)
            {
                var name = WaveOut.GetCapabilities(i).ProductName ?? "";
                if (name.Contains("cable", StringComparison.OrdinalIgnoreCase)
                    || name.Contains("vb-audio", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }
        catch
        {
            // ignore
        }

        return false;
    }

    private string? FindVbCableSetup()
    {
        var dir = Path.Combine(KitRoot, "VBCable");
        if (!Directory.Exists(dir))
        {
            return null;
        }

        return Directory.EnumerateFiles(dir, "*.exe", SearchOption.AllDirectories)
            .OrderByDescending(f => f.Contains("x64", StringComparison.OrdinalIgnoreCase))
            .ThenBy(f => f.Length)
            .FirstOrDefault(f =>
            {
                var n = Path.GetFileName(f);
                return n.Contains("VBCABLE", StringComparison.OrdinalIgnoreCase)
                       || n.Contains("VB-Cable", StringComparison.OrdinalIgnoreCase)
                       || (n.Contains("Cable", StringComparison.OrdinalIgnoreCase)
                           && n.Contains("Setup", StringComparison.OrdinalIgnoreCase));
            });
    }

    private static string? FindInKit(string root, string fileName)
    {
        var direct = Path.Combine(root, fileName);
        if (File.Exists(direct))
        {
            return direct;
        }

        return Directory.Exists(root)
            ? Directory.EnumerateFiles(root, fileName, SearchOption.AllDirectories).FirstOrDefault()
            : null;
    }

    private static string? FindKgapiJsInKit(string sidecarsRoot)
    {
        var direct = Path.Combine(sidecarsRoot, SidecarLocator.KugouJsFolderName);
        if (SidecarLocator.IsKgapiJsReady(direct))
        {
            return direct;
        }

        if (!Directory.Exists(sidecarsRoot))
        {
            return null;
        }

        foreach (var dir in Directory.EnumerateDirectories(sidecarsRoot, SidecarLocator.KugouJsFolderName, SearchOption.AllDirectories))
        {
            if (SidecarLocator.IsKgapiJsReady(dir))
            {
                return dir;
            }
        }

        return null;
    }

    private static bool IsJavaAvailable(out string detail)
    {
        detail = "";
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "java",
                Arguments = "-version",
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var p = Process.Start(psi);
            if (p == null)
            {
                return false;
            }

            var err = p.StandardError.ReadToEnd();
            var stdout = p.StandardOutput.ReadToEnd();
            p.WaitForExit(5000);
            detail = (err + stdout).Trim();
            if (detail.Length > 200)
            {
                detail = detail[..200];
            }

            return p.ExitCode == 0 || detail.Contains("version", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            detail = ex.Message;
            return false;
        }
    }

    private string? FindOllamaSetup()
    {
        var dir = Path.Combine(KitRoot, "AiSpeech", "Ollama");
        if (!Directory.Exists(dir))
        {
            return null;
        }

        return Directory.EnumerateFiles(dir, "*.exe", SearchOption.AllDirectories)
            .FirstOrDefault(f =>
            {
                var n = Path.GetFileName(f);
                return n.Contains("OllamaSetup", StringComparison.OrdinalIgnoreCase)
                       || n.Contains("Ollama", StringComparison.OrdinalIgnoreCase)
                          && n.Contains("Setup", StringComparison.OrdinalIgnoreCase);
            });
    }

    private string? FindPortableOllamaDir()
    {
        var dir = Path.Combine(KitRoot, "AiSpeech", "Ollama");
        if (!Directory.Exists(dir))
        {
            return null;
        }

        if (File.Exists(Path.Combine(dir, "ollama.exe")))
        {
            return dir;
        }

        return Directory.EnumerateFiles(dir, "ollama.exe", SearchOption.AllDirectories)
            .Select(Path.GetDirectoryName)
            .FirstOrDefault(d => !string.IsNullOrWhiteSpace(d));
    }

    private string? FindGptSovitsKitDir()
    {
        var direct = Path.Combine(KitRoot, "AiSpeech", "GPT-SoVITS");
        if (File.Exists(Path.Combine(direct, "service", "main.py")))
        {
            return direct;
        }

        var root = Path.Combine(KitRoot, "AiSpeech");
        if (!Directory.Exists(root))
        {
            return null;
        }

        return Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories)
            .FirstOrDefault(d => File.Exists(Path.Combine(d, "service", "main.py")));
    }

    private static bool ProbeHttpOk(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return false;
        }

        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            using var resp = client.GetAsync(url).GetAwaiter().GetResult();
            return resp.IsSuccessStatusCode || (int)resp.StatusCode is >= 200 and < 500;
        }
        catch
        {
            return false;
        }
    }

    private bool ProbeOllamaHasModel(string model, out string detail)
    {
        detail = "";
        try
        {
            var baseUrl = string.IsNullOrWhiteSpace(_config.Settings.AiSpeech.OllamaUrl)
                ? "http://127.0.0.1:11434"
                : _config.Settings.AiSpeech.OllamaUrl.TrimEnd('/');
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            var json = client.GetStringAsync(baseUrl + "/api/tags").GetAwaiter().GetResult();
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("models", out var models)
                || models.ValueKind != System.Text.Json.JsonValueKind.Array)
            {
                detail = "Ollama /api/tags 无 models 字段";
                return false;
            }

            var want = model.Trim();
            var wantBase = want.Split(':')[0];
            foreach (var m in models.EnumerateArray())
            {
                var name = m.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                if (string.Equals(name, want, StringComparison.OrdinalIgnoreCase)
                    || name.StartsWith(want + ":", StringComparison.OrdinalIgnoreCase)
                    || name.StartsWith(wantBase + ":", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(name, wantBase, StringComparison.OrdinalIgnoreCase))
                {
                    detail = $"已安装模型：{name}";
                    return true;
                }
            }

            detail = $"已连接 Ollama，但列表中无 {want}";
            return false;
        }
        catch (Exception ex)
        {
            detail = "无法查询模型：" + ex.Message;
            return false;
        }
    }

    private static void TryStartOllamaServe(string exe)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = exe,
                Arguments = "serve",
                WorkingDirectory = Path.GetDirectoryName(exe) ?? "",
                UseShellExecute = false,
                CreateNoWindow = true
            });
        }
        catch
        {
            // ignore — pull 阶段会再探测
        }
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(source, file);
            var dest = Path.Combine(destination, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(file, dest, true);
        }
    }
}
