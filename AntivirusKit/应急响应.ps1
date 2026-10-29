# 应急响应 (MhyunBox) —— 病毒泛滥 / 系统被入侵时的一键止血与强杀
# 适用场景：已经中了毒、木马正在运行/横向扩散、或者用户根本没装任何防护软件。
# 设计目标：不依赖任何杀毒软件，纯进程/启动项/网络层处置，先把血止住再谈清除。
#
# 模式:
#   stop        一键止血：断网（防火墙全阻断） + 强杀可疑进程 + 冻结自启动 + 守护系统关键进程
#   standard    止血 + 调用同目录「威胁进化扫描」做全项检测 +（可用时）Defender 快速扫描
#   deep        标准 + 常见投放目录全枚举 + 引导层/固件时间检查 + 取证信息打包
#   restore     恢复：删除应急防火墙规则、解冻自启动项、停止守护
#   defender-on 一键开启 Windows 安全中心 Defender（家庭版自带，被木马关掉时恢复）
#   status      系统能力档案 + 当前防护状态 + 守卫任务状态
#
# 用法: powershell -NoProfile -ExecutionPolicy Bypass -File 应急响应.ps1 -Mode stop [-Force] [-Silent]
#   -Force   跳过二次确认（程序内部调用时使用）
#   -Silent  静默模式：无任何控制台输出，只写日志（扫描/守卫默认隐藏窗口运行）
#
# 适配说明（Win10 / Win11，家庭版 / 专业版通用）:
#   - 不使用组策略（gpedit 家庭版没有），只用注册表/防火墙/计划任务/WMI，家庭版全部可用
#   - Defender 模块、BitLocker、Windows 沙盒等按版本能力探测，缺失时优雅降级并说明
#   - 未安装第三方杀软的电脑同样可用，本脚本自身就是最后一道防线
#
# 日志: %LOCALAPPDATA%\MhyunBox\Emergency\emergency.log
# 取证: %LOCALAPPDATA%\MhyunBox\Incident_yyyyMMdd_HHmmss\
# 提权: 自动请求 UAC。

param(
    [ValidateSet('stop','standard','deep','restore','defender-on','status','watchdog')]
    [string]$Mode = 'stop',
    [switch]$Force,
    [switch]$Silent,
    [string]$LogFile = ''
)

