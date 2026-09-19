# 抖音 CDP 弹幕接口对接审查报告

生成时间：2026-09-18

审查范围：

- 点歌系统客户端：`LiveAssistant/Services/DouyinService.cs`、`DanmakuService.cs`、`FileCookieProvider.cs`、`GiftImFetchClient.cs`、`SidecarLocator.cs`、`ProcessWatchdogService.cs`
- CDP 侧车：`E:\我的源码目录\抖音cdp弹幕`（API 版本 `1.2.0`，默认 `127.0.0.1:17891`）

结论先说：**点歌系统源码还没有切到 CDP 侧车。** 配置、进程拉起、端口都还指向旧的「抖音直播弹幕助手」`http://127.0.0.1:4723`。CDP 项目自己写了「与网页弹幕同风格」的路径，所以聊天弹幕轮询、发 @、禁言这几条主路径看起来能通。真正会断的是 Cookie、礼物、进场/点赞、按会话停止采集，以及登录昵称过滤。

本报告只做对照，没有改代码。

---

## 1. 现在实际连的是谁

| 项 | 点歌系统现在写死的 | CDP 侧车实际 |
|---|---|---|
| 默认地址 | `DouyinSettings.BaseUrl = http://127.0.0.1:4723` | `config.DefaultAddr = 127.0.0.1:17891` |
| 配置文件 | `LiveAssistant/Config/appsettings.json` 的 `douyin.baseUrl` 仍是 `4723` | `data/settings.json` 的 `addr` |
| 进程名 | `抖音直播弹幕助手.exe`（`SidecarLocator.PreferredDouyinFileName`） | `cmd/native` 另编的 Windows 程序，不是这个文件名 |
| 拉起方式 | `ProcessWatchdogService` 按 `DouyinExePath` 启动旧 exe | 自己开 Chrome CDP，不读旧侧车的 `cookies.json` |
| 登录方式 | 读侧车 `cookies.json`，再 `POST /api/cookie/import` | 只认本软件拉起的 Chrome 扫码，没有 Cookie 导入接口 |

界面状态栏也还写着 `抖音 http://127.0.0.1:4723`（`MainForm.cs`）。

所以：即便本机已经在跑 `E:\我的源码目录\抖音cdp弹幕`，点歌系统默认也打不到它。要打到，至少要把 `douyin.baseUrl` 改成 `http://127.0.0.1:17891`，并停掉看门狗对旧 exe 的拉起。下面的兼容性是按「客户端现有调用打到 CDP 现有路由」来审的，不是按已经改完来审的。

---

## 2. 调用链（改端口之后会走到的）

连接直播间（`DanmakuService.StartAsync`）：

1. `GET /api/health`，取 `login_ok`、`nickname`
2. `POST /api/live/room/resolve`，取 `owner.nickname`、`title`、`room_id`、`raw.cookie`
3. `GET /api/live/collect/sessions`，再 `POST /api/live/collect/{sessionId}/stop` 停掉别的房间
4. `SyncCookieProfileAsync` → 读 `cookies.json` → `POST /api/cookie/import`
5. `EnsureWriteGateReadyAsync`（只有 health 里带 `write_gate` 且被挡住时才会打验证接口）
6. `POST /api/live/room/reconnect`
7. `POST /api/live/collect/start`
8. 轮询 `POST /api/live/danmaku/feed` 和 `POST /api/live/danmaku/at/feed`，用返回的 `message_count` / `mention_count` 当游标

业务发送：

- @ 回复：`ReplyQueue` → `POST /api/live/danmaku/mention`
- 禁言：`POST /api/live/mod/silence`
- 查人：`POST /api/live/user/lookup`
- 礼物：不走弹幕 feed。`GiftCollectorService` → `FileCookieProvider.GetActiveCookieAsync`（先 `GET /api/cookie`）→ 自己请求 `https://live.douyin.com/webcast/im/fetch/`

`DouyinService.PollGiftAsync`（`POST /api/live/gift/feed`）在仓库里没有调用方，礼物不依赖这条。

---

## 3. 路径对照

CDP 同时挂了短路径（`/api/send`、`/api/listen/poll`）和长路径（`/api/live/...`）。点歌系统只用长路径。

### 3.1 换端口后仍可能工作

