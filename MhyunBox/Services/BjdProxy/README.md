# 布吉岛专用 A/B 会话共享代理（BjdProxy）

本目录是「只对布吉岛服务器生效」的指定代理实现。它**不是** `ProxyCaptureService` 的替代品，
两者解决的是不同问题，请先分清：

| | 通用代理（`ProxyCaptureService`） | 本目录（BjdProxy） |
| --- | --- | --- |
| 原理 | 本机 TCP 中继 + 拦 Minecraft `EncryptionRequest`，用账号会话重新走 `login-otp → GameStart → Yggdrasil joinServer` | 把 payload 反射注入 A 客户端 JVM，payload Hook A 的 Netty 连接、把世界快照推给 B |
| 账号需求 | **每个客户端都要有可用账号会话** | **不需要第二个账号**，B 复用 A 的身份 |
| 生效范围 | 所有服务器预设 | 仅布吉岛（GameId `4661334467366178884`） |
| 监听端口 | `ProxyStarter` basePort 起递增 | payload 固定 `13337` 起（`ProxyPortProbe.Span` 个） |

> 结论：**不要把门禁加进 `ProxyCaptureService`**。那会直接封死元素之诗 / 失落世界等所有服务器的
> 正常代理；而本功能是叠加在通用代理之上的另一条独立链路。

---

## 一、组成

| 文件 | 职责 |
| --- | --- |
| `BjdGate.cs` | **唯一门禁真源**：`GameId = 4661334467366178884`，任何入口都调它 |
| `BjdProcessLocator.cs` | 定位 A 客户端：可见窗口标题含「布吉岛」+ 进程是 `java.exe`/`javaw.exe` |
| `BjdInjector.cs` | 注入编排：payload 路径解析 + 反射注入 / ManualMap / LoadLibrary 兜底 |
| `BjdProxyService.cs` | 服务层：门禁 → 定位 → 注入 → 等端口 → 状态；含 `StateChanged` 事件 |
| `Native/` | 从 WtfNel 移植的托管注入栈（PE 解析、远程进程、反射加载、手动映射、端口探测） |

`Native/` 的代码**逐行等价移植**自 `WtfNel/src/WtfNel.Core/Injection/`，只改了命名空间与日志调用。
上游 README 已注明该 payload 源自 **SakuraTools**（GzSakura1338/SakuraTools），WtfNel 只做移植。

---

## 二、门禁（"仅布吉岛生效"）

判定只认 `BjdGate.GameId`，共三道：

1. **UI 可见性**：`ProxyCapturePage.UpdateBjdSectionVisibility()` —— 只有选中的预设是布吉岛时区块才可见；
2. **点击兜底**：`BjdProxyService.CanUse()` —— 即便按钮被残留状态点到，也在这里拒绝并提示；
3. **编排入口**：`BjdProxyService.StartAsync()` 第一行再判一次（防绕过 / 防并发改选）。

另有第二重"进程级"确认：`BjdProcessLocator.MatchesBjdWindow()` 要求目标进程仍是 java 且窗口标题仍含「布吉岛」。
之所以不用命令行 `-DlauncherGameId=` 复核：本工程没有 `System.Management` 引用，读 PEB 又要额外权限且易碎；
窗口标题 + java 进程是两个上游工具的生产验证做法（SakuraTools `injector/cli.c`、WtfNel `InjectionProfile.Minecraft`），
再叠加第 1 道 GameId 门禁已经足够。

**新增入口时务必调用 `BjdGate`**，禁止在别处重复硬编码 GameId 或服务器名。

---

## 三、预设

布吉岛已内置进 `ServerPresets.All`（`Name = "布吉岛·新玩法上线"`，`Host = pc.bjdmc.net`，`Port = 25565`，
`GameId = 4661334467366178884`）。

原因：它原先只靠「服务器列表」页 `RegisterDiscovered()` 写进
`MhyUnbox_Logs/discovered_servers.json` 动态注册 —— 换目录、清日志、新机器上预设就没了，
而门禁依赖这个 GameId 必须稳定。内置化后：

- `ServerPresets.IsBuiltin()` 认它 → 不可删除；
- `ServerPresets.SaveDiscovered()` 排除它 → 不会重复写回 JSON；
- `LoadDiscovered()` 的 `ContainsGameId` 去重 → 旧 JSON 里那条被自动忽略，无害。

---

## 四、payload 供给

优先级（`BjdInjector.ResolvePayloadPath`）：

1. 环境变量 `MHYUNBOX_BJD_PAYLOAD`（完整路径，便于换版本/排障）；
2. `<程序目录>\payloads\WtfNel.Payload.Proxy.dll` ← **正常发布路径**，由 `MhyunBox.csproj` 的
   `Content Include="..\WtfNel\native\prebuilt\..." Link="payloads\..."` 自动拷贝；