[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$OutputEncoding = [System.Text.Encoding]::UTF8
$ErrorActionPreference = 'Continue'

# ---------- 基础路径 ----------
$DirSelf    = $PSScriptRoot
$LogDir     = Join-Path $env:LOCALAPPDATA 'MhyunBox\Emergency'
$GuardLog   = Join-Path $LogDir 'emergency.log'
$FwRuleName = 'MhyunBox_Emergency_BlockAll'
$FwStateFile= Join-Path $LogDir 'fw_original_state.txt'
$FrozenTag  = '.frozen_mhyunbox'
$BackupReg  = Join-Path $LogDir 'frozen_autoruns.reg'

$script:LogPath = $LogFile
$script:Findings = New-Object System.Collections.Generic.List[string]
$script:Killed   = 0
$script:Frozen   = 0

New-Item -ItemType Directory -Force -Path $LogDir | Out-Null

function L([string]$Message) {
    # 进度行：静默模式只写内部日志，不刷屏、不回传程序
    if ($Silent) {
        try { Add-Content -LiteralPath $GuardLog -Value ((Get-Date -Format 'HH:mm:ss.fff') + '  ' + $Message) -Encoding UTF8 } catch { }
        return
    }
    $line = (Get-Date -Format 'HH:mm:ss.fff') + '  ' + $Message
    Write-Output $line
    try { Add-Content -LiteralPath $GuardLog -Value $line -Encoding UTF8 } catch { }
    if ($script:LogPath) { try { Add-Content -LiteralPath $script:LogPath -Value $line -Encoding UTF8 } catch { } }
}

function Lk([string]$Message) {
    # 关键行：始终回传程序日志面板（扫描开始/发现威胁/结束汇总），静默模式下也只回传这些
    $line = (Get-Date -Format 'HH:mm:ss.fff') + '  ' + $Message
    if (-not $Silent) { Write-Output $line }
    try { Add-Content -LiteralPath $GuardLog -Value $line -Encoding UTF8 } catch { }
    if ($script:LogPath) { try { Add-Content -LiteralPath $script:LogPath -Value $line -Encoding UTF8 } catch { } }
}

function Note([string]$Item) {
    $script:Findings.Add($Item)
    Lk "[发现] $Item"
}

function Test-Admin {
    $id = [System.Security.Principal.WindowsIdentity]::GetCurrent()
    $p  = New-Object System.Security.Principal.WindowsPrincipal($id)
    return $p.IsInRole([System.Security.Principal.WindowsBuiltInRole]::Administrator)
}

# ---------- 系统能力档案（Win10/Win11、家庭版/专业版自适应） ----------
function Get-SystemProfile {
    $os = Get-CimInstance Win32_OperatingSystem -ErrorAction SilentlyContinue
    $caption = "$($os.Caption)"
    $build   = "$($os.BuildNumber)"
    $isHome  = ($caption -match '(?i)home|家庭') -and ($caption -notmatch '(?i)pro|专业|enterprise|education')
    $isWin11 = [int]($build -replace '\D','') -ge 22000
    $hasDefenderModule = $null -ne (Get-Command Get-MpPreference -ErrorAction SilentlyContinue)
    $hasFirewallModule = $null -ne (Get-Command Get-NetFirewallRule -ErrorAction SilentlyContinue)
    $hasBitLocker      = $null -ne (Get-Command Get-BitLockerVolume -ErrorAction SilentlyContinue) -and ($caption -match '(?i)pro|enterprise|education|专业|企业|教育')
    $thirdPartyAv = @()
    try {
        $thirdPartyAv = @(Get-CimInstance -Namespace root\SecurityCenter2 -ClassName AntiVirusProduct -ErrorAction SilentlyContinue |
            Where-Object { $_.displayName -notmatch 'Windows Defender|Windows 安全中心|Microsoft Defender' } |
            ForEach-Object { $_.displayName })
    } catch { }
    [pscustomobject]@{
        Caption = $caption; Build = $build; IsHome = $isHome; IsWin11 = $isWin11
        HasDefenderModule = $hasDefenderModule; HasFirewallModule = $hasFirewallModule
        HasBitLocker = $hasBitLocker; ThirdPartyAv = $thirdPartyAv
        HasAnyAv = ($hasDefenderModule -or $thirdPartyAv.Count -gt 0)
    }
}

$script:Profile = Get-SystemProfile

function Show-Profile {
    Lk '---- 系统能力档案 ----'
    Lk ("系统: {0} (Build {1})" -f $script:Profile.Caption, $script:Profile.Build)
    Lk ("版本类型: {0} / {1}" -f $(if ($script:Profile.IsWin11) { 'Windows 11' } else { 'Windows 10' }), $(if ($script:Profile.IsHome) { '家庭版（无组策略，全部功能已按可用命令适配）' } else { '专业版及以上' }))
    Lk ("Defender 管理模块: {0}" -f $(if ($script:Profile.HasDefenderModule) { '可用' } else { '不可用（可能已被第三方安全软件替换或卸载）' }))
    Lk ("防火墙管理模块: {0}" -f $(if ($script:Profile.HasFirewallModule) { '可用' } else { '不可用（将尝试 netsh 命令）' }))
    Lk ("BitLocker: {0}" -f $(if ($script:Profile.HasBitLocker) { '可用' } else { '当前版本不提供（家庭版无 BitLocker，属正常现象）' }))
    if ($script:Profile.ThirdPartyAv.Count -gt 0) {
        Lk ("已安装第三方杀毒软件: {0}" -f ($script:Profile.ThirdPartyAv -join ', '))
    } elseif ($script:Profile.HasDefenderModule) {
        Lk '未检测到第三方杀毒软件：自带 Windows 安全中心（Defender）是当前唯一实时防护，建议保持开启。'
    } else {
        Lk '【重要】未检测到任何杀毒软件（第三方 + Defender 均不可用）——本机目前毫无实时防护，应急响应脚本就是最后一道防线，建议处置完立即开启 Defender 或安装防护软件。'
    }
}

# ---------- 关键进程白名单（永不击杀，避免"阻拦病毒"变成"搞崩系统"） ----------
# 无条件关键：这些进程名在任何路径下都不该由我们杀（杀错直接系统级故障）
$script:AlwaysCriticalProcs = @(
    'system','smss.exe','csrss.exe','wininit.exe','services.exe','lsass.exe','lsaiso.exe',
    'dwm.exe','explorer.exe','winlogon.exe','userinit.exe','fontdrvhost.exe','audiodg.exe',
    'memory compression','registry','idle','secure system','mpsvc.exe','msgameassist.exe'
)
# 位置关键：只有位于系统目录时才是正身；跑到 Users/Temp 下的同名进程按伪装处理
$script:PathCriticalProcs = @(
    'svchost.exe','conhost.exe','ctfmon.exe','sihost.exe','taskhostw.exe','runtimebroker.exe',
    'startmenuexperiencehost.exe','shellhost.exe','searchindexer.exe','searchprotocolhost.exe',
    'spoolsv.exe','dllhost.exe','wmiprvse.exe','unsecapp.exe','sppsvc.exe','trustedinstaller.exe',
    'tiworker.exe','mousocoreworker.exe','gamebar.exe','xboxgamecallableui.exe'
)
# 安全软件进程：应急期间尤其不能误杀（用户最后的实时防护）
$script:SecurityProcs = @(
    'msmpeng.exe','msmpengcp.exe','nissrv.exe','securityhealthservice.exe','securityhealthsystray.exe',
    '360safe.exe','360tray.exe','hipsdaemon.exe','hipstray.exe','zhudongfangyu.exe','hrws.exe','hrsword.exe',
    'kxetray.exe','kxescore.exe','kxecenter.exe','mcapexe.exe','avp.exe','avpui.exe','egui.exe','qqpctrtp.exe',
    'baidusdt.exe','baiduprotect.exe','ksafe.exe','kavsvc.exe','tanumsvc.exe'
)
$script:SelfPid = $PID

function Test-CriticalProc([string]$Name, [int]$ProcId, [string]$Path) {
    if ($ProcId -eq $script:SelfPid) { return $true }
    $n = ("$Name").ToLower()
    foreach ($c in $script:AlwaysCriticalProcs) { if ($n -eq $c.ToLower()) { return $true } }
    foreach ($c in $script:SecurityProcs)     { if ($n -eq $c.ToLower()) { return $true } }
    foreach ($c in $script:PathCriticalProcs) {
        if ($n -eq $c.ToLower()) {
            # 正身必须位于系统目录；出现在用户可写目录 = 伪装，不放行
            if (Is-SystemPath $Path) { return $true }
            return $false
        }
    }
    # 本程序（MhyunBox）与 PowerShell 宿主自身
    try {
        $mine = Get-Process -Id $script:SelfPid -ErrorAction SilentlyContinue
        if ($mine -and $n -eq $mine.ProcessName.ToLower()) { return $true }
    } catch { }
    return $false
}

function Is-SystemPath([string]$Path) {
    if (-not $Path) { return $true }
    return ($Path -match '(?i)^' + [regex]::Escape($env:SystemRoot) + '\\(System32|SysWOW64|WinSxS|assembly|Tasks|Fonts|DriverStore)\\')
}

function Test-ValidSignature([string]$Path) {
    if (-not $Path -or -not (Test-Path -LiteralPath $Path)) { return $false }
    try {
        $sig = Get-AuthenticodeSignature -LiteralPath $Path -ErrorAction SilentlyContinue
        if ($sig.Status -eq 'Valid') {
            $s = "$($sig.SignerCertificate.Subject)"
            if ($s -match '(?i)O=Microsoft|Microsoft Windows|Microsoft Corporation') { return $true }
        }
    } catch { }
    return $false
}

# 已知良好目录：微软官方组件目录（ProgramData 下 Defender 平台目录曾是误报重灾区）
function Test-KnownGoodPath([string]$Path) {
    if (-not $Path) { return $false }
    $good = @(
        (Join-Path $env:SystemRoot 'Microsoft.NET'),
        (Join-Path $env:SystemRoot 'assembly'),
        (Join-Path $env:SystemRoot 'SystemApps'),
        (Join-Path $env:ProgramFiles 'Microsoft'),
        (Join-Path ${env:ProgramFiles(x86)} 'Microsoft'),
        (Join-Path $env:ProgramData 'Microsoft'),
        (Join-Path $env:ProgramFiles 'WindowsApps'),
        (Join-Path $env:ProgramFiles 'Windows Defender'),
        (Join-Path $env:ProgramFiles 'Windows Security')
    )
    foreach ($g in $good) {
        if ($g -and $Path.StartsWith($g, [System.StringComparison]::OrdinalIgnoreCase)) { return $true }
    }
    return $false
}

# ---------- 可疑进程判定（与命令守卫规则库同源，家庭版/无杀软环境同样适用） ----------
$script:FatalPatterns = @(
    @{ Rx = 'vssadmin(\.exe)?\s+delete\s+shadows';                                    Why = '删除卷影副本（勒索软件典型行为）' }
    @{ Rx = 'wmic(\.exe)?\s+shadowcopy\s+delete';                                     Why = '删除卷影副本（勒索软件典型行为）' }
    @{ Rx = 'bcdedit(\.exe)?.*(recoveryenabled\s+no|ignoreallfailures)';              Why = '关闭系统恢复（勒索软件典型行为）' }
    @{ Rx = 'taskkill(\.exe)?\s+.*(/f|/im).*(MsMpEng|MsMpEngCP|360|HipsTray|HipsDaemon|ZhuDongFangYu|HRSword|kxescore|McAPExe|avp\.exe|egui\.exe|QQPCRTP)'; Why = '强杀安全软件进程' }
    @{ Rx = '(sc|net)\s+(stop|delete|config).*(WinDefend|WdNisSvc|WdFilter|Sense|HipsDaemon|HipsTray|ZhuDongFangYu|HRSword)'; Why = '停用/删除安全软件服务' }
    @{ Rx = 'Set-MpPreference.*(-DisableRealtimeMonitoring|-DisableIOAVProtection|-DisableBehaviorMonitoring)'; Why = '关闭 Defender 实时防护' }
    @{ Rx = 'Add-MpPreference.*-Exclusion(P|Path|Process|Extension)';                 Why = '为 Defender 添加排除项（让木马免杀）' }
    @{ Rx = 'powershell(\.exe)?["'']?\s+.*(-enc\s+[A-Za-z0-9+/=]{20,}|-encodedcommand\s+[A-Za-z0-9+/=]{20,}|-e\s+[A-Za-z0-9+/=]{20,})'; Why = '编码/加密的 PowerShell 载荷（无文件攻击）' }
    @{ Rx = 'powershell(\.exe)?["'']?\s+.*(downloadstring|downloaddata|downloadfile|invoke-webrequest|iwr|invoke-restmethod)'; Why = 'PowerShell 联网下载载荷' }
    @{ Rx = 'powershell(\.exe)?["'']?\s+.*(iex|invoke-expression)';                   Why = 'Invoke-Expression 动态执行' }
    @{ Rx = 'mshta(\.exe)?["'']?\s+(https?://|javascript:|vbscript:)';                Why = 'mshta 加载远程脚本（白名单程序滥用）' }
    @{ Rx = 'regsvr32(\.exe)?["'']?\s+.*(scrobj|/i:https?://|\.sct\b)';               Why = 'regsvr32 远程 scriptlet 载荷' }
    @{ Rx = 'certutil(\.exe)?["'']?\s+.*(-urlcache|-decode|-f\s+https?://)';         Why = 'certutil 下载/解码载荷' }
    @{ Rx = '(curl|wget)(\.exe)?["'']?\s+.*\|\s*(powershell|cmd(\.exe)?|iex)';        Why = '下载即执行（curl 管道 shell）' }
    @{ Rx = 'comsvcs(\.exe)?\s+.*MiniDump.*(74024|72024)';                            Why = 'comsvcs 转储 LSASS（凭据窃取）' }
)

function Get-ThreatAssessment {
    param([string]$Name, [string]$Cmd, [string]$Path, [int]$ProcPid, [int]$ParentProcId, [string]$ParentName)
    if (-not $Name) { return $null }
    $cmdLower = ("$Cmd").ToLower()

    # 系统进程伪装：名字像系统进程但不在系统目录
    $sysNames = 'svchost|lsass|csrss|winlogon|wininit|services|smss|dwm|spoolsv|taskhostw|runtimebroker|sihost|ctfmon'
    if ($Name -match "(?i)^($sysNames)(\.exe)?$" -and $Path -and -not (Is-SystemPath $Path)) {
        return @{ Level='FATAL'; Why = '系统进程伪装（' + $Name + ' 不在 System32）' }
    }
    # 父进程异常：Office/浏览器派生脚本引擎
    if ($ParentName -match '(?i)^(winword|excel|powerpnt|outlook|wps|et|wpp|msaccess|iexplore|chrome|msedge|firefox)\.exe$' -and $Name -match '(?i)^(powershell|cmd|wscript|cscript|mshta|rundll32|regsvr32)\.exe$') {
        return @{ Level='FATAL'; Why = 'Office/浏览器派生脚本宿主（钓鱼文档执行链）' }
    }
    foreach ($p in $script:FatalPatterns) {
        if ($cmdLower -match $p.Rx) { return @{ Level='FATAL'; Why = $p.Why } }
    }
    # ---- 以下为启发式规则：必须先排除微软签名与已知良好目录，避免误伤正常组件 ----
    if (Test-KnownGoodPath $Path) { return $null }
    if (Test-ValidSignature $Path) { return $null }
    # 诱导名（Update/Setup/Service 一类李鬼名字）+ 用户可写目录
    if ($Path -and -not (Is-SystemPath $Path) -and $Path -match '(?i)\\(temp|tmp|appdata|public|programdata|downloads|recent|desktop|documents)\\' -and $Name -match '(?i)(update|setup|install|patch|service|helper|runtime|driver|crash|report|svc)[^\\]*\.exe$') {
        return @{ Level='HIGH'; Why = '用户可写目录的诱导名可执行文件（李鬼更新/服务程序）' }
    }
    # 随机名 + 用户可写目录（最常见投放特征）：元音比极低，或 数字多+大小写频繁跳变
    $leaf = ''
    try { $leaf = [System.IO.Path]::GetFileNameWithoutExtension($Path) } catch { }
    if ($leaf -and $leaf.Length -ge 7 -and $leaf -cmatch '^[A-Za-z0-9]{7,}$' -and $Path -match '(?i)\\(temp|tmp|appdata|public|programdata|downloads|recent|desktop|documents)\\' -and -not (Is-SystemPath $Path)) {
        $vowelCount = ([regex]::Matches($leaf, '[aeiouAEIOU]')).Count
        $vowelRatio = $vowelCount / $leaf.Length
        $digitCount  = ([regex]::Matches($leaf, '\d')).Count
        $caseJumps   = ([regex]::Matches($leaf, '[a-z][A-Z]|[A-Z][a-z]')).Count
        if ($vowelRatio -lt 0.2 -or ($digitCount -ge 2 -and $caseJumps -ge 2)) {
            return @{ Level='FATAL'; Why = '随机名可执行文件位于用户可写目录（典型投放）' }
        }
    }
    return $null
}

# ---------- 隔离 ----------
$Quarantine = Join-Path $env:LOCALAPPDATA 'MhyunBox\Quarantine'

function Protect-Sample([string]$Path, [string]$Name, [string]$Why) {
    if (-not $Path -or -not (Test-Path -LiteralPath $Path)) { return }
    # 安全护栏：系统目录或微软有效签名的文件只记录不隔离
    if ((Is-SystemPath $Path) -or (Test-ValidSignature $Path)) {
        Lk "样本不隔离（系统目录/微软签名，仅击杀）: $Path"
        return
    }
    $leaf = ([System.IO.Path]::GetFileName($Path)) + "." + (Get-Date).ToString('yyyyMMdd_HHmmss') + '.quarantined'
    $dest = Join-Path $Quarantine $leaf
    try {
        New-Item -ItemType Directory -Force -Path $Quarantine | Out-Null
        Copy-Item -LiteralPath $Path -Destination $dest -Force -ErrorAction Stop
        try { Rename-Item -LiteralPath $Path -NewName ([System.IO.Path]::GetFileName($Path) + '.quarantined') -Force -ErrorAction SilentlyContinue } catch { }
        Lk "样本已隔离: $dest"
    } catch {
        L "样本隔离失败（文件可能被占用，已击杀进程）: $Path"
    }
}

function Stop-ThreatProcess {
    param([int]$ProcPid, [string]$Name, [string]$Path, [string]$Why)
    if (Test-CriticalProc $Name $ProcPid) { return $false }
    Lk "强杀: $Name PID=$ProcPid ($Why) 路径=$Path"
    try { taskkill.exe /PID $ProcPid /T /F *> $null } catch { }
    try { Stop-Process -Id $ProcPid -Force -ErrorAction SilentlyContinue } catch { }
    $script:Killed++
    Protect-Sample -Path $Path -Name $Name -Why $Why
    Note "已强杀 $Name (PID $ProcPid)：$Why"
    return $true
}

# ---------- 一、断网止血 ----------
function Invoke-NetworkBlock {
    Lk '---- 断网止血：防火墙全规则阻断出站/入站 ----'
    try {
        if (Get-Command Get-NetFirewallRule -ErrorAction SilentlyContinue) {
            # 记录原策略，restore 时还原
            try {
                $states = Get-NetFirewallProfile | ForEach-Object { "$($_.Name)=$($_.DefaultInboundAction)/$($_.DefaultOutboundAction)" }
                Set-Content -LiteralPath $FwStateFile -Value $states -Encoding UTF8
            } catch { }
            if (-not (Get-NetFirewallRule -DisplayName $FwRuleName -ErrorAction SilentlyContinue)) {
                New-NetFirewallRule -DisplayName $FwRuleName -Direction Outbound -Action Block -Enabled True -Profile Any -RemoteAddress Any | Out-Null
                New-NetFirewallRule -DisplayName $FwRuleName -Direction Inbound  -Action Block -Enabled True -Profile Any -RemoteAddress Any | Out-Null
                Lk '已添加应急防火墙规则：阻断全部出站与入站（restore 模式可恢复）'
            } else {
                L '应急防火墙规则已存在'
            }
        } else {
            netsh advfirewall set allprofiles firewallpolicy blockinbound,blockoutbound | Out-Null
            Lk '已通过 netsh 阻断全部出站与入站（restore 模式可恢复）'
        }
    } catch {
        L '防火墙阻断失败：' + $_.Exception.Message
    }
    # 顺手停掉常见的外联通道（远程桌面/任意位置网络发现），降低横向移动面
    foreach ($svc in @('RemoteRegistry','sshd','termservice')) {
        try { Stop-Service -Name $svc -Force -ErrorAction SilentlyContinue } catch { }
    }
    Lk '已关闭远程注册表/SSH/远程桌面服务（降低横向移动面）'
}

# ---------- 二、强杀 ----------
function Invoke-MassKill {
    L '---- 强杀可疑进程（白名单外命中即击杀+隔离）----'
    $procs = @()
    try { $procs = @(Get-CimInstance Win32_Process -ErrorAction SilentlyContinue) } catch { return }
    foreach ($proc in $procs) {
        $pid2 = [int]$proc.ProcessId
        $name = "$($proc.Name)"
        $path = "$($proc.ExecutablePath)"
        $parentName = ''
        if ($proc.ParentProcessId) {
            try {
                $pp = Get-CimInstance Win32_Process -Filter "ProcessId=$($proc.ParentProcessId)" -ErrorAction SilentlyContinue
                if ($pp) { $parentName = $pp.Name }
            } catch { }
        }
        if (Test-CriticalProc $name $pid2 $path) { continue }
        if (Is-SystemPath $path -and (Test-ValidSignature $path)) { continue }
        if (Test-KnownGoodPath $path) { continue }
        $asmt = Get-ThreatAssessment -Name $name -Cmd "$($proc.CommandLine)" -Path $path -ProcPid $pid2 -ParentProcId ([int]$proc.ParentProcessId) -ParentName $parentName
        if ($asmt) {
            Stop-ThreatProcess -ProcPid $pid2 -Name $name -Path $path -Why $asmt.Why
        }
    }
    Lk ("强杀完成：本次击杀 {0} 个可疑进程" -f $script:Killed)
}

# ---------- 三、冻结自启动 ----------
$script:RunKeys = @(
    'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run',
    'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce',
    'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run',
    'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce',
    'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run',
    'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon',
    'HKCU:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon'
)

function Protect-AutorunKey([string]$KeyPath, [string]$ValueName) {
    try {
        $item = Get-ItemProperty -LiteralPath $KeyPath -Name $ValueName -ErrorAction SilentlyContinue
        if (-not $item) { return }
        $orig = "$($item.$ValueName)"
        # 只冻结指向用户可写目录/可疑命令的值；系统组件原样保留
        if ($orig -match '(?i)^[a-z]:\\windows\\' -and $orig -notmatch '(?i)\(temp|tmp|appdata|public|programdata)') { return }
        if ($orig -notmatch '(?i)\(temp|tmp|appdata|public|programdata|downloads|recent|\.ps1|\.vbs|\.js|\.bat|powershell|mshta|rundll32|regsvr32|certutil|bitsadmin|wscript|cscript)') { return }
        $frozen = $orig + $FrozenTag
        Set-ItemProperty -LiteralPath $KeyPath -Name $ValueName -Value $frozen -ErrorAction Stop
        $script:Frozen++
        Lk "已冻结启动项 [$KeyPath] $ValueName => $frozen"
        Note "冻结自启动项 $ValueName：$orig"
    } catch {
        L "冻结启动项失败 [$KeyPath] $ValueName ：$($_.Exception.Message)"
    }
}

function Invoke-FreezeAutoruns {
    L '---- 冻结自启动持久化（可恢复）----'
    foreach ($key in $script:RunKeys) {
        if (-not (Test-Path -LiteralPath $key)) { continue }
        try {
            $props = (Get-Item -LiteralPath $key).Property
            foreach ($vn in $props) { Protect-AutorunKey -KeyPath $key -ValueName $vn }
        } catch { }
    }
    # 计划任务：禁用非微软的
    try {
        $tasks = Get-ScheduledTask -ErrorAction SilentlyContinue | Where-Object { $_.TaskPath -notmatch '^\\Microsoft\\' -and $_.State -ne 'Disabled' }
        foreach ($t in $tasks) {
            try {
                $act = ($t.Actions | ForEach-Object { "$($_.Execute) $($_.Arguments)" }) -join ' '
                if ($act -match '(?i)\(temp|tmp|appdata\\local\\temp|users\\public|programdata|powershell|mshta|rundll32|regsvr32|certutil|\.ps1|\.vbs|\.bat)') {
                    Disable-ScheduledTask -TaskPath $t.TaskPath -TaskName $t.TaskName -ErrorAction Stop | Out-Null
                    $script:Frozen++
                    Lk "已禁用可疑计划任务: $($t.TaskPath)$($t.TaskName)"
                    Note "禁用计划任务 $($t.TaskName)：$act"
                }
            } catch { }
        }
    } catch { }
    Lk ("冻结完成：共冻结/禁用 {0} 个持久化点（restore 模式可解冻）" -f $script:Frozen)
}

# ---------- 四、系统守护（防止"阻拦"变"搞崩"） ----------
function Start-SystemWatchdog {
    L '---- 启动系统守护：盯防关键进程崩溃导致系统不可用 ----'
    $script:Watchdog = $true
    while ($script:Watchdog) {
        try {
            $exp = Get-Process -Name explorer -ErrorAction SilentlyContinue
            if (-not $exp) {
                L '检测到 explorer 桌面进程不存在，正在自动重启（防止系统看起来像崩溃）'
                Start-Process explorer.exe
            }
            foreach ($svc in @('Themes','UxSms','ProfSvc','Schedule','wuauserv')) {
                try {
                    $s = Get-Service -Name $svc -ErrorAction SilentlyContinue
                    if ($s -and $s.Status -ne 'Running' -and $s.StartType -ne 'Disabled') {
                        Start-Service -Name $svc -ErrorAction SilentlyContinue
                        L "系统服务 $svc 未运行，已尝试启动"
                    }
                } catch { }
            }
        } catch { }
        Start-Sleep -Seconds 5
    }
}

# ---------- 五、取证 ----------
function Export-Forensics {
    $stamp = (Get-Date).ToString('yyyyMMdd_HHmmss')
    $dir = Join-Path $env:LOCALAPPDATA ("MhyunBox\Incident_" + $stamp)
    New-Item -ItemType Directory -Force -Path $dir | Out-Null
    L "---- 导出取证信息到 $dir ----"
    try {
        Get-CimInstance Win32_Process | Select-Object ProcessId,ParentProcessId,Name,ExecutablePath,CommandLine |
            Export-Csv -LiteralPath (Join-Path $dir 'processes.csv') -NoTypeInformation -Encoding UTF8
        Get-NetTCPConnection -ErrorAction SilentlyContinue |
            Select-Object LocalAddress,LocalPort,RemoteAddress,RemotePort,State,OwningProcess |
            Export-Csv -LiteralPath (Join-Path $dir 'tcp_connections.csv') -NoTypeInformation -Encoding UTF8
        Get-CimInstance Win32_Service | Select-Object Name,DisplayName,State,StartMode,PathName |
            Export-Csv -LiteralPath (Join-Path $dir 'services.csv') -NoTypeInformation -Encoding UTF8
        try {
            reg export 'HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Run' (Join-Path $dir 'autoruns_hklm.reg') /y 2>$null | Out-Null
            reg export 'HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\Run' (Join-Path $dir 'autoruns_hkcu.reg') /y 2>$null | Out-Null
        } catch { }
        Get-ScheduledTask -ErrorAction SilentlyContinue | Select-Object TaskPath,TaskName,State |
            Export-Csv -LiteralPath (Join-Path $dir 'scheduled_tasks.csv') -NoTypeInformation -Encoding UTF8
        Copy-Item -LiteralPath $GuardLog -Destination (Join-Path $dir 'emergency.log') -Force -ErrorAction SilentlyContinue
    } catch {
        L '取证导出部分失败：' + $_.Exception.Message
    }
    Lk '取证完成（进程/网络连接/服务/启动项/计划任务）'
    return $dir
}

# ---------- 六、恢复 ----------
function Invoke-Restore {
    L '---- 恢复应急响应改动 ----'
    # 停止系统守护进程
    try {
        $wds = Get-CimInstance Win32_Process -Filter "Name='powershell.exe'" -ErrorAction SilentlyContinue |
               Where-Object { "$($_.CommandLine)" -match '应急响应\.ps1' -and "$($_.CommandLine)" -match '(?i)-Mode\s+watchdog' }
        foreach ($wd in $wds) {
            try { taskkill.exe /PID $wd.ProcessId /T /F *> $null; L "已停止系统守护进程 PID=$($wd.ProcessId)" } catch { }
        }
    } catch { }
    try {
        if (Get-Command Get-NetFirewallRule -ErrorAction SilentlyContinue) {
            Get-NetFirewallRule -DisplayName $FwRuleName -ErrorAction SilentlyContinue | Remove-NetFirewallRule -ErrorAction SilentlyContinue
            Lk '已删除应急防火墙规则'
        }
        if (Test-Path -LiteralPath $FwStateFile) {
            $lines = Get-Content -LiteralPath $FwStateFile -ErrorAction SilentlyContinue
            foreach ($ln in $lines) {
                if ($ln -match '^(.+)=(.+)/(.+)$') {
                    try {
                        netsh advfirewall set profile name="$($matches[1])" firewallpolicy "$($matches[2]),$($matches[3])" 2>$null | Out-Null
                    } catch { }
                }
            }
            Remove-Item -LiteralPath $FwStateFile -Force -ErrorAction SilentlyContinue
            Lk '已还原防火墙默认策略'
        }
    } catch { L '防火墙恢复失败：' + $_.Exception.Message }
    # 解冻启动项
    foreach ($key in $script:RunKeys) {
        if (-not (Test-Path -LiteralPath $key)) { continue }
        try {
            $props = (Get-Item -LiteralPath $key).Property
            foreach ($vn in $props) {
                $item = Get-ItemProperty -LiteralPath $key -Name $vn -ErrorAction SilentlyContinue
                if ($item -and "$($item.$vn)" -match '\' + $FrozenTag + '$') {
                    $restored = "$($item.$vn)" -replace ($FrozenTag + '$'), ''
                    Set-ItemProperty -LiteralPath $key -Name $vn -Value $restored -ErrorAction SilentlyContinue
                    Lk "已解冻启动项 [$key] $vn"
                }
            }
        } catch { }
    }
    # 重新启用被禁用的可疑计划任务由用户按需处理（列出清单）
    Lk '恢复完成。被禁用的计划任务如有需要请在任务计划程序里手动启用（名单见本日志）。'
}

# ---------- 七、开启 Defender ----------
function Enable-Defender {
    if (-not $script:Profile.HasDefenderModule) {
        L '当前系统没有 Defender 管理模块（可能被第三方安全软件替换）。请使用已安装的第三方杀毒软件。'
        return
    }
    L '---- 尝试开启 Windows 安全中心（Defender）实时防护 ----'
    try {
        Set-MpPreference -DisableRealtimeMonitoring $false -ErrorAction Stop
        L '已开启实时监控'
    } catch { L 'Set-MpPreference 失败：' + $_.Exception.Message }
    try {
        Remove-ItemProperty -Path 'HKLM:\SOFTWARE\Policies\Microsoft\Windows Defender' -Name 'DisableAntiSpyware' -ErrorAction SilentlyContinue
        Set-ItemProperty -Path 'HKLM:\SOFTWARE\Policies\Microsoft\Windows Defender' -Name 'DisableAntiSpyware' -Value 0 -ErrorAction SilentlyContinue
    } catch { }
    try {
        Set-Service -Name WinDefend -StartupType Automatic -ErrorAction SilentlyContinue
        Start-Service -Name WinDefend -ErrorAction SilentlyContinue
        Set-Service -Name WdNisSvc -StartupType Automatic -ErrorAction SilentlyContinue
        Start-Service -Name WdNisSvc -ErrorAction SilentlyContinue
        L 'Defender 服务已设为自动并尝试启动'
    } catch { L 'Defender 服务操作失败：' + $_.Exception.Message }
    L '提示：部分木马会篡改安全中心，若开启后仍显示关闭，请重启电脑再检查。'
}

# ---------- 主流程 ----------
if (-not (Test-Admin)) {
    $argList = @('-NoProfile','-ExecutionPolicy','Bypass','-File',"`"$PSCommandPath`"",'-Mode',"`"$Mode`"")
    if ($Force)  { $argList += '-Force' }
    if ($Silent) { $argList += '-Silent' }
    if ($LogFile){ $argList += @('-LogFile',"`"$LogFile`"") }
    try { Start-Process -FilePath 'powershell.exe' -Verb RunAs -ArgumentList $argList | Out-Null; exit 0 }
    catch { L '未获得管理员授权（UAC 被拒绝），应急响应需要管理员权限。'; exit 1 }
}

if ($Mode -eq 'watchdog') {
    # 隐藏的内部模式：由 stop/standard/deep 收尾时分离出来的守护进程，防止桌面/关键服务被木马连带打崩
    Start-SystemWatchdog
    exit 0
}

Lk "================ 应急响应启动 Mode=$Mode ================"
Show-Profile

switch ($Mode) {
    'status' {
        # 守卫与防护状态
        $guardTask = Get-ScheduledTask -TaskName 'MhyunBoxCommandGuard' -ErrorAction SilentlyContinue
        Lk ("命令守卫计划任务: {0}" -f $(if ($guardTask) { $guardTask.State } else { '未安装' }))
        $defStatus = '不可用'
        if ($script:Profile.HasDefenderModule) {
            try { $defStatus = [string](Get-MpComputerStatus -ErrorAction Stop).RealTimeProtectionEnabled } catch { }
        }
        Lk "Defender 实时防护: $defStatus"
        Lk "状态查询完成"
    }
    'defender-on' { Enable-Defender }
    'restore'     { Invoke-Restore }
    'stop' {
        Invoke-NetworkBlock
        Invoke-MassKill
        Invoke-FreezeAutoruns
    }
    'standard' {
        Invoke-NetworkBlock
        Invoke-MassKill
        Invoke-FreezeAutoruns
        L '---- 调用下一代威胁扫描做全项检测 ----'
        $evo = Join-Path $DirSelf '威胁进化扫描.ps1'
        if (Test-Path -LiteralPath $evo) {
            & $evo -AutoRun -Silent -LogFile $GuardLog
        } else { L '未找到 威胁进化扫描.ps1' }
        if ($script:Profile.HasDefenderModule) {
            L '---- 启动 Defender 快速扫描 ----'
            try { Start-MpScan -ScanType QuickScan -ErrorAction Stop } catch { L 'Defender 快速扫描启动失败：' + $_.Exception.Message }
        }
    }
    'deep' {
        Invoke-NetworkBlock
        Invoke-MassKill
        Invoke-FreezeAutoruns
        L '---- 深度检查：常见投放目录全枚举 ----'
        $dropDirs = @("$env:TEMP","$env:LOCALAPPDATA\Temp","$env:APPDATA","$env:LOCALAPPDATA","$env:PUBLIC","$env:ProgramData","$env:USERPROFILE\Downloads","$env:USERPROFILE\Desktop","$env:WINDIR\Temp","$env:WINDIR\Tasks")
        foreach ($d in $dropDirs) {
            if (-not (Test-Path -LiteralPath $d)) { continue }
            try {
                $files = Get-ChildItem -LiteralPath $d -File -ErrorAction SilentlyContinue | Where-Object { $_.Extension -match '(?i)\.(exe|dll|scr|cpl|bat|cmd|ps1|vbs|js|hta|msi|jar|reg)$' }
                foreach ($f in $files) {
                    if (Test-ValidSignature $f.FullName) { continue }
                    Note "投放目录存在未签名可执行/脚本: $($f.FullName) ($($f.Length) 字节, $($f.LastWriteTime))"
                }
            } catch { }
        }
        L '---- 深度检查：引导与固件 ----'
        try {
            $bcd = bcdedit /enum '{current}' 2>$null | Out-String
            if ($bcd -match '(?i)testsigning\s+yes') { Note 'bcdedit 测试模式已开启（可能被用来加载未签名驱动）' }
            if ($bcd -match '(?i)nointegritychecks\s+yes') { Note 'bcdedit 完整性检查已关闭（内核防护被旁路）' }
        } catch { }
        $evo = Join-Path $DirSelf '威胁进化扫描.ps1'
        if (Test-Path -LiteralPath $evo) { & $evo -AutoRun -Silent -LogFile $GuardLog }
        $dir = Export-Forensics
        Lk ("深度检查完成。取证目录: {0}" -f $dir)
    }
}

# 止血后分离出系统守护进程（独立隐藏进程，5 秒巡检 explorer/关键服务，防止桌面被打崩）
if ($Mode -in @('stop','standard','deep')) {
    try {
        $wdAlive = Get-CimInstance Win32_Process -Filter "Name='powershell.exe'" -ErrorAction SilentlyContinue |
                   Where-Object { "$($_.CommandLine)" -match '应急响应\.ps1' -and "$($_.CommandLine)" -match '(?i)-Mode\s+watchdog' }
        if (-not $wdAlive) {
            Start-Process -FilePath 'powershell.exe' -ArgumentList @('-NoProfile','-ExecutionPolicy','Bypass','-WindowStyle','Hidden','-File', "`"$PSCommandPath`"", '-Mode','watchdog','-Silent') -WindowStyle Hidden | Out-Null
            Lk '系统守护进程已挂载（后台隐藏运行，巡检桌面与关键服务；应急结束后运行 restore 或重启电脑即会自动结束）'
        } else {
            Lk '系统守护进程已在运行'
        }
    } catch {
        Lk '系统守护进程挂载失败（不影响已完成的止血操作）'
    }
}

Lk ("================ 应急响应结束：击杀 {0} 进程 / 冻结 {1} 持久化点 / 发现 {2} 项 ================" -f $script:Killed, $script:Frozen, $script:Findings.Count)
if ($script:Findings.Count -gt 0) {
    Lk '---- 发现清单 ----'
    foreach ($f in $script:Findings) { Lk "  * $f" }
}
exit 0
