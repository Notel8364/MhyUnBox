# 布防守护脚本 (MhyunBox)
# 由「电脑布防」注册的计划任务周期性调用：即使 MhyunBox 程序没有运行，
# 也会自动检查蜜罐诱饵完整性、防护是否被篡改，并比对特征库样本；
# 发现异常时自动恢复诱饵、写入告警日志，并自动运行威胁清理脚本（仅扫描）。
# 打开 MhyunBox 后可在「布防实时日志」看到守护脚本产生的告警。

$ErrorActionPreference = 'SilentlyContinue'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

$dataDir   = Join-Path $env:LOCALAPPDATA 'MhyunBox\HoneyPots'
$manifest  = Join-Path $dataDir 'honeypots.txt'
$backupDir = Join-Path $dataDir 'Backup'
$alertLog  = Join-Path $dataDir 'defense_alerts.log'
$stateFile = Join-Path $dataDir 'guard_state.txt'
$killScript = Join-Path $PSScriptRoot '威胁清理.ps1'
$sigFile    = Join-Path $PSScriptRoot '特征库.txt'
$guardOpt   = Join-Path $dataDir 'guard_options.txt'

$script:newAlert = $false

New-Item -ItemType Directory -Force -Path $dataDir | Out-Null

# 选项：AutoScan=0 时不自动联动扫描、也不自动添加防火墙阻断（可在 MhyunBox「电脑布防」切换）
$autoScan = $true
if (Test-Path -LiteralPath $guardOpt) {
    $optText = [string](Get-Content -LiteralPath $guardOpt -Raw -Encoding UTF8 -ErrorAction SilentlyContinue)
    if ($optText -match 'AutoScan=0') { $autoScan = $false }
}

# 上次告警去重：同一问题只告警一次，避免每 30 分钟重复刷屏
$prevAlerts = @()
if (Test-Path -LiteralPath $stateFile) {
    $prevAlerts = @(Get-Content -LiteralPath $stateFile -Encoding UTF8)
}
$script:curAlerts = @()

function Write-Alert([string]$msg) {
    if ($prevAlerts -contains $msg) { return }
    $line = (Get-Date -Format 'yyyy-MM-dd HH:mm:ss') + '  ' + $msg
    Add-Content -LiteralPath $alertLog -Value $line -Encoding UTF8 -ErrorAction SilentlyContinue
    $script:curAlerts += $msg
    $script:newAlert = $true
}

# ============ 1) 蜜罐诱饵完整性检查（对照备份自动恢复） ============
if (Test-Path -LiteralPath $manifest) {
    foreach ($p in Get-Content -LiteralPath $manifest -Encoding UTF8) {
        $p = $p.Trim()
        if (-not $p) { continue }
        $backup = Join-Path $backupDir (Split-Path $p -Leaf)
        if (Test-Path -LiteralPath $backup) {
            $current = $null
            if (Test-Path -LiteralPath $p) {
                $current = (Get-FileHash -LiteralPath $p -Algorithm MD5 -ErrorAction SilentlyContinue).Hash
            }
            $expected = (Get-FileHash -LiteralPath $backup -Algorithm MD5 -ErrorAction SilentlyContinue).Hash
            if (-not $current -or $current -ne $expected) {
                Copy-Item -LiteralPath $backup -Destination $p -Force -ErrorAction SilentlyContinue
                Write-Alert "蜜罐诱饵被修改/删除，已自动恢复: $p"
            }
        }
    }
}

