# AUDIT_AFTER_FIX — 修复后第二轮审查包说明

- 生成日期：2026-09-15
- 分支：`main`
- 审计基线 commit：见仓库当前 HEAD（本文件与 ZIP 生成时点）
- 原则：以**当前修复后源码**为准；本文件不做“已修复”空话，只描述真实调用链与一致性边界。

---

## A. 当前弹幕实际调用链

入口：

1. `DanmakuService` 轮询/接收侧车弹幕 → 去重 → `ShouldIgnoreBotMessage`（`OutboundReplyTracker` / 登录号昵称）  
2. 触发 `DanmakuReceived`  
3. `LiveAppHost.OnDanmakuReceived` → `ProcessDanmakuSafeAsync` → `ProcessDanmakuAsync`

`ProcessDanmakuAsync`（`LiveAppHost.cs`）顺序：

| 步骤 | 条件 | consumed / return | 继续往后？ |
|------|------|-------------------|------------|
| UI 事件 | 总是 `DanmakuReceived?.Invoke` | 否 | 是 |
| 紧急暂停 | `Emergency.PauseInteraction` | **return**（未记 route） | 否 |
| 无 webRid | `Douyin.WebRid` 空 | **return** | 否 |
| EnsureUser / Touch | 总是 | 否 | 是 |
| member | `MsgType==member` | Welcome 后 **return** | 否 |
| gift | `MsgType==gift` | **return**（礼物走 GiftCollector） | 否 |
| 积分查询 | `PointsQueryService.TryHandle` | **consumed=true, return** | 否 |
| 切歌 | `SkipSongService.TryHandleAsync` | **consumed=true, return** | 否 |
| 禁言投票 | `BanVoteService.TryHandleAsync` | **consumed=true, return** | 否 |
| 点歌 | `SongRequestService.HandleDanmakuAsync` | **true → consumed=true, return**；**false → 不消费** | false 时继续 |
| 关键词 | `KeywordReplyService.TryHandleAsync` | **consumed=true, return** | 否 |
| AI | `AiSpeechCoordinator.TryEnqueueDanmaku` | **consumed=false**（仅投递语音队列） | 结束路由 |

要点：

- **业务模块返回 true = 本条弹幕被消费**，后面的关键词/AI 不会再处理。
- 点歌待确认期间的**闲聊** `HandleDanmakuAsync` 返回 **false**，可继续走关键词/AI。
- AI **不**向抖音发弹幕；语音播放走独立 `AiSpeechPlayer`，与 `ReplyQueue` 并行存在。
- 真正发出抖音 @ 回复的路径：各业务 → `ReplyQueue.EnqueueMention` / `EnqueueSongRequestReply` → Worker → `DouyinService.SendMention*` → **成功后** `OutboundReplyTracker.Track`。

```
观众弹幕
  → DanmakuService（回显过滤）
  → LiveAppHost.ProcessDanmakuAsync
       → 积分命令?
       → 切歌?
       → 禁言?
       → 点歌?  (true 则停；确认闲聊 false 继续)
       → 关键词?
       → AI.TryEnqueueDanmaku (consumed=false)
  → ReplyQueue → 抖音发送（业务回复）
  → AiSpeech 队列 → Ollama → GPT-SoVITS → AiSpeechPlayer（语音，不发弹幕）
```

---

## B. 点歌完整状态机

```
普通聊天
  →（未解析为点歌）HandleDanmakuAsync=false → 可走关键词/AI
点歌「点歌 xxx」
  → 用户闸门 AcquireAsync(userId)
  → 权限预检 Evaluate / EvaluateSong
  → Kugou 搜索 SearchCandidatesAsync
  → 自动选第一首 → Session(Step=Confirm, TTL=5min) → 发确认提示
待确认
  → 闲聊：return false（会话保留）
  → 取消：Clear session，回复已取消，consumed=true
  → 新点歌：Clear 旧 session，外层再 StartNewRequest
  → 「确定」：ConfirmConsumed 去重 → 权限再检 → ConfirmConsumed=true
       → ResolveCandidateAsync 取链
       → 失败：ConfirmConsumed=false，保留会话可重试
       → 成功：EnqueueTrackAsync
            → TryAddWithPriority（容量原子检查）
            → 队列满：不扣分，清会话（返回 true）
            → RecordSuccessfulRequest（扣积分/次数）
                 → 失败：Remove(queueItem)，不扣成功路径，清会话
            → 成功：扣分已落库，EnqueueSongRequestReply，RequestHandled→EnsurePlaying
```

### 明确问答

1. **入队成功以后，如果扣积分数据库操作失败怎么办？**  
   - `TryDeductPoints`/`TryChangePoints` **返回 false**（余额不足等）：`EnqueueTrackAsync` 调用 `_queue.Remove(added.Id)`，回滚队列项，回复积分不足。  
   - **抛异常**（例如 `points_ledger` 写入失败后 `throw`）：`EnqueueTrackAsync` **没有 try/catch**；事务内 UPDATE 会 Rollback（积分未扣），但 **队列项已插入且不会 Remove** → 可能出现「歌在队列、积分未扣」脏状态。

