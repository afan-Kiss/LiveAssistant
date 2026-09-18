# 直播点歌系统 — 启动编排扫描报告

扫描时间：2026-09-18  
扫描根目录：`E:\我的源码目录\`  
原则：仅读取源码与配置，本报告阶段不修改业务代码。

---

## 1. 扫描到的软件列表

| # | 项目名称 | 实际路径 | 说明 |
|---|----------|----------|------|
| 1 | LiveAssistant（点歌系统） | `E:\我的源码目录\抖音弹幕点歌系统` | 当前工作区；另有镜像目录 `E:\我的源码目录\LiveAssistant` |
| 2 | 抖音 CDP 弹幕 | `E:\我的源码目录\抖音cdp弹幕` | Go 模块 `douyin-cdp-danmaku` |
| 3 | 酷狗 API | `E:\我的源码目录\酷狗协议` | 实际入口为 `KgDesktop`（`酷狗api_v1.5.exe`） |
| 4 | 猫眼票房助手 | `E:\我的源码目录\猫眼票房助手` | **未找到**名为 `douyin-maoyan-overlay` 的目录；对应此 Node 票房 API |

---

## 2. 各软件启动入口 / 运行环境 / 端口 / 依赖

### 2.1 LiveAssistant

| 项 | 值 |
|----|----|
| 正式入口 | `publish\LiveAssistant-one\LiveAssistant.exe`（优先） |
| 开发入口 | `dotnet run --project LiveAssistant` / `LiveAssistant\bin\Debug\net8.0-windows\LiveAssistant.exe` |
| 运行环境 | .NET 8 Windows Forms（`net8.0-windows`） |
| 管理后台端口 | `5088`（`admin.port`） |
| 依赖 | CDP `:17891`、酷狗 `:17888`、可选猫眼互动（`movieInteraction.serverBaseUrl`，当前配置为空） |
| 单实例 | `Global\LiveAssistant.SingleInstance` Mutex |

配置摘录（`LiveAssistant\Config\appsettings.json`）：

- `douyin.baseUrl` = `http://127.0.0.1:17891`
- `kugou.baseUrl` = `http://127.0.0.1:17888`
- `douyin.douyinExePath` = `cdp-danmaku.exe`
- `kugou.kugouExePath` = `酷狗api_v1.5.exe`

### 2.2 抖音 CDP

| 项 | 值 |
|----|----|
| 正式入口 | `E:\我的源码目录\抖音cdp弹幕\bin\cdp-danmaku.exe`（另有根目录 `抖音直播弹幕v1.3.exe`） |
| 开发入口 | `go run ./cmd/server`（需 Go） |
| 运行环境 | Go 1.23+（`go.mod`）；本机预编译产物可直接运行 |
| 端口 | `127.0.0.1:17891`（`internal/config.DefaultAddr`） |
| 健康检查 | `GET http://127.0.0.1:17891/api/health` → JSON 含 `login_ok` |
| 依赖 | Google Chrome（CDP / go-rod） |
| 现有脚本 | 点歌仓库 `启动抖音API.cmd`（查找 `cdp-danmaku.exe`） |

注意：`sidecars\抖音直播弹幕助手.exe` 被 `SidecarLocator` **排除**（旧助手），不能当作 CDP。

### 2.3 酷狗 API

| 项 | 值 |
|----|----|
| 正式入口 | `E:\我的源码目录\酷狗协议\KgDesktop\build\bin\酷狗api_v1.5.exe` |
| 仓库侧车 | `抖音弹幕点歌系统\sidecars\酷狗api_v1.5.exe` + `kgapijs\` |
| 开发入口 | KgDesktop（Wails/Go）；另有 `KgApiServer`（`kgapiserver.exe`，同端口族） |
| 运行环境 | Go + 内嵌 Node `kgapijs` |
| 端口 | `127.0.0.1:17888`（`KG_ADDR` 默认） |
| 健康检查 | `GET http://127.0.0.1:17888/health` |
| 依赖 | 同目录 `kgapijs\`（含 `app.js`） |
| Python | **主路径不依赖 Python**（部分采样脚本可用 Python，非启动必需） |

### 2.4 猫眼票房助手（用户所称 Overlay）

| 项 | 值 |
|----|----|
| 正式入口 | `dist\maoyan-box\start.bat` 或 `dist\猫眼票房助手\启动.bat` → `node index.js` |
| 开发入口 | 源码根目录 `start.bat` / `npm start` |
| 运行环境 | Node.js + Express + Playwright |
| 端口 | `8765`（`config.ini` `[server] port=`） |
| 健康检查 | `GET http://127.0.0.1:8765/health` |
| 依赖 | Node、`node_modules`、Chrome（签名抓取） |
| Electron | **无** Electron 构建；为 Node 终端服务，非独立 Overlay GUI |

---

## 3. 运行环境版本（本机实测）

| 组件 | 实测 | 源码要求 | 结论 |
|------|------|----------|------|
| .NET SDK | 8.0.406 | `net8.0-windows` | 满足 |
| .NET Runtime | 8.0.13 / 8.0.30（含 WindowsDesktop、AspNetCore） | WinExe + AspNetCore | 满足 |
| Node | v22.22.0 | 猫眼 Node 服务 | 满足 |
| npm | 10.9.4 | — | 满足 |
| Go | 1.24.5（`C:\go\bin\go.exe`，**未在 PATH**） | CDP `go 1.23.0` | 正式 exe 足够；源码编译需把 Go 加入 PATH |
| Python | 3.12.10 | 酷狗主路径不依赖 | 可选 |
| Chrome | 133.0.6943.99（`%LOCALAPPDATA%\Google\Chrome\Bin\chrome.exe`） | CDP / 猫眼 Playwright | **偏旧**，建议升级到当前稳定版（不自动升级） |

---

## 4. 当前进程/端口快照（扫描时）

- `:17891` CDP — 未监听
- `:17888` 酷狗 — 未监听
- `:8765` 猫眼 — 未监听
- 存在无关进程 `douyin-danmaku`（`抖音网页弹幕`，非 CDP 17891）

---

## 5. 启动编排目标（后续实现）

顺序：CDP → 酷狗 → 猫眼 → LiveAssistant  
入口：`start-live-system.bat` / `StartLiveSystem.exe`  
配置：`launcher.json`（路径不写死，相对路径 + 源码扫描）