3. `<程序目录>\WtfNel.Payload.Proxy.dll`；
4. 从程序目录向上回溯 6 层，找源码仓库里的 `WtfNel\native\prebuilt\`（直接跑 `bin\` 调试时用）。

payload 大小 310,784 字节，SHA256
`e0af741f5932510099ce92c2de8062afd04018cee3b9ee0f3dfae52f394f7b8a`
（见 `WtfNel/native/prebuilt/SHA256SUMS.txt`）。

---

## 五、使用流程

1. 用 PCL 启动布吉岛客户端（**1.20.1 + Forge**；payload 按 1.20.1 对齐），登录**并进入服务器世界**；
2. 在「代理启动」页选中 **布吉岛·新玩法上线** → 区块出现「布吉岛专用代理（A/B 会话共享）」；
3. 点「注入 / 恢复布吉岛代理」→ 程序先做**入服检查**（见第八节）→ 定位 java 窗口 → 反射注入 → 等 13337 起的新监听；
4. 成功后状态行显示 `127.0.0.1:<端口>`，第二个客户端在游戏内添加该地址即可共用 A 的身份。

**停用（正常 mid-session 共享时）**：payload 常驻 A 进程，程序**无法远程卸载**。把 A 客户端窗口切到前台，
松开再按一次 DEL 停用（长按只触发一次）。再次点注入按钮会唤醒常驻控制器重新启用。
⚠ 若 payload 已把 Render 线程挂起（画面卡死），**按 DEL 不会解除**，见第八节。

---

## 六、已知限制 / 注意

| 项 | 说明 |
| --- | --- |
| 客户端版本 | payload 按 **Minecraft 1.20.1** 对齐。换版本需重新对齐 `b_server.cpp` 的类名/字段映射并重编 payload |
| **注入时机（最重要）** | **必须 A 已进入服务器世界再注入**。A 未入服时 payload 会把 Render 线程无限期挂起（画面卡死），且不可逆（DEL 无效）。程序已做硬性入服检查拦截，详见第八节 |
| Forge 映射缺口 | 实测 Forge 47.4.29 下 payload 的 `BServer_TryCaptureLiveConnection` 可能报 `mid-session: refs missing`，从而回落到阻塞路径。程序会**即时检测并警告**；彻底修需要重编 payload 对齐映射 |
| 端口占用 | payload 固定用 `13337`。若该端口已被别的 WtfNel 会话占用，`Before` 快照会把它排除，表现为「注入成功但无新监听」——先关掉旧会话 |
| 局域网暴露 | 该 payload 版本监听 `0.0.0.0:13337`（不是 `127.0.0.1`），并对局域网广播。**在不可信网络里请勿开启**；如需收紧只能重编 payload |
| 提权 | 若客户端以管理员运行而本程序不是，`OpenProcess` 会失败（注入需要 PROCESS_VM_WRITE/CREATE_THREAD 权限）。此时以同等权限运行本程序 |
| 32/64 位 | 必须 x64 客户端 + x64 payload（`BjdInjector` 注入前会做位宽校验） |
| 安全软件 | 反射注入 + 远程线程是典型杀软敏感行为，可能被拦截或删除 payload |

---

## 七、验证

### 7.1 已完成的自动化验证（2026-11-18，本机 .NET SDK 10.0.402 构建 net8 目标）

| 项 | 结果 |
| --- | --- |
| `dotnet build MhyunBox.csproj -c Release --no-restore` | **成功，0 错误、11 警告**；11 条全是仓库既有 CS4014×10 / CS0067×1，无一条来自 `BjdProxy/` 或 `Native/` |
| payload 随输出拷贝 | ✅ `bin\Release\net8.0-windows10.0.19041.0\payloads\WtfNel.Payload.Proxy.dll`，310,784 字节（与 `WtfNel/native/prebuilt/SHA256SUMS.txt` 记录一致） |
| 运行期门禁校验 | ✅ 见下方 `tools/bjdgate_test`，**31 条断言全 PASS、退出码 0**（含 TCP 连接表自测、真实 payload 日志读取、逐包刷屏过滤规则） |

```powershell
# 门禁回归（反射读已编译的 MhyunBox.dll，不启动 GUI）
cd C:\Notel_Exe\MhyunBox-v7.5.0_FULLVER_F\MhyunBox-8.0.0_src
dotnet run --project tools\bjdgate_test\BjdGateTest.csproj -c Release
# 期望末行：全部断言通过。EXIT=0
```

它固定断言：
- `ServerPresets.All` 内有 `GameId=4661334467366178884` 的预设，`Host=pc.bjdmc.net`、`Port=25565`、`IsPending=false`、`IsBuiltin=true`；
- `BjdGate.IsBjd` 仅对 `4661334467366178884` 为真（对元素之诗 GameId、0 为假）；
- `BjdGate.IsBjdTarget` 仅对 `pc.bjdmc.net` 为真（对其它 host、`null`、空串为假）；
- 按预设判定：元素之诗 / 失落世界 为 false，布吉岛为 true。

> 改动门禁相关代码后请重跑此工具，它是「仅布吉岛生效」的回归护栏。

### 7.2 仍需人工做的验证

```powershell
# 1. 编译（增量即可）
cd C:\Notel_Exe\MhyunBox-v7.5.0_FULLVER_F\MhyunBox-8.0.0_src
dotnet build MhyunBox.csproj -c Release

# 2. 跑起来，开调试模式看日志
#    日志应出现：[布吉岛代理] payload: ...，[布吉岛代理] 目标客户端：pid ...
#               [布吉岛代理] 布吉岛 A/B 会话共享代理已就绪：127.0.0.1:13337

