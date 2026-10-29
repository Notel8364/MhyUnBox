# 下一代威胁扫描 (MhyunBox)
# 用法: powershell -NoProfile -ExecutionPolicy Bypass -File 威胁进化扫描.ps1 [-AutoRun] [-LogFile 路径]
#   -Mode scan    由 MhyunBox 传入（兼容参数，无实际作用）
#   -AutoRun      程序静默调用模式：不弹 UAC（调用方已提权），仅检测不清理
#   -LogFile 路径 逐行输出同时追加到该日志文件
# 退出码: 0=未发现威胁  2=发现威胁  1=运行出错
# 覆盖方向（对应"下一代病毒木马"）：
#   1) 杀软对抗痕迹（Defender 被关/排除项/IFEO 劫持/DisallowRun/杀软服务被肢解）
#   2) LOLBins 滥用与无文件载荷（powershell 编码、mshta/regsvr32/rundll32/certutil/bitsadmin 远程载荷）
#   3) 进程伪装与异常亲缘（svchost 不在 System32、Office/浏览器派生脚本宿主）
#   4) WMI 持久化订阅与计划任务隐藏载荷
#   5) 启动项与自启动（Run/RunOnce/Winlogon/Startup）
#   6) 服务持久化与随机名服务
#   7) BYOVD 驱动滥用（带漏洞的第三方驱动打穿内核防护，参考 lolDrivers 公开名单）
#   8) 系统 DLL 签名/伪造与启动配置篡改（bcdedit 测试模式/关闭恢复）
#   9) hosts/DNS 劫持与 LSP 网络层持久化

param(
    [string]$Mode = 'scan',
    [switch]$AutoRun,
    [switch]$Silent,
    [string]$LogFile = ''
)