| 点歌系统调用 | CDP 路由 | 说明 |
|---|---|---|
| `GET api/health` | 有 | 信封是 `{ok,message,data}`。`data.login_ok`、`login_hint`、`collect_sessions` 有。没有 `nickname`、没有 `write_gate` |
| `POST api/live/room/resolve` `{web_rid}` | 有 | 返回 `web_rid`、`room_id`、`title`。没有 `owner`、`status`、`user_count`、`raw.cookie` |
| `POST api/live/collect/start` `{web_rid}` | 有 | 单房间。成功时 `session_id` 固定为 `room_` + 短号 |
| `GET api/live/collect/sessions` | 有 | 数组，字段 `session_id` / `web_rid` / `running` 对得上。最多 1 条 |
| `POST api/live/danmaku/feed` | 有 | `items[]` 含 `msg_id`、`content`、`msg_type`、`user`、`timestamp`。`message_count` 被 CDP 故意写成游标（最新 `seq`），不是条数。点歌系统正好把 `message_count` 当游标，聊天弹幕这条是对齐的 |
| `POST api/live/danmaku/at/feed` | 有 | 同上，游标写在 `mention_count` |
| `POST api/live/danmaku/mention` | 有 | 认 `web_rid` + `user_id` + `content`。多出来的 `cookie` 字段会被丢掉。成功时 `data.msg_id` 在，`TryExtractPlatformMessageId` 能读到 |
| `POST api/live/mod/silence` | 有 | `action=silence/unsilence` 认。昵称、`sec_uid` 由 CDP 从已听过的弹幕里补 |
| `POST api/live/user/lookup` | 有 | 请求里的 `web_rid` CDP 不读，只认 `keyword` / `user_id` / `nickname`。返回 `data` 是用户数组，`ParseLookupUser` 能解析 |
| `POST api/live/room/reconnect` | 有 | 只是重新接上 Chrome，不按 `web_rid` 重进房间。请求体里的 `web_rid` 被忽略 |

聊天弹幕条目字段（`internal/models/models.go` 的 `DanmakuMessage`）和 `DouyinDanmakuMessage` 对得上：`msg_id`、`content`、`msg_type`、`user.user_id`、`user.nickname`、`timestamp`（RFC3339 字符串）。多出来的 `seq`、`sec_uid`、`at_users` 客户端会忽略，不导致反序列化失败。

游标细节（请审查者核对，不应当成已经坏了）：

- CDP `handleFeed` 把 `message_count`（或 @ 流的 `mention_count`）设成本批最大 `seq`；没有新消息时保持请求里的 `after`。
- 点歌系统 `_after = feed.MessageCount`，`_afterAt` 优先用 `mention_count`。和旧侧车的游标用法一致。
- `seq` 是 `uint64`，客户端模型是 `int`。单场从 1 递增，正常直播不会溢出 `int`。
- 空读时 CDP 注释写了「仍回报最新游标」，但代码把 `latest` 丢掉了（`web.go` 里 `_ = latest`）。空读时游标不前跳，点歌系统的追赶逻辑可以接受。

### 3.2 调用了但 CDP 没有（会失败）

| 点歌系统 | 期望 | CDP | 后果 |
|---|---|---|---|
| `POST api/live/collect/{sessionId}/stop` | 按会话停采集 | 只有 `POST /api/live/collect/stop`，路径里不能带 session | `StopOtherCollectSessionsAsync` 每次换房都 404。CDP 本来就只听一间房，功能上多半只是日志报错；但旧侧车的「停掉别的 session」语义没了 |
| `POST api/cookie/import` | 把 `cookies.json` 灌进侧车 | 无 | `StartAsync` 里的 `SyncCookieProfileAsync` 失败。CDP 登录只走 Chrome，这条本就不该再调 |
| `GET api/cookie` | `{active, login_ok, login_hint}` | 无 | `FileCookieProvider` 直接抛「无法访问 Sidecar /api/cookie」。礼物采集起不来 |
| `POST api/live/gift/feed` | 礼物列表 | 无 | 当前无调用方，先不算运行时故障 |
| `POST api/cookie/verify-write` | 写凭据验证 | 无 | 仅当 health 带 `write_gate` 且 `allowed=false` 才会打到。CDP health 没有 `write_gate`，启动路径默认不会进这里 |
| `POST api/diag/write-candidate/probe/arm` | 同上 | 无 | 同上 |
| `POST api/diag/send-pause/clear` | 清 403 暂停 | 无 | 同上，依赖 `write_gate.paused` |

失败形态：缺路由时 Go 标准库是 **404 空 body**，不是 `{ok:false}`。`DouyinService.SafeAsync` 会吞异常并打 `douyin` 警告，连接循环不会因此退出。所以日志会看到一串失败，界面仍可能显示「已连接」。

### 3.3 CDP 有、点歌系统没用