# 3. 反证：选中元素之诗 / 失落世界时，该区块必须不可见；
#    用调试构造的调用也必须被 BjdProxyService.CanUse 拒绝（日志有「拒绝启动」）。
```

游戏内验证（必须做，编译通过不等于链路通过）：

1. A 已进服后注入 → B 连 `127.0.0.1:13337` → B 进入 A 当前区块，位置一致、无持续拉回；
2. A 在服内做一次移动/挖方块，B 侧应同步；
3. A 在窗口前台松开再按 DEL → B 断开、端口关闭；再点注入按钮 → 恢复；
4. B 断开后重新加入；
5. 断连/超时路径：注入后 120 秒内没等到端口 → UI 报「端口未就绪」而不是假成功。

---

## 八、注入后游戏画面卡住 —— 定位与防护（2026-11-19）

### 8.1 现象与证据

用户反馈「注入后游戏画面卡住」。payload 自己的日志
（`%TEMP%\WtfNel\logs\proxy-20261119.log`）给出直接证据：

```
BServer: bound 0.0.0.0:13337 (all interfaces, LAN-wide)
ProxyInitWorker: InstallBServer OK — listening on 127.0.0.1:13337
mid-session: refs missing, cannot capture live connection
BServer: defined A main-thread gate as comet/orbit/DeepRavenCrimson
[MAIN-GATE] task queued on A Render thread
ProxyInitWorker: Queued A Render thread blocking gate task
[MAIN-GATE] blocking A Render thread until B reaches PLAY     ← 就卡在这里
```

### 8.2 根因（payload 内两段源码）

`WtfNel/native/proxy/b_server.cpp`：

```cpp
void JNICALL Native_MainThreadGate_run(JNIEnv*, jobject) {
    if (BServer_WaitForBConnected(0)) return;          // B 已在，直接返回
    LogTo("[MAIN-GATE] blocking A Render thread until B reaches PLAY");
    BServer_WaitForBConnected(-1);                     // 无限期 wait
    LogTo("[MAIN-GATE] B reached PLAY; resuming A Render thread");
}
```

- `BServer_WaitForBConnected(-1)` 等的是 `g_bConnCv` + 谓词 `g_bConnected`，
  **没有超时、也没有 stop 标志**；`g_bConnected` 只在 B 连上时被置 true（`notify_all`）。
- 所以一旦进入该等待：**按 DEL 也解除不了**（`deletePressed` 只是请求停服务，
  不会唤醒这个条件变量），唯一出路是 B 连上 `13337` 并到达 PLAY，或结束游戏进程。
- 走这条路径的前提是 `BServer_TryCaptureLiveConnection()` 返回 false。日志里是
  `mid-session: refs missing`，即 payload 缓存的部分 JNI 引用（`minecraftCls` /
  `mcGetConnectionMid` / `cplGetConnectionMid` / `connectionChannelFid` / `channelPipelineMid`）为空。
  在 A 尚未进入服务器世界时，这是必然结果；在 Forge 47.4.29 下也可能因映射缺口触发。

### 8.3 已落地的四层防护

| 层 | 位置 | 行为 |
| --- | --- | --- |
| ① 入服硬检查（注入前） | `BjdConnectionProbe.IsLikelyInGame(pid)` | 读内核 TCP 连接表（`GetExtendedTcpTable`），要求游戏进程存在 ESTABLISHED 且远端端口 ∈ {25565, 代理监听端口, 13337}。不满足则**拒绝注入**并说明原因，从根上避免卡死 |
| ② 强制注入逃生口 | UI 复选「强制注入（我已进入游戏世界）」→ `StartAsync(forceInject: true)` | 检测不到但用户确认在游戏内时可用；日志会记 `强制注入` |
| ③ 注入后即时识别 | `BjdProxyService.DetectRenderGate()` 读 payload 日志 | 3 秒内匹配 `[MAIN-GATE] blocking A Render thread` → 置 `IsGameFrozen=true`，返回**带恢复步骤的警告**（红字 Toast + 状态行），并明确「DEL 无效、让 B 连 127.0.0.1:端口」 |
| ④ 健康路径确认 | 同上，匹配 `mid-session: captured A's live connection` | 正常共享时提示「未阻塞游戏」 |

另外 `DetectRenderGate` 只看本次注入起点之后的日志（`BjdPayloadLog.MarkInjectionStart`），
不会把上一次注入的历史 `MAIN-GATE` 行误判成本次。

### 8.4 卡住之后的恢复步骤（给用户）

1. **首选**：再启动一个 1.20.1 客户端（B），在游戏内「添加服务器」填 `127.0.0.1:<prog port>` 并进入；
   payload 在 B 到达 PLAY 时会唤醒 A 的 Render 线程，A 自动恢复。
2. 若无法做到：只能结束 A 的游戏进程（画面卡住时窗口通常已无响应），重新启动并**先进服务器再注入**。
3. 不要指望按 DEL：该等待没有 stop 唤醒，DEL 只停代理服务，不解除主线程挂起。

---

## 九、日志接入与一键导出（2026-11-19）

### 9.0 区块常驻与按钮语义（2026-11-19 二次调整）

用户反馈「这块常驻显示」「强制注入和重新注入好像没反应」。调整如下：

