#!/usr/bin/env python3
"""Generate LiveAssistant-Full-CodeReview.zip for external code review."""

from __future__ import annotations

import json
import os
import re
import sys
import zipfile
from datetime import datetime
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
OUT = ROOT / "LiveAssistant-Full-CodeReview.zip"

SKIP_DIRS = {
    ".git",
    ".vs",
    "__pycache__",
    "bin",
    "obj",
    "publish",
    "node_modules",
    "terminals",
}
SKIP_SUFFIX = {
    ".pdb",
    ".zip",
    ".db",
    ".log",
    ".dll",
    ".exe",
    ".cache",
}
SKIP_NAMES = {
    "admin-accounts.json",
    "DeployCredentials.local.json",
    "appsettings.json.bak-publish",
    "Thumbs.db",
    "desktop.ini",
}
MAX_FILE_BYTES = 5 * 1024 * 1024

REQUIRED_PATHS = [
    "LiveAssistant/Services/PlaybackService.cs",
    "LiveAssistant/Services/PlaybackCommandQueue.cs",
    "LiveAssistant/Services/SongRequestService.cs",
    "LiveAssistant/Services/SongRequestSessionStore.cs",
    "LiveAssistant/Services/SongRequestPermissionService.cs",
    "LiveAssistant/Services/SongRequestControlService.cs",
    "LiveAssistant/Services/KugouService.cs",
    "LiveAssistant/Services/GiftService.cs",
    "LiveAssistant/Services/DanmakuService.cs",
    "LiveAssistant/Services/ReplyQueue.cs",
    "LiveAssistant/Services/OutboundReplyTracker.cs",
    "LiveAssistant/SidecarBootstrap.cs",
    "LiveAssistant/Services/LiveHealthService.cs",
    "LiveAssistant/Services/UserDetailService.cs",
    "LiveAssistant/Database/AppDatabase.cs",
    "LiveAssistant/Database/UserRepository.cs",
    "LiveAssistant/Database/GiftRepository.cs",
    "LiveAssistant/Database/PointsLedgerRepository.cs",
    "LiveAssistant/Admin/AdminWebHost.cs",
    "LiveAssistant/Admin/AdminAppContext.cs",
    "LiveAssistant/Admin/wwwroot/index.html",
    "LiveAssistant/UI/MainForm.cs",
    "LiveAssistant/Utils/HttpJson.cs",
    "LiveAssistant/Services/DouyinService.cs",
    "LiveAssistant/Config/AppSettings.cs",
    "LiveAssistant.Tests/LiveAssistant.Tests.csproj",
]


def should_skip(path: Path) -> bool:
    if any(part in SKIP_DIRS for part in path.parts):
        return True
    if path.name in SKIP_NAMES or "admin-accounts" in path.name.lower():
        return True
    if path.suffix.lower() in SKIP_SUFFIX:
        return True
    lower = path.name.lower()
    if "cookie" in lower and path.suffix.lower() in {".json", ".txt", ".dat"}:
        return True
    if lower.endswith(".db-shm") or lower.endswith(".db-wal"):
        return True
    return False


SENSITIVE_PATTERNS = [
    (re.compile(r"fanfan9724"), "<REDACTED>"),
    (re.compile(r"qwe123"), "<REDACTED>"),
    (re.compile(r"la-tunnel-9f3c2e8b7a1d4c6e"), "<REDACTED>"),
]


def sanitize_text(text: str) -> str:
    for pattern, repl in SENSITIVE_PATTERNS:
        text = pattern.sub(repl, text)
    return text