2. **有没有回滚已经加入的歌曲？**  
   - 扣分返回 false：有，`Remove`。  
   - 扣分抛异常：无自动回滚。

3. **两条「确定」同时到达能不能重复点歌？**  
   - 同用户：`SongRequestUserGateRegistry` 串行 + `ConfirmConsumed`；成功后 Clear session。不应重复入队。

4. **会不会扣两次积分？**  
   - 同用户双确认：正常路径只扣一次。  
   - 并发靠用户闸门串行。

5. **队列满时有没有可能已经扣积分？**  
   - 不会。顺序是 **先 `TryAddWithPriority`，失败则直接返回，尚未调用 `RecordSuccessfulRequest`**。

6. **待确认多久过期？**  
   - `SongRequestSessionStore` 默认 **TTL = 5 分钟**（`Set` 时刷新 `ExpiresAt`）。

7. **用户重新点另一首歌时旧 pending 怎么处理？**  
   - 会话中再发点歌：`HandleSessionAsync` Clear 旧会话并 return false，外层重新 `StartNewRequestAsync`，旧 pending 被替换。

8. **用户「取消」后旧的「确定」还能不能生效？**  
   - 取消即 `Clear(userId)`；之后的「确定」走 orphan 提示，**不能**入队。

9. **同一个用户在多个直播间是否共用 pending 状态？**  
   - **是**。Session 仅按 `userId` 索引，**不含 webRid**；进程内跨房间共用同一 pending。

---

## C. 切歌事务

实现：`SkipSongService.TryHandleAsync`。

1. **当前到底是先扣积分还是先 SkipAsync？**  
   - **先原子扣积分**（`TryDeductPoints`），再 `await _engine.SkipAsync()`（即 `PlaybackCommandQueue.EnqueueSkipAsync`）。

2. **如果扣积分成功、SkipAsync 失败怎么办？**  
   - `accepted==false` 时：`TryChangePoints(..., Refund, "切歌命令入队失败退回积分")` **自动退款**。

3. **有没有自动退款？**  
   - **有**，仅针对「命令入队失败」路径。若命令已入队但后续播放执行失败，**不在本服务退款**。

4. **两次切歌命令并发是否会重复扣分？**  
   - 无用户级锁；两次都可能通过余额检查并各扣一次。`PlaybackCommandQueue` 会对进行中的 Skip/Advance 做合并计数，但**扣分已在入队前完成**，合并不能退回多扣的分。

5. **PlaybackCommandQueue 满/关闭/异常时如何处理？**  
   - Channel 为 **Unbounded**，正常不会“满”。  
   - `Dispose` 后 `TryComplete`，`TryWrite` 失败 → `EnqueueSkipAsync` 返回 false → **退款**。  
   - 入队成功后 worker 异常：属于播放侧问题，切歌积分不自动退。

---

## D. OutboundReplyTracker

```
ReplyQueue.TryEnqueue（入队时不 Track）
  → Worker 发送 HTTP/API
  → 成功（或判定 likely already sent）
  → Track(replyId, content)
  → 抖音回显进入 DanmakuService
  → ShouldIgnoreBotMessage：IsRecentOutbound(msgId, content) → 过滤
```

1. **Track 是发生在 HTTP/API 调用前还是成功后？**  
   - **成功后**（及 “likely already sent” 失败分支）。注释明确：不在入队时 Track。

2. **发送成功到 Track 之间有没有竞态窗口？**  
   - **有**：`SendMention*` 返回 ok 到 `Track` 之间，若回显极快到达，可能漏过滤一次；Track 后同文案在 TTL（默认 2 分钟）内会被过滤。

3. **AI TTS 为什么不 Track？**  
   - AI **不向抖音发弹幕**；`ProcessTaskAsync` 注释禁止写入 `OutboundReplyTracker`。另有未调用的私有 `TrackAiReply`（仅内部 AI 回复去重意图，当前生成路径未调用）。

4. **如果 AI 真正发送了一条抖音弹幕，是否会 Track？**  
   - 当前实现 **不会发**；若未来走 `ReplyQueue` 且发送成功，会按 ReplyQueue 成功路径 Track。AI 模块本身不 Track。

5. **同文案真实观众弹幕是否可能被误杀？**  
   - **可能**：Track 后按 **规范化正文** 匹配（去 @、去空白），TTL 内任意用户发相同正文都会被 `IsRecentOutbound` 判为回显而丢弃。`DanmakuService` 已取消“仅模板启发式误杀”，但 **content-key Track 仍会误杀同文案真人**。

---

## E. AI 并发模型

共享资源：

- `SemaphoreSlim _executionGate (1,1)`：`ProcessTaskAsync` / `TestVoiceAsync` /（`TestAiAsync`→`ProcessTaskAsync`）互斥。
- 每任务独立 `playCts = CreateLinkedTokenSource(ct)`，写入 `_activePlayCts`；`finally` 里 `CompareExchange` 清自己的引用并 Dispose **自己的** CTS。
- 单一 `_player`（`AiSpeechPlayer`）：任务结束 **不 Dispose player**；仅 coordinator Dispose 时 Dispose。
- WorkerLoop 取队列任务后调用 `ProcessTaskAsync`；与 Test* 抢同一 gate。

