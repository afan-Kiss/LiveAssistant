# AI_SPEECH_EVENT_FIX_REPORT

## 1. 进房为什么之前没说

根因不是采集断了（`LiveAppHost` 已对 `member` 调用 `TryEnqueueMemberJoin`），而是：

1. **`AiSpeech.WelcomeUser` 默认 `false`**，未勾选时直接静默 return，没有任何诊断日志。
2. UI 文案/开关虽能保存，但关闭时看不到 `AI_MEMBER_SKIP`，表现为「有进房、AI 完全不说话」。
3. 批量缓冲本身正常；本轮补齐了 `AI_MEMBER_RECEIVED / BUFFER / FLUSH / ENQUEUE / SKIP`，勾选「欢迎进入直播间」后立即 `SaveSettingsFromUi` 生效。

## 2. 礼物为什么之前没说

礼物主链路本来就在：

`GiftCollector → GiftService.HandleGiftEvent → GiftReceived → TryEnqueueGift`

真实根因更可能是：

1. AI 总开关 / `ThankGift` 关闭时静默跳过；
2. 或下游 TTS/队列失败，但缺少分层日志，无法定位。

本轮确认 **不另接弹幕 gift 通道**（避免双路感谢），并增加：

- `AI_GIFT_RECEIVED / BUFFER / FLUSH / ENQUEUE / SKIP`
- AI 侧按 `eventId / userId+giftId+group+combo` 短时去重
- 保留原有 `GiftMergeBuffer` 合并策略

## 3. 点歌为什么之前没说

这是**功能缺失**，不是偶发 bug：

- `HandleDanmakuAsync` 返回 `true` 后 `LiveAppHost` 必须 `return`（避免点歌命令再进普通弹幕 AI）
- 旧事件只有无参 `RequestHandled`，**从未通知 AI 语音**

本轮新增：

- `SongRequestSucceeded`（仅在 `SONG_REQUEST_TRANSACTION stage=commit` 成功后触发）
- `AiSpeech.AnnounceSongRequest` + UI「点歌成功播报」
- `TryEnqueueSongRequest` + 固定口语模板入队（复用现有 TTS 队列，不走 danmaku_prompt）

## 4. 音量为什么小

`AiSpeechPlayer` 原先直接 `WaveOutEvent.Init(WaveFileReader)`，**没有软件增益**；`WaveOutEvent.Volume` 上限约 1.0，无法把偏小的 TTS wav 再放大。

现改为：

`WaveFileReader → ToSampleProvider → VolumeSampleProvider(gain) → SoftLimitingSampleProvider → 播放`

- `AiSpeech.VolumePercent` 默认 **150%**（50～200）
- 100%=原始，150%≈+3.5dB，200%≈+6dB
- soft-knee + clamp 到 [-1,1]，避免 200% 爆音
- 测试声音与真实任务共用同一音量链路

## 5. 修改文件

- `LiveAssistant/Config/AppSettings.cs`
- `LiveAssistant/Config/appsettings.json`
- `LiveAssistant/Config/AiSpeech/song_request_prompt.txt`（新）
- `LiveAssistant/LiveAssistant.csproj`
- `LiveAssistant/Services/LiveAppHost.cs`
- `LiveAssistant/Services/SongRequestService.cs`
- `LiveAssistant/Services/AiSpeech/AiSpeechModels.cs`
- `LiveAssistant/Services/AiSpeech/AiSpeechCoordinator.cs`
- `LiveAssistant/Services/AiSpeech/AiSpeechPlayer.cs`
- `LiveAssistant/Services/AiSpeech/AiPromptStore.cs`
- `LiveAssistant/Services/AiSpeech/ContextPromptBuilder.cs`
- `LiveAssistant/Services/AiSpeech/EmotionPresets.cs`
- `LiveAssistant/Services/AiSpeech/GiftMergeBuffer.cs`
- `LiveAssistant/Services/AiSpeech/WelcomeBatchBuffer.cs`
- `LiveAssistant/UI/MainForm.cs`
- `LiveAssistant.Tests/AiSpeechEventFixTests.cs`（新）
- `LiveAssistant.Tests/AiSpeechEventFixSerialCollection.cs`（新）
- `AI_SPEECH_EVENT_FIX_REPORT.md`（本文件）

未改：点歌积分事务核心、礼物积分规则、抖音协议、登录、酷狗播放核心、`SongNameParser.cs`、无关审查 md。

## 6. 测试结果

`dotnet test --filter FullyQualifiedName~AiSpeech`：**66 通过 / 0 失败**

其中 `AiSpeechEventFixTests` 覆盖：

1. Welcome 关 → 不入队  
2. Welcome 开 → member 最终入队  
3. Gift 关 → 不入队  
4. Gift 开 → 入队  
5. 连击礼物合并  
6–9. 点歌成功开关 / commit 入队 / 详情字段  
10. 优先级 Gift > SongRequest > Danmaku > Welcome  
11–12. 音量 100/150/200 幅度变化，200% 不溢出  
13. UI 配置立即生效 + song_request_prompt 热加载文件存在  

## 7. Commit

`fix: restore AI event speech and add voice gain control`

## 8. 新 EXE 路径

`E:\我的源码目录\抖音弹幕点歌系统\publish\LiveAssistant-one\LiveAssistant.exe`

旧包备份：`publish\backup\20260916_015655\LiveAssistant-one\`