| 项 | 调整 |
| --- | --- |
| 区块可见性 | **常驻显示**（不再随服务器预设切换隐藏）。若当前选中的预设不是布吉岛，区块内会注明「注入目标固定为内置布吉岛预设，与选中项无关」 |
| 注入目标 | `BjdTargetPreset()`：选中布吉岛预设就用它，否则回退到内置的布吉岛预设（`BjdGate.ResolvePreset()`） |
| 主按钮（运行中） | 文案变「复制 B 连接地址（已在运行）」，点击**复制 `127.0.0.1:<端口>` 到剪贴板**并 Toast。此前点它在服务层直接 early-return，看起来像没反应 |
| 副按钮 | 新增「重新注入（唤醒 / 恢复）」→ `StartAsync(..., reInject: true)`，**真的再注入一次**（payload 用命名事件识别重复加载并请求恢复），适用于 DEL 停用后唤醒或重挂 Hook |
| 操作反馈 | 状态行常显「上次操作：HH:mm:ss 结果」，即使在 Toast 被窗口挡住时也能看到结果 |

### 9.0.1 payload 逐包刷屏的过滤（重要）

payload 的 `relay_handler.cpp:130` 对**每一个** C2S 包无条件执行
`LogTo("[C2S PASS] <类名>")`（S2C/VIEW 也有类似行），这是 payload 内部硬编码、**只能重编才能关**。
实测 10 分钟刷出 10968 行 / 1.1MB，镜像全量转发会把本程序日志撑到 2.2MB 并拖慢界面。

`BjdPayloadLog` 现在在镜像侧过滤：

- 跳过 `[C2S `、`[S2C `、`[VIEW] S2C` 前缀的逐包行；
- 每 5 秒最多报一句汇总：`已过滤 N 行逐包日志（累计 M 行）。原始逐包日志仍在 payload 日志文件里。`
- 单轮最多转发 200 行，防止突发刷屏打爆日志；
- `FilteredPacketLines` 暴露累计过滤数，导出时会带上。

因此：**本程序日志只保留 payload 的关键事件**（注入、挂载、MAIN-GATE、mid-session、异常），
逐包明细只在 payload 原始日志里。

### 9.1 payload 日志接入

payload 是注入到游戏进程里的原生 DLL，日志写在：

- `%TEMP%\WtfNel\logs\proxy-yyyyMMdd.log`（默认）
- 或环境变量 `WTFNEL_LOG_DIR` 指定目录（payload 读它所在进程的环境，通常由启动器传入）

接入方式（`BjdPayloadLog`）：

- **镜像**：后台线程每 700ms 增量读取该文件（`FileShare.ReadWrite | Delete`，绝不锁它），
  逐行写入本程序日志，前缀 `[布吉岛代理/payload]`。于是调试模式、日志页、一键导出都能看到。
- **起点对齐**：注入前 `MarkInjectionStart()` 记录文件末尾，镜像/标记匹配只看本次注入产生的新行。
- **开关**：跟随「设置 → 底部 调试模式」。开 = 启动镜像；关 = 停止（且不产出任何文件）。
- **手动入口**：
  - 「代理启动」页布吉岛区块的「查看 payload 日志」按钮；
  - 「设置」页底部调试模式旁的「打开 payload 日志目录 / 复制 payload 日志路径」。

### 9.2 一键导出问题日志（`LogExporter`）修复点

| 问题 | 修复 |
| --- | --- |
| 当前正在写的日志可能没落盘 | 导出前 `LogHelper.FlushAndClose()` |
| 日志文件被占用导致 `CreateEntryFromFile` 抛异常 → 整体导出失败 | 改为 `FileShare.ReadWrite \| Delete` 打开 + 流复制；单文件失败只写一条「读取失败」说明条目，不中断整体 |
| payload 日志完全没进包（卡死问题最关键的证据缺失） | 新增 zip 内 `布吉岛代理payload日志/` 子目录，收最近 3 天 |
| 同名 zip 同秒重复导出会失败 | 自动加 `_2`/`_3`… 或随机后缀 |
| 导出中途失败会留下坏包 | `catch` 里删除半成品再抛出 |
| 环境信息太薄，无法定位 | 增加：进程 pid/程序目录、调试模式、布吉岛代理运行态（`DescribeStatus`、是否挂起、GameId）、payload 路径+大小+SHA256、payload 日志路径与末尾 40 行 |

导出内容结构：

```
MhyUnBox_日志_yyyyMMdd_HHmmss.zip
├─ MhyUnBox-20261119.log            （程序日志，含 [布吉岛代理] 与 [布吉岛代理/payload] 行）
├─ RiskAudit-*.log / MemoryDiag.log （如果有）
├─ 布吉岛代理payload日志/proxy-20261119.log
└─ 环境信息.txt                      （版本、系统、代理状态、payload 校验值、payload 日志尾部）
```

## 十、注入成功但没有实际效果 —— 根因分析（2026-11-19）

### 10.1 两个独立结论（都有日志证据）

**A. B 从未连接过 13337，所以本来就不会有任何共享效果。**
payload 日志里没有任何 B 侧事件（`BServer: B channelActive` / `B reached PLAY` 均 0 次），
`13337` 只监听、无会话。A/B 共享代理的本质是「B 连上后才把 A 的会话镜像给 B」；
**只注入、不连 B，等于什么都没发生**。

**B. 注入之后 A 再进服会崩：Forge 网络过滤器重复注册 → 反作弊验证超时。**
客户端 `logs/latest.log` 反复出现：

```
java.lang.IllegalArgumentException: Duplicate handler name: forge:vanilla_filter
    at DefaultChannelPipeline.checkDuplicateName
    at DefaultChannelPipeline.addBefore
    at net.minecraftforge.network.filters.NetworkFilters.injectIfNecessary(NetworkFilters.java:39)
    at net.minecraftforge.network.NetworkHooks.sendMCRegistryPackets(NetworkHooks.java:115)
    at net.minecraft.client.multiplayer.ClientPacketListener.m_5998_(ClientPacketListener.java:429)  ← handleLogin
    at ClientboundLoginPacket.m_5797_
```