[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$OutputEncoding = [System.Text.Encoding]::UTF8
$ErrorActionPreference = 'Continue'

$script:LogPath = $LogFile
$script:Threats  = New-Object System.Collections.Generic.List[string]

function L([string]$Message) {
    # 进度行：静默模式只写文件，不刷屏
    if ($Silent) {
        if ($script:LogPath) { try { Add-Content -LiteralPath $script:LogPath -Value ((Get-Date -Format 'HH:mm:ss.fff') + '  ' + $Message) -Encoding UTF8 } catch { } }
        return
    }
    $line = (Get-Date -Format 'HH:mm:ss.fff') + '  ' + $Message
    Write-Output $line
    if ($script:LogPath) { try { Add-Content -LiteralPath $script:LogPath -Value $line -Encoding UTF8 } catch { } }
}

function Add-Threat([string]$Item) {
    $script:Threats.Add($Item)
    # 威胁行是结果，静默模式也回传
    $line = (Get-Date -Format 'HH:mm:ss.fff') + '  [威胁] ' + $Item
    Write-Output $line
    if ($script:LogPath) { try { Add-Content -LiteralPath $script:LogPath -Value $line -Encoding UTF8 } catch { } }
}

# 关键行：始终输出并回传（扫描开始/汇总），静默模式也只回传这些
function Lk([string]$Message) {
    $line = (Get-Date -Format 'HH:mm:ss.fff') + '  ' + $Message
    Write-Output $line
    if ($script:LogPath) { try { Add-Content -LiteralPath $script:LogPath -Value $line -Encoding UTF8 } catch { } }
}

function Test-Admin {
    $id = [System.Security.Principal.WindowsIdentity]::GetCurrent()
    $p  = New-Object System.Security.Principal.WindowsPrincipal($id)
    return $p.IsInRole([System.Security.Principal.WindowsBuiltInRole]::Administrator)
}

if (-not (Test-Admin)) {
    if (-not $AutoRun) {
        L '未以管理员运行，正在请求 UAC 提权...'
        $argList = @('-NoProfile','-ExecutionPolicy','Bypass','-File',"`"$PSCommandPath`"")
        if ($LogFile) { $argList += @('-LogFile',"`"$LogFile`"") }
        if ($Silent)  { $argList += '-Silent' }
        try { Start-Process -FilePath 'powershell.exe' -Verb RunAs -ArgumentList $argList | Out-Null; L '已提权重启。'; exit 0 }
        catch { L '【失败】未获得管理员授权（UAC 被拒绝）。'; exit 1 }
    } else {
        L '【警告】-AutoRun 模式下未以管理员运行：部分检查项（驱动/WMI 订阅/bcdedit）可能不完整。'
    }
}

# 系统能力档案：Win10/Win11、家庭版/专业版通用；家庭版无组策略，
# 本脚本只用注册表/WMI/计划任务查询，家庭版全部可用；未装杀软时提示用户
$script:ProfileOS = Get-CimInstance Win32_OperatingSystem -ErrorAction SilentlyContinue
$script:ProfileCaption = "$($script:ProfileOS.Caption)"
$script:ProfileIsHome  = ($script:ProfileCaption -match '(?i)home|家庭') -and ($script:ProfileCaption -notmatch '(?i)pro|专业|enterprise|education|workstation')
$script:ProfileHasAvModule = $null -ne (Get-Command Get-MpPreference -ErrorAction SilentlyContinue)
$script:ProfileThirdPartyAv = @()
try {
    $script:ProfileThirdPartyAv = @(Get-CimInstance -Namespace root\SecurityCenter2 -ClassName AntiVirusProduct -ErrorAction SilentlyContinue |
        Where-Object { $_.displayName -notmatch 'Windows Defender|Windows 安全中心|Microsoft Defender' } |
        ForEach-Object { $_.displayName })
} catch { }
$script:ProfileHasAnyAv = ($script:ProfileHasAvModule -or $script:ProfileThirdPartyAV.Count -gt 0)
$editionDesc = $(if ($script:ProfileIsHome) { '家庭版（无组策略，检查项已按家庭版可用命令适配）' } else { '专业版及以上' })
L ("系统: {0} / {1}" -f $script:ProfileCaption, $editionDesc)
if (-not $script:ProfileHasAnyAv) {
    L '【重要】未检测到任何杀毒软件：本机没有实时防护，扫描发现的威胁不会被杀软件自动处理，请按报告手动清理或尽快安装防护软件。'
}

function Get-PropValue($Obj, [string]$Name) {
    if ($null -eq $Obj) { return $null }
    $p = $Obj.PSObject.Properties[$Name]
    if ($p) { return $p.Value }
    return $null
}

function Is-SystemPath([string]$Path) {
    if (-not $Path) { return $true }
    return ($Path -match '(?i)^' + [regex]::Escape($env:SystemRoot) + '\\(System32|SysWOW64|WinSxS|assembly)\\')
}

Lk '== 下一代威胁扫描开始（仅检测，不清理）=='

# ---------- 0) 防护能力快照 ----------
L '--- 0) 杀软与防护能力快照 ---'
try {
    $defStatus = (Get-Service WinDefend -ErrorAction SilentlyContinue)
    if ($defStatus) { L "Defender 服务状态: $($defStatus.Status)" }
    else { L '未检测到 Defender 服务（可能已卸载或被木马破坏）'; Add-Threat 'Defender 服务不存在或已被卸载' }
    $rt = (Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows Defender\Real-Time Protection' -Name DisableRealtimeMonitoring -ErrorAction SilentlyContinue).DisableRealtimeMonitoring
    if ($rt -eq 1) { Add-Threat 'Defender 实时保护被注册表禁用' }
    $as = (Get-ItemProperty 'HKLM:\SOFTWARE\Policies\Microsoft\Windows Defender' -Name DisableAntiSpyware -ErrorAction SilentlyContinue).DisableAntiSpyware
    if ($as -eq 1) { Add-Threat 'Defender 被组策略禁用（DisableAntiSpyware=1）' }
    try {
        $pref = Get-MpPreference -ErrorAction Stop
        $exPaths = @($pref.ExclusionPath); $exProcs = @($pref.ExclusionProcess); $exExt = @($pref.ExclusionExtension)
        # 排除项本身≠中毒：只有覆盖全盘/过于宽泛（C:\、* 之类）或排除 Defender 自身目录时才算威胁
        $rootRx = '^[A-Za-z]:\\?$|^\\\\|^\*|^\*\.exe$|\\$'
        $susEx = @()
        foreach ($e in ($exPaths + $exProcs + $exExt)) {
            if ($e -and $e -match $rootRx) { $susEx += $e }
        }
        if ($susEx.Count -gt 0) {
            Add-Threat "Defender 存在过于宽泛的扫描排除项（疑似木马所为）: $($susEx -join ', ')"
        } elseif ($exPaths.Count -gt 0 -or $exProcs.Count -gt 0 -or $exExt.Count -gt 0) {
            L "Defender 存在用户添加的扫描排除项（人工确认用途即可，非威胁）: 路径=$($exPaths -join ',')"
        }
    } catch { L '无法读取 Defender 排除项（Defender 模块不可用）' }
} catch { L '防护快照检查异常' }

# ---------- 1) IFEO / DisallowRun 杀软对抗 ----------
L '--- 1) IFEO 劫持与 DisallowRun 检查 ---'
try {
    $ifeoRoot = 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options'
    if (Test-Path -LiteralPath $ifeoRoot) {
        foreach ($sub in (Get-ChildItem -LiteralPath $ifeoRoot -ErrorAction SilentlyContinue)) {
            $n = $sub.PSChildName
            # 只有带 Debugger 重定向（杀软启动即被转走）才是对抗痕迹；纯 MitigationOptions 键是系统正常项
            $dbg = (Get-ItemProperty -LiteralPath $sub.PSPath -Name Debugger -ErrorAction SilentlyContinue).Debugger
            if ($dbg) {
                $dangerous = $dbg -match 'verclsid|gpupdate|cmd(\.exe)?|powershell|rundll32|regsvr32|wscript'
                $avNames2 = @('MsMpEng','MsMpEngCP','NisSrv','WdFilter','360Safe','360Tray','HipsTray','HipsDaemon','ZhuDongFangYu','HRSword','kxetray','kxescore','McAPExe','avp\.exe','Norton')
                $isAv = $false
                foreach ($av in $avNames2) { if ($n -like "$av*") { $isAv = $true } }
                if ($dangerous -or $isAv) {
                    Add-Threat "IFEO Debugger 劫持: $n -> $dbg"
                } else {
                    L "IFEO 存在自定义 Debugger（多数为兼容性设置，已排除误报）: $n -> $dbg"
                }
            }
        }
    }
    $disallow = Get-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Policies\Explorer' -Name DisallowRun -ErrorAction SilentlyContinue
    if ($disallow) {
        $vals = @($disallow.PSObject.Properties | Where-Object { $_.Name -match '^\d+$' } | ForEach-Object { $_.Value })
        if ($vals.Count -gt 0) { Add-Threat "存在 DisallowRun 黑名单（禁止运行的程序）: $($vals -join ', ')" }
    }
    # Policy 支的 WDAC 策略（若被写自定义策略，杀软可能被挡）
    try {
        $ciPol = Get-CIPolicy -ErrorAction SilentlyContinue
        if ($ciPol) { L "当前生效的 WDAC/代码完整性策略数: $(@($ciPol).Count)" }
    } catch { }
} catch { L 'IFEO 检查异常' }

# ---------- 2) LOLBins 滥用（运行中的进程）----------
L '--- 2) LOLBins 滥用与无文件载荷检查（当前进程命令行）---'
try {
    $procs = Get-CimInstance Win32_Process -ErrorAction SilentlyContinue
    $badRx = @(
        @{ Rx='powershell(\.exe)?["'']?\s+.*(-enc|-encodedcommand)\s+[A-Za-z0-9+/=]{20,}'; Why='编码的 PowerShell 载荷' }
        @{ Rx='powershell(\.exe)?["'']?\s+.*(downloadstring|downloaddata|downloadfile|iex\s*\(|invoke-webrequest|iwr\s|invoke-restmethod)'; Why='PowerShell 联网执行' }
        @{ Rx='mshta(\.exe)?["'']?\s+.*(https?://|javascript:|vbscript:)'; Why='mshta 远程脚本' }
        @{ Rx='regsvr32(\.exe)?["'']?\s+.*(scrobj|/i:https?://|\.sct)'; Why='regsvr32 远程载荷' }
        @{ Rx='rundll32(\.exe)?["'']?\s+.*(javascript:|https?://)'; Why='rundll32 远程载荷' }
        @{ Rx='certutil(\.exe)?["'']?\s+.*(-urlcache|-f\s+https?://|-decode)'; Why='certutil 下载/解码' }
        @{ Rx='bitsadmin(\.exe)?["'']?\s+.*(/transfer|/addfile)'; Why='bitsadmin 后台下载' }
        @{ Rx='wmic(\.exe)?["'']?\s+process.*call.*create'; Why='WMI 远程创建进程' }
        @{ Rx='(curl|wget)(\.exe)?["'']?\s+.*\|\s*(powershell|cmd)'; Why='下载即执行' }
        @{ Rx='wscript|cscript.*\.(jse|vbe|vbs|js)\s'; Why='混淆脚本引擎载荷' }
    )
    foreach ($p in $procs) {
        $c = "$($p.CommandLine)"
        if (-not $c) { continue }
        foreach ($b in $badRx) {
            if ($c -match $b.Rx) {
                Add-Threat "$($p.Name) PID=$($p.ProcessId) => $($b.Why) : $($c.Substring(0, [Math]::Min(160, $c.Length)))"
            }
        }
    }
} catch { L 'LOLBins 检查异常' }

# ---------- 3) 进程伪装与异常亲缘 ----------
L '--- 3) 进程伪装与异常亲缘检查 ---'
try {
    $procs = Get-CimInstance Win32_Process -ErrorAction SilentlyContinue
    $sysNames = @('svchost','services','csrss','wininit','winlogon','lsass','smss','spoolsv','taskhostw','sihost','dwm')
    foreach ($p in $procs) {
        $base = ("$($p.Name)" -replace '\.exe$','').ToLower()
        if ($base -in $sysNames) {
            $ppath = "$($p.ExecutablePath)"
            if ($ppath -and (-not (Is-SystemPath $ppath))) {
                Add-Threat "系统进程 $($p.Name) 运行位置异常: $ppath（PID $($p.ProcessId)）"
            }
        }
        # explorer 的非典型子进程（游戏/浏览器除外太多噪音，只报高危宿主）
        if ($base -eq 'explorer') { continue }
        $parent = $procs | Where-Object { $_.ProcessId -eq $p.ParentProcessId } | Select-Object -First 1
        if ($parent) {
            $pb = ("$($parent.Name)" -replace '\.exe$','').ToLower()
            if ($pb -in @('winword','excel','powerpnt','outlook') -and $base -in @('powershell','cmd','mshta','rundll32','regsvr32','wscript','cscript','msbuild','installutil')) {
                Add-Threat "文档进程 $($parent.Name) 派生脚本宿主 $($p.Name)（PID $($p.ProcessId)）: $("$($p.CommandLine)".Substring(0,[Math]::Min(120,"$($p.CommandLine)".Length)))"
            }
        }
    }
} catch { L '进程检查异常' }

# ---------- 4) WMI 持久化订阅 ----------
L '--- 4) WMI 持久化订阅检查 ---'
try {
    $filters = Get-CimInstance -Namespace root\subscription -ClassName __EventFilter -ErrorAction SilentlyContinue
    $bindings  = Get-CimInstance -Namespace root\subscription -ClassName __FilterToConsumerBinding -ErrorAction SilentlyContinue
    foreach ($f in @($filters)) {
        $q = "$($f.Query)"
        if ($q -match '__InstanceCreationEvent|__InstanceModificationEvent' -and $q -match 'Win32_Process|Win32_LogicalDisk|Win32_Volume') {
            Add-Threat "WMI 事件过滤器持久化: $($f.Name) => $($q.Substring(0,[Math]::Min(120,$q.Length)))"
        } else { L "WMI 事件过滤器 $($f.Name)（已排查）" }
    }
    # 分别查询恶意常用的两类消费者（类名直接查，避免 CIM Class 属性读取不稳定）
    foreach ($c in @(Get-CimInstance -Namespace root\subscription -ClassName CommandLineEventConsumer -ErrorAction SilentlyContinue)) {
        $tpl = "$($c.CommandLineTemplate)"
        Add-Threat "WMI 命令行消费者持久化: $($c.Name) => $($tpl.Substring(0,[Math]::Min(150,$tpl.Length)))"
    }
    foreach ($c in @(Get-CimInstance -Namespace root\subscription -ClassName ActiveScriptEventConsumer -ErrorAction SilentlyContinue)) {
        $txt = "$($c.ScriptText)"
        Add-Threat "WMI 脚本消费者持久化: $($c.Name) => $($txt.Substring(0,[Math]::Min(150,$txt.Length)))"
    }
    foreach ($b in @($bindings)) {
        L "WMI 绑定: $($b.Filter) -> $($b.Consumer)"
    }
} catch { L 'WMI 订阅检查异常（可能未安装 WMI 提供程序或无订阅）' }

# ---------- 5) 计划任务隐藏载荷 ----------
L '--- 5) 计划任务隐藏载荷检查 ---'
try {
    $tasks = Get-ScheduledTask -ErrorAction SilentlyContinue
    $susRx = 'powershell.*(-enc|-encodedcommand|downloadstring|iex)|mshta|regsvr32|rundll32.*(javascript|https?://)|certutil.*urlcache|bitsadmin.*transfer|\.hta\b|\.vbs\b|\\temp\\|\\public\\|\\programdata\\|invoke-webrequest'
    foreach ($t in @($tasks)) {
        if ($t.TaskPath -like '\Microsoft\Windows\*') { continue }
        $acts = @($t.Actions | ForEach-Object { "$($_.Execute) $($_.Arguments)" })
        foreach ($a in $acts) {
            if ($a -match $susRx) {
                Add-Threat "计划任务 [$($t.TaskName)] 含可疑指令: $($a.Substring(0,[Math]::Min(150,$a.Length)))"
            }
        }
    }
} catch { L '计划任务检查异常' }

# ---------- 6) 启动项 ----------
L '--- 6) 自启动项检查（Run/RunOnce/Winlogon/Startup）---'
try {
    $runKeys = @(
        'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run',
        'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce',
        'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run',
        'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run',
        'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce'
    )
    $badRx = 'powershell|cmd(\.exe)?\s+/c|mshta|rundll32|regsvr32|wscript|cscript|certutil|bitsadmin|\.bat\b|\.vbs\b|\.hta\b|\\temp\\|\\appdata\\local\\temp\\|\\users\\public\\|\\programdata\\'
    foreach ($k in $runKeys) {
        if (-not (Test-Path -LiteralPath $k)) { continue }
        $item = Get-ItemProperty -LiteralPath $k -ErrorAction SilentlyContinue
        foreach ($prop in $item.PSObject.Properties) {
            if ($prop.Name -match '^PS(Path|ParentPath|ChildName|Drive|Provider)$') { continue }
            $v = "$($prop.Value)"
            if ($v -match $badRx -or $v -match '(?i)\\temp\\.*\.(exe|vbs|js|bat)') {
                Add-Threat "启动项可疑 [$k] $($prop.Name) => $($v.Substring(0,[Math]::Min(150,$v.Length)))"
            }
        }
    }
    foreach ($w in @('HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon','HKCU:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon')) {
        $item = Get-ItemProperty -LiteralPath $w -ErrorAction SilentlyContinue
        if ($item) {
            foreach ($n in @('Shell','Userinit')) {
                $v = "$($item.$n)"
                if ($v -and $v -match 'powershell|\.vbs|\.bat|http|-enc') {
                    Add-Threat "Winlogon $n 被篡改: $v"
                }
            }
        }
    }
    foreach ($su in @("$env:APPDATA\Microsoft\Windows\Start Menu\Programs\Startup", "$env:ProgramData\Microsoft\Windows\Start Menu\Programs\Startup")) {
        if (Test-Path -LiteralPath $su) {
            foreach ($f in (Get-ChildItem -LiteralPath $su -File -ErrorAction SilentlyContinue)) {
                if ($f.Extension -match '(?i)\.(lnk|exe|bat|vbs|js|cmd)$') {
                    if ($f.Extension -ne '.lnk') { Add-Threat "启动文件夹存在可执行/脚本: $($f.FullName)" }
                }
            }
        }
    }
} catch { L '启动项检查异常' }

# ---------- 7) 服务持久化 ----------
L '--- 7) 服务持久化检查 ---'
try {
    $svcs = Get-CimInstance Win32_Service -ErrorAction SilentlyContinue
    $badRx = '\\temp\\|\\appdata\\|\\users\\public\\|\\programdata\\|powershell|cmd(\.exe)?\s+/c|mshta|rundll32|regsvr32|\.bat\b|\.vbs\b|\.hta\b'
    # 这些是系统自身合法放在 ProgramData 的（Defender 4.x 平台目录）
    $trustedPathRx = '(?i)Microsoft\\Windows Defender\\Platform\\|Microsoft\\Windows\\Defender\\|Windows Defender\\Platform\\'
    foreach ($s in $svcs) {
        $pn = "$($s.PathName)"
        if ($pn -match $badRx) {
            if ($pn -match $trustedPathRx) { continue }
            $exe = $null
            if ($pn -match '"([^"]+\.exe)"') { $exe = $matches[1] } elseif ($pn -match '(?i)([a-z]:\\[^\s]+\.exe)') { $exe = $matches[1] }
            $signed = $false
            if ($exe -and (Test-Path -LiteralPath $exe)) {
                $sig = Get-AuthenticodeSignature -LiteralPath $exe -ErrorAction SilentlyContinue
                if ($sig -and $sig.Status -eq 'Valid') { $signed = $true }
            }
            if ($signed) {
                L "服务 [$($s.Name)] 路径在非常见目录但签名有效（已排除误报）: $($pn.Substring(0,[Math]::Min(160,$pn.Length)))"
            } else {
                Add-Threat "服务 [$($s.Name)] 启动路径可疑或未签名: $($pn.Substring(0,[Math]::Min(160,$pn.Length)))"
            }
        }
    }
} catch { L '服务检查异常' }

# ---------- 8) BYOVD 驱动 ----------
L '--- 8) BYOVD 恶意驱动检查 ---'
# 参考公开的易被滥用的驱动名单（lolDrivers 项目常见样本）
$script:ByovdNames = @(
 'IQvw64e.sys','dbutildrv2.sys','RTCore64.sys','gdrv.sys','ene.sys','eneio64.sys','inpoutx64.sys',
 'GLCKIO2.sys','MsIo64.sys','MsIo32.sys','PhlashNT.sys','Viraglt64.sys','atillk64.sys','BS_Def.sys',
 'WinRing0x64.sys','WinFlash64.sys','NeoFabio.sys','AMDPCNDRV.sys','eneTechSrv.sys','AsUpIO64.sys',
 'AsIO.sys','AsUpIO.sys','GVCIDrv.sys','HpPortIox64.sys','LHD64.sys','MEMZ.sys','NTIOLib.sys',
 'NtiIo64.sys','PhlashNT.sys','PlgLdr64.sys','PmpAvr.sys','rtkio.sys','ZAM32.sys'
)
try {
    $drivers = Get-CimInstance Win32_SystemDriver -ErrorAction SilentlyContinue
    foreach ($d in $drivers) {
        $path = "$($d.PathName)"
        $leaf = [System.IO.Path]::GetFileName($path)
        if ($script:ByovdNames -contains $leaf) {
            Add-Threat "存在已知易被滥用的驱动（BYOVD）: $leaf (状态 $($d.State), 服务 $($d.Name))"
        }
        if ($path -and (-not ($path -match '(?i)^' + [regex]::Escape($env:SystemRoot) + '\\System32\\(drivers|DriverStore)\\'))) {
            if ($d.State -eq 'Running') {
                Add-Threat "内核驱动运行位置异常: $($d.Name) => $path"
            }
        }
    }
    # 驱动文件是否在驱动目录里被写入
    $drvDir = Join-Path $env:SystemRoot 'System32\drivers'
    $byovdFiles = Get-ChildItem -LiteralPath $drvDir -File -ErrorAction SilentlyContinue | Where-Object { $script:ByovdNames -contains $_.Name }
    foreach ($bf in $byovdFiles) {
        $sig = Get-AuthenticodeSignature -LiteralPath $bf.FullName -ErrorAction SilentlyContinue
        if (-not ($sig -and $sig.Status -eq 'Valid')) {
            Add-Threat "驱动目录中的 BYOVD 样本签名异常: $($bf.FullName)"
        }
    }
} catch { L '驱动检查异常' }

# ---------- 9) 系统 DLL 签名/伪造 ----------
L '--- 9) 关键系统 DLL 签名检查 ---'
try {
    $sysDir = Join-Path $env:SystemRoot 'System32'
    $keyFiles = @('ntdll.dll','kernel32.dll','kernelbase.dll','user32.dll','ws2_32.dll','sechost.dll','advapi32.dll','lsass.exe','services.exe','svchost.exe')
    foreach ($f in $keyFiles) {
        $fp = Join-Path $sysDir $f
        if (-not (Test-Path -LiteralPath $fp)) { continue }
        $sig = Get-AuthenticodeSignature -LiteralPath $fp -ErrorAction SilentlyContinue
        if (-not $sig -or $sig.Status -ne 'Valid') {
            Add-Threat "关键系统文件签名异常: $fp (状态: $(if ($sig) { $sig.Status } else { '无签名' }))"
        }
    }
} catch { L 'DLL 签名检查异常' }

# ---------- 10) 启动配置（bcdedit）----------
L '--- 10) 启动配置篡改检查（bcdedit）---'
try {
    $bcd = (& bcdedit.exe /enum 2>$null) -join "`n"
    if ($bcd -match '(?i)testsigning\s+Yes')       { Add-Threat '系统启用了测试签名模式（testsigning），可加载未签名驱动' }
    if ($bcd -match '(?i)nointegritychecks\s+Yes') { Add-Threat '系统禁用了完整性检查（nointegritychecks）' }
    if ($bcd -match '(?i)recoveryenabled\s+No')     { Add-Threat '系统恢复被禁用（recoveryenabled No）' }
    if ($bcd -match '(?i)debug\s+Yes')              { L '提示：系统处于内核调试模式（正常开发者环境除外）' }
} catch { L 'bcdedit 检查异常' }

# ---------- 11) hosts / DNS / LSP ----------
L '--- 11) hosts / DNS / 网络层持久化检查 ---'
try {
    $hosts = Join-Path $env:SystemRoot 'System32\drivers\etc\hosts'
    if (Test-Path -LiteralPath $hosts) {
        $avHostRx = '(?i)(360|kaspersky|huorong|eset|sophos|bitdefender|defender|microsoft\.com/defender|virus|safe)'
        foreach ($hl in (Get-Content -LiteralPath $hosts -ErrorAction SilentlyContinue)) {
            $t = $hl.Trim()
            if (-not $t -or $t.StartsWith('#')) { continue }
            if ($t -match '^(0\.0\.0\.0|127\.0\.0\.1)\s') {
                if ($t -match $avHostRx) {
                    Add-Threat "hosts 疑似屏蔽安全厂商域名: $t"
                }
            }
        }
    }
    $dns = @(Get-DnsClientServerAddress -AddressFamily IPv4 -ErrorAction SilentlyContinue | Where-Object { $_.ServerAddresses })
    foreach ($n in $dns) {
        if ("$($n.ServerAddresses)" -match '185\.222|45\.9\.148|185\.235\.84\.|217\.12\.210\.') {
            Add-Threat "疑似恶意 DNS: 网卡[$($n.InterfaceAlias)] => $($n.ServerAddresses -join ',')"
        }
    }
    # Winsock LSP 持久化
    $lspRoot = 'HKLM:\SYSTEM\CurrentControlSet\Services\WinSock2\Parameters\Protocol_Catalog9\Catalog_Entries'
    if (Test-Path -LiteralPath $lspRoot) {
        foreach ($e in (Get-ChildItem -LiteralPath $lspRoot -ErrorAction SilentlyContinue)) {
            $pname = (Get-ItemProperty -LiteralPath $e.PSPath -ErrorAction SilentlyContinue)
            $dll = ''
            try { $dll = (Get-ItemProperty -LiteralPath $e.PSPath -Name PackedCatalogItem -ErrorAction SilentlyContinue).PackedCatalogItem } catch { }
            if ($dll) {
                $str = [System.Text.Encoding]::Unicode.GetString($dll)
                if ($str -match '(?i)[a-z]:\\' -and $str -notmatch '(?i)\\windows\\system32\\') {
                    if ($str -match '(?i)\.dll') {
                        Add-Threat "Winsock LSP 存在非系统目录 DLL: $($str.Substring(0,[Math]::Min(120,$str.Length)))"
                    }
                }
            }
        }
    }
} catch { L 'hosts/DNS/LSP 检查异常' }

L '=== 扫描结束汇总 ==='
if ($script:Threats.Count -gt 0) {
    Lk("共发现 $($script:Threats.Count) 项可疑：")
    foreach ($t in $script:Threats) { Lk(" - $t") }
} else {
    Lk('未发现威胁。')
}
Lk('== 下一代威胁扫描完成 ==')
if ($script:Threats.Count -gt 0) { exit 2 } else { exit 0 }