# ============ 2) 防护篡改检查 ============
$tamper = @()
$rt1 = (Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows Defender\Real-Time Protection' -Name DisableRealtimeMonitoring -ErrorAction SilentlyContinue).DisableRealtimeMonitoring
$rt2 = (Get-ItemProperty 'HKLM:\SOFTWARE\Policies\Microsoft\Windows Defender\Real-Time Protection' -Name DisableRealtimeMonitoring -ErrorAction SilentlyContinue).DisableRealtimeMonitoring
if ($rt1 -eq 1 -or $rt2 -eq 1) { $tamper += 'Defender 实时保护被关闭（挖矿木马常见手法）' }
$as = (Get-ItemProperty 'HKLM:\SOFTWARE\Policies\Microsoft\Windows Defender' -Name DisableAntiSpyware -ErrorAction SilentlyContinue).DisableAntiSpyware
if ($as -eq 1) { $tamper += 'Defender 防病毒策略被禁用（DisableAntiSpyware）' }

$ifeoRoot = 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options'
$ifeoTargets = @()
if (Test-Path -LiteralPath $sigFile) {
    $section = ''
    foreach ($line in Get-Content -LiteralPath $sigFile -Encoding UTF8) {
        $t = $line.Trim()
        if ($t.StartsWith('[')) { $section = $t; continue }
        if ($section -eq '[IFEO目标]' -and $t -and -not $t.StartsWith('#')) { $ifeoTargets += $t.ToLower() }
    }
}
foreach ($sk in (Get-ChildItem $ifeoRoot -ErrorAction SilentlyContinue)) {
    $dbg = $sk.GetValue('Debugger')
    if ($dbg) {
        if ($dbg -match 'AppData' -or $dbg -match 'Temp' -or ($ifeoTargets -contains $sk.PSChildName.ToLower())) {
            $tamper += "IFEO 进程劫持: $($sk.PSChildName) => $dbg（杀软/安装器可能无法启动）"
        }
    }
}

$drKey = Get-Item 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Policies\Explorer\DisallowRun' -ErrorAction SilentlyContinue
if ($drKey -and $drKey.Property.Count -gt 0) { $tamper += 'DisallowRun 应用被禁运行（防护软件可能无法安装/启动）' }

$hostsFile = 'C:\Windows\System32\drivers\etc\hosts'
if (Test-Path $hostsFile) {
    $hc = Get-Content $hostsFile -Raw -ErrorAction SilentlyContinue
    if ($hc -match '0\.0\.0\.0\s+\S*(360|huorong|qqpc|kaspersky|defender|microsoftupdate|update\.microsoft)') {
        $tamper += 'hosts 文件疑似被写入屏蔽杀软更新/下载站点的规则'
    }
}
foreach ($t in $tamper) { Write-Alert "防护篡改告警: $t" }

# ============ 3) 特征库样本比对（对照「特征库.txt」） ============
function Test-SignatureHits {
    $hits = @()
    if (-not (Test-Path -LiteralPath $sigFile)) { return $hits }
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
    $procs = @(Get-CimInstance Win32_Process -ErrorAction SilentlyContinue)
    foreach ($n in $names) {
        $hit = $procs | Where-Object { $_.Name -ieq $n -and $_.Path -and -not $_.Path.StartsWith($env:WINDIR, [System.StringComparison]::OrdinalIgnoreCase) }
        if ($hit) { $hits += "进程命中特征库: $n (PID $($hit[0].ProcessId), $($hit[0].Path))" }
    }
    foreach ($k in $cmdKeys) {
        $rx = [regex]::Escape($k)
        foreach ($p in $procs) {
            if (($p.Name -imatch $rx) -or ($p.CommandLine -and $p.CommandLine -imatch $rx)) {
                $hits += "进程命令命中特征库: $k (PID $($p.ProcessId))"
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
                $hits += "服务命中特征库: $($k.PSChildName) => $img"
            }
        }
    }
    return @($hits)
}
# 汇总"应做网络阻断"的 exe 路径（特征库进程名命中 + 服务关键字命中的非系统 exe）
function Get-SignatureBlockCandidates {
    $paths = @()
    $section = ''
    $names = @(); $svcKeys = @()
    if (Test-Path -LiteralPath $sigFile) {
        foreach ($line in Get-Content -LiteralPath $sigFile -Encoding UTF8) {
            $t = $line.Trim()
            if ($t.StartsWith('[')) { $section = $t; continue }
            if (-not $t -or $t.StartsWith('#')) { continue }
            switch ($section) {
                '[进程名]'     { $names   += $t }
                '[服务关键字]'  { $svcKeys += $t }
            }
        }
    }
    $procs = @(Get-CimInstance Win32_Process -ErrorAction SilentlyContinue)
    foreach ($n in $names) {
        foreach ($p in ($procs | Where-Object { $_.Name -ieq $n -and $_.Path -and -not $_.Path.StartsWith($env:WINDIR, [System.StringComparison]::OrdinalIgnoreCase) })) {
            if ($p.Path -and (Test-Path -LiteralPath $p.Path -PathType Leaf)) { $paths += $p.Path }
        }
    }
    $svcRoot = 'HKLM:\SYSTEM\CurrentControlSet\Services'
    foreach ($sk in $svcKeys) {
        $rx = '\b' + [regex]::Escape($sk) + '\b'
        foreach ($k in (Get-ChildItem $svcRoot -ErrorAction SilentlyContinue)) {
            $img = $k.GetValue('ImagePath')
            if (($k.PSChildName -imatch $rx) -or ($img -and ([string]$img -imatch $rx))) {
                $exe = ([string]$img).Trim()
                if ($exe -match '"([^"]+)"') { $exe = $Matches[1] } else { $exe = ($exe -split '\s+')[0] }
                $exe = $exe.TrimEnd(')')
                if ($exe -and (Test-Path -LiteralPath $exe -PathType Leaf) -and -not $exe.StartsWith($env:WINDIR, [System.StringComparison]::OrdinalIgnoreCase)) { $paths += $exe }
            }
        }
    }
    return @($paths | Where-Object { $_ } | Sort-Object -Unique)
}

foreach ($h in (Test-SignatureHits)) { Write-Alert "特征库命中: $h" }

# 特征库命中后自动添加"程序级出站阻断"（即使木马更换矿池 IP 也无法联网）
# 仅当自动联动扫描开启时才执行，避免在用户不知情时改动系统
if ($autoScan) {
foreach ($exe in (Get-SignatureBlockCandidates)) {
    $name = 'MhyunBox_阻断_' + ([System.IO.Path]::GetFileName($exe) -replace '[^a-zA-Z0-9_]', '_')
    if (Get-NetFirewallRule -DisplayName $name -ErrorAction SilentlyContinue) { continue }
    try {
        New-NetFirewallRule -DisplayName $name -Direction Outbound -Action Block -Program $exe -Profile Any -ErrorAction Stop | Out-Null
        Write-Alert "特征库命中自动阻断（出站封禁）: $exe"
    } catch {
        Write-Alert "无法添加防火墙阻断（需管理员权限运行守护任务）: $exe"
    }
}
}

# ============ 3.5) 系统 DLL 完整性 / 注入检测 ============
# 门罗币木马常把混淆代码与"拉起"代码注入到删不掉、关不掉的系统 DLL 里：
# 1) 关键系统 DLL 必须仍带微软有效签名，被改过会失去签名 → 能直接查出来；
# 2) 关键进程（explorer/svchost/winlogon/lsass 等）加载了来自用户可写目录的
#    无签名 DLL，基本就是 DLL 注入 → 列出来。
$sysDir = Join-Path $env:WINDIR 'System32'
$criticalDlls = @('ntdll.dll','kernel32.dll','kernelbase.dll','user32.dll','gdi32.dll','ws2_32.dll','wininet.dll','dnsapi.dll','shell32.dll','advapi32.dll','ole32.dll','rpcrt4.dll','version.dll','winhttp.dll','wtsapi32.dll','crypt32.dll','cryptbase.dll','samlib.dll')
foreach ($d in $criticalDlls) {
    $f = Join-Path $sysDir $d
    if (-not (Test-Path -LiteralPath $f)) { continue }
    $st = (Get-AuthenticodeSignature -LiteralPath $f -ErrorAction SilentlyContinue).Status
    if ($st -ne 'Valid') { Write-Alert "系统 DLL 签名异常（可能被木马注入/篡改）: $d ($st)" }
}
$criticalProcs = @('explorer','svchost','winlogon','lsass','services','csrss','dwm','spoolsv')
foreach ($cp in $criticalProcs) {
    $p = Get-Process -Name $cp -ErrorAction SilentlyContinue
    foreach ($proc in $p) {
        try {
            foreach ($m in $proc.Modules) {
                $fp = $m.FileName
                if (-not $fp) { continue }
                if ($fp -match '\\System32\\|\\SysWOW64\\|\\WinSxS\\|\\servicing\\|\\microsoft\.windows\.appruntime|\\ShellComponents\\|\\Program Files\\|\\Windows Defender\\|\\AppData\\Local\\Programs\\|\\Programs\\') { continue }
                if ($fp -match '\\AppData\\|\\Temp\\|\\ProgramData\\|\\Downloads\\|\\Desktop\\') {
                    $sig = Get-AuthenticodeSignature -LiteralPath $fp -ErrorAction SilentlyContinue
                    if ($sig -and $sig.Status -eq 'Valid' -and $sig.SignerCertificate -and $sig.SignerCertificate.Subject -match 'Microsoft') { continue }
                    Write-Alert "可疑 DLL 注入/加载: $($proc.ProcessName)(PID $($proc.Id)) -> $fp"
                }
            }
        } catch { }
    }
}

# ============ 4) 发现异常则自动联动扫描（仅扫描，不修改系统，静默运行） ============
if ($script:newAlert -and $autoScan -and (Test-Path -LiteralPath $killScript)) {
    Write-Alert '== 检测到异常，自动调用威胁清理脚本（仅扫描，不修改系统，静默运行） =='
    & powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -WindowStyle Hidden -File $killScript -Mode scan -LogFile $alertLog
    Write-Alert '== 守护扫描完成，请打开 MhyunBox 查看日志并按提示处置 =='
}
elseif ($script:newAlert -and -not $autoScan) {
    Write-Alert '== 检测到异常，自动联动扫描已关闭（可在 MhyunBox「电脑布防」中重新开启） =='
}

# 保存本次告警到状态文件，用于下次去重
Set-Content -LiteralPath $stateFile -Value $script:curAlerts -Encoding UTF8 -ErrorAction SilentlyContinue
