# AI Speech V2 Final Stability Report

Generated: 2026-09-16

Base commit before this work: `e104ad8`  
This commit: `fix: improve AI speech runtime monitoring and stability`

## Summary

在不大改架构、不触碰抖音连接/弹幕采集/点歌/礼物积分/登录的前提下，补齐 AI 直播运行监控、重复回复防护、模型主动沉默 `[SKIP]`、播放/队列/提示词热加载稳定性，并增加压测与关停相关单测。

## Modified / Added Files

### Added

| File | Reason |
|------|--------|
| `LiveAssistant/Services/AiSpeech/AiSpeechMetrics.cs` | 运行指标：任务数、耗时均值、队列峰值、跳过/错误计数；`AI_METRIC_SNAPSHOT` 日志格式 |
| `LiveAssistant/Services/AiSpeech/AiReplyDuplicateGuard.cs` | 最近 20 条 AI 输出 hash / 5 分钟窗口，防连续同句 |
| `LiveAssistant.Tests/AiSpeechStabilityV3Tests.cs` | Metrics / 重复 / SKIP / 播放释放 / 队列抽干 / Prompt 保旧 / 10000 事件模拟 / 关停 |
| `AI_SPEECH_V2_FINAL_STABILITY_REPORT.md` | 本报告 |

### Modified

| File | Reason |
|------|--------|
| `AiSpeechCoordinator.cs` | 接入 Metrics / DuplicateGuard；SKIP 日志；5 分钟快照循环；播放失败回 Idle；退出写最终快照 |
| `AiSpeechPlayer.cs` | 播放硬超时；空音频拒绝；取消/失败/超时均释放 WaveOut/stream 并删 temp wav |
| `AiSpeechScheduler.cs` | 过期/丢弃计数；`TryDequeue` 返回本轮过期数，避免静默丢任务无指标 |
| `AiPromptStore.cs` | 稳定双读；半文件/空读/异常时 `AI_PROMPT_KEEP_OLD`；AtomicWrite 尽量 Replace |
| `ModelOutputSanitizer.cs` | 纯 `[SKIP]`/`SKIP` 拒绝进 TTS；修正 Assistant 前缀不误删整行口语 |
| `ContextPromptBuilder.cs` | 输出硬规则增加：无意义时可只回 `[SKIP]`、勿解释 |
| `AiSpeechModels.cs` | Status 增加 RuntimeStatus / 今日回复 / 成功率 / 平均延迟 / 最近错误 |
| `UserConversationContext.cs` | maxUsers 下限放开到 1（设置层仍 clamp≥16），修复 LRU 单测与小容量场景 |
| `MainForm.cs` | AI语音 Tab 增加运行状态区：AI状态/今日回复/成功率/平均延迟/队列/最近错误 |

**未修改：** 抖音连接、弹幕采集主链路、点歌、礼物积分、登录。

## Call Chain (unchanged)

```
Danmaku/Gift/Join/Like
  → LiveAppHost
  → AiSpeechCoordinator (filter / buffer)
  → AiSpeechScheduler
  → Worker → Ollama → Sanitizer(+SKIP/Dup) → GPT-SoVITS → AiSpeechPlayer
```

## Test Results

### Unit / stability (must-pass locally)

| Suite | Result |
|-------|--------|
| AiSpeech V2 + Stability V3 + related (45 tests) | **PASS** |
| 含：Metrics / Duplicate / SKIP / Player 取消·异常·超时 / 100 任务 20% 失败抽干 / Prompt KEEP_OLD / 10000 事件模拟 / 关停 Dispose | **PASS** |
| think 剥离 / JSON content-only / 泄露拒绝 | **PASS** |

### Live integration / E2E（本机服务）

| Item | Result | Note |
|------|--------|------|
| 测试声音 / 测试AI / 连续弹幕礼物 | **NOT RUN** | Ollama `:11434` 与 GPT-SoVITS `:9880` health 不可用；用例按约定 MarkNotRun，不记 FAIL |
| 关闭 Ollama / 关闭 TTS 真实联调 | **NOT RUN** | 同上（代码路径已有 Fail→Idle + 指标计数，由单测覆盖模拟失败） |
| Prompt 热加载真实编辑器写盘 | 单测覆盖 KEEP_OLD | 建议上机再点一次「编辑提示词」验证 |

### Acceptance checklist mapping

| # | Item | Result |
|---|------|--------|
| 1 | 测试声音 | NOT RUN（服务未起） |
| 2 | 测试AI | NOT RUN |
| 3 | 连续 20 条弹幕 | 单测队列/过滤 PASS；真机 NOT RUN |
| 4 | 连续 20 个礼物 | 同上 |
| 5 | 10000 事件压力 | **PASS**（模拟：队列≤5、内存增量有界） |
| 6 | 关闭 GPT-SoVITS | 代码路径+失败指标；真机 NOT RUN |
| 7 | 关闭 Ollama | 同上 |
| 8 | 播放中退出 | **PASS**（Dispose + Cancel 单测） |
| 9 | Prompt 热加载 | **PASS**（KEEP_OLD 单测） |
| 10 | think 泄露 | **PASS** |

## Performance Notes (simulation)

- 10000 混合事件：过滤+MaxQueue=5 → 实际「开口」次数显著少于事件数；`max_queue_seen ≤ 5`；内存增量断言 `< 80MB`。
- Metrics 快照每 5 分钟写 `ai_speech` 日志一行 `AI_METRIC_SNAPSHOT`（无 Prompt / 无上下文 / 无弹幕正文）。
- 播放硬超时默认 3 分钟，避免 Playing 永久卡住占用 `_executionGate`。

## Remaining Risks

1. **本机未起 Ollama/TTS 时无法完成真实听感验收** —— 开播前请先 `测试声音` / `测试AI`。
2. WaveOut 在极端驱动故障下仍可能慢于超时时间才返回；已用超时兜底，但建议直播机确认默认音频设备稳定。
3. 模型若输出 `[SKIP] 原因…`（带解释）不会按 SKIP 跳过，而会当普通文本消毒；规则已要求「只输出 [SKIP]」。
4. 重复回复防护基于规范化 hash，近义不同句不会拦截（仅防几乎相同连发）。

## Publish

- EXE: `E:\我的源码目录\抖音弹幕点歌系统\publish\LiveAssistant-one\LiveAssistant.exe`
- 覆盖前备份目录见发布步骤输出的 `publish\backup\...`

## Can go live?

**可以进入真实直播试跑**，前提：

1. Ollama + 目标模型已加载  
2. GPT-SoVITS `:9880` + `my_voice` 就绪  
3. 开播前在 AI语音 Tab 点一次「测试声音」「测试AI」均为成功  
4. 观察面板：AI状态会回到「空闲」、队列不清零堆积、最近错误无持续刷屏  
