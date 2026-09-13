# LiveAssistant

Windows 原生直播互动助手（WinForms + Sidecar 架构）。

## 架构

- **LiveAssistant.exe** — 弹幕展示、点歌队列、音乐播放、自动回复
- **抖音 Sidecar** — `E:\我的源码目录\抖音网页弹幕`（HTTP API，默认 `127.0.0.1:4723`）
- **酷狗 Sidecar** — `E:\我的源码目录\酷狗协议`（HTTP API，默认 `127.0.0.1:17888`）

## 功能（P1）

- 主窗口三区：实时弹幕 / 系统消息 / 点歌队列
- 点歌识别：`点歌 泡沫` / `点歌:泡沫`
- 播放模式：点歌优先 / 随机播放 / **点歌+随机补位（默认）**
- 回复模板可配置：`Config/ReplyTemplates.json`
- SQLite 本地存储

## 构建

```bat
cd LiveAssistant
dotnet build
dotnet run --project LiveAssistant
```

## 配置

编辑 `LiveAssistant/Config/appsettings.json`：

- `douyin.webRid` — 直播间短号
- `douyin.baseUrl` — 抖音 API 地址
- `kugou.baseUrl` — 酷狗 API 地址
- `randomPlaylist.items` — 随机补位歌单

运行时配置会同步到 `data/appsettings.json`。

## 推送 GitHub

1. 复制 `Config/DeployCredentials.example.json` → `Config/DeployCredentials.local.json` 并填写凭证
2. 执行：

```powershell
.\scripts\push-github.ps1 "commit message"
```

真实凭证仅保存在 `DeployCredentials.local.json`（已在 `.gitignore`）。