`sendMCRegistryPackets` 抛异常 ⇒ 注册表同步包没发全 ⇒ 服务端校验失败。
最终服务端消息：

```
[CHAT] 反作弊验证超时，客户端异常，请重启客户端后重新进入。
```

### 10.2 触发链

1. payload 通过 JVMTI 把 `net.minecraft.network.Connection#channelActive` 打成补丁，
   在**每一条**新建连接上 `addBefore("packet_handler", "proxy_relay_N", relay)`（`trampolines.cpp:40`、`relay_handler.cpp:284`）。
2. payload 日志确认 A 在注入后**重新建连了 2 次**（08:44:08、08:47:17，`Native_onChannelActive fired`）。
3. 该客户端是 **Forge 47.4.29 + ViaForge（1.8 协议翻译）+ 该服反作弊**；
   进服/换维度时会产生多次 `ClientboundLoginPacket` 处理，
   Forge 的 `NetworkFilters.injectIfNecessary` 会在同一条 pipeline 上第二次添加
   `forge:vanilla_filter` → 直接抛 `IllegalArgumentException`。
4. 异常中断了 `sendMCRegistryPackets`，服务端收不到注册表/反作弊校验数据 → 超时踢人。

也就是说：**payload 只在「注入时已在游戏内、且之后不再换服/重连」这一小段窗口里是安全的**；
一旦 A 重连，它挂上的 Relay 就让 Forge 的进服流程失效。

### 10.3 我们这一侧踩的坑（已修复）

**重复注入**：08:47:21 payload 日志出现第二次 `=== DllMain PROCESS_ATTACH ===`，
随后又完整执行了一遍 `InstallHookBridge` / `InstallRelayHandler` / `InstallConnectionHook` /
`InstallBServer`。那是上一版「重新注入（唤醒/恢复）」按钮干的 —— payload 的重复加载
**不是幂等的**，会把 Hook 与 Relay 再叠一层，进一步加剧上面的问题。

### 10.4 本次强化（已落地）

| 项 | 说明 |
| --- | --- |
| **禁止同进程重复注入** | `StartAsync` 增加 `_injectedPid` 去重；`reInject` 一律拒绝，并提示「重启客户端 → 先进服务器 → 再注入」。实测依据写在拒绝消息里 |
| **重新注入按钮禁用** | UI 上固定 `IsEnabled=false`，Tooltip 说明为什么（会重复挂 Relay、破坏进服）；不再提供误操作的入口 |
| **B 接入状态常显** | 状态行每 2 秒刷新：`B：未接入 —— 在第二个客户端连上 127.0.0.1:<端口> 之前，这里不会产生任何共享效果`；接入后显示 `B：已接入` |
| **A 重连告警** | `CapturedLiveSession && AConnectionCount > 0` 时，状态行直接提示「A 已重新建连 N 次…可能触发 Forge 重复注册，建议重启客户端后按流程重来」 |
| **状态行三行结构** | 运行态 / B 状态 / 上次操作，避免「点了没反应 / 看起来在跑其实没用」的歧义 |

新增只读接口：`BjdProxyService.BConnected`、`AConnectionCount`、`CapturedLiveSession`；
`BjdPayloadLog.CountSinceBaseline(marker)` 用于从 payload 日志统计运行期事实。

### 10.5 若要让这条路线真正可用，必须重编 payload（本机无工具链，未做）

按优先级：

1. **Relay 改为只读观察者**：把 `addBefore("packet_handler", relay)` 改成
   `addAfter("packet_handler", relay)`，只旁路观察、不改原派发链 —— 不影响 Forge 的 filter 注入。
2. **登录包去重**：relay 里对 `ClientboundLoginPacket` 只放行一次（同一条连接），
   避免 `NetworkFilters.injectIfNecessary` 二次执行。
3. **改用 Forge Coremod / Mixin**：在 Forge 的启动变换阶段挂接，而不是 JVMTI 字节码补丁，
   避开与 `NetworkFilters` 的冲突。
4. 若能拿到该服的 1.8 协议细节（ViaForge 场景），还要确认 `ServerboundCustomPayloadPacket`
   的反作弊回包不被 `shouldAllowC2S` 丢弃。

### 10.6 更稳的替代路线（推荐先评估）

MhyunBox 现有的**通用代理（认证 MITM）**在布吉岛是可用的（日志里布吉岛角色查询一直正常），
代价是**每个客户端需要一个账号**，但不需要注入、不受 Forge/反作弊影响。
如果目标是「多开同一账号」，通用代理做不到；那就只能走「重编 payload」或「换实现方案」。

---


## 十一、payload 已本地重编（2026-11-19）：Forge/Via 兼容补丁

### 11.1 为什么重编

上一轮（第十节）确认了两个硬伤：A 重连后 Forge 重复注册 `forge:vanilla_filter` 导致反作弊超时，
以及 payload 重复加载不幂等。前者在 payload 侧才能修，所以这次直接改源码重编。

本机没有 Visual Studio / CMake，改用 winget 的 **llvm-mingw clang 23.1.4** 直接产出 DLL，
构建脚本：`WtfNel/native/build_clang.ps1`（可复用，见其注释里的三个坑）。

### 11.2 打了什么补丁