这些不用改客户端也能活，列出来避免审查时当成漏接：

- `GET /api/status`、`GET /api/meta/endpoints`、`GET /api/logs`
- `POST /api/cookie/login`、`POST /api/login/start`（扫码，不是导入 Cookie）
- `POST /api/send`、`GET /api/listen/poll`（短路径别名）
- `POST /api/mute`、`POST /api/unmute`
- `POST /api/live/mod/kick`、`GET /api/live/mod/silence/list`
- `GET/POST /api/settings/refresh`、`/api/settings/room`
- feed 上的 `no_emoji`、`unicode`（给易语言用的）

---

## 4. 字段对不上之后，功能会怎样

### 4.1 聊天弹幕、点歌、关键词、@ 回复：主路径可以通

前提是 `baseUrl` 已改到 `17891`，并且 CDP 里已经扫码、已经 `collect/start`。

- 监听只入队 `WebcastChatMessage`，`msg_type` 固定 `"chat"`（`internal/listen/listen.go`）。点歌、切歌、关键词、电影评分旁路都认 `chat`，这条不受影响。
- @ 发送不要求客户端带昵称。CDP 用已听过的弹幕补 `nickname` / `sec_uid`。对方还没说过话时，@ 会失败，这是 CDP 使用说明里写明的限制。
- 正文里的 `cookie` 字段无效。旧侧车那种「Profile 不匹配就同步 Cookie 再重发」在 CDP 上不会发生（错误文案里没有 `ProfileID`），重试分支是死的。
- `user_id` 在 CDP 是 `flexString`，数字或字符串都能收。客户端传的是字符串，兼容。

### 4.2 登录昵称过滤会失效

`GET /api/health` 的 `data` 没有 `nickname`。`DanmakuService` 把 `DouyinLoginNickname` 设成 `"-"`。

`ShouldIgnoreBotMessage` 里「和登录号昵称相同就丢掉」这条不会生效。机器人自己的弹幕只能靠 `OutboundReplyTracker` 的文案去重。文案不完全一致时，可能把助手刚发出的 @ 再当观众弹幕处理一遍（重复点歌、重复回复）。

CDP 的 `status` 里也没有昵称，只有 `logged_in`、`listen_web_rid` 等。要修的话得让 health 增加登录昵称，或客户端改从别的字段读。

### 4.3 房间主播名会空

`room/resolve` 的 `RoomInfo` 只有 `web_rid`、`room_id`、`title`。没有 `owner`。

界面账号名、`RoomOwnerNickname` 会一直是 `"-"`。如果有逻辑用主播昵称过滤房主弹幕，也会失效。`room_id` 还在，礼物 IM 的房间号本身还能解析出来。

### 4.4 礼物积分这条会断

礼物不靠弹幕 feed，靠 Cookie：

1. `GET /api/cookie`：CDP 没有，`GetCookieStatusAsync` 返回 null，`FileCookieProvider` 立刻失败。
2. 即便绕过这一步，CDP 也不写旧侧车的 `data/cookies.json`。
3. 再退一步，`room/resolve` 也不再回 `raw.cookie`。

三条拿 Cookie 的路都没了。`GiftImFetchClient` 要求 Cookie 里有 `sessionid`。结果是礼物积分、礼物点歌解锁、礼物致谢都不会进账。弹幕点歌如果走积分/礼物门槛，会表现为「能看到弹幕，但礼物不加分」。

CDP 进程内的 Chrome 是有登录态的，只是 HTTP API 不把 Cookie 交出来。这是协议缺口，不是客户端少调一个已有接口。

### 4.5 进场、点赞、弹幕里的礼物事件没有了

CDP `oneFetch` 只保留 `WebcastChatMessage`。`member` / `like` / `gift` 不会出现在 feed 里。

点歌系统里因此静默失效的：

- `WelcomeService`（`MsgType == member`）
- `LiveAppHost` 里 `member` / `like` / `gift` 分支
- AI 语音里靠弹幕类型触发的欢迎、点赞

礼物如果只靠 IM fetch，本来也不依赖 feed 里的 `gift`。但 IM fetch 又因 Cookie 断了，所以礼物是双断，不是「改走另一条还能用」。

### 4.6 写凭据门闩：CDP 上等于没有

`IsWriteCredentialBlocked` 在 `write_gate` 缺失时返回 false。CDP health 不带这个对象，所以不会卡在「凭据待验证」。

这也意味着旧侧车那套 403 暂停、`verify-write`、probe arm，在 CDP 上都不存在。发送失败只会变成 mention 接口的 `ok:false` + `message`。`ReplyQueue` 原有重试还在，但不会再走清暂停接口。