1. **TestVoice 执行期间正式直播 AI 是否必须等待？**  
   - **是**。TestVoice 持有 `_executionGate`，Worker 的 `ProcessTaskAsync` 会在 `WaitAsync` 阻塞。

2. **TestAi 执行期间正式 Worker 是否暂停？**  
   - Worker 循环不暂停，但下一个 `ProcessTaskAsync` 必须等 TestAi 释放 gate → **等效串行等待**。

3. **一个 TTS 播放取消是否可能取消下一条？**  
   - `StopCurrentPlayback` 取消当前 `_activePlayCts`。任务 finally 用 `CompareExchange` 避免误清新任务 CTS。若在新旧交接窗口调用 Stop，理论上可能打到新任务（竞态窗口小）。**正常一任务结束不会 Cancel 下一条的独立 CTS**。

4. **是否存在旧任务 finally Dispose 新任务资源的可能？**  
   - CTS：`CompareExchange` 防护，旧任务不会把新 CTS 引用清成 null 后 Dispose 新的。  
   - Player：共享且不在任务 finally Dispose；旧任务 `StopInternal` 可能打断当前播放，但若 gate 互斥则新任务尚未开始播。

---

## F. ReplyQueue 固定回复窗口

算法（`EnqueueSongRequestReply`）：

```
lock(_batchLock):
  加入条目
  if (_batchTimerRunning) return   // 不重置
  _batchTimerRunning = true
  启动 Task.Delay(windowMs) 一次 → FlushSongRequestBatch
```

- `windowMs = max(100, ReplySettings.SongRequestBatchWindowMs)`  
- 当前配置默认：`appsettings.json` → **5000ms**

### 时间推演

假设第一条点歌回复进入时间为 **12:00:00.000**，窗口 **W=5000ms**（默认）：

| 时间 | 事件 | deadline |
|------|------|----------|
| 12:00:00.000 | 第一条进入，启动定时器 | **12:00:05.000** |
| 12:00:00.300 | 新点歌加入 | 不改 deadline |
| 12:00:00.700 | 再加入 | 不改 |
| 12:00:00.950 | 再加入 | 不改 |
| …持续加入… | 只要第一批未 Flush | **仍为 12:00:05.000** |
| 12:00:05.000 | Flush 第一批 | 之后新消息可再开新窗口 |

**第一次回复应在约 12:00:05.000 进入发送队列**（再加 ReplyQueue 速率限制的微小延迟）。  
**新消息不会不断延长第一批 deadline。**

若 W=400ms（测试常用），则第一次约在 12:00:00.400。

---

## G. 本轮针对性测试（AfterFixConsistencyTests）

见 `LiveAssistant.Tests/AfterFixConsistencyTests.cs`：

1. 同用户双「确定」→ 只入队一次、只扣一次  
2. 队列最后一坑两用户同时确认 → 仅一人成功且另一人不扣分  
3. 入队后流水表破坏导致扣分抛异常 → 断言禁止脏状态（若实现不保证则测试失败）  
4. 切歌扣分成功但命令队列已 Dispose → 积分退回  
5. 发送成功后立即同文案回显 → Track 可过滤  
6. 未 Track 时真人同文案 → 不过滤  
7. 固定窗口持续入队 → 第一批 deadline 不延长  
8. Worker 期间 TestVoice/TestAi → 无死锁 / 无 Player Dispose / 可回到 Idle  

### 实跑结果（2026-09-15，本轮未改业务代码）

| # | 测试 | 结果 |
|---|------|------|
| 1 | ConcurrentConfirm_SameUser_EnqueuesOnce_DeductsOnce | **PASS** |
| 2 | LastQueueSlot_TwoUsersConfirm_OnlyOneSucceeds_LoserNotCharged | **PASS** |
| 3 | EnqueueThenPointsLedgerThrow_MustNotLeaveSongWithoutCharge | **FAIL** — waiting=1, points=100（入队后扣分抛异常留下脏状态） |
| 4 | Skip_DeductOkButCommandRejected_RefundsPoints | **PASS** |
| 5 | Outbound_EchoRightAfterSend_IsFiltered | **PASS** |
| 6 | Outbound_RealAudienceSameText_IsNotFiltered_WithoutTrack | **PASS** |
| 7 | SongRequestBatch_ContinuousEnqueue_DoesNotExtendFirstDeadline | **PASS** |
| 8 | Ai_TestVoiceAndTestAi_DuringWorker_NoDeadlockOrPlayerDispose | **PASS** |

全量 `dotnet test`：**总计 203，通过 202，失败 1**（即上表 #3）。  
`dotnet build -c Release`：见交付摘要。

---

## H. 已知一致性风险（供第二轮审查）

1. **点歌：入队后扣分抛异常** → 可能脏状态（歌在、分未扣）。  
2. **切歌：并发双扣** → 命令可合并，积分不合并退。  
3. **Outbound content-key** → TTL 内同文案真人可能被误杀。  
4. **Session 按 userId 全局** → 多直播间 pending 共用。