**`relay_handler.cpp` / `.h`：摘掉遗留的 Forge 滤器**

在 `Native_RelayChannelRead` 收到 `ClientboundLoginPacket` 时，先调用新增的
`RelayHandler_RemovePipelineHandler(env, ctx, "forge:vanilla_filter")`，再放行给 `packet_handler`。

依据（Forge 1.20.1 源码，`NetworkFilters.java`）：

```java
public static void injectIfNecessary(Connection manager) {
    ChannelPipeline pipeline = manager.channel().pipeline();
    if (pipeline.get("packet_handler") == null) return;
    instances.forEach((key, filterFactory) -> {
        VanillaPacketFilter filter = filterFactory.apply(manager);
        if (filter.isNecessary(manager))          // ← 只看连接类型，不查同名 handler
            pipeline.addBefore("packet_handler", key, filter);
    });
}
```

`isNecessary` 只在连接被判为 `ConnectionType.VANILLA` 时为真 —— 1.8 服务器经 ViaForge 翻译后正是 VANILLA。
Via 把 1.8 的 `JoinGame`（换世界也用它）翻译成 `ClientboundLoginPacket`，于是同一条连接上
`handleLogin` 跑第二次 → `sendMCRegistryPackets` → `addBefore` 撞名 → 崩。

摘掉旧滤器后 Forge 会干净地重新添加，行为等价于首次进服；正常游戏阶段（非登录包）完全不介入。

### 11.3 注入器健壮性修复（我们自己的代码）

`Native/ReflectivePeLoader.cs` 原来按 `SizeOfImage` 分配远程内存，然后写入**整个文件**。
MinGW 产物带调试段时文件 > SizeOfImage（910KB vs 748KB），导致 `WriteProcessMemory` 失败。
已改为 `max(SizeOfImage, 文件长度)`，并对齐了构建脚本里的 strip 步骤（最终 482,304 < 503,808）。

### 11.4 当前产物

| 项 | 值 |
| --- | --- |
| 文件 | `WtfNel/native/prebuilt/WtfNel.Payload.Proxy.dll` |
| 大小 | **482,304 字节**（上游 MSVC 原版 310,784） |
| SHA256 | `bfdd2b374fb3758bb87f38f916e28885d2c7cc8922f90a8757fc48d936861746` |
| 原版备份 | `WtfNel.Payload.Proxy.msvc-orig.dll.bak`（未覆盖，可回滚） |
| 导入 | 仅 `KERNEL32`、`USER32` 与 `api-ms-win-crt-*`（系统 API set），无 libc++/libgcc |
| 导出 | `ReflectiveLoader`（x64） |

MhyunBox 构建时由 csproj 自动拷到 `bin\...\payloads\`，哈希一致已核对。

### 11.5 验证（新增 `tools/payload_smoketest`）

```powershell
cd MhyunBox-8.0.0_src
dotnet run --project tools\payload_smoketest -c Release
# PASS: 反射注入成功，payload 已执行到自己的启动代码
```

做法：起一个无 JVM 的 `cmd.exe`，用 MhyunBox 自带托管注入栈对它做**真正的反射注入**，
然后检查 payload 是否写出了 `=== DllMain PROCESS_ATTACH ===` / `BootstrapThread start` 日志。
这一步同时验证了：PE 解析、远程内存分配/写入、CreateRemoteThread、以及 mingw 产物带 `.tls` 段时
反射加载后 DllMain 能正常执行（实测可执行；若将来换编译参数导致 TLS 相关崩溃，这个测试会立刻失败）。

> 注意：该测试不会启动 Minecraft，也不验证 A/B 共享链路本身；后者仍需实机。

### 11.6 仍需实机确认

1. A 先入服 → 注入 → **B 立刻连 `127.0.0.1:13337`**，看是否进入 A 的区块、位置一致；
2. 注入后**换世界/重进服务器**，确认不再出现
   `Duplicate handler name: forge:vanilla_filter`，也不再被「反作弊验证超时」踢；
3. 若仍失败，用「查看 payload 日志」/「一键导出问题日志」把新日志发回来（导出已含 payload 日志）。
### 11.7 外部调研交叉验证（2026-11-19）

本次联网核对了 Forge 源码与同类项目，结论与我们改法一致：

1. **Forge 1.18.x → 1.21.1 的 `NetworkFilters.injectIfNecessary` 全部没有判重**，
   所以「换 Forge 版本」不是出路；
   <https://github.com/MinecraftForge/MinecraftForge/blob/1.20.1/src/main/java/net/minecraftforge/network/filters/NetworkFilters.java>
   调用点只有 `NetworkHooks.sendMCRegistryPackets`（L114-115），而它被 Forge 的 patch 插在
   `ClientPacketListener.handleLogin` 里（对应崩溃栈 `ClientPacketListener.java:429`）。
2. **上游后来怎么修**：NeoForge 1.20.4 起在 `injectIfNecessary` 开头调用 `cleanIfNecessary()`，
   先 remove 已有的 `DynamicChannelHandler` 再 add（幂等注入）。
   <https://github.com/neoforged/NeoForge/blob/1.20.4/src/main/java/net/neoforged/neoforge/network/filters/NetworkFilters.java>
   → 我们的「登录包前先摘掉 `forge:vanilla_filter`」正是同一思路，只是发生在 FML filter 之前。
3. **社区同类绕法**：Forge-Client-Reset-Packet 的 PR #12「Clean network handlers」
   在客户端 reset 时显式 remove `forge:forge_fixes` / `forge:vanilla_filter`（catch `NoSuchElementException`）——
   与我们 `RelayHandler_RemovePipelineHandler` 的实现方式完全一致。
   <https://github.com/Just-Chaldea/Forge-Client-Reset-Packet/pull/12>
4. **触发条件收敛**：唯一必要条件是「同一条 Connection 上收到第二个 `ClientboundLoginPacket`」
   （`handleLogin` 第二次）。注意 1.8 的原生换维度用的是 `Respawn` 而不是 `JoinGame`，
   所以本服的二次 Login 更可能来自服务端代理/转服链路重发 JoinGame（Via 把 1.8 `JoinGame` 映射为 `LOGIN`），
   或是任何对登录快照的重放——我们的修法对这两类都成立。
5. **上游未记录此问题**：SakuraTools 与 WtfNel 的 issue/commit 里都没有 NetworkFilters / 重连破坏 Forge 的条目，
   说明这不是它们的目标场景（WtfNel 面向网易/布吉岛、非 Forge 1.20.1 环境）。
---

## 十二、B 客户端连不上 A —— 根因与修复（2026-11-19 第五轮）

### 12.1 现象（payload 日志 + B 客户端日志对照）

payload 日志里 B 侧流程：
```
BServer: B channelActive
BServer: RX net.minecraft.network.protocol.handshake.ClientIntentionPacket (state=0)
BServer: intention → LOGIN (B protocol=766)
（此后没有任何 B 侧日志；120 秒后）
BServer: B channelInactive
```
B 客户端日志（`logs/latest.log`）：
```
java.lang.NullPointerException: ... VFNetworkManager.viaForge$getTrackedVersion() is null
  at de.florianmichael.viaforge.common.ViaForgeCommon.inject(ViaForgeCommon.java:95)
  at net.minecraft.network.Connection$1...initChannel
