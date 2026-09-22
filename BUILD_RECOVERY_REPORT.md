# BUILD_RECOVERY_REPORT

生成时间：2026-09-22  
源码目录：`E:\恢复重建\LiveAssistant\merged`  
原则：只编译验收，不改业务、不重构、不补功能。

---

## 1. 命令与结果

| 步骤 | 命令 | 退出码 | 结果 |
|------|------|--------|------|
| 还原 | `dotnet restore LiveAssistant.sln` | 0 | 成功 |
| 编译 | `dotnet build LiveAssistant.sln --no-restore` | 0 | **0 错误 / 5 警告** |

还原项目：

- `StartLiveSystem\StartLiveSystem.csproj`
- `LiveAssistant\LiveAssistant.csproj`
- `LiveAssistant.Tests\LiveAssistant.Tests.csproj`

产出：

- `StartLiveSystem\bin\Debug\net8.0-windows\StartLiveSystem.dll` / `.exe`
- `LiveAssistant\bin\Debug\net8.0-windows\LiveAssistant.dll` / `.exe`
- `LiveAssistant.Tests\bin\Debug\net8.0-windows\LiveAssistant.Tests.dll`

---

## 2. 编译错误

**无。**

主工程与测试工程均可完整通过编译。

---

## 3. 编译警告（仅记录，未改代码）

| 位置 | 警告 | 说明 |
|------|------|------|
| `Services\AiSpeech\AiSpeechCoordinator.cs:477` | CS8604 | 可能向 `IsDuplicateRecent` 传入 null |
| `GiftConcurrentPointsTests.cs:70-72` | xUnit1031 | 测试里使用阻塞式 Task 等待 |
| `SongRequestConfirmFlowTests.cs:84` | xUnit2013 | 集合数量断言建议用 `Assert.Single` |

不影响生成。

---

## 4. 缺失文件（相对“可运行打包态”，非编译失败）

编译本身不缺 `.cs` / NuGet。下列是 **运行/侧车** 层缺口（构建日志未报错，但 `CopySidecarsToOutput` 因文件不存在而跳过）：

| 预期路径（相对 merged） | 状态 | 影响 |
|-------------------------|------|------|
| `sidecars\cdp-danmaku.exe` | **缺失** | 弹幕 CDP 无法由本仓库输出目录启动 |
| `sidecars\douyin-cdp\cdp-danmaku.exe` | **缺失** | 同上 |
| `sidecars\酷狗api_v1.5.exe` | **缺失** | 搜歌/取链无法启动 |
| `sidecars\kgapijs\server.js` | **缺失** | kgapijs 不完整（仅有 module/util/app.js 残片） |
| `sidecars\kgapijs\main.js` / `package.json` / `node_modules` | **缺失** | 酷狗 Node 旁路无法工作 |
| `LiveAssistant\Deploy\MachineSetup\**` | **几乎空**（merged 仅 1 文件） | 一键装机资源不在源码树 |

对照：`D:\TestXiangyuLive\LiveAssistant\` 与其 `Deploy\MachineSetup\sidecars\` **具备完整 exe + kgapijs**。

---

## 5. 缺失依赖

| 依赖 | 状态 |
|------|------|
| .NET 8 Windows（`net8.0-windows`） | 本机可用，可 restore/build |
| NuGet：`NAudio` 2.2.1 | 已还原 |
| NuGet：`Microsoft.Data.Sqlite` 8.0.11 | 已还原 |
| NuGet：`SSH.NET` 2026.0.0 | 已还原 |
| NuGet：`Google.Protobuf` + `Grpc.Tools` | 已还原（含 `douyin_live.proto`） |
| FrameworkReference：`Microsoft.AspNetCore.App` | 可用（管理后台 Kestrel） |
| `git` CLI | **本机 PATH 不可用**（仓库有 `.git` 目录，但不影响 dotnet 编译） |

---

## 6. 源码完整度（编译视角）

| 项 | 数值 |
|----|------|
| `LiveAssistant/**/*.cs` | 166 个，约 3.48 MB |
| 空 `.cs` 文件 | **0** |
| 测试项目 `.cs` | 55 个 |
| `*.from_recovery` 残留副本 | 6 个（不影响编译，属恢复过程备份） |

---

## 7. 相关测试（验收，未改代码）

```text
dotnet test --filter FullyQualifiedName~SongRequest
失败: 0，通过: 35，跳过: 0
```

点歌确认/扣费/冷却相关单测在当前 merged 源码上 **全部通过**。

---

## 8. 结论

- **源码编译态：可恢复成功。** 无需为“能编过”而改代码。
- **运行态：仍缺 sidecar 二进制与完整 kgapijs。** 应从旧部署 `D:\TestXiangyuLive` 拷贝，而不是重写业务。
- 下一步优先：**资产回填（exe + kgapijs）→ 启动链探活**，不要重构。
