# RECOVERY_STATUS

生成时间：2026-09-22  
项目：LiveAssistant（24小时直播助手 / 抖音弹幕点歌系统）  
唯一源码根：`E:\恢复重建\LiveAssistant\merged`  
旧可运行包：`D:\TestXiangyuLive`  

原则：**恢复旧项目，不重新设计、不开发新功能。**

配套报告：

- `BUILD_RECOVERY_REPORT.md`
- `RUNTIME_CHAIN_REPORT.md`
- `DEPLOY_COMPARE.md`
- `DIANGE_FLOW_CHECK.md`

---

## 1. 当前源码完整度

| 维度 | 评估 |
|------|------|
| 解决方案编译 | **通过**（0 错误） |
| C# 业务源码 | **高**（166 个 cs，无空文件） |
| 点歌相关单测 | **35/35 通过** |
| 管理后台源码 + wwwroot | **有** |
| 配置模板（appsettings / AiSpeech prompts） | **有** |
| sidecar 二进制（CDP / 酷狗） | **缺失** |
| kgapijs | **残片**（缺 server.js / node_modules 等） |
| Deploy/MachineSetup 装机资源 | **基本缺失**（旧部署有） |
| launcher.json 路径 | **仍指向事故前 `E:\我的源码目录\...`** |
| 本机 git CLI | 不可用（有 `.git` 目录但不影响编译） |

**完整度结论：**  
主程序 **源码态 ≈ 可恢复完成**；**发布/运行态资产未齐**。不要因缺 exe 去重写业务。

---

## 2. 可运行程度

| 级别 | 状态 | 说明 |
|------|------|------|
| 编译运行（dotnet build） | ✅ | 已验证 |
| 单元测试（点歌） | ✅ | 已验证 |
| 本机完整点歌端到端 | ❌ | 17891/17888/5088/AI 均 DOWN；sidecar 未就位 |
| 旧目录直接运行 | 🟡 高概率可用 | `D:\TestXiangyuLive` 含 exe+kgapijs+launcher；本次未实际拉起长跑（按“只检查”） |

粗评：

- **源码恢复进度：约 90%+**
- **可独立运行进度（仅 merged）：约 40%**（缺侧车）
- **若复用 TestXiangyuLive 资产：可升到约 85%+**（仍需登录 CDP/酷狗与直播间实测）

---

## 3. 缺失模块

### 3.1 阻塞点歌主链（优先回填）

1. `cdp-danmaku.exe`  
2. `酷狗api_v1.5.exe`  
3. 完整 `kgapijs\`（server.js、main.js、package.json、node_modules、public）  
4. 可用的 `launcher.json`（路径改到当前盘符/目录，或直接用旧部署根）

### 3.2 非阻塞 / 可选

| 模块 | 状态 |
|------|------|
| 猫眼 Overlay | 旧部署有 exe；非点歌必需 |
| Ollama / GPT-SoVITS | 代码在；默认关闭；非点歌必需 |
| Admin 云端隧道 | 代码在；依赖本机 5088 先起来 |
| 快手弹幕 | 默认关闭 |
| Deploy\MachineSetup 全集 | 装机体验用；开发跑可用旧 sidecar 拷贝代替 |

### 3.3 不需要“重新开发”的模块

- `SongRequestService` 确认状态机  
- `KugouService` / `QueueService` / `PlaybackService(NAudio)`  
- `DanmakuService` + AdminWebHost  
- WinForms `MainForm`  

这些在 merged 中已存在且能编译。

---

## 4. 下一步需要修复什么（按恢复优先序）

> 下列均为 **资产/配置恢复**，不是新功能开发。

1. **从 `D:\TestXiangyuLive\LiveAssistant\` 回填 sidecar 到 `merged\sidecars\`**  
   - `cdp-danmaku.exe`  
   - `酷狗api_v1.5.exe`  
   - 整棵 `kgapijs\` 覆盖残片  

2. **选定一种运行根并统一 launcher**  
   - 方案 A：继续用 `D:\TestXiangyuLive` 做运行验收（最快验证旧行为）  
   - 方案 B：用 merged 的 `dotnet build` 输出 + 回填 sidecar（便于继续源码对照）  

3. **探活顺序**  
   - `17891/api/health`（登录）  
   - `17888/health`  
   - 启动主程序 → `5088/diangexitong/api/ping`  

4. **点歌实机烟测**（见 `DIANGE_FLOW_CHECK.md`）  
   - `点歌 xxx` → `确定` → 队列 → 出声  

5. **暂缓**  
   - AI 口播环境  
   - 重写/重构任何业务  
   - 大规模改配置键名或端口约定  

---

## 5. 最优恢复路线（一句话）

**先把 `D:\TestXiangyuLive` 的 sidecar/配置当作“删除前运行真相”，回填到 merged 或直接在该目录验收；merged 源码已能编译并通过点歌单测，勿重建点歌系统。**

---

## 6. 本轮已完成动作

- [x] `dotnet restore` / `dotnet build`（成功）  
- [x] 运行结构与端口检查（报告）  
- [x] 旧 exe 目录对照（报告）  
- [x] 点歌链路静态 + 单测断点分析（报告）  
- [x] 汇总本文件  

**未做（按你的要求）：** 改代码、重构、补业务、强制启动长跑进程。