```
且 `intention → LOGIN (B protocol=766)` 里的 **766 = 1.20.5/1.20.6**，而 A/B 实际都是 1.20.1（763）。

### 12.2 根因链

1. payload 的 `BSide_OnPacket` 只处理 `PingRequest → Pong`，**从未回应 server list 的 StatusRequest**
   （代码里解析了 `ServerStatus` 的构造引用却没用）。
2. B 客户端装了 **ViaForge**，它靠 ping 拿服务端版本来决定翻译策略；拿不到 →
   `getTrackedVersion()` 为 null → `initChannel` 里 NPE（列表 ping 直接失败）。
3. 真实连接时 ViaForge 退化成"按最新协议翻译"，把 B 的登录包按 1.20.6 编码发出
   （所以 payload 读到 protocol=766），payload 用 1.20.1 的 `ServerboundHelloPacket` 解不出来
   → 之后再无 `RX` 日志 → B 客户端 120 秒读超时。

### 12.3 修复 A：payload 补 server list 状态响应

`b_server.cpp` 新增 `buildStatusResponse()`：
- `ServerStatus(Component.literal("BJD A/B Proxy"), Optional.empty(), Optional.of(Version("1.20.1", 763)), Optional.empty(), false)`
- 包进 `ClientboundStatusResponsePacket`，在 `BSide_OnPacket` 收到 `ServerboundStatusRequestPacket` 时回写；
  构造一次后缓存（global ref）复用。
- 于是 ViaForge 看到「服务端 1.20.1 = 客户端 1.20.1」→ **不做翻译**，B 的 Hello 就是原生 1.20.1。

签名依据（用 javap 直接查客户端 srg.jar 得到，不是猜的）：
`ServerStatus(Component, Optional, Optional, Optional, Z)`、
`ServerStatus$Version(String, int)`、`Component.m_237113_(String)`、`Optional.empty()/of(T)`。

### 12.4 修复 B：注入策略改为 ManualMap 优先

加上这段状态响应代码后，**反射注入在高优化档下静默失败**：`-O0` 能进 DllMain，`-O1/-O2/-Oz`
全部远程线程退出码 `0x80000003` 且 payload 一个字都不写。改用 ManualMap 后正常。
根因是 MinGW 产物带 `.tls` 段而 `ReflectiveLoader.c` 不初始化 TLS，属于跟代码布局相关的潜伏问题。

因此：
- `BjdInjector` 的 `Auto` 策略改为 **x64 一律先走 ManualMap**（原为反射优先），
  非 x64 才退回反射；`FallbackToLoadLibrary` 保留。
- `BjdProxyService` 在注入返回成功后**再验一次 payload 自己的 `[raw] DllMain entry` 日志**
  （RawWriteFile 直写、无缓冲）；3 秒内没写出来 → 自动改用 LoadLibrary 重试一次。

### 12.5 当前产物

| 项 | 值 |
| --- | --- |
| payload | 483,840 字节 / SHA256 `cc888bdd4c5d6d9c8e91658cda5a2263c6d33e5ee31ef32cc7d1df90fa2a7b0c` |
| 大小约束 | 483,840 < SizeOfImage 512,000（反射路径也不会越界） |
| 编译守卫 | 状态响应代码可用 `-DBJD_NO_STATUS` 关闭（二分定位用） |

### 12.6 验证

| 项 | 结果 |
| --- | --- |
| `dotnet build -c Release` | 0 错误 |
| `tools/payload_smoketest`（默认 Auto） | **PASS**：策略 ManualMap，`DllMain entry` 计数递增 |
| `tools/bjdgate_test` | 31 条断言全 PASS |

### 12.7 实机验证步骤（需要重启游戏）

> 旧 payload 已经在当前 A 客户端进程里，且 13337 仍被它占用；
> MhyunBox 会走"已在运行"分支而不会重新注入。

1. **关闭 A 客户端**（旧 payload 一起消失，13337 释放），重新启动并进入布吉岛服务器世界；
2. 启动新版 MhyunBox → 选布吉岛预设 → 注入（A 已在服，走 mid-session，不阻塞）；
3. B 客户端在多人游戏里刷新 `127.0.0.1:13337`：现在应能显示「1.20.1」且不再 NPE；
4. 进服；若仍有问题，导出问题日志（已含 payload 日志）。
### 12.8 补丁修正：Forge 给 ServerStatus 加了第 6 个字段（2026-11-19 第六轮）

第一次补 StatusResponse 后，实机日志是：
```
BServer: RX ...ServerboundStatusRequestPacket
BServer: status request received, but StatusResponse refs missing
BServer: status refs: status=0 version=1 component=1 optional=0
```
`status=0` 说明 `ServerStatus` 构造器没匹配上。查 Forge 1.20.1 的补丁
`patches/minecraft/net/minecraft/network/protocol/status/ServerStatus.java.patch` 后确认：

```diff
-public record ServerStatus(Component, Optional<Players>, Optional<Version>, Optional<Favicon>, boolean)
+public record ServerStatus(Component, Optional<Players>, Optional<Version>, Optional<Favicon>, boolean,
+                           Optional<net.minecraftforge.network.ServerStatusPing> forgeData)
```

Forge 追加了第 6 个 record 字段 `Optional<ServerStatusPing> forgeData`，所以原版 5 参描述符
`(...Z)V` 在 Forge 运行时根本不存在。已改为：

- 先找 6 参构造器 `(...ZLjava/util/Optional;)V`，命中则置 `serverStatusHasForgeData = true`；
- 找不到再回退原版 5 参（非 Forge 环境）；
- 构造时按标志传 6 个参数（最后一个 `Optional.empty()`）。

> `optional=0` 那行只是初始化阶段的打印顺序问题（`Optional` 的引用在同一个初始化函数的更后面才解析），
> 真正用到时已经就绪，不是故障。

修正后产物：484,352 字节 / SHA256 `f0c548715462438ea2afdf0ceffb35c56a0dba5553f3e769c69ad0a0f469bc4a`。
再启动后 payload 日志里应出现 `status refs: status=1 ...` 与
`BServer: replied StatusResponse (version=1.20.1, protocol=763)`。
### 12.9 第六轮实测日志与最终修正：惰性解析引用

这次实机日志给出了两条决定性证据：

**1) B 的 Hello 确实到了，但编码是 766（A 侧解码器报错，写在 A 的客户端日志里）：**
```
io.netty.handler.codec.DecoderException: java.io.IOException:
  Packet 2/0 (ServerboundHelloPacket) was larger than I expected, found 15 bytes extra whilst reading packet 0
    at net.minecraft.network.PacketDecoder.decode(PacketDecoder.java:47)
