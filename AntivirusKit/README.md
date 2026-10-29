# MhyunBox 查杀毒工具箱（AntivirusKit）

把 MhyunBox「工具」页用到的**全部查杀毒脚本、规则库、杀软/开源工具地址**打包成一个独立目录，
可脱离 App 单独使用，也可随 App 一起分发。

## 目录结构
```
AntivirusKit/
├── 查杀毒工具箱.ps1      ← 总入口（合并调度，推荐从这里开始）
├── 扩展脚本模板.ps1      ← 后续新增脚本的规范模板（统一接口）
├── 一致性自检.ps1        ← 校验与 App(SilverFoxTool.cs) 引用是否对齐
├── 杀软地址清单.md       ← 全部杀软/在线查杀/沙箱/开源工具地址
├── 威胁清理.ps1          ← 银狐等木马综合清理（扫描+清理）
├── 杀毒扫描.ps1          ← 主力杀毒扫描（Defender + 自研特征）
├── 威胁进化扫描.ps1      ← 下一代威胁：LOLBins/进程伪装/WMI/BYOVD
├── 命令守卫.ps1          ← 异常命令实时监控 + 置顶弹窗杀木马
├── 应急响应.ps1          ← 病毒泛滥/被入侵一键止血与强杀
├── 断网防护.ps1          ← 默认阻断全部出站 + 白名单放行
├── 系统急救箱.ps1        ← 系统级一键修复急救
├── guard.ps1             ← 蜜罐诱饵守护（布防计划任务调用）
├── 布防守护.vbs          ← 守护计划任务壳
├── 特征库.txt / 规则库.txt / 断网白名单.txt   ← 数据/规则
└── README.md
```

## 快速使用
- **图形界面（App）**：打开 MhyunBox → 工具页，按按钮即可（脚本在 `KillTools/` 下被 App 调用）。
- **命令行（独立使用）**：用管理员 PowerShell 运行总入口：
  ```powershell
  powershell -NoProfile -ExecutionPolicy Bypass -File 查杀毒工具箱.ps1 -Action emergency-stop
  ```
  常用 Action：`scan`（扫描并自动清理）、`kill`（仅清理）、`evo-scan`（下一代威胁扫描）、
  `guard-install`（安装命令守卫）、`emergency-stop`（断网+强杀+冻结自启动）、
  `emergency-standard` / `emergency-deep`（标准/深度应急）、`emergency-restore`（恢复）、
  `netguard-on`（挖矿断网防护）、`rescue`（系统急救箱）、`status`（系统能力档案）。
- **逐个脚本直接用**：例如 `powershell -NoProfile -ExecutionPolicy Bypass -File 应急响应.ps1 -Mode stop -Silent -Force`。

## 设计约定（保持“两边一致”）
- 脚本默认**静默**：只回传关键行到控制台（被 App 捕获进日志面板），不弹窗；
  仅显式传 `-LogFile 路径` 时才落盘日志文件（满足“隐藏不写日志”）。
- 退出码：0=无威胁 / 2=发现威胁 / 1=出错。
- App 侧 `MhyunBox\Services\SilverFoxTool.cs` 通过**固定文件名**引用上述脚本；
  若改文件名或新增脚本，必须**同时**更新 SilverFoxTool.cs 与本文档，运行
  `一致性自检.ps1` 可一键核对两边是否对齐。

## 如何继续追加脚本（扩展）
1. 复制 `扩展脚本模板.ps1`，按模板实现 `-Mode / -Silent / -LogFile` 接口与 `L()` 输出；
2. 把新脚本放进本目录（命名与模板一致）；
3. 在 `查杀毒工具箱.ps1` 的 `$Actions` 表里加一行映射；
4. 如需在 App 内出现按钮，再对应修改 `ToolsPage.cs`（并同步 `SilverFoxTool.cs` 路径）。

## 注意
- `火绒恶性木马专杀工具.exe` 与 `HEU_KMS_Activator_v42.3.0.exe` 为外部程序，需自行放入本目录或 `KillTools/`；
- 应急处置类脚本会改动系统（断网、杀进程、冻结启动项、改防火墙），请在确认感染后或配合断网环境使用。
