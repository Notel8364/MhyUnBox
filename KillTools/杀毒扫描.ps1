# MhyunBox 杀毒扫描（主力扫描 · 手动触发）
# 用法: powershell -NoProfile -ExecutionPolicy Bypass -File 杀毒扫描.ps1 [-AutoRun] [-NoDefenderUpdate] [-LogFile 路径]
#   -AutoRun          供程序无交互调用：不弹询问，发现威胁时自动调用同目录「威胁清理.ps1 -Mode kill」清理
#   -NoDefenderUpdate 跳过 Defender 病毒库更新（离线/弱网环境）
#   -LogFile 路径     逐行输出同时追加到该日志文件
# 退出码: 0=未发现威胁  2=发现威胁（已提示/已调用威胁清理）  1=运行出错
# 扫描内容: Defender 病毒库更新 + MpCmdRun 快速扫描 + 自研特征
#          （随机名/乱码名进程 + 高CPU/外联、可疑 Run 启动项、随机命名/伪装 Edge 计划任务、Public/PF86/ProgramData 载荷目录、特征库样本比对）

param(
    [switch]$AutoRun,
    [switch]$NoDefenderUpdate,
    [string]$LogFile = ''
)

[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$OutputEncoding = [System.Text.Encoding]::UTF8
$ErrorActionPreference = 'Continue'

$script:LogPath = $LogFile
$script:Threats = New-Object System.Collections.Generic.List[string]

function L([string]$Message) {
    $line = (Get-Date -Format 'HH:mm:ss.fff') + '  ' + $Message
    Write-Output $line
    if ($script:LogPath) { try { Add-Content -LiteralPath $script:LogPath -Value $line -Encoding UTF8 } catch { } }
}

function Add-Threat([string]$item) {
    $script:Threats.Add($item)
    L "[威胁] $item"
}

function Test-Admin {
    $id = [System.Security.Principal.WindowsIdentity]::GetCurrent()
    $p = New-Object System.Security.Principal.WindowsPrincipal($id)
    return $p.IsInRole([System.Security.Principal.WindowsBuiltInRole]::Administrator)
}

# 自提权：交互模式未提权时弹 UAC；-AutoRun 模式不弹窗（由调用方程序先提权），降级继续
if (-not (Test-Admin)) {
    if (-not $AutoRun) {
        L '未以管理员运行，正在请求 UAC 提权...'
        $argList = @('-NoProfile','-ExecutionPolicy','Bypass','-File',"`"$PSCommandPath`"")
        if ($NoDefenderUpdate) { $argList += '-NoDefenderUpdate' }
        if ($LogFile) { $argList += @('-LogFile',"`"$LogFile`"") }
        try { Start-Process -FilePath 'powershell.exe' -Verb RunAs -ArgumentList $argList | Out-Null; exit 0 }
        catch { L '【失败】未获得管理员授权（UAC 被拒绝）。'; exit 1 }
    } else {
        L '【警告】-AutoRun 模式下未以管理员运行：Defender 更新/扫描可能受限，自研特征扫描仍将执行。'
    }
}

# ---------- 特征判定 ----------
$script:RandomDirWhitelist = @('Desktop','Documents','Downloads','Music','Pictures','Videos','AccountPictures','Libraries')

function Test-RandomMixedName([string]$name) {
    $base = [System.IO.Path]::GetFileNameWithoutExtension($name)
    if ([string]::IsNullOrEmpty($base)) { return $false }
    if ($base -notmatch '^[A-Za-z0-9]{5,10}$') { return $false }
    if ($base -match '^[0-9]+$') { return $false }
    $hasUpper = $false; $hasLower = $false; $innerUpper = $false
    for ($i = 0; $i -lt $base.Length; $i++) {
        $c = $base[$i]
        if ([char]::IsUpper($c)) { $hasUpper = $true; if ($i -gt 0) { $innerUpper = $true } }
        elseif ([char]::IsLower($c)) { $hasLower = $true }
    }
    return ($hasUpper -and $hasLower -and $innerUpper)
}

function Test-GarbledName([string]$name) {
    if ([string]::IsNullOrWhiteSpace($name)) { return $false }
    if ($name -match "\uFFFD|锟斤拷|\u25A0|\u25A1") { return $true }
    $base = [System.IO.Path]::GetFileNameWithoutExtension($name)
    if ([string]::IsNullOrEmpty($base)) { return $false }
    $normal = 0
    foreach ($ch in $base.ToCharArray()) {
        if ([char]::IsLetterOrDigit($ch) -or $ch -eq '_' -or $ch -eq '-' -or $ch -eq ' ') { $normal++ }
    }
    return (($normal / [Math]::Max(1, $base.Length)) -lt 0.5)
}

function Extract-ExePath([string]$data) {
    $data = $data.Trim()
    if ($data -match '"([^"]+)"') { return $Matches[1].Trim() }
    return (($data -split '\s+')[0]).TrimEnd(')')
}

# ---------- 1) Defender 病毒库更新 ----------
function Get-MpCmdRun {
    $pf = Join-Path $env:ProgramFiles 'Windows Defender\MpCmdRun.exe'
    if (Test-Path -LiteralPath $pf) { return $pf }
    $plat = Join-Path $env:ProgramData 'Microsoft\Windows Defender\Platform'
    if (Test-Path -LiteralPath $plat) {
        $latest = Get-ChildItem -LiteralPath $plat -Directory -ErrorAction SilentlyContinue | Sort-Object Name -Descending | Select-Object -First 1
        if ($latest) {
            $c = Join-Path $latest.FullName 'MpCmdRun.exe'
            if (Test-Path -LiteralPath $c) { return $c }
        }
    }
    return $null
}

if ($NoDefenderUpdate) {
    L '[跳过] -NoDefenderUpdate：不更新 Defender 病毒库'
} else {
    L '—— 步骤 1/4：更新 Defender 病毒库 ——'
    $job = Start-Job -ScriptBlock {
        try { Update-MpSignature -ErrorAction Stop; 'OK' } catch { 'FAIL: ' + $_.Exception.Message }
    }
    if (Wait-Job $job -Timeout 180) {
        $r = [string](Receive-Job $job)
        if ($r -eq 'OK') { L '[完成] Defender 病毒库已更新（或已是最新）' } else { L "[警告] 病毒库更新失败: $r" }
    } else {
        Stop-Job $job -ErrorAction SilentlyContinue
        L '[警告] 病毒库更新超时（180 秒），已跳过'
    }
    Remove-Job $job -Force -ErrorAction SilentlyContinue
}

# ---------- 2) Defender 快速扫描（MpCmdRun） ----------
L '—— 步骤 2/4：Defender 快速扫描（MpCmdRun -Scan -ScanType 1）——'
$mp = Get-MpCmdRun
if ($mp) {
    try {
        $out = & $mp -Scan -ScanType 1 2>&1
        foreach ($line in @($out)) { if ([string]$line) { L "  $line" } }
    } catch { L "[警告] MpCmdRun 扫描失败: $($_.Exception.Message)" }
    try {
        $dets = @(Get-MpThreatDetection -ErrorAction SilentlyContinue | Where-Object { $_.InitialDetectionTime -gt (Get-Date).AddDays(-7) })
        foreach ($d in $dets) {
            $name = ''
            try { $t = Get-MpThreat -ThreatID $d.ThreatID -ErrorAction SilentlyContinue; if ($t) { $name = $t.ThreatName } } catch { }
            if (-not $name) { $name = "ThreatID $($d.ThreatID)" }
            foreach ($res in @($d.Resources)) { Add-Threat "Defender 检测: $name => $res" }
        }
        if ($dets.Count -eq 0) { L 'Defender 近 7 天无威胁记录' }
    } catch { }
} else {
    L '[警告] 未找到 MpCmdRun.exe（Defender 可能被卸载/禁用），跳过官方引擎扫描'
}

# ---------- 3) 自研特征扫描 ----------
L '—— 步骤 3/4：自研特征扫描 ——'

# 3.1 随机名/乱码名 + 高CPU/外联/可写目录/无签名 进程
L '扫描：随机名/乱码名高占用进程 ...'
$writable = @($env:PUBLIC, $env:ProgramData, (Join-Path $env:LOCALAPPDATA ''), $env:TEMP)
$sysWhitelist = @('system','idle','registry','memcompression','svchost','csrss','services','lsass','winlogon','smss','wininit','dwm','explorer','conhost','msmpeng','nissrv','securityhealthservice','audiodg','runtimebroker','taskhostw','fontdrvhost','sihost','ctfmon','spoolsv','searchindexer','dllhost')
$procs = @(Get-Process -ErrorAction SilentlyContinue)
$t1 = @{}
foreach ($p in $procs) { try { $t1[$p.Id] = $p.TotalProcessorTime.TotalMilliseconds } catch { } }
Start-Sleep -Milliseconds 800
$cpuCount = [Math]::Max(1, [Environment]::ProcessorCount)
$randomProcCount = 0
foreach ($p in $procs) {
    try { $path = $p.Path } catch { $path = $null }
    if (-not $path) { continue }
    if ($sysWhitelist -contains $p.ProcessName.ToLower()) { continue }
    $random = Test-RandomMixedName $p.ProcessName
    $garbled = Test-GarbledName $p.ProcessName
    if (-not $random -and -not $garbled) { continue }
    $cpu = 0.0
    try { if ($t1.ContainsKey($p.Id)) { $p.Refresh(); $d = $p.TotalProcessorTime.TotalMilliseconds - $t1[$p.Id]; $cpu = [Math]::Round(($d / 800.0) / $cpuCount * 100.0, 1) } } catch { }
    $conns = 0
    try { $conns = @(Get-NetTCPConnection -OwningProcess $p.Id -ErrorAction SilentlyContinue).Count } catch { }
    $sig = Get-AuthenticodeSignature -LiteralPath $path -ErrorAction SilentlyContinue
    if ($sig -and $sig.Status -eq 'Valid' -and $sig.SignerCertificate) { continue }
    $low = $path.ToLower(); $inWritable = $false
    foreach ($w in $writable) { if ($w -and $low.StartsWith($w.ToLower())) { $inWritable = $true; break } }
    if (-not ($inWritable -or $cpu -ge 40 -or $conns -ge 15)) { continue }
    $reasons = @()
    if ($random) { $reasons += '随机混合大小写名' } elseif ($garbled) { $reasons += '乱码名' }
    if ($inWritable) { $reasons += '位于可写目录' }
    if (-not ($sig -and $sig.Status -eq 'Valid')) { $reasons += '无有效签名' }
    if ($cpu -ge 40) { $reasons += "CPU ${cpu}%" }
    if ($conns -ge 15) { $reasons += "外联连接 $conns" }
    Add-Threat "随机名进程: $($p.ProcessName) (PID $($p.Id)) [$($reasons -join '/')] => $path"
    $randomProcCount++
}
if ($randomProcCount -eq 0) { L '未发现随机名/乱码名可疑进程' }

# 3.2 载荷目录：Public / Program Files (x86) 随机短名目录；ProgramData hex 目录与 hex.exe
L '扫描：Public/PF86/ProgramData 随机名载荷目录 ...'
function Get-ScanPayloadDirs {
    $out = @()
    $sysDirs = @('USOPrivate','USOShared','DiagTrack','Microsoft','Packages','Windows','DeliveryOptimization','WindowsHolographicDevices')
    $roots = @(
        @{ p = $env:PUBLIC; hex = $false },
        @{ p = ${env:ProgramFiles(x86)}; hex = $false },
        @{ p = $env:ProgramData; hex = $true }
    )
    foreach ($r in $roots) {
        if (-not $r.p -or -not (Test-Path -LiteralPath $r.p)) { continue }
        foreach ($d in @(Get-ChildItem -LiteralPath $r.p -Directory -Force -ErrorAction SilentlyContinue)) {
            $hit = $false; $isHex = ($d.Name -match '^[0-9a-fA-F]{6,12}$')
            if ($r.hex -and $isHex) { $hit = $true }
            if (-not $hit -and (Test-RandomMixedName $d.Name) -and ($script:RandomDirWhitelist -notcontains $d.Name) -and ($sysDirs -notcontains $d.Name)) { $hit = $true }
            if (-not $hit) { continue }
            $out += [pscustomobject]@{ Path = $d.FullName; IsHex = $isHex }
        }
    }
    return @($out)
}
function Test-DirHasUnsignedPayload([string]$dir) {
    $files = @(Get-ChildItem -LiteralPath $dir -Recurse -Depth 2 -Force -ErrorAction SilentlyContinue |
        Where-Object { $_.Extension -in @('.exe','.dll','.scr','.pif','.bat','.cmd','.vbs','.js','.ps1') } |
        Select-Object -First 12)
    if ($files.Count -eq 0) { return 'empty' }
    foreach ($f in $files) {
        if ($f.Extension -in @('.bat','.cmd','.vbs','.js','.ps1')) { return 'unsigned' }
        $sig = Get-AuthenticodeSignature -LiteralPath $f.FullName -ErrorAction SilentlyContinue
        if (-not $sig -or $sig.Status -ne 'Valid') { return 'unsigned' }
    }
    return $null
}
$payloadDirs = 0
foreach ($d in (Get-ScanPayloadDirs)) {
    $kind = Test-DirHasUnsignedPayload $d.Path
    if (-not $kind) { continue }
    if ($kind -eq 'empty' -and -not $d.IsHex) { continue }
    Add-Threat "载荷目录($kind): $($d.Path)"; $payloadDirs++
}
if ($env:ProgramData -and (Test-Path -LiteralPath $env:ProgramData)) {
    foreach ($f in @(Get-ChildItem -LiteralPath $env:ProgramData -File -Force -ErrorAction SilentlyContinue)) {
        if ($f.Name -notmatch '^[0-9a-fA-F]{6,12}\.exe$') { continue }
        $sig = Get-AuthenticodeSignature -LiteralPath $f.FullName -ErrorAction SilentlyContinue
        if (-not $sig -or $sig.Status -ne 'Valid') { Add-Threat "载荷文件: $($f.FullName)（hex 名 + 无有效签名）"; $payloadDirs++ }
    }
}
if ($payloadDirs -eq 0) { L '未发现随机名载荷目录/文件' }

# 3.3 伪造 SecurityHealth 启动项 + 可疑 Run 启动项
L '扫描：伪造 SecurityHealth / 可疑 Run 启动项 ...'
$runKeys = @(
    'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run',
    'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run',
    'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run'
)
$runHits = 0
foreach ($rk in $runKeys) {
    if (-not (Test-Path $rk)) { continue }
    $it = Get-Item -Path $rk -ErrorAction SilentlyContinue
    if (-not $it) { continue }
    foreach ($n in $it.Property) {
        if ($n -like 'PS*') { continue }
        $val = ''
        try { $val = [string](Get-ItemProperty -Path $rk -Name $n -ErrorAction SilentlyContinue).$n } catch { continue }
        if (-not $val.Trim()) { continue }
        $exe = Extract-ExePath $val
        if (-not $exe) { continue }
        $exe = [Environment]::ExpandEnvironmentVariables($exe)
        # 伪造 SecurityHealth：值名含 SecurityHealth 但未指向系统正版组件
        if ($n -match '(?i)securityhealth') {
            $ok = $false
            foreach ($l in @('securityhealthsystray.exe','securityhealthservice.exe')) {
                if ($exe -ieq (Join-Path $env:WINDIR "System32\$l")) { $ok = $true; break }
            }
            if (-not $ok) { Add-Threat "伪造SecurityHealth启动项: [$rk] $n = $val"; $runHits++ }
            continue
        }
        if (-not ($exe -match '^[A-Za-z]:\\')) { continue }
        if (-not (Test-Path -LiteralPath $exe)) { Add-Threat "启动项指向不存在的程序: [$rk] $n = $val"; $runHits++; continue }
        $low = $exe.ToLower(); $inWritable = $false
        foreach ($w in $writable) { if ($w -and $low.StartsWith($w.ToLower())) { $inWritable = $true; break } }
        if (-not $inWritable) { continue }
        $sig = Get-AuthenticodeSignature -LiteralPath $exe -ErrorAction SilentlyContinue
        if (-not ($sig -and $sig.Status -eq 'Valid')) { Add-Threat "启动项指向可写目录且无签名: [$rk] $n = $val"; $runHits++ }
    }
}
if ($runHits -eq 0) { L '未发现伪造 SecurityHealth / 可疑 Run 启动项' }

# 3.4 随机命名 / 伪装 Edge 更新计划任务（目标 exe 不存在才计为威胁）
L '扫描：随机命名 / 伪装 Edge 更新计划任务 ...'
$taskHits = 0
foreach ($t in @(Get-ScheduledTask -ErrorAction SilentlyContinue)) {
    if (-not $t -or $t.TaskPath -like '\Microsoft\*') { continue }
    if ($t.TaskName -match 'Mhyun') { continue }
    $random = Test-RandomMixedName $t.TaskName
    $fakeUa = ($t.TaskName -like 'MicrosoftEdgeUpdateTaskUA*')
    if (-not $random -and -not $fakeUa) { continue }
    $tag = if ($fakeUa) { '伪装Edge更新任务' } else { '随机命名任务' }
    $missing = $false; $raws = @()
    foreach ($a in $t.Actions) {
        $raw = [string]$a.Execute
        if (-not $raw) { continue }
        $raws += $raw
        $exe = [Environment]::ExpandEnvironmentVariables((Extract-ExePath $raw))
        if ($exe -match '^[A-Za-z]:\\' -and -not (Test-Path -LiteralPath $exe)) { $missing = $true }
    }
    if ($missing) { Add-Threat "${tag}: $($t.TaskPath)$($t.TaskName) -> $($raws -join ' | ')（目标 exe 不存在）"; $taskHits++ }
}
if ($taskHits -eq 0) { L '未发现随机命名/伪装 Edge 计划任务' }

# 3.5 特征库样本比对（特征库.txt：[进程名]/[命令关键字]/[服务关键字]）
L '扫描：特征库样本比对 ...'
$sigFile = Join-Path $PSScriptRoot '特征库.txt'
$sigHits = 0
if (Test-Path -LiteralPath $sigFile) {
    $section = ''
    $names = @(); $cmdKeys = @(); $svcKeys = @()
    foreach ($line in Get-Content -LiteralPath $sigFile -Encoding UTF8) {
        $t = $line.Trim()
        if ($t.StartsWith('[')) { $section = $t; continue }
        if (-not $t -or $t.StartsWith('#')) { continue }
        switch ($section) {
            '[进程名]'     { $names   += $t }
            '[命令关键字]'  { $cmdKeys += $t }
            '[服务关键字]'  { $svcKeys += $t }
        }
    }
    $wprocs = @(Get-CimInstance Win32_Process -ErrorAction SilentlyContinue)
    foreach ($n in $names) {
        $hit = $wprocs | Where-Object { $_.Name -ieq $n -and $_.Path -and -not $_.Path.StartsWith($env:WINDIR, [System.StringComparison]::OrdinalIgnoreCase) }
        if ($hit) { Add-Threat "特征库-进程名: $n (PID $($hit[0].ProcessId), $($hit[0].Path))"; $sigHits++ }
    }
    foreach ($k in $cmdKeys) {
        $rx = [regex]::Escape($k)
        foreach ($p in $wprocs) {
            if (($p.Name -imatch $rx) -or ($p.CommandLine -and $p.CommandLine -imatch $rx)) {
                Add-Threat "特征库-命令关键字: $k (PID $($p.ProcessId))"; $sigHits++
                break
            }
        }
    }
    $svcRoot = 'HKLM:\SYSTEM\CurrentControlSet\Services'
    foreach ($sk in $svcKeys) {
        $rx = '\b' + [regex]::Escape($sk) + '\b'
        foreach ($k in (Get-ChildItem $svcRoot -ErrorAction SilentlyContinue)) {
            $img = $k.GetValue('ImagePath')
            if (($k.PSChildName -imatch $rx) -or ($img -and ([string]$img -imatch $rx))) {
                Add-Threat "特征库-服务关键字: $($k.PSChildName) => $img"; $sigHits++
            }
        }
    }
}
if ($sigHits -eq 0) { L '未命中特征库样本' }

# ---------- 4) 汇总与处置 ----------
L ''
L '=============================================='
if ($script:Threats.Count -eq 0) {
    L '== 扫描完成：未发现威胁 =='
    exit 0
}
L "== 扫描完成：共发现 $($script:Threats.Count) 项威胁 =="
foreach ($t in $script:Threats) { L "  - $t" }
L '=============================================='

$cleanup = Join-Path $PSScriptRoot '威胁清理.ps1'
if (-not (Test-Path -LiteralPath $cleanup)) {
    L '[错误] 未找到同目录 威胁清理.ps1，无法自动清理。'
    exit 2
}

if ($AutoRun) {
    L '== -AutoRun：自动调用 威胁清理.ps1 -Mode kill =='
    if (Test-Admin) {
        & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $cleanup -Mode kill
        $code = $LASTEXITCODE
        if ($code) { L "[提示] 威胁清理退出码: $code" }
    } else {
        $klog = Join-Path $env:TEMP ("MhyunBox_威胁清理_" + (Get-Date -Format 'yyyyMMdd_HHmmss') + '.log')
        $argStr = '-NoProfile -ExecutionPolicy Bypass -File "' + $cleanup + '" -Mode kill -LogFile "' + $klog + '"'
        try {
            Start-Process -FilePath 'powershell.exe' -Verb RunAs -WindowStyle Hidden -ArgumentList $argStr | Out-Null
            L "[提权运行中] 日志: $klog"
            $shown = 0
            $deadline = (Get-Date).AddMinutes(20)
            while ((Get-Date) -lt $deadline) {
                Start-Sleep -Seconds 3
                if (-not (Test-Path -LiteralPath $klog)) { continue }
                $lines = @(Get-Content -LiteralPath $klog -ErrorAction SilentlyContinue)
                for ($i = $shown; $i -lt $lines.Count; $i++) { Write-Output $lines[$i] }
                $shown = $lines.Count
                $raw = [string](Get-Content -LiteralPath $klog -Raw -ErrorAction SilentlyContinue)
                if ($raw -match '== 威胁清理完成 ==') { break }
            }
        } catch {
            L "[取消] 未获得管理员授权或提权失败: $($_.Exception.Message)"
        }
    }
    L '== 已调用威胁清理 =='
    exit 2
}

$ans = Read-Host '发现威胁。是否立即调用「威胁清理.ps1」进行清理？(y=清理 / n=仅退出)'
if ($ans -match '^[Yy]') {
    if (Test-Admin) {
        & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $cleanup -Mode kill
    } else {
        try {
            Start-Process -FilePath 'powershell.exe' -Verb RunAs -ArgumentList @('-NoProfile','-ExecutionPolicy','Bypass','-File',"`"$cleanup`"",'-Mode','kill') | Out-Null
            L '已弹 UAC 提权运行威胁清理（独立窗口）。'
        } catch { L '[取消] 未获得管理员授权。' }
    }
} else {
    L '用户选择暂不清理。'
}
exit 2
