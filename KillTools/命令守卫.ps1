# 命令守卫 (MhyunBox) —— 异常运行指令实时监控 + 置顶弹窗 + 自动拦截
# 由 MhyunBox 「恶意木马与威胁清理」卡片调用，也可手工运行：
#   powershell -NoProfile -ExecutionPolicy Bypass -File 命令守卫.ps1 -Mode install
# 模式:
#   monitor    前台/后台守护进程：WMI 进程创建事件 + 15 秒周期扫描双通道监控
#   install    注册开机自启计划任务（隐藏、最高权限）并立即启动监控
#   uninstall  注销计划任务并停止监控进程
#   status     查看计划任务与监控进程状态
# 行为:
#   命中「致命规则」（勒索清卷影/杀软对抗/加密混淆的 powershell/LOLBins 远程载荷/系统进程伪装）
#     -> 击杀进程树 + 隔离样本 + 防火墙阻断出网 + 置顶弹窗告知
#   命中「高危规则」（Office/浏览器派生脚本宿主、随机名载荷、远程计划任务等）
#     -> 置顶弹窗询问：是=击杀并隔离，否=仅记录
# 日志:
#   %LOCALAPPDATA%\MhyunBox\CommandGuard\guard.log          运行日志
#   %LOCALAPPDATA%\MhyunBox\HoneyPots\defense_alerts.log    告警（MhyunBox 布防日志可查看）
# 提权: 交互运行未提权时自动请求 UAC；install/uninstall/monitor 均需管理员权限。

param(
    [string]$Mode = 'monitor',
    [string]$LogFile = '',
    [switch]$Silent
)