def sanitize_appsettings(text: str) -> str:
    data = json.loads(text)
    admin = data.get("admin", {})
    admin["username"] = ""
    admin["password"] = ""
    admin["passwordEnvVar"] = "ADMIN_PASSWORD"
    admin["tunnelSecret"] = ""
    admin["accounts"] = [
        {"username": "reviewer", "password": "<REDACTED>"},
        {"username": "operator", "password": "<REDACTED>"},
    ]
    data["admin"] = admin

    tunnel = data.get("adminTunnel", {})
    tunnel["host"] = ""
    tunnel["user"] = "root"
    tunnel["password"] = ""
    tunnel["credentialsFile"] = "DeployCredentials.local.json"
    data["adminTunnel"] = tunnel

    douyin = data.get("douyin", {})
    douyin["apiToken"] = ""
    douyin["webRid"] = ""
    data["douyin"] = douyin

    kugou = data.get("kugou", {})
    kugou["apiKey"] = ""
    data["kugou"] = kugou
    return json.dumps(data, ensure_ascii=False, indent=2) + "\n"


def add_file(zf: zipfile.ZipFile, src: Path, arc: str, stats: dict) -> None:
    if should_skip(src):
        stats["skipped"] += 1
        return
    if not src.is_file():
        return
    size = src.stat().st_size
    if size > MAX_FILE_BYTES:
        stats["skipped_large"] += 1
        stats["large_files"].append(f"{arc} ({size // 1024 // 1024}MB)")
        return

    if src.name == "appsettings.json" and "LiveAssistant/Config/" in arc.replace("\\", "/"):
        payload = sanitize_appsettings(src.read_text(encoding="utf-8"))
        zf.writestr(arc, payload)
    elif src.suffix.lower() in {".cs", ".md", ".json", ".html"}:
        payload = sanitize_text(src.read_text(encoding="utf-8"))
        zf.writestr(arc, payload)
    else:
        zf.write(src, arc)

    stats["files"] += 1
    top = arc.split("/", 1)[0]
    stats["dirs"][top] = stats["dirs"].get(top, 0) + 1


def add_tree(zf: zipfile.ZipFile, base: Path, arc_prefix: str, stats: dict) -> None:
    if not base.is_dir():
        print(f"WARN missing directory: {base}", file=sys.stderr)
        return
    for dirpath, dirnames, filenames in os.walk(base):
        dirnames[:] = sorted(d for d in dirnames if d not in SKIP_DIRS)
        dp = Path(dirpath)
        for fn in sorted(filenames):
            fp = dp / fn
            rel = fp.relative_to(base).as_posix()
            add_file(zf, fp, f"{arc_prefix}/{rel}", stats)


def collect_cs_files(base: Path) -> list[str]:
    if not base.is_dir():
        return []
    return sorted(
        str(p.relative_to(base)).replace("\\", "/")
        for p in base.rglob("*.cs")
        if not should_skip(p)
    )