### 4.7 单房间

CDP 全局一个监听。再 `collect/start` 另一间房会换房并清空队列和用户缓存（`listen.go` 的 `stopLocked`）。点歌系统仍按「多 session、按 id 停掉其它房」来写。换房时停旧 session 会 404；若两个逻辑同时 start，后一次会清掉前一次的弹幕缓存和用户资料，@ / 禁言补全会暂时失败。

---

## 5. 建议审查时重点看的问题

请按严重程度看，不要把「路径名字一样」当成已经接上。

1. **端口和进程还没改。** 不改 `baseUrl` 和看门狗，后面的接口差异都不会在现网出现，打到的仍是旧 exe。改了之后，看门狗如果还去拉 `抖音直播弹幕助手.exe`，会和 CDP 抢登录或抢不到端口（端口其实不同，更可能是两套浏览器/两套登录并存）。
2. **礼物 Cookie 协议缺口。** `GET /api/cookie`、`cookies.json`、`raw.cookie` 三者 CDP 都没有。这是功能断裂，不是字段名写错。审查时应决定：CDP 增加只读 Cookie 导出，还是点歌系统放弃 IM fetch、改由 CDP 提供礼物事件。
3. **只出 chat。** 欢迎、点赞、弹幕礼物类型需要 CDP 放行对应 `Webcast*`，或点歌系统明确关掉这些功能。
4. **health 没有登录昵称。** 机器人弹幕防回环会弱一档。
5. **停止采集 URL 不一致。** 客户端是 `/api/live/collect/{id}/stop`，服务端是 `/api/live/collect/stop`。单房间下影响小，但是实打实的 404。
6. **resolve 没有 owner。** 界面和房主过滤。
7. **mention 的 `cookie` 参数被忽略。** 确认 CDP 发送始终用当前 Chrome 登录态，而不是请求体里的 Cookie。多账号时这是行为变化。
8. **游标空读不推进到 `latest`。** 当前和客户端追赶逻辑兼容；若有人按注释去「修」CDP，把空响应的 `message_count` 改成缓冲最新 seq，要同时看客户端会不会跳过尚未返回的消息。现在不要单独改这一处。

不建议在这次审查里改的：

- 酷狗、快手、播放、积分账本本身。它们只是消费弹幕/礼物事件。
- CDP 短路径（`/api/send` 等）。点歌系统没用。
- `PollGiftAsync`。没有调用方。

---

## 6. 最小对接清单（给审查后改代码用，本次未改）

若决定点歌系统改去打 CDP，最小集合是：

1. `douyin.baseUrl` → `http://127.0.0.1:17891`；看门狗改为拉 CDP 程序，或改为「只探测 17891、不启动旧 exe」。
2. 删掉或跳过 `SyncCookieProfileAsync` / `cookie/import`（CDP 不吃 Cookie 文件）。
3. `StopCollectSessionAsync` 改为 `POST /api/live/collect/stop`，不要拼 session id。
4. 登录昵称：要么 CDP `health` 增加 `nickname`，要么客户端不再用它做过滤，并确认 `OutboundReplyTracker` 够用。
5. 礼物：先定 Cookie 从哪来，再动 `FileCookieProvider`。在这之前礼物积分不可用。
6. 进场/点赞：产品上确认要不要。要的话改 CDP `listen.go` 的 method 过滤，并约定 `msg_type` 仍用 `member` / `like` / `gift`。

---

## 7. 包内文件

审查包只含源码和本报告，不含浏览器配置、Cookie、`data/`、`bin/`。

点歌系统侧：

- `LiveAssistant/Services/DouyinService.cs`
- `LiveAssistant/Services/DanmakuService.cs`
- `LiveAssistant/Services/FileCookieProvider.cs`
- `LiveAssistant/Services/SidecarCookieStore.cs`
- `LiveAssistant/Services/GiftImFetchClient.cs`
- `LiveAssistant/Services/GiftCollectorService.cs`
- `LiveAssistant/Services/ProcessWatchdogService.cs`
- `LiveAssistant/SidecarLocator.cs`
- `LiveAssistant/Models/ApiModels.cs`
- `LiveAssistant/Models/DanmakuItem.cs`
- `LiveAssistant/Models/MentionSendResult.cs`
- `LiveAssistant/Config/AppSettings.cs`（只含设置模型，不含 `appsettings.json` 账号口令）

CDP 侧：`cmd/`、`internal/` 全部 `.go`，外加 `go.mod`、`使用说明.md`。