[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$OutputEncoding = [System.Text.Encoding]::UTF8
$ErrorActionPreference = 'Continue'

# ---------- 基础路径 ----------
$DirSelf      = $PSScriptRoot
$LogDir       = Join-Path $env:LOCALAPPDATA 'MhyunBox\CommandGuard'
$GuardLog     = Join-Path $LogDir 'guard.log'
$AlertLog     = Join-Path $env:LOCALAPPDATA 'MhyunBox\HoneyPots\defense_alerts.log'
$Quarantine   = Join-Path $env:LOCALAPPDATA 'MhyunBox\Quarantine'
$TaskName     = 'MhyunBoxCommandGuard'
$FwPrefix     = 'MhyunBox_CmdGuard_Block_'

# 兼容 C# 侧传进来的 guard-xxx 前缀
$m = $Mode.ToLower()
if ($m.StartsWith('guard-')) { $m = $m.Substring(6) }
$Mode = $m

$script:LogPath = $LogFile

New-Item -ItemType Directory -Force -Path $LogDir       | Out-Null
New-Item -ItemType Directory -Force -Path (Split-Path $AlertLog -Parent) | Out-Null

$script:Suppressed = @{}   # 内存去重：PID|原因

# 系统能力档案：Win10/Win11、家庭版/专业版通用；家庭版无组策略，
# 本脚本只用注册表/计划任务/防火墙/WMI，家庭版全部可用；未装杀软也不影响守卫自身运行。
function Get-SystemProfile {
    $os = Get-CimInstance Win32_OperatingSystem -ErrorAction SilentlyContinue
    $caption = "$($os.Caption)"
    $isHome  = ($caption -match '(?i)home|家庭') -and ($caption -notmatch '(?i)pro|专业|enterprise|education|workstation')
    $hasAvModule = $null -ne (Get-Command Get-MpPreference -ErrorAction SilentlyContinue)
    $thirdParty = @()
    try {
        $thirdParty = @(Get-CimInstance -Namespace root\SecurityCenter2 -ClassName AntiVirusProduct -ErrorAction SilentlyContinue |
            Where-Object { $_.displayName -notmatch 'Windows Defender|Windows 安全中心|Microsoft Defender' } |
            ForEach-Object { $_.displayName })
    } catch { }
    [pscustomobject]@{
        Caption = $caption; IsHome = $isHome
        HasDefenderModule = $hasAvModule; ThirdPartyAv = $thirdParty
        HasAnyAv = ($hasAvModule -or $thirdParty.Count -gt 0)
    }
}
$script:Profile = Get-SystemProfile

function L([string]$Message) {
    if ($Silent) {
        try { Add-Content -LiteralPath $GuardLog -Value ((Get-Date -Format 'HH:mm:ss.fff') + '  ' + $Message) -Encoding UTF8 } catch { }
        return
    }
    $line = (Get-Date -Format 'HH:mm:ss.fff') + '  ' + $Message
    Write-Output $line
    try { Add-Content -LiteralPath $GuardLog -Value $line -Encoding UTF8 } catch { }
    if ($script:LogPath) { try { Add-Content -LiteralPath $script:LogPath -Value $line -Encoding UTF8 } catch { } }
}

function Write-Alert([string]$Message) {
    $line = (Get-Date -Format 'yyyy-MM-dd HH:mm:ss') + '  [命令守卫] ' + $Message
    try { Add-Content -LiteralPath $AlertLog -Value $line -Encoding UTF8 } catch { }
}

function Test-Admin {
    $id = [System.Security.Principal.WindowsIdentity]::GetCurrent()
    $p  = New-Object System.Security.Principal.WindowsPrincipal($id)
    return $p.IsInRole([System.Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Invoke-Elevate([string]$newMode) {
    L "未以管理员运行，正在请求 UAC 提权..."
    $argList = @('-NoProfile','-ExecutionPolicy','Bypass','-WindowStyle','Hidden','-File',"`"$PSCommandPath`"", '-Mode', $newMode)
    if ($script:LogPath) { $argList += @('-LogFile',"`"$script:LogPath`"") }
    try {
        Start-Process -FilePath 'powershell.exe' -Verb RunAs -ArgumentList $argList -WindowStyle Hidden | Out-Null
        L '已提权重启（原进程退出）。'
        exit 0
    } catch {
        L '【失败】未获得管理员授权（UAC 被拒绝）。'
        exit 1
    }
}

if (-not (Test-Admin)) {
    if ($Mode -ne 'status') { Invoke-Elevate -newMode $Mode }
}

# ---------- 置顶弹窗 ----------
function Show-TopMost([string]$Title, [string]$Text, [int]$Flags) {
    try {
        if (-not ('Wb.MsgBox' -as [type])) {
            Add-Type -Namespace Wb -Name MsgBox -MemberDefinition @'
[DllImport("user32.dll", CharSet = CharSet.Unicode)]
public static extern int MessageBoxW(IntPtr hWnd, string lpText, string lpCaption, uint uType);
'@
        }
        return [Wb.MsgBox]::MessageBoxW([IntPtr]::Zero, $Text, $Title, $Flags)
    } catch {
        L "弹窗失败：$($_.Exception.Message)"
        return 0
    }
}

# 置顶 + 系统模态 + 警告图标 + 是/否
$script:MB_YESNO = 0x00040000 -bor 0x00001000 -bor 0x00000030 -bor 0x00000004
# 置顶 + 系统模态 + 警告图标 + 确定
$script:MB_OK    = 0x00040000 -bor 0x00001000 -bor 0x00000030

# ---------- 规则库 ----------
# 致命：一律击杀 + 隔离 + 阻断 + 弹窗
$script:FatalPatterns = @(
    @{ Rx = 'vssadmin(\.exe)?["'']?\s+delete\s+shadows';                                    Why = '删除卷影副本（勒索软件典型行为）' }
    @{ Rx = 'wmic(\.exe)?["'']?\s+shadowcopy\s+delete';                                     Why = '删除卷影副本（勒索软件典型行为）' }
    @{ Rx = 'wbadmin(\.exe)?["'']?\s+delete\s+(catalog|systemstatebackup)';                 Why = '删除系统备份目录（勒索软件典型行为）' }
    @{ Rx = 'bcdedit(\.exe)?["'']?.*(recoveryenabled\s+no|ignoreallfailures)';              Why = '关闭系统恢复（勒索软件典型行为）' }
    @{ Rx = 'wevtutil(\.exe)?["'']?\s+cl\s+';                                               Why = '清空事件日志（销毁痕迹）' }
    @{ Rx = 'taskkill(\.exe)?["'']?\s+.*(/f|/im).*(MsMpEng|MsMpEngCP|360|HipsTray|HipsDaemon|ZhuDongFangYu|HRSword|kxescore|McAPExe|avp\.exe|NortonData|egui\.exe|QQPCRTP)'; Why = '强杀安全软件进程' }
    @{ Rx = '(sc|net)(\.exe)?["'']?\s+(stop|delete|config).*(WinDefend|WdNisSvc|WdFilter|Sense|HipsDaemon|HipsTray|ZhuDongFangYu|HRSword|kxecenter)'; Why = '停用/删除安全软件服务' }
    @{ Rx = 'Set-MpPreference.*(-DisableRealtimeMonitoring|-DisableIOAVProtection|-DisableBehaviorMonitoring|-DisableScriptScanning)'; Why = '关闭 Defender 实时防护' }
    @{ Rx = 'Add-MpPreference.*-Exclusion(P|Path|Process|Extension)';                 Why = '为 Defender 添加排除项（让木马免杀）' }
    @{ Rx = 'reg(\.exe)?["'']?\s+add.*Defender.*DisableAntiSpyware.*1';                    Why = '注册表禁用 Defender' }
    @{ Rx = 'powershell(\.exe)?["'']?\s+.*(-enc\s+[A-Za-z0-9+/=]{20,}|-encodedcommand\s+[A-Za-z0-9+/=]{20,}|-e\s+[A-Za-z0-9+/=]{20,})'; Why = '编码/加密的 PowerShell 载荷（无文件攻击）' }
    @{ Rx = 'powershell(\.exe)?["'']?\s+.*(downloadstring|downloaddata|downloadfile|invoke-webrequest|iwr|invoke-restmethod)'; Why = 'PowerShell 联网下载载荷' }
    @{ Rx = 'powershell(\.exe)?["'']?\s+.*(iex|invoke-expression)';                         Why = 'Invoke-Expression 动态执行' }
    @{ Rx = 'mshta(\.exe)?["'']?\s+(https?://|javascript:|vbscript:)';                      Why = 'mshta 加载远程脚本（白名单程序滥用）' }
    @{ Rx = 'regsvr32(\.exe)?["'']?\s+.*(scrobj|/i:https?://|\.sct\b)';                     Why = 'regsvr32 远程 scriptlet 载荷' }
    @{ Rx = 'rundll32(\.exe)?["'']?\s+.*(javascript:)';                                     Why = 'rundll32 javascript 协议载荷' }
    @{ Rx = 'certutil(\.exe)?["'']?\s+.*(-urlcache|-decode|-decodehex|-f\s+https?://)';    Why = 'certutil 下载/解码载荷' }
    @{ Rx = 'bitsadmin(\.exe)?["'']?\s+.*(/transfer|/addfile)';                             Why = 'bitsadmin 后台下载载荷' }
    @{ Rx = '(curl|wget)(\.exe)?["'']?\s+.*\|\s*(powershell|cmd(\.exe)?|iex|bash)';        Why = '下载即执行（curl 管道 shell）' }
    @{ Rx = 'amsiInitFailed|amsiutils|amsiScanBuffer\s*.*patch';                      Why = 'AMSI 特征（杀软对抗）' }
    @{ Rx = 'comsvcs(\.exe)?["'']?\s+.*MiniDump.*(74024|72024)';                            Why = 'comsvcs 转储 LSASS（凭据窃取）' }
)

# 高危：弹窗询问后再处置
$script:HighPatterns = @(
    @{ Rx = 'powershell(\.exe)?["'']?\s+.*(-w\s+hidden|-windowstyle\s+hidden)';           Why = '隐藏窗口的 PowerShell（无文件攻击常见）' }
    @{ Rx = 'wmic(\.exe)?["'']?\s+process\s+call\s+create.*(https?://|powershell|cmd\s|\.hta|\.vbs|\.js)'; Why = 'WMI 远程创建进程（横向移动/持久化）' }
    @{ Rx = 'schtasks(\.exe)?["'']?\s+/create.*(https?://|powershell|/tr\s+.*\.hta)';     Why = '计划任务持久化（远程载荷）' }
    @{ Rx = 'sc(\.exe)?["'']?\s+create.*(\\temp\\|\\public\\|\\programdata\\)';             Why = '服务持久化（可疑路径）' }
    @{ Rx = 'net(\.exe)?["'']?\s+(user|localgroup)\s+.*\s/add';                             Why = '新增账户（后门预备）' }
    @{ Rx = 'netsh(\.exe)?["'']?\s+(advfirewall\s+set\s+.*state\s+off|firewall\s+set\s+opmode\s+disable)'; Why = '关闭防火墙' }
    @{ Rx = 'conhost(\.exe)?["'']?\s+.*--headless';                                         Why = '隐藏 conhost（进程伪装）' }
    @{ Rx = 'vaultcmd|cmdkey\s+/list';                                                Why = '凭据列举' }
    @{ Rx = 'makecab(\.exe)?["'']?\s+.*\\system32\\';                                       Why = '打包文件到系统目录（lolbin 外传/投递）' }
)

# 会被派生脚本宿主的"上游进程"（钓鱼文档/浏览器典型入口）
$script:HighParents = @('winword','excel','powerpnt','outlook','iexplore','chrome','msedge','firefox','acrord32','acrobat','soffice','wps','et','wpp','thunderbird','java','javaw')
# 上述上游派生这些子进程时才告警
$script:LolbinChildren = @('powershell','pwsh','cmd','mshta','rundll32','regsvr32','wscript','cscript','conhost','bitsadmin','certutil','curl','wget','msbuild','installutil')

$script:TempPathRx = '\\appdata\\local\\temp\\|\\windows\\temp\\|\\users\\public\\|\\programdata\\|\\downloads\\|\\appdata\\roaming\\'

function Test-RandomMixedName([string]$name) {
    $base = [System.IO.Path]::GetFileNameWithoutExtension($name)
    if ([string]::IsNullOrEmpty($base)) { return $false }
    if ($base -notmatch '^[A-Za-z0-9]{5,12}$') { return $false }
    if ($base -match '^[0-9]+$') { return $false }
    $hasUpper = $false; $hasLower = $false; $innerUpper = $false
    for ($i = 0; $i -lt $base.Length; $i++) {
        $c = $base[$i]
        if ([char]::IsUpper($c)) { $hasUpper = $true; if ($i -gt 0) { $innerUpper = $true } }
        elseif ([char]::IsLower($c)) { $hasLower = $true }
    }
    return ($hasUpper -and $hasLower -and $innerUpper)
}

# ---------- 判定 ----------
function Get-ThreatAssessment {
    param([string]$Name, [string]$Cmd, [string]$Path, [int]$ProcPid, [int]$ParentProcId, [string]$ParentName)

    $out = $null
    $n = "$Name"; $c = "$Cmd"; $p = "$Path"

    # 1) 系统进程伪装：svchost 等不在系统目录
    $sysNames = @('svchost','services','csrss','wininit','winlogon','lsass','smss','spoolsv','taskhostw','sihost','dwm','ctfmon','lsm','lsmose')
    if (($n -replace '\.exe$','') -in $sysNames) {
        $ok = $p -match '^(?i)' + [regex]::Escape($env:SystemRoot) + '\\(System32|SysWOW64|WinSxS)\\'
        if ((-not $ok) -and $p -and (-not ($p -match '(?i)^' + [regex]::Escape($env:SystemRoot)))) {
            return @{ Level='FATAL'; Why = "系统进程伪装（$n 实际位于 $p）" }
        }
    }

    foreach ($f in $script:FatalPatterns) {
        if ($c -match $f.Rx) { return @{ Level='FATAL'; Why = $f.Why } }
    }

    foreach ($h in $script:HighPatterns) {
        if ($c -match $h.Rx) { return @{ Level='HIGH'; Why = $h.Why } }
    }

    # 2) Office/浏览器派生 LOLBin
    $childBase = ($n -replace '\.exe$','').ToLower()
    $parentBase = ("$ParentName" -replace '\.exe$','').ToLower()
    if ($parentBase -in $script:HighParents -and $childBase -in $script:LolbinChildren) {
        if ($c -and $c -notmatch '(?i)-NoLogo') {
            return @{ Level='HIGH'; Why = "上游进程 $ParentName 派生脚本/白名单程序 $n（钓鱼文档典型链）" }
        }
    }

    # 3) 临时/公共目录里的随机名可执行
    if ($p -match "(?i)$($script:TempPathRx)") {
        if ($n -match '(?i)\.(exe|scr|dll|pif|cpl)$' -and (Test-RandomMixedName $n)) {
            return @{ Level='HIGH'; Why = "临时/公共目录中的随机名可执行文件" }
        }
    }

    return $out
}

# ---------- 处置 ----------
function Invoke-Containment {
    param([int]$ProcPid, [string]$Name, [string]$Path, [string]$Why, [string]$Cmd)

    # 击杀进程树
    try { taskkill.exe /PID $ProcPid /T /F *> $null } catch { }
    try { Stop-Process -Id $ProcPid -Force -ErrorAction SilentlyContinue } catch { }

    # 系统目录/已签名文件只击杀不隔离，避免误伤系统文件
    $inSystem = $false
    if ($Path) {
        $inSystem = ($Path -match '(?i)^' + [regex]::Escape($env:SystemRoot) + '\\(System32|SysWOW64|WinSxS|assembly|explorer\.exe|regedit\.exe|notepad\.exe)\b') -or
                   ($Path -match '(?i)^' + [regex]::Escape($env:SystemRoot) + '\\[^\\]+\.exe$')
    }
    $signValid = $false
    if ($Path -and (Test-Path -LiteralPath $Path)) {
        $sig = Get-AuthenticodeSignature -LiteralPath $Path -ErrorAction SilentlyContinue
        if ($sig -and $sig.Status -eq 'Valid') { $signValid = $true }
    }
    $userWritable = $false
    if ($Path) {
        $userWritable = $Path -match '(?i)[\\/](appdata|temp|public|downloads|documents|desktop|programdata)[\\/]'
    }

    if ($Path -and (Test-Path -LiteralPath $Path) -and (-not $inSystem) -and ($userWritable -or (-not $signValid))) {
        $leaf = "$([System.IO.Path]::GetFileName($Path)).$((Get-Date).ToString('yyyyMMdd_HHmmss')).quarantined"
        $dest = Join-Path $Quarantine $leaf
        try {
            New-Item -ItemType Directory -Force -Path $Quarantine | Out-Null
            Copy-Item -LiteralPath $Path -Destination $dest -Force -ErrorAction Stop
            # 原文件改名，阻止继续运行
            try { Rename-Item -LiteralPath $Path -NewName ([System.IO.Path]::GetFileName($Path) + '.quarantined') -Force -ErrorAction SilentlyContinue } catch { }
            L "样本已隔离: $dest"
        } catch {
            L "样本隔离失败（可能无法删除源文件）: $Path"
        }
        # 防火墙阻断出网
        if ($Path -match '(?i)\\(exe|dll|scr|cpl)$') {
            $rule = ($FwPrefix + ($Name -replace '\.exe$','')) -replace '[^A-Za-z0-9_]','_'
            try {
                if (-not (Get-NetFirewallRule -DisplayName $rule -ErrorAction SilentlyContinue)) {
                    New-NetFirewallRule -DisplayName $rule -Direction Outbound -Program $Path -Action Block -Enabled True | Out-Null
                    L "已添加防火墙出站阻断规则: $rule"
                }
            } catch {
                L "防火墙阻断规则添加失败: $rule"
            }
        }
    } elseif ($Path) {
        L "文件位于系统目录或签名有效（$Path），仅击杀不隔离。"
    }
    Write-Alert "已拦截[$Why] $Name PID=$ProcPid 路径=$Path 命令=$Cmd"
}

function Handle-Process {
    param([int]$ProcPid, [string]$Name)

    $key = "$ProcPid|$Name"
    if ($script:Suppressed.ContainsKey($key)) { return }

    $proc = $null
    try { $proc = Get-CimInstance Win32_Process -Filter "ProcessId=$ProcPid" -ErrorAction SilentlyContinue } catch { $proc = $null }
    if ($null -eq $proc) { return }

    $parentName = ''
    if ($proc.ParentProcessId) {
        try {
            $pp = Get-CimInstance Win32_Process -Filter "ProcessId=$($proc.ParentProcessId)" -ErrorAction SilentlyContinue
            if ($pp) { $parentName = $pp.Name }
        } catch { }
    }

    $asmt = Get-ThreatAssessment -Name $proc.Name -Cmd ("$($proc.CommandLine)") -Path "$($proc.ExecutablePath)" -ProcPid $ProcPid -ParentProcId ([int]$proc.ParentProcessId) -ParentName $parentName
    if ($null -eq $asmt) { return }

    $cmdShort = "$($proc.CommandLine)"
    if ($cmdShort.Length -gt 300) { $cmdShort = $cmdShort.Substring(0,300) + '...' }
    $msg  = "时间: $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')`r`n"
    $msg += "进程: $($proc.Name)  PID: $ProcPid`r`n"
    $msg += "路径: $($proc.ExecutablePath)`r`n"
    $msg += "父进程: $parentName (PID $($proc.ParentProcessId))`r`n"
    $msg += "命令: $cmdShort`r`n"
    $msg += "命中规则: $($asmt.Why)"

    if ($asmt.Level -eq 'FATAL') {
        L "【致命】$($asmt.Why) => $($proc.Name) PID=$ProcPid"
        Write-Alert "检测到致命异常指令[$($asmt.Why)]: $($proc.Name) PID=$ProcPid 命令=$cmdShort"
        # 先处置再弹窗：用户不在电脑前也不会耽误拦截
        Invoke-Containment -ProcPid $ProcPid -Name $proc.Name -Path "$($proc.ExecutablePath)" -Why $asmt.Why -Cmd $cmdShort
        $body = $msg + "`r`n`r`n已自动执行：击杀进程树 / 隔离样本 / 防火墙阻断出网。"
        Show-TopMost "⚠ MhyunBox 命令守卫 - 发现危险指令" $body $script:MB_OK
        $script:Suppressed[$key] = $true
    }
    else {
        L "【高危】$($asmt.Why) => $($proc.Name) PID=$ProcPid"
        $body = $msg + "`r`n`r`n这可能是木马/钓鱼行为。`r`n【是】= 立即击杀并隔离样本`r`n【否】= 仅记录，不再弹窗"
        $r = Show-TopMost "⚠ MhyunBox 命令守卫 - 发现可疑指令" $body $script:MB_YESNO
        if ($r -eq 6) {
            Invoke-Containment -ProcPid $ProcPid -Name $proc.Name -Path "$($proc.ExecutablePath)" -Why $asmt.Why -Cmd $cmdShort
            Write-Alert "用户确认后已拦截[$($asmt.Why)]: $($proc.Name) PID=$ProcPid"
        } else {
            $script:Suppressed[$key] = $true
            Write-Alert "高危未确认[$($asmt.Why)]: $($proc.Name) PID=$ProcPid 命令=$cmdShort（用户选择忽略）"
        }
    }
}

# ---------- 模式: 监控 ----------
function Start-Monitor {
    L "== 命令守卫监控启动（PID $PID）=="
    Write-Alert "命令守卫监控已启动（守护进程）"

    # 系统能力档案（家庭版/专业版通用，未装杀软时给出提示）
    $edition = $(if ($script:Profile.IsHome) { '家庭版（无组策略，功能已按家庭版可用命令适配）' } else { '专业版及以上' })
    L "系统: $($script:Profile.Caption) / $edition"
    if (-not $script:Profile.HasAnyAv) {
        L "【重要】未检测到任何杀毒软件：命令守卫是本机当前唯一实时防护，请保持监控开启，并建议尽快安装防护软件或开启 Windows 安全中心。"
        Write-Alert "未检测到任何杀毒软件，命令守卫为唯一实时防护"
    }

    # 启动时先扫一遍存量进程
    try {
        $all = Get-CimInstance Win32_Process -ErrorAction SilentlyContinue
        foreach ($pr in $all) { Handle-Process -ProcPid ([int]$pr.ProcessId) -Name $pr.Name }
    } catch { }

    $watcher = $null
    try {
        $wql  = New-Object System.Management.WqlEventQuery("SELECT * FROM Win32_ProcessStartTrace")
        $watcher = New-Object System.Management.ManagementEventWatcher($wql)
        $watcher.Options.Timeout = [System.Management.ManagementOptions]::InfiniteTimeout
        Register-ObjectEvent -InputObject $watcher -EventName EventArrived -SourceIdentifier 'CmdGuardWatcher' | Out-Null
        $watcher.Start()
        L "WMI 进程创建事件监听已启用"
    } catch {
        L "WMI 进程事件监听不可用（$($_.Exception.Message)），仅使用周期扫描"
    }

    $timer = New-Object System.Timers.Timer(15000)
    Register-ObjectEvent -InputObject $timer -EventName Elapsed -SourceIdentifier 'CmdGuardTicker' | Out-Null
    $timer.AutoReset = $true
    $timer.Start()

    L "进入守护循环..."
    while ($true) {
        $ev = $null
        try {
            if ($null -ne $watcher) { $ev = Wait-Event -SourceIdentifier CmdGuardWatcher,CmdGuardTicker -ErrorAction Stop }
            else { $ev = Wait-Event -SourceIdentifier CmdGuardTicker -ErrorAction Stop }
        } catch {
            Start-Sleep -Seconds 3
            try {
                $all = Get-CimInstance Win32_Process -ErrorAction SilentlyContinue
                foreach ($pr in $all) { Handle-Process -Pid ([int]$pr.ProcessId) -Name $pr.Name }
            } catch { }
            continue
        }

        if ($ev.SourceIdentifier -eq 'CmdGuardWatcher') {
            try { Handle-Process -Pid ([int]$ev.SourceEventArgs.NewEvent.Properties['ProcessID'].Value) -Name ("$($ev.SourceEventArgs.NewEvent.Properties['ProcessName'].Value)") } catch { }
            try { Remove-Event -EventIdentifier $ev.EventIdentifier -ErrorAction SilentlyContinue | Out-Null } catch { }
            # 顺手清扫队列
            $more = Get-Event -SourceIdentifier CmdGuardWatcher -ErrorAction SilentlyContinue
            foreach ($e in $more) {
                try { Handle-Process -Pid ([int]$e.SourceEventArgs.NewEvent.Properties['ProcessID'].Value) -Name ("$($e.SourceEventArgs.NewEvent.Properties['ProcessName'].Value)") } catch { }
                Remove-Event -EventIdentifier $e.EventIdentifier -ErrorAction SilentlyContinue | Out-Null
            }
        } else {
            try { Remove-Event -EventIdentifier $ev.EventIdentifier -ErrorAction SilentlyContinue | Out-Null } catch { }
            try {
                $all = Get-CimInstance Win32_Process -ErrorAction SilentlyContinue
                foreach ($pr in $all) { Handle-Process -Pid ([int]$pr.ProcessId) -Name $pr.Name }
            } catch { }
        }
    }
}

# ---------- 模式: 安装 ----------
function Install-Guard {
    $taskAction = 'powershell.exe -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File "' + $PSCommandPath + '" -Mode monitor -Silent'
    $arguments  = @('/Create','/TN', $TaskName, '/SC','ONLOGON','/RL','HIGHEST','/F','/IT','/TR', $taskAction)
    try {
        $out = & schtasks.exe @arguments 2>&1
        L "注册计划任务: $($out -join ' ')"
    } catch {
        L "【错误】注册计划任务失败: $($_.Exception.Message)"
        L "== 命令守卫操作失败 =="
        return
    }
    try { & schtasks.exe /Run /TN $TaskName | Out-Null } catch { }
    # 保险起见，不等计划任务也直接拉一个后台监控进程（如果计划任务延迟启动）
    $alive = Get-CimInstance Win32_Process -Filter "Name='powershell.exe'" -ErrorAction SilentlyContinue |
             Where-Object { "$($_.CommandLine)" -like "*命令守卫.ps1*-Mode monitor*" -or "$($_.CommandLine)" -like "*命令守卫.ps1* -Mode monitor*" }
    if (-not $alive) {
        try {
            Start-Process -FilePath 'powershell.exe' -ArgumentList @('-NoProfile','-ExecutionPolicy','Bypass','-WindowStyle','Hidden','-File', "`"$PSCommandPath`"", '-Mode','monitor','-Silent') -WindowStyle Hidden | Out-Null
            L "已直接启动监控进程"
        } catch {
            L "【警告】直接启动监控进程失败，但计划任务已注册（下次登录生效）"
        }
    }
    L "命令守卫已开启：开机自启 + 实时监控，命中危险指令将置顶弹窗并自动拦截。"
    L "== 命令守卫操作完成 =="
}

# ---------- 模式: 卸载 ----------
function Uninstall-Guard {
    try { $out = & schtasks.exe /Delete /TN $TaskName /F 2>&1; L "注销计划任务: $($out -join ' ')" } catch { L "计划任务删除时异常（可能本来就不存在）" }
    # 停止监控进程（不碰自己：自己跑的是 -Mode uninstall）
    $mons = Get-CimInstance Win32_Process -Filter "Name='powershell.exe'" -ErrorAction SilentlyContinue |
            Where-Object { "$($_.CommandLine)" -match '命令守卫\.ps1' -and "$($_.CommandLine)" -match '(?i)-Mode\s+monitor' }
    foreach ($mp in $mons) {
        try { taskkill.exe /PID $mp.ProcessId /T /F *> $null; L "已停止监控进程 PID=$($mp.ProcessId)" } catch { }
    }
    L "命令守卫已关闭。"
    L "== 命令守卫操作完成 =="
}

# ---------- 模式: 状态 ----------
function Show-Status {
    $q = ''
    try { $q = (& schtasks.exe /Query /TN $TaskName /FO LIST 2>&1) -join "`n" } catch { }
    $taskOk = $q -match $TaskName -and $q -match '\S'
    $monRunning = @()
    try {
        $monRunning = @(Get-CimInstance Win32_Process -Filter "Name='powershell.exe'" -ErrorAction SilentlyContinue |
                        Where-Object { "$($_.CommandLine)" -match '命令守卫\.ps1' -and "$($_.CommandLine)" -match '(?i)-Mode\s+monitor' })
    } catch { }
    if ($taskOk) { L "计划任务 $TaskName : 已注册（开机自启）" } else { L "计划任务 $TaskName : 未注册" }
    if ($monRunning.Count -gt 0) { L "监控进程: 正在运行（PID: $(($monRunning | ForEach-Object { $_.ProcessId }) -join ', ')）" } else { L "监控进程: 未运行" }
    $alertCount = 0
    if (Test-Path -LiteralPath $AlertLog) {
        $lines = Get-Content -LiteralPath $AlertLog -Encoding UTF8 -ErrorAction SilentlyContinue
        $alertCount = @($lines | Where-Object { $_ -match '\[命令守卫\]' }).Count
    }
    L "累计命令守卫告警: $alertCount 条（MhyunBox 布防日志可查看）"
    L "== 命令守卫操作完成 =="
}

switch ($Mode) {
    'monitor'   { Start-Monitor }
    'install'   { Install-Guard }
    'uninstall' { Uninstall-Guard }
    'status'    { Show-Status }
    default     { L "未知模式: $Mode（可用: monitor/install/uninstall/status）" }
}

exit 0
