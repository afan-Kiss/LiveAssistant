# 开发构建规范

修改任何代码前先读本文，并读 `PROJECT_MAP.md` 确认当前任务属于哪个项目。

扫描日期：2026-09-19。

## 当前工作区边界

项目名称：24小时直播助手（仓库目录 `抖音弹幕点歌系统`）

技术栈：C# / .NET 8 / WinForms。解决方案 `LiveAssistant.sln`。无本仓库 `package.json`、`go.mod`、`docker-compose`、CI 配置。

开发入口：

- 点歌主程序：`dotnet build LiveAssistant/LiveAssistant.csproj`
- 启动编排：已有 exe 时直接跑 `start-live-system.bat`；没有 exe 时只 `dotnet build StartLiveSystem/StartLiveSystem.csproj -c Release`

正式发布入口：`scripts\publish-single.cmd`（内部 `dotnet publish` 单文件到 `publish/LiveAssistant-one`）。带校验与离线状态：`scripts\publish-and-verify.cmd`。只在发布模式使用。

测试入口：`dotnet test LiveAssistant.Tests/LiveAssistant.Tests.csproj`。快速验证只加 `--filter` 跑相关测试，不跑全量，不跑 `scripts\e2e-newpc-verify.py`。

打包入口：同上正式发布入口。不要手写另一套 `dotnet publish`。

禁止修改区域：

- `bin/`、`obj/`、`publish/`、`sidecars/` 里的编译产物和用户 `data/`
- `Config/DeployCredentials.local.json` 及任何密钥、Cookie、隧道密码
- 兄弟目录源码（见 `PROJECT_MAP.md`），除非用户明确说改那个项目
- 版本号文件：改版本必须同时改 `.cursor/rules/product-version.mdc` 列出的全部位置，当前统一版本 **1.0.0**

依赖组件（由 `launcher.json` 拉起，不在本仓库内编译）：

- 抖音 CDP：`http://127.0.0.1:17891/api/health`
- 酷狗 API：`http://127.0.0.1:17888/health`
- 猫眼 Overlay：`http://127.0.0.1:8765/health`

运行端口：

- 管理后台 `5088`，路径 `/diangexitong`，探活 `http://127.0.0.1:5088/diangexitong/api/ping`
- 云端隧道远端默认 `15088`
- 快手 `18900`（默认关闭）、Ollama `11434`。旧网页弹幕 `4723` 已废弃，不要再接

启动方式：

- 用户说「启动软件」且没点名别的项目：只走本仓库快速验证，用已有 `StartLiveSystem.exe` 或 `LiveAssistant.exe`。缺文件才 `dotnet build` 被改到的那一个工程。
- 不要因为启动就去编 CDP、猫眼、酷狗。

本仓库没有 `dist/`、`release/` 作为发布根。发布目录是 `publish/`。没有 GitHub Actions / docker-compose。

## 三种工作模式

先看用户原话，再选一种。没说发布，就不要发布。

### 模式 1：快速验证

触发：启动软件、跑一下、测试一下、看看修改有没有问题、验证一下。默认就是这个模式。

目标：确认刚改的代码能编译、能起来。控制在几十秒到 1 分钟。

禁止：完整发布、安装包、`electron-builder`、`dotnet publish`、全量测试、E2E、上传更新服务器。

做法：

- C#：`dotnet build` 被改的那个 csproj。不要 `dotnet publish`。
- Node/Electron：`npm start` 或已有的 dev 脚本。不要 `electron-builder`。
- Go：`go build` 已有入口。不要重新下载依赖。CDP 必须 `go build -ldflags "-H windowsgui" -o bin/cdp-danmaku.exe ./cmd/native`。
- Python：直接跑入口脚本。

验证只做相关模块测试、核心能否启动、一个健康检查。回复只写：修改、验证、结果（通过/失败）。不要贴大段日志。

### 模式 2：编译发更新

触发：编译发更新、发布版本、上传更新、发正式版、打包。

顺序：

1. 看 git 状态。不要 `git clean`、`git reset`。
2. 完整测试：C# `dotnet test`；Go `go test ./...`；Node 用该项目自己的 test 脚本。
3. 正式构建：C# 走本仓库 `scripts\publish-single.cmd`；Go 按该项目正式 build；Electron 才允许 `electron-builder` / 该项目的 `build:release`。
4. 记录版本号、commit hash、构建时间、更新包路径。版本仍遵守 `product-version.mdc`。
5. 检查 exe 能启动、版本号、健康接口。
6. 出发布报告。

回复写：修改文件、测试结果、构建产物、版本、commit、更新包、git 状态。

### 模式 3：线上排查

触发：线上有问题、崩了、用户打不开、日志分析。

不要重新编译。先读日志和堆栈，核对正在跑的版本，定位原因，再做最小修复。修完若要验证，回到模式 1，不要直接发布。

## 禁止行为

除非用户当次明确要求，否则不要：

- 改一点界面或一个 C# 文件就 `dotnet publish` / `electron-builder`
- 小修复跑全部 E2E
- 删除 `bin`、`obj`、`node_modules`、Go 缓存
- `git clean`、`git reset`
- 发现别的项目有问题就顺手改。只记录「发现，不处理」

优先增量编译。

## 范围

每次先对上 `PROJECT_MAP.md` 里的一个项目，只改那一个目录。用户没点名时，默认就是本仓库的 LiveAssistant / StartLiveSystem，不要把兄弟项目一起完整构建。
