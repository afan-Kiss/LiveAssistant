# 项目地图

「启动软件」先在这里对上一个项目，只构建、只启动那一个。不要默认把下面全部正式打包。

未点名时，默认是本表第一行（当前工作区）。

| 项目 | 路径 | 用途 | 启动 | 依赖 | 开发构建 | 正式发布 |
| --- | --- | --- | --- | --- | --- | --- |
| LiveAssistant | `E:\我的源码目录\抖音弹幕点歌系统` | 点歌、弹幕、播放、管理后台 | `start-live-system.bat`；或已有 `LiveAssistant.exe` | CDP 17891、酷狗 17888、后台 5088 | `dotnet build LiveAssistant/LiveAssistant.csproj` | `scripts\publish-single.cmd` → `publish/LiveAssistant-one` |
| StartLiveSystem | 同上 `StartLiveSystem/` | 按 `launcher.json` 拉起各组件 | `start-live-system.bat` | 上表各 exe 已存在即可 | 缺 exe 才 `dotnet build StartLiveSystem/StartLiveSystem.csproj -c Release` | 不单独打安装包；产物可放 `publish/StartLiveSystem` |
| 抖音 CDP | `E:\我的源码目录\抖音cdp弹幕` | 抖音弹幕窗口与 HTTP API | 已有 `bin\cdp-danmaku.exe`；健康检查 `http://127.0.0.1:17891/api/health` | Go 1.23、Chrome | `go build -ldflags "-H windowsgui" -o bin/cdp-danmaku.exe ./cmd/native` | 同上，禁止 `./cmd/server`（会弹出黑 CMD） |
| 猫眼 Overlay | `E:\我的源码目录\抖音直播24小时无人直播` | 直播票房无边框展示 | `npm start` 或已有 `dist/MaoyanOverlay-1.0.0.exe`；健康检查 `http://127.0.0.1:8765/health` | Node、Electron、Playwright | `npm start` | `npm run build:release`（内含 electron-builder）。版本与点歌软件同为 1.0.0 |
| 酷狗 API | `E:\我的源码目录\酷狗协议` | 搜歌、播放。桌面端 `KgDesktop` | 已有 `KgDesktop\build\bin\酷狗api_v1.5.exe`；健康检查 `http://127.0.0.1:17888/health` | 旁路 `kgapijs`（Node），内网端口 16521 | 不改文件名版本。开发不要重下依赖 | `cd KgDesktop` 后 `build.bat 1.5` |
| Telegram 机器人 | `E:\我的源码目录\telegram_自动发消息` | Telegram 自动发言（Wails + React + Python） | 前端 `frontend` 里 `npm run dev`；后端直接跑 `python_backend` | Go、Node、Python | 前端 `npm run dev` / `npm run build`；相关测试 `npm run test:run` 或对应 pytest | `wails build`。不要用 electron-builder |
| 猫眼票房助手 | `E:\我的源码目录\猫眼票房助手` | 票房协议与打包，不是直播 Overlay | `node index.js`；打包 `pack.bat` | Node、Playwright | 直接跑 `index.js` | `pack.bat`。与 Overlay 不是同一个工程 |
| 抖音网页弹幕 | `E:\我的源码目录\抖音网页弹幕` | 旧侧车，端口 4723 | 不要再作为启动入口 | — | 不要为点歌系统重新编译 | 已废弃，点歌统一走 CDP 17891 |

未发现独立目录：小红书系统。不要为了凑地图去创建或搜索后顺手改别的仓库。

每个项目的 `bin`、`obj`、`node_modules`、`dist`、`publish`、`build/bin` 都是产物或缓存。清理必须用户当次明确要求。
