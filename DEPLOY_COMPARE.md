# DEPLOY_COMPARE

对照对象：

- 旧可运行部署：`D:\TestXiangyuLive`
- 恢复源码树：`E:\恢复重建\LiveAssistant\merged`

原则：找出旧版本运行依赖，指导回填；不重新设计。

---

## 1. 旧部署顶层结构

```text
D:\TestXiangyuLive\
  StartLiveSystem.exe          # 一键启动器（单文件约 35MB）
  launcher.json                # 指向本目录各组件
  setup-prereqs.ps1
  启动直播系统.bat
  安装说明.txt
  unins000.exe / .dat          # 安装残留
  LiveAssistant\               # 主程序运行根（关键）
  sidecars\                    # CDP 副本
  Maoyan\                      # 猫眼 Overlay
  publish\LiveAssistant-one\   # 目前几乎只剩 cdp-danmaku.exe
  data\                        # 运行数据/配置/库
  logs\
```

---

## 2. 旧 exe 旁关键依赖（LiveAssistant 目录）

| 文件/目录 | 大小约 | 作用 |
|-----------|--------|------|
| `LiveAssistant.exe` | 87.8 MB | 单文件主程序（旁无散落 DLL） |
| `cdp-danmaku.exe` | 15.9 MB | 抖音 CDP 弹幕 |
| `酷狗api_v1.5.exe` | 13.3 MB | 酷狗 API |
| `kgapijs\` | 完整（含 node_modules） | 酷狗 Node 旁路 |
| `Config\` | appsettings / 模板 / AiSpeech prompts | 运行配置 |
| `Admin\wwwroot\index.html` | 85 KB | 管理页 |
| `Deploy\MachineSetup\` | 很大（含 sidecar 副本） | 装机包 |
| `data\` | client_id / settings / browser-profile 等 | 运行态 |
| `downloads\` / `logs\` | — | 缓存与日志 |

结论：旧版本是 **“主程序单文件 + 同目录 sidecar + kgapijs”** 的部署形态，不是 framework-dependent 散落 DLL。

---

## 3. merged 构建输出 vs 旧部署

| 项 | merged Debug 输出 | 旧 D:\TestXiangyuLive\LiveAssistant |
|----|-------------------|--------------------------------------|
| 主程序 | `LiveAssistant.exe` ~152KB + `LiveAssistant.dll` ~2.2MB + 依赖 DLL | 单文件 ~88MB |
| NAudio | 有（NAudio*.dll） | 打进单文件 |
| Sqlite / SSH.NET | 有 | 打进单文件 |
| `cdp-danmaku.exe` | **无** | **有** |
| `酷狗api_v1.5.exe` | **无** | **有** |
| `kgapijs` | 残片（无 server.js / node_modules） | **完整** |
| `Config\*` | 源码树有，会 CopyToOutput | 有 |
| `Admin\wwwroot` | 有（略新/略大） | 有 |
| `Deploy\MachineSetup` | 几乎空 | 完整 |

---

## 4. launcher.json 路径差异

| 组件 | merged `launcher.json` | 旧 `D:\TestXiangyuLive\launcher.json` |
|------|------------------------|----------------------------------------|
| SourceRoot | `E:\我的源码目录`（删除前路径） | 本机安装根 |
| CDP Exe | 指向已删除的源码 `抖音cdp弹幕\bin\...` | `...\sidecars\douyin-cdp\cdp-danmaku.exe` |
| 酷狗 Exe | 指向已删除的 `酷狗协议\KgDesktop\...` | `LiveAssistant\酷狗api_v1.5.exe` |
| 猫眼 | 旧源码 dist | `Maoyan\MaoyanOverlay-1.0.0.exe` |
| LiveAssistant | `publish\LiveAssistant-one\...` | `LiveAssistant\LiveAssistant.exe` |
| Environment | RequireDotnet=true | RequireDotnet=false（面向安装包用户） |

**含义：** merged 的 launcher 仍指向事故前源码绝对路径，**不能直接当当前恢复环境的启动配置**。旧 `D:\TestXiangyuLive\launcher.json` 才是可运行参考。

---

## 5. kgapijs 差异（高优先级）

| 文件 | merged\sidecars\kgapijs | 旧 LiveAssistant\kgapijs |
|------|-------------------------|--------------------------|
| app.js / index.js | 有 | 有 |
| module\ / util\ | 有（协议模块） | 有 |
| server.js | **无** | 有 |
| main.js | **无** | 有 |
| package.json / lock | **无** | 有 |
| node_modules | **无** | 有 |
| public | **无** | 有 |
| 文件数 / 体积 | ~100 / ~41KB | ~1168 / ~9.6MB |

---

## 6. 配置与数据

| 资产 | merged | 旧部署 |
|------|--------|--------|
| `Config\appsettings.json` | 有（douyin 已是 17891） | 有（同样 17891） |
| AiSpeech prompt txt | 有 | 有 |
| `data\liveassistant.db` | 无（运行后生成） | 有（运行库） |
| `data\appsettings.json` | 无 | 有（运行副本） |
| ReplyTemplates | Config 有 | Config + data 有 |

说明：报告中不摘录账号口令等敏感字段；恢复时以旧 Config 为行为参考即可。

---

## 7. 旧版本运行依赖清单（恢复目标）

最小可跑点歌链（相对 LiveAssistant 运行根）：

1. `LiveAssistant.exe`（或 `dotnet` 构建出的 Debug/Release 输出）
2. `cdp-danmaku.exe`
3. `酷狗api_v1.5.exe`
4. 完整 `kgapijs\`（至少含 `app.js` + `server.js` + `node_modules` + `package.json`）
5. `Config\appsettings.json`（及 ReplyTemplates / AiSpeech 文本）
6. `Admin\wwwroot\index.html`
7. 本机：Chrome（CDP）、Node（kgapijs）、.NET 8（若非单文件）

可选：

- `MaoyanOverlay-1.0.0.exe`
- Ollama / GPT-SoVITS（AI 口播）
- `StartLiveSystem.exe` + 正确路径的 `launcher.json`

---

## 8. 建议回填映射（只拷贝，不改业务）

| 从（旧） | 到（merged，建议） |
|----------|-------------------|
| `D:\TestXiangyuLive\LiveAssistant\cdp-danmaku.exe` | `merged\sidecars\cdp-danmaku.exe` 及 `sidecars\douyin-cdp\` |
| `D:\TestXiangyuLive\LiveAssistant\*api_v1.5.exe` | `merged\sidecars\酷狗api_v1.5.exe` |
| `D:\TestXiangyuLive\LiveAssistant\kgapijs\`（整树） | `merged\sidecars\kgapijs\`（覆盖残片） |
| 可选：旧 `launcher.json` 作模板 | 改路径指向 merged 输出或继续用 `D:\TestXiangyuLive` 作为运行根 |

也可 **直接以 `D:\TestXiangyuLive` 作为运行验收根**，merged 只负责源码对齐——这更贴近“恢复旧项目”。

---

## 9. 结论

- 旧 exe 旁依赖齐全；**点歌运行不缺主程序逻辑，缺的是 sidecar 资产回填与 launcher 路径纠正**。
- merged 已能编译，但 **不能单独当作完整运行包**。
- 不要为补齐依赖去重写酷狗/CDP；从 `D:\TestXiangyuLive` 拷贝即可。
