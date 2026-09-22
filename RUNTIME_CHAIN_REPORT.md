# RUNTIME_CHAIN_REPORT

生成时间：2026-09-22  
范围：静态结构核对 + 本机端口探活（**未改代码、未强制拉起进程**）

---

## 1. 项目入口

| 项 | 结论 |
|----|------|
| 主程序入口 | `LiveAssistant\Program.cs` |
| UI 框架 | WinForms（`UseWindowsForms`，`[STAThread]`，`Application.Run(MainForm)`） |
| 启动编排 | `new LiveAppHost()` → `MainForm` |
| 单实例 | Mutex `Global\LiveAssistant.SingleInstance` |
| 启动附加 | `PackagedContent.EnsureExtracted()`、`SidecarBootstrap.EnsureReady()` |
| 一键启动器 | `StartLiveSystem\Program.cs` + 根目录 `launcher.json` / `start-live-system.bat` |

启动器顺序（`StartLiveSystem`）：

1. Douyin CDP  
2. 酷狗 API  
3. 猫眼 Overlay  
4. LiveAssistant 主程序  

---

## 2. 管理后台 `:5088`

| 项 | 值 |
|----|-----|
| 实现 | `LiveAssistant\Admin\AdminWebHost.cs`（ASP.NET Core Minimal API） |
| 绑定 | `http://127.0.0.1:{port}`，默认 **5088** |
| PathBase | `/diangexitong` |
| 探活 | `GET /api/ping`、`GET /health` |
| 静态页 | `Admin\wwwroot\index.html`（merged 有；约 87KB） |
| 配置键 | `admin.port` / `admin.path`（见 `Config\appsettings.json`） |
| 云端隧道 | `AdminTunnelService`，远端默认 `15088` |

本机探活（2026-09-22）：

```text
http://127.0.0.1:5088/diangexitong/api/ping  → DOWN（超时，进程未运行）
```

结构：**代码完整**。当前断点 = **主程序未启动**，不是 Admin 源码缺失。

---

## 3. 弹幕 CDP `:17891`

| 项 | 值 |
|----|-----|
| 健康检查 | `http://127.0.0.1:17891/api/health` |
| 主程序默认 | `douyin.baseUrl = http://127.0.0.1:17891` |
| 期望 exe | `cdp-danmaku.exe`（`SidecarLocator.PreferredDouyinFileName`） |
| 弹幕消费 | `DanmakuService` → `LiveAppHost` → `SongRequestService` |
| launcher | `DouyinCDP.Port = 17891`，`WaitLogin=true`，字段 `login_ok` |

本机探活：

```text
http://127.0.0.1:17891/api/health  → DOWN
```

结构：

- merged **源码侧对接完整**（已切到 17891，旧 4723 废弃）。
- merged **二进制缺失**：`sidecars\cdp-danmaku.exe` 不存在。
- 旧部署可用：`D:\TestXiangyuLive\LiveAssistant\cdp-danmaku.exe`（约 15.9MB）及 `sidecars\` 副本。

当前断点 = **CDP 进程/exe 未就位**。

---

## 4. 酷狗 Sidecar `:17888`

| 项 | 值 |
|----|-----|
| 健康检查 | `http://127.0.0.1:17888/health` |
| 主程序默认 | `kugou.baseUrl = http://127.0.0.1:17888` |
| 期望 exe | `酷狗api_v1.5.exe` |
| 旁路目录 | `kgapijs\`（入口判定文件 `app.js`；实际运行还需 `server.js` + Node 依赖） |
| 业务调用 | `KugouService`（搜索 / 取链 / VIP） |

本机探活：

```text
http://127.0.0.1:17888/health  → DOWN
```

结构：

| 资产 | merged | 旧部署 D:\TestXiangyuLive |
|------|--------|---------------------------|
| 酷狗 exe | 缺失 | 有（LiveAssistant 旁） |
| kgapijs\app.js | 有 | 有 |
| kgapijs\server.js / main.js / package.json | **无** | 有 |
| kgapijs\node_modules | **无** | 有（体量大） |

注意：`SidecarLocator.IsKgapiJsReady` **只检查 `app.js`**，因此残片 kgapijs 可能“看起来就绪”，但真实搜歌仍会因缺 `server.js`/依赖而失败。

当前断点 = **酷狗 exe + 完整 kgapijs 未回填到 merged 运行目录**。

---

## 5. AI：Ollama / GPT-SoVITS

| 项 | 值 |
|----|-----|
| 开关 | `aiSpeech.enabled`（旧部署配置为 **false**） |
| Ollama | `http://127.0.0.1:11434`，模型默认 `qwen3:8b` |
| GPT-SoVITS / TTS | `http://127.0.0.1:9880` |
| UI | `MainForm` 状态灯：Ollama / GPT-SoVITS |
| 装机逻辑 | `MachineSetupService` + `AiSpeechServiceLauncher` |
| 点歌主链路依赖 | **否**（口播增强；点歌播放走 NAudio，不依赖 AI） |

本机探活：

```text
http://127.0.0.1:11434/api/tags  → DOWN
http://127.0.0.1:9880/           → DOWN
```

结论：AI 链当前未运行；**不影响“点歌→队列→NAudio”核心恢复优先级**。

---

## 6. 其它运行链节点

| 组件 | 端口 | 本机 | 说明 |
|------|------|------|------|
| 猫眼 Overlay | 8765 | DOWN | StartLiveSystem 可选组件；旧部署有 `Maoyan\MaoyanOverlay-1.0.0.exe` |
| 快手 | 18900 | 未测 | 配置默认 `enabled: false` |
| 旧网页弹幕 | 4723 | 废弃 | 文档明确勿再接 |

---

## 7. 运行结构总图（旧版本）

```text
StartLiveSystem.exe / start-live-system.bat
        │
        ├─ cdp-danmaku.exe          :17891   弹幕 + mention
        ├─ 酷狗api_v1.5.exe + kgapijs :17888  搜歌/取链
        ├─ MaoyanOverlay            :8765    可选
        └─ LiveAssistant.exe (WinForms)
                ├─ DanmakuService ← 17891
                ├─ SongRequestService → KugouService ← 17888
                ├─ QueueService → PlaybackService (NAudio)
                ├─ AdminWebHost :5088 /diangexitong
                └─ AiSpeech（可选）← Ollama:11434 + TTS:9880
```

---

## 8. 结论

| 链路 | 源码结构 | 本机运行 | 断点 |
|------|----------|----------|------|
| WinForms 入口 | 完整 | 未主动长跑验证 | 可 `dotnet run`，但缺 sidecar 会告警 |
| Admin 5088 | 完整 | DOWN | 主程序未起 |
| CDP 17891 | 对接完整 | DOWN | **缺 cdp-danmaku.exe / 未启动** |
| 酷狗 17888 | 调用完整 | DOWN | **缺酷狗 exe + 完整 kgapijs** |
| Ollama / SoVITS | 代码在 | DOWN | 可选；配置默认关 |

**最优动作（仍属恢复，非开发）：** 从 `D:\TestXiangyuLive\LiveAssistant\` 把 sidecar 二进制与完整 `kgapijs` 回填到 `merged\sidecars\`（或直接用旧目录作运行根），再探活端口。
