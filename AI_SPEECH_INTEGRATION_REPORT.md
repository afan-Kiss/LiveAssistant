# AI 语音互动集成报告

生成时间：2026-09-15

## 结论

已在现有 LiveAssistant 上新增独立模块 **AI语音互动**：复用现有弹幕事件，经 Ollama 生成短回复，再经本机 GPT-SoVITS（`my_voice`）合成并播放到可选音频设备。未改动抖音协议、登录、点歌核心、礼物积分核心。

---

## 【找到的真实弹幕入口】

| 项 | 值 |
|---|---|
| 文件 | `LiveAssistant/Services/DanmakuService.cs` |
| 类 | `DanmakuService` |
| 方法 | `DispatchItems` → 触发 `DanmakuReceived` |
| 上游 | `DouyinService.PollDanmakuAsync` → Sidecar `POST api/live/danmaku/feed` |
| 业务分发 | `LiveAppHost.OnDanmakuReceived` |
| 点歌 | `SongRequestService.HandleDanmakuAsync`（本次未改核心逻辑） |
| AI 接入点 | 同方法末尾调用 `_aiSpeech.TryEnqueueDanmaku(...)`（失败隔离，不影响点歌） |

普通弹幕模型：`DanmakuItem`（`MsgId` / `UserId` / `Nickname` / `Content` / `MsgType` / `Timestamp`）。

说明：无 openId/secUid 字段；稳定 ID 使用 sidecar 的 `user_id`。主播/登录昵称来自 `RoomOwnerNickname` / `DouyinLoginNickname`。

---

## 【新增文件】

```
LiveAssistant/Services/AiSpeech/AiSpeechModels.cs
LiveAssistant/Services/AiSpeech/AiDanmakuFilter.cs
LiveAssistant/Services/AiSpeech/SpeechTextCleaner.cs
LiveAssistant/Services/AiSpeech/OllamaClient.cs
LiveAssistant/Services/AiSpeech/GptSovitsClient.cs
LiveAssistant/Services/AiSpeech/AudioOutputDevices.cs
LiveAssistant/Services/AiSpeech/AiSpeechPlayer.cs
LiveAssistant/Services/AiSpeech/AiSpeechCoordinator.cs
LiveAssistant.Tests/AiSpeechTests.cs
LiveAssistant.Tests/AiSpeechCoordinatorSmokeTests.cs
LiveAssistant.Tests/AiSpeechLiveIntegrationTests.cs
AI_SPEECH_INTEGRATION_REPORT.md
```

配置类型：`LiveAssistant/Config/AppSettings.cs` 中新增 `AiSpeechSettings`。

---

## 【修改文件】

- `LiveAssistant/Config/AppSettings.cs` — `AiSpeech` 配置
- `LiveAssistant/Config/appsettings.json` — 默认 AI 配置段
- `LiveAssistant/Services/LiveAppHost.cs` — 创建/暴露/释放 `AiSpeechCoordinator`，弹幕投递
- `LiveAssistant/Services/LogService.cs` — `AiInfo` / `AiWarn` → `ai_speech.log`
- `LiveAssistant/UI/MainForm.cs` — 右侧 Tab「AI语音」面板
- `LiveAssistant.csproj` — 无新 NuGet（复用已有 NAudio）

---

## 【AI调用链】

```
Sidecar 弹幕 feed
  → DanmakuService.DispatchItems
  → LiveAppHost.OnDanmakuReceived
  → AiSpeechCoordinator.TryEnqueueDanmaku
       ├─ AiDanmakuFilter（点歌/礼物/短消息等跳过）
       ├─ 主播/登录昵称 + OutboundReplyTracker + AI回复去重 → AI_SELF_MESSAGE_SKIP
       └─ 单队列（默认最大 5，>30s 过期丢弃）
  → Worker（同时仅 1 个任务）
       Idle → Thinking(Ollama /api/chat)
            → Synthesizing(POST /tts)
            → Playing(NAudio WaveOutEvent 指定设备)
            → 间隔 MinIntervalSeconds（默认 8s）
```

---

## 【Ollama模型】

- 地址：`http://127.0.0.1:11434`
- 列表：`GET /api/tags`（UI 下拉，不写死模型名）
- 本机当前可见：`qwen3.5:27b`
- 生成：`POST /api/chat`（timeout 默认 45s，最多重试 1 次）
- 显存不足：记 `AI_OLLAMA_RESOURCE_ERROR`，不风暴重试

**重要环境事实（本机实测）：**  
`qwen3.5:27b` 在当前机器上加载常因 `CUDA_Host` / 内存分配失败返回 HTTP 500（约需再钉住 ~3.5GB host buffer）。与 GPT-SoVITS 同卡时更难共存。  
LiveAssistant **不会因此崩溃**；UI 提示「AI模型不可用」。建议另拉一个更小的聊天模型（如 7B/14B）专供直播互动，或先保证系统内存充足后再测「测试AI」。

---

## 【GPT-SoVITS】

