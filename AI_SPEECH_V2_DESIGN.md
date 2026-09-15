# AI 主播语音互动 V2 — 设计审计

Generated: 2026-09-15

## 1. 现有调用链（真实）

| 步骤 | 文件 | 类/方法 |
|------|------|---------|
| 弹幕采集 | `LiveAssistant/Services/DanmakuService.cs` | `DispatchItems` → `DanmakuReceived` |
| 宿主 | `LiveAssistant/Services/LiveAppHost.cs` | `OnDanmakuReceived` → `ProcessDanmakuAsync` |
| 过滤 | `LiveAssistant/Services/AiSpeech/AiDanmakuFilter.cs` | `Evaluate` |
| 协调 | `LiveAssistant/Services/AiSpeech/AiSpeechCoordinator.cs` | `TryEnqueueDanmaku` → `ProcessTaskAsync` |
| LLM | `LiveAssistant/Services/AiSpeech/OllamaClient.cs` | `GenerateAsync`（已 `think=false`） |
| 清洗 | `LiveAssistant/Services/AiSpeech/SpeechTextCleaner.cs` | `Clean` |
| TTS | `LiveAssistant/Services/AiSpeech/GptSovitsClient.cs` | `SynthesizeAsync` |
| 播放 | `LiveAssistant/Services/AiSpeech/AiSpeechPlayer.cs` | `PlayWavAsync` |

弹幕用户字段：`DanmakuItem.UserId` / `Nickname` / `Content` / `MsgType` / `MsgId`

## 2. 礼物 / 进房 / 点赞真实入口

### 礼物
- 入口：`GiftCollectorService` → `GiftService.HandleGiftEvent` → `GiftReceived`
- 模型：`GiftEvent`（`UserId`, `Nickname`, `GiftId`, `GiftName`, `Count`, `Value`, `RepeatCount`, …）
- **稳定用户 ID**：`GiftEvent.UserId`（来自 `IdStr` 或 `Id`）
- 当前：仅 `_health.RecordGift()`，**未进 AI**

### 进房
- 入口：弹幕侧车 `MsgType=member` → `WelcomeService.HandleMemberJoin`
- 模型：`DanmakuItem`（`UserId`, `Nickname`）
- 当前：欢迎模板回复后 `return`，**未进 AI**

### 点赞
- 运行时无独立 Like 采集器；协议有 `LikeMessage` 未解析
- 若弹幕侧车下发 `MsgType=like`，可复用 `DanmakuItem`（`UserId`/`Nickname`/`Content` 可含计数）
- V2：**复用弹幕 like 类型**；无 like 事件时功能可开关但不伪造协议

## 3. V2 架构要点

- `AiSpeechScheduler`：优先级队列（P1 礼物 > P2 弹幕 > P3 总结 > P4 欢迎 > P5 点赞）
- `AiPromptStore`：`Config/AiSpeech/*.txt` + mtime 热加载
- `UserConversationContext` / `RoomConversationContext` / `RoomContextSummary`
- `ModelOutputSanitizer`：think/analysis 剥离 + 拒绝无最终答案
- `SpeechNameCleaner`：昵称朗读
- 礼物合并 / 欢迎批量 / 点赞累计
- TTS：`speed_factor`（GPT-SoVITS `/tts` 最小扩展）
- 情绪：`EmotionPreset` + 参考音频；未配置则 UI 标明 UNCONFIGURED

## 4. 禁止改动

抖音连接 / 点歌 / 礼物积分 / 登录 / 弹幕采集核心协议 —— 仅旁路订阅现有事件。
