# AI Speech Health Check Report

Generated: 2026-09-16

Commit: `f0cb8f9` — `fix: add AI speech startup health check and auto recovery`

## Goal

用户打开 LiveAssistant 后，**无需手动检查** Ollama / 模型 / GPT-SoVITS。  
AI 语音页自动显示可用或不可用；主程序启动**永不被** AI 依赖阻塞。

## Modified Files

| File | Change |
|------|--------|
| `LiveAssistant/Services/AiSpeech/AiSpeechHealthChecker.cs` | **新增** 健康检查器 |
| `LiveAssistant/Services/AiSpeech/AiSpeechCoordinator.cs` | 接入启动探测、运行恢复、失败后自动再检 |
| `LiveAssistant/Services/AiSpeech/AiSpeechModels.cs` | Status 增加模型/摘要/AiReady |
| `LiveAssistant/UI/MainForm.cs` | 服务状态区 +「刷新AI状态」按钮 |
| `LiveAssistant.Tests/AiSpeechHealthCheckerTests.cs` | 单测：全挂 / 模型缺失 / 全就绪 / 声音未加载 |
| `AI_SPEECH_HEALTHCHECK_REPORT.md` | 本报告 |

**未修改：** 抖音连接、弹幕采集、点歌、礼物积分、登录。  
**不自动启动：** `ollama.exe`、GPT-SoVITS 脚本。

## Check Flow

```
LiveAssistant 启动
  → AiSpeechCoordinator 构造（不 await 网络）
  → StartBackgroundStartupChecks（Task.Run）
       立即 CheckOnce
       失败 → 30s 后再试，最多 3 次
       成功 → 停止启动重试
  → StartBackgroundRuntimeRecovery
       每 30s：若未 FullyReady → 再 CheckOnce
  → UI StatusChanged 刷新「AI语音状态」

手动：点击「刷新AI状态」→ RefreshHealthAsync → 立刻再检
任务失败（Ollama/TTS）：标记「AI服务异常」→ 后台再检，不崩溃
```

### Probe details

1. **Ollama**：`GET /api/tags`（经 `ListModelsAsync`）  
   - 成功 → `OLLAMA_AVAILABLE` + 模型列表  
   - 失败 → 「Ollama未启动」
2. **模型**：配置名（如 `qwen3:8b`）是否在 tags 中  
   - 缺失 → 「当前AI模型未安装」+ 已安装列表
3. **GPT-SoVITS**：`GET /health`  
   - 不可达 → 「语音服务未启动」  
   - `tts_ready=false` → TTS 不可用  
   - `voice_ready=false` → 「声音模型未加载」

## Logs

- `AI_HEALTH_CHECK reason=... ollama=... model_ok=... tts=... voice_ready=...`
- `AI_STARTUP_CHECK ollama=... model=... tts=... voice=...`
- `OLLAMA_AVAILABLE`（Ollama 在线时）

无完整 Prompt / 弹幕正文。

## UI

- Ollama / GPT-SoVITS / 声音：● 正常 或 明确失败文案  
- 模型状态：可用 / 未安装（附已安装列表）  
- AI语音状态：✅ AI模型正常 / ✅ 声音正常 / ✅ 可以发言（或 ❌）  
- 按钮：**刷新AI状态**（无需重启软件）

## Exception Handling

| Case | Behavior |
|------|----------|
| Ollama 关闭启动 | 主程序正常；AI 显示不可用 |
| TTS 关闭启动 | 主程序正常；提示语音服务未启动 |
| 模型未安装 | 不崩溃；提示缺失 + 已安装列表 |
| 运行中 Ollama 掉线 | 任务失败隔离；显示 AI 服务异常；30s 自动恢复探测 |
| 服务恢复 | 自动变绿 / 可发言；也可点刷新 |

## Test Results

### Unit (mocked HTTP)

| Case | Result |
|------|--------|
| 双服务全挂 | PASS — FullyReady=false，提示 Ollama 未启动 |
| Ollama 在线但缺 qwen3:8b | PASS — 列出已安装模型 |
| 全就绪 | PASS — FullyReady + AI_STARTUP_CHECK + OLLAMA_AVAILABLE |
| voice_ready=false | PASS — 声音模型未加载 |
| 既有 AI Speech 稳定性单测 | PASS |

### Live machine checklist

| # | Scenario | Result |
|---|----------|--------|
| 1 | Ollama 开着启动 | 需本机服务；逻辑已覆盖 FullyReady |
| 2 | Ollama 关闭启动 | 单测模拟 PASS；主程序不阻塞 |
| 3 | 恢复后自动恢复 | 运行循环 30s；单测覆盖 CheckOnce 恢复路径 |
| 4 | TTS 关闭 | 单测模拟 PASS |
| 5 | TTS 恢复 | 同上 |
| 6 | 模型不存在 | 单测 PASS |

> 真机听感仍依赖本机 Ollama `:11434` 与 GPT-SoVITS `:9880`。打开软件后看 AI 语音页是否自动变绿即可。

## Publish

- EXE: `publish\LiveAssistant-one\LiveAssistant.exe`
- 覆盖前请保留 `publish\backup\...` 备份

## Outcome

用户只需打开 LiveAssistant → 打开「AI语音」页：  
看到 ✅ 可以发言，或明确的 ❌ 原因与「刷新AI状态」入口；**不必再手动 curl / 查模型**。