```
→ ViaForge 把 B 的登录包按 1.20.6 编码了，A 侧 1.20.1 的 decoder 解不出来。所以 StatusResponse 必须做对。

**2) StatusResponse 的引用解析在注入时是"看运气"的：**
```
12:42:44 BServer: status refs: status=0 version=1 component=1 optional=0
12:56:46 BServer: status refs: status=0 version=0 component=1 optional=0   ← version 又变 0
```
`ServerStatus$Version` 等类在注入那一刻可能还没被 MC 加载，`loadOrFind`/JVMTI 查找会随机失败。

**修正：把引用解析移到"收到 status 请求"时惰性执行**（`resolveStatusRefsLazy`）：

- 用注入时缓存下来的 MC `ClassLoader`（`g_bs.mcLoader`，global ref）加载四个类；
- 用 **JNI `GetMethodID` + 精确描述符**解析（先 Forge 6 参，失败清异常再试原版 5 参；Component.literal 试 `m_237113_` 再试 `m_130674_`）；
- `Optional.empty/of` 用 `FindClass` 拿；
- 每次解析打一行 `[STATUS_V2] lazy resolve: respCls=… respCtor=… ssCls=… ssCtor=… forge=… literal=… opt=…`，
  下次出问题一眼就能定位缺哪个。

当前产物：485,888 字节 / SHA256 `42e2d9b56f0d69692b1af0e874b833fb6115820e6501c86060f06064ab192df5`。

### 12.10 测试顺序（A、B 都要重启）

1. **关掉 A 和 B** 两个客户端（B 也要重开：ViaForge 会缓存服务器版本探测结果）；
2. 开 A → 进布吉岛服务器世界；
3. 开新版 MhyunBox → 注入。日志确认：`payload 已启动：[raw] DllMain entry` + `mid-session 捕获成功`；
4. 开 B → 多人游戏刷新 `127.0.0.1:13337`：应显示 **1.20.1**；
   若仍显示"无法连接/未知版本"，看 payload 日志里的 `[STATUS_V2] lazy resolve:` 行即可定位；
5. 进服。这次 payload 日志应出现 `RX ...ServerboundHelloPacket`（而不是停在 `intention → LOGIN`）。