def build_audit_context(stats: dict, missing: list[str], cs_count: int, test_count: int) -> str:
    now = datetime.now().strftime("%Y-%m-%d %H:%M")
    dir_lines = "\n".join(f"- `{name}/`: {count} files" for name, count in sorted(stats["dirs"].items()))
    missing_block = "\n".join(f"- `{p}`" for p in missing) if missing else "- 无"
    large_block = "\n".join(f"- {x}" for x in stats["large_files"]) if stats["large_files"] else "- 无"

    return f"""# LiveAssistant 代码审查上下文

> 生成时间: {now}
> 包内文件数: {stats['files']}
> C# 源文件: {cs_count}
> 测试 C# 文件: {test_count}

## 1. 项目架构说明

LiveAssistant 是一个 **24 小时直播助手** WinForms 桌面程序，负责：

- 通过 **抖音 Sidecar** (`http://127.0.0.1:4723`) 采集弹幕、发送 @ 回复
- 通过 **酷狗 Sidecar** (`http://127.0.0.1:17888`) 搜索/播放歌曲
- 本地 **SQLite** 持久化用户、队列、积分、礼物、模板
- 内嵌 **ASP.NET Core 管理后台** (`AdminWebHost`) 提供 Web 运营界面
- 可选 **SSH 反向隧道** 暴露后台到云端

### 分层结构

| 层 | 目录 | 职责 |
|---|---|---|
| UI | `LiveAssistant/UI/` | WinForms 主界面（纯代码布局，无 Designer） |
| Admin | `LiveAssistant/Admin/` | Web 后台 Host + 静态前端 `wwwroot/index.html` |
| Services | `LiveAssistant/Services/` | 业务编排、Sidecar 通信、播放/点歌/礼物 |
| Database | `LiveAssistant/Database/` | SQLite Repository |
| Models | `LiveAssistant/Models/` | DTO / 领域模型 |
| Config | `LiveAssistant/Config/` | `appsettings.json` + `AppSettings.cs` |
| Utils | `LiveAssistant/Utils/` | HTTP 封装、解析器 |
| GiftProtocol | `LiveAssistant/GiftProtocol/` | 抖音礼物 protobuf 解析 |
| Tests | `LiveAssistant.Tests/` | xUnit 单元/集成测试 |

### 启动链路

```
Program.cs
  -> SidecarBootstrap.EnsureReady()     # 复制 sidecar 到 EXE 旁
  -> LiveAppHost                        # 组装全部服务
  -> MainForm                           # WinForms UI
  -> ProcessWatchdogService             # 守护 sidecar 进程
  -> AdminWebHost.Start()               # 本地 Web 后台
```

### Sidecar 通信

| 方向 | 客户端 | Sidecar | 超时 |
|---|---|---|---|
| 抖音 | `DouyinService` + `HttpJson` | 4723 | 30s (HttpClient 默认) |
| 酷狗 | `KugouService` + `HttpJson` | 17888 | 45s (KugouService 覆盖) |
| 礼物 IM | `GiftImFetchClient` | 4723 | 见实现 |
| 回复 | `ReplyQueue` -> `DouyinService.SendMentionAsync` | 4723 | 带重试 |

**审查重点**: `HttpJson.cs`, `DouyinService.cs`, `KugouService.cs`, `ReplyQueue.cs`, `DanmakuService.cs`, `ProcessWatchdogService.cs`, `SidecarBootstrap.cs`

## 2. 当前功能列表

### 点歌
- 弹幕 `点歌 歌名` -> 搜索 -> 多结果确认 -> 入队
- 策略: 免费 / 积分 / 礼物解锁 (`SongRequestPolicy`)
- 等级权限、冷却、每日上限、黑名单
- 后台紧急关闭 / 暂停互动

### 播放
- `PlaybackEngine` 调度
- `PlaybackCommandQueue` 串行命令队列
- `PlaybackService` NAudio 本地播放
- 模式: 点歌+随机补位 / 点歌优先 / 纯随机
- 酷狗 `requireFullPlayback` 完整播放保护

### 队列
- `QueueService` + SQLite `queue_items`
- 置顶 / 立即播放 / 删除 / 清空
- 启动时 `playing` 状态恢复为 `waiting`

### 积分
- 礼物 -> `GiftService` -> `PointsLedgerRepository`
- 点歌扣费 / 切歌扣费
- 后台手动调账（需 operator + reason）
- `查积分` 弹幕指令

### 礼物
- `GiftCollectorService` 轮询 + protobuf 解析
- `GiftRuleRepository` 可配置礼物积分
- 连击合并 `ComboTracker`

### 等级
- `UserLevelService` 按累计积分升级
- `LevelPermissionRepository` 表格化权限
- 冷却 / 插队优先级 / 点歌开关

### 用户
- `UserRepository` + `UserDetailService`
- 角色: normal / admin / manager / blacklist
- 禁言投票 `BanVoteService`

### 后台
- 7 个一级模块: 驾驶舱 / 点歌 / 用户 / 礼物 / 播放 / 回复 / 系统
- Bearer Token 登录
- 紧急操作: 暂停互动 / 关闭点歌 / 酷狗重登
- 运营日志 `LogService.ReadRecentOpsEntries`

### 模板
- `ReplyTemplateRepository` + `ReplyTemplates.json`
- 变量替换 + 预览服务

### 健康监控
- `LiveHealthService` 聚合运行时状态
- `RuntimeStatus` 暴露给 UI 与后台
- Sidecar 在线检测 / 近期错误计数

## 3. 数据流

### 弹幕

```
抖音直播间
  -> 抖音 Sidecar (poll feed)
  -> DanmakuService
  -> DanmakuDeduplicator
  -> LiveAppHost 事件分发
       -> SongRequestService (点歌/确认/切歌/查积分)
       -> WelcomeService
       -> KeywordReplyService
       -> BanVoteService
  -> ReplyQueue -> DouyinService.SendMentionAsync
  -> OutboundReplyTracker (去重/追踪)
```

### 点歌

```
用户弹幕 "点歌 xxx"
  -> SongRequestPermissionService (等级/积分/冷却/开关)
  -> SongRequestSessionStore (多结果确认会话)
  -> KugouService.SearchAsync
  -> 用户回复 "确定"
  -> SongRequestService 扣费/校验
  -> QueueService.Enqueue
  -> PlaybackEngine
  -> PlaybackCommandQueue
  -> KugouService.PlayAsync / PlaybackService
```

### 礼物

```
抖音礼物消息
  -> GiftCollectorService
  -> GiftProtocolPipeline (protobuf)
  -> GiftEventDeduplicator
  -> GiftService.ApplyGiftAsync
  -> GiftRepository + PointsLedgerRepository
  -> UserLevelService.MaybePromote
  -> ReplyQueue (感谢语)
```

### 后台命令

```
AdminWebHost API
  -> AdminAppContext.Commands
  -> CommandQueueService
  -> LiveAppHost 主循环消费
  -> PlaybackEngine / SongRequestControl / Config reload
```

## 4. 已完成修复列表

| 领域 | 说明 |
|---|---|
| 播放卡顿 | `PlaybackCommandQueue` 串行化播放命令，避免并发冲突 |
| 播放稳定性 | `PlaybackStabilityTests` 覆盖暂停/恢复/切歌/随机补位 |
| 积分事务 | `PointsLedgerRepository` + `UserRepository` 同事务写入流水 |
| 用户锁 | `SongRequestUserGateRegistry` 防止同用户并发点歌竞态 |
| 礼物去重 | `event_id` 唯一索引 + `GiftEventDeduplicator` |
| 队列恢复 | 启动时将 `playing` 重置为 `waiting` |
| UI 重构 | Web 7 模块导航 + WinForms 60/40 分栏 + 告警条 |
| 酷狗完整播放 | `requireFullPlayback` + 播放进度监控 |
| 回复稳定性 | `ReplyQueue` 限速/重试 + `OutboundReplyTracker` |
| 后台运营 | 等级表格化、运营日志、紧急操作条 |

## 5. 当前已知风险

| 风险 | 说明 |
|---|---|
| Sidecar 不可见 | 本包 **不含** 抖音/酷狗 Sidecar 二进制与源码，通信边界仅能审查 LiveAssistant 侧 |
| HTTP 超时不一致 | 默认 30s，`KugouService` 45s，部分调用未单独配置 CancellationToken |
| HTTP 无统一重试 | `HttpJson` 无自动重试；`ReplyQueue` 有重试，Sidecar API 多数 fail-open |
| Sidecar 状态同步 | `_douyinSidecarOk` / `_kugouSidecarOk` 由 watchdog 异步更新，存在短暂不一致窗口 |
| WinForms 无 Designer | `MainForm.cs` 纯代码布局，DPI/分辨率问题需运行时验证 |
| 单文件前端 | 后台 CSS/JS 内联于 `index.html`，无独立资源文件 |
| 敏感配置 | 审查包已脱敏；真实部署需 `DeployCredentials.local.json`（未包含） |
| SSH 隧道 | `AdminTunnelService` 依赖 SSH.NET + 远端配置，本包不含远端环境 |
| 数据库迁移 | `AppDatabase.Migrate()` 为运行时 ALTER，无独立 migration 文件 |
| 礼物协议 | protobuf 来自第三方 MIT 仓库，需关注抖音协议变更 |

## 6. 包内容说明

### 顶层目录

{dir_lines}

### 配置脱敏

- `LiveAssistant/Config/appsettings.json`: 已清空 token / 密码 / 真实账号
- 未包含: `DeployCredentials.local.json`, `admin-accounts.json`, cookie 文件, `.db` 数据库

### 测试

- 目录: `LiveAssistant.Tests/`
- 运行: `dotnet test LiveAssistant.Tests/LiveAssistant.Tests.csproj`

### 关键文件检查

缺失的关键路径:

{missing_block}

跳过大文件:

{large_block}

## 7. 建议审查顺序

1. `AUDIT_CONTEXT.md`（本文件）
2. `LiveAssistant/Services/LiveAppHost.cs` — 组装与生命周期
3. `LiveAssistant/Utils/HttpJson.cs` + `DouyinService.cs` + `KugouService.cs` — Sidecar 通信
4. `LiveAssistant/Services/SongRequestService.cs` + `SongRequestPermissionService.cs` — 点歌核心
5. `LiveAssistant/Services/PlaybackCommandQueue.cs` + `PlaybackService.cs` — 播放链路
6. `LiveAssistant/Database/AppDatabase.cs` + Repositories — 数据层
7. `LiveAssistant/Admin/AdminWebHost.cs` + `wwwroot/index.html` — 后台
8. `LiveAssistant/UI/MainForm.cs` — 桌面 UI
9. `LiveAssistant.Tests/` — 回归测试理解预期行为

## 8. 无法在本包内审查的部分

- 抖音 Sidecar 内部实现（`抖音直播弹幕助手.exe`）
- 酷狗 Sidecar 内部实现（`酷狗api_v1.5.exe` + `kgapijs/`）
- 运行时 SQLite 数据文件
- 真实 Cookie / Session / 登录态
- 云端 Nginx / SSH 部署环境
- WinForms 实际 DPI 渲染效果（需本地运行）
"""


