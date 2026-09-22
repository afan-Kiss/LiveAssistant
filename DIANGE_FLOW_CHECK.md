# DIANGE_FLOW_CHECK

生成时间：2026-09-22  
原则：**不改代码**；用源码静态链路 + 单测 + 本机端口状态判断“断在哪”。

目标链路：

```text
弹幕「点歌 xxx」→ 确认提示 →「确定」→ 搜索歌曲 → 加入播放队列 → NAudio 播放
```

---

## 1. 代码链路是否存在（静态）

| 步骤 | 关键类型/文件 | 状态 |
|------|----------------|------|
| 弹幕拉取 | `DanmakuService` | 源码在 |
| 分发 | `LiveAppHost.OnDanmakuReceived` | 源码在 |
| 解析「点歌 …」 | `SongNameParser` | 源码在 |
| 会话/确认 | `SongRequestService` + `SongRequestSessionStore` + `SongRequestConfirmParser` | 源码在 |
| 权限/积分 | `SongRequestPermissionService` | 源码在 |
| 搜歌/取链 | `KugouService` → `:17888` | 源码在 |
| 入队 | `QueueService` | 源码在 |
| 播放编排 | `PlaybackEngine` + `PlaybackCommandQueue` | 源码在 |
| 实际出声 | `PlaybackService`（`NAudio`：`MediaFoundationReader` + `WaveOutEvent`） | 源码在 |
| @ 回复 | `ReplyQueue` → 抖音 mention API（经 CDP） | 源码在 |

文档对照：`点歌流程-代码审查.md`（流程与实现一致；文中旧端口 4723 已被配置改为 **17891**）。

默认策略（`appsettings` / 旧部署）：

- `songRequestPolicy.requireConfirm = true`
- `songRequestPolicy.mode = Free`（旧部署样例）
- `kugou.requireFullPlayback = true`

---

## 2. 单测验收（无侧车也可验证业务状态机）

```text
dotnet test --filter FullyQualifiedName~SongRequest
通过 35 / 失败 0
```

覆盖：确认流、冷却、扣费生命周期等。  
说明：**点歌业务逻辑在源码层是通的**；端到端仍依赖外部进程。

---

## 3. 逐步断点判定

### Step A — 弹幕进入「点歌 xxx」

| 检查 | 结果 |
|------|------|
| 解析器支持 `点歌 歌名` / `点歌:歌名` / `点歌歌名` | OK（代码） |
| CDP `:17891` 健康 | **DOWN** |
| `cdp-danmaku.exe` 在 merged sidecars | **缺失** |
| 旧部署旁有 exe | 有（`D:\TestXiangyuLive\LiveAssistant\`） |

**断点：A（弹幕源）。** 无 CDP 进程则后续点歌不会触发。

---

### Step B — 搜索并提示确认

| 检查 | 结果 |
|------|------|
| `SongRequestService` 搜索 → 建 `Confirm` 会话 | OK（代码 + 单测） |
| 酷狗 `:17888` 健康 | **DOWN** |
| `酷狗api_v1.5.exe` | merged 缺失；旧部署有 |
| `kgapijs` 完整度 | merged 残片（无 `server.js`/node_modules）；旧部署完整 |

**断点：B（搜歌）。** 即使人工注入弹幕事件，无酷狗侧车也搜不到歌、发不出可靠确认。

---

### Step C — 用户回复「确定」/「确认」

| 检查 | 结果 |
|------|------|
| `SongRequestConfirmParser`（确定/确认/好的…） | OK |
| 会话门闩 / ConfirmConsumed 去重 | OK（代码 + 单测） |
| 依赖再次收弹幕 | 仍依赖 CDP（同 A） |

**逻辑不断；运行仍卡在弹幕源。**

---

### Step D — 取链并加入播放队列

| 检查 | 结果 |
|------|------|
| `ResolveAndEnqueueAsync` / `EnqueueTrackAsync` | OK（代码） |
| `QueueService` | OK |
| 完整播放地址（`requireFullPlayback`） | 依赖酷狗 VIP/登录态（运行配置有 `autoClaimVip`） |
| 酷狗侧车 | **未运行** |

**断点：D（取链/入队）——因 B 未恢复而连带中断。**

---

### Step E — NAudio 播放

| 检查 | 结果 |
|------|------|
| `PlaybackService` + NAudio 包 | 编译已还原，输出目录有 NAudio*.dll |
| 需要合法 `PlayUrl` | 依赖 D |
| 本机音频设备 | 未做实机播放烟测（避免改环境/启动业务） |

**播放器代码不断；当前无 URL 可播。**

---

## 4. 哪里断（汇总）

```text
[断] CDP 17891 / cdp-danmaku.exe
        ↓
弹幕「点歌 xxx」          ← 运行时到不了
        ↓
[断] 酷狗 17888 / 酷狗exe / 完整 kgapijs
        ↓
确认提示 /「确定」        ← 逻辑在，端到端不可达
        ↓
搜索 / 取链 / 入队
        ↓
NAudio 播放               ← 代码在，缺上游 URL
```

**第一断点：弹幕 CDP。**  
**第二断点：酷狗 sidecar（exe + 完整 kgapijs）。**  
**业务 C# 点歌状态机与 NAudio：未断。**

AI（Ollama / GPT-SoVITS）不在本主链上；旧配置默认关闭，可不作为点歌恢复阻塞项。

---

## 5. 建议的恢复验收顺序（仍不改代码）

1. 用旧目录或回填 sidecar 后启动：`cdp-danmaku` → 探活 `17891/api/health`（含登录）。  
2. 启动酷狗 exe（旁路完整 kgapijs）→ 探活 `17888/health`。  
3. 启动 `LiveAssistant` → 探活 `5088/diangexitong/api/ping`。  
4. 直播间发：`点歌 测试歌名` → 应收到确认问句。  
5. 回：`确定` → 队列出现条目 → 扬声器有声。  

可选：`tools\ChainSmokeTest` / `PlaySmokeTest`（针对 17888/5088）在侧车起来后使用。

---

## 6. 结论

- **源码级点歌完整链路：已恢复且可编译、单测通过。**
- **端到端点歌：当前断开在外部运行依赖（CDP + 酷狗），不是 SongRequest/NAudio 逻辑丢失。**
- 下一步只做资产回填与探活，不要重写点歌流程。