- 地址：`http://127.0.0.1:9880`
- 健康：`GET /health` → `tts_ready` / `voice_ready` / `voice=my_voice`
- 合成：`POST /tts` `{ "text", "voice": "my_voice" }` → `audio/wav`
- 部署目录：`E:\AI\GPT-SoVITS`（启动 bat：启动 GPT-SoVITS 本地服务）
- **未**把模型打进 LiveAssistant

本轮实测：

| 测试 | 结果 |
|---|---|
| `/health` | OK，`tts_ready=true`，`voice_ready=true` |
| `/tts`「你好，现在测试一下…」 | OK，约 73KB wav |
| `/tts`「对，这个确实是自己写的。」 | OK，约 63KB wav |

---

## 【声音播放实现】

- `AiSpeechPlayer`：独立于点歌 `PlaybackService`
- 优先内存 wav → 写入 `data/ai-speech/temp/{uuid}.wav` → `WaveFileReader` + `WaveOutEvent`
- 播放结束/取消/退出时清理临时文件
- 「停止当前播放」只停本地播放与当前任务，不杀 Ollama / GPT-SoVITS

---

## 【音频设备选择】

- `AudioOutputDevices.ListDevices()` 枚举 `WaveOut` 设备（扬声器 / 耳机 / VB-CABLE / VoiceMeeter 等）
- 配置持久化：`OutputDeviceNumber` + `OutputDeviceName`
- AI 声音只输出到所选设备，便于接到直播伴侣

---

## 【队列策略】

- 单队列、单 worker，禁止并行 Ollama / TTS / 播放
- 默认 `MaxQueueSize=5`，溢出丢最旧（`AI_QUEUE_DROP`）
- 排队超过 `MaxAgeSeconds=30` → `AI_QUEUE_EXPIRED`
- 播放完成后至少等待 `MinIntervalSeconds`（3～60，默认 8）
- 「AI语音测试模式」：同样走真实弹幕 + 间隔单条处理（便于首次直播观察）

---

## 【防止AI自回复】

组合机制：

1. 登录昵称 / 房主昵称过滤  
2. `OutboundReplyTracker` 记录 AI 回复文本  
3. 短期 AI 回复文本归一化去重  
4. 过滤器排除机器人点歌话术  

日志：`AI_SELF_MESSAGE_SKIP`

说明：第一版 AI **不自动发弹幕**，只语音播放；上述机制防止将来发弹幕或主播账号回声形成死循环。

---

## 【测试结果】

| 项 | 结果 |
|---|---|
| 单元测试过滤/清洗/队列（19） | 通过 |
| TTS 关闭时 `TestVoice` 不抛异常 | 通过 |
| 连续 20 条入队不超过 5 | 通过 |
| 主播自消息不入队 | 通过 |
| 真实 TTS HTTP → wav | 通过 |
| 真实 Ollama `qwen3.5:27b` 生成 | **环境失败**（CUDA_Host 分配 / 显存争用），应用侧已隔离 |
| 新 EXE 可启动并正常退出 | 通过 |
| 点歌/弹幕原链路 | 未改核心，仅追加隔离订阅 |

---

## 【最终EXE】

```
E:\我的源码目录\抖音弹幕点歌系统\publish\LiveAssistant-one\LiveAssistant.exe
```

- 大小约 83.7 MB  
- 备份：`publish\backup\20260915_211253\LiveAssistant-one\`  
- 发布脚本：`scripts\publish-single.py`

---

## 【日志位置】

优先：`{data目录的上一级}/logs/ai_speech.log`  
（与现有 `app.log` / `douyin.log` 同目录策略）

关键事件：

`AI_DANMAKU_RECEIVED` / `AI_DANMAKU_FILTERED` / `AI_QUEUE_ADD` / `AI_QUEUE_DROP` / `AI_QUEUE_EXPIRED` / `AI_GENERATE_*` / `AI_TTS_*` / `AI_AUDIO_PLAY_*` / `AI_SELF_MESSAGE_SKIP` / `AI_OLLAMA_RESOURCE_ERROR`

---

## 【需要我手工做什么】

1. 打开 `publish\LiveAssistant-one\LiveAssistant.exe`  
2. 确认本机已启动：  
   - Ollama（`11434`）  
   - GPT-SoVITS（`E:\AI\GPT-SoVITS` 启动本地服务 → `9880`）  
3. 切到右侧 Tab **「AI语音」**  
4. 点 **刷新**，选择可用 Ollama 模型（若 27B 加载失败，请另装更小模型）  
5. 选择 **语音输出设备**（建议 VB-CABLE / 虚拟声卡 → 直播伴侣）  
6. 勾选 **启用AI语音**（首次可再勾「AI语音测试模式」）  
7. 先点 **测试声音**（不依赖 Ollama）  
8. 再点 **测试AI**（完整链路；需 Ollama 能成功加载所选模型）  
9. 开直播观察

---

## 未改动的边界（遵守）

- 未重写抖音弹幕协议 / 登录  
- 未改点歌、礼物积分核心  
- 未把 GPT-SoVITS / Ollama 模型打进 EXE  
- 未大规模重构；仅为独立模块 + 事件订阅  