def main() -> int:
    stats = {
        "files": 0,
        "skipped": 0,
        "skipped_large": 0,
        "large_files": [],
        "dirs": {},
    }
    included: set[str] = set()

    if OUT.exists():
        OUT.unlink()

    with zipfile.ZipFile(OUT, "w", zipfile.ZIP_DEFLATED, compresslevel=6) as zf:
        add_tree(zf, ROOT / "LiveAssistant", "LiveAssistant", stats)
        add_tree(zf, ROOT / "LiveAssistant.Tests", "LiveAssistant.Tests", stats)

        extras = [
            (ROOT / "LiveAssistant.sln", "LiveAssistant.sln"),
            (ROOT / "README.md", "README.md"),
            (ROOT / "功能说明.md", "功能说明.md"),
            (ROOT / "安装使用说明.md", "安装使用说明.md"),
            (ROOT / "Config" / "DeployCredentials.example.json", "Config/DeployCredentials.example.json"),
            (ROOT / "deploy" / "www" / "health-ok.json", "deploy/www/health-ok.json"),
            (ROOT / "deploy" / "www" / "diangexitong-offline.html", "deploy/www/diangexitong-offline.html"),
        ]
        for src, arc in extras:
            if src.is_file():
                add_file(zf, src, arc, stats)
                included.add(arc.replace("\\", "/"))

        for info in zf.infolist():
            included.add(info.filename.replace("\\", "/"))

        missing = [p for p in REQUIRED_PATHS if p not in included]
        cs_count = len(collect_cs_files(ROOT / "LiveAssistant"))
        test_count = len(collect_cs_files(ROOT / "LiveAssistant.Tests"))
        audit = build_audit_context(stats, missing, cs_count, test_count)
        zf.writestr("AUDIT_CONTEXT.md", audit)

    size_mb = OUT.stat().st_size / 1024 / 1024
    print(f"OK: {OUT}")
    print(f"SIZE_MB: {size_mb:.2f}")
    print(f"FILES: {stats['files'] + 1}")  # + AUDIT_CONTEXT.md
    print(f"SKIPPED: {stats['skipped']}")
    print(f"DIRS: {', '.join(sorted(stats['dirs']))}")
    missing = [p for p in REQUIRED_PATHS if p not in included]
    if missing:
        print("MISSING:")
        for p in missing:
            print(f"  - {p}")
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
