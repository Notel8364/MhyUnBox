# 银狐木马综合威胁清理工具
# 由 @SYSTEM-RAMOS-ZDY 的「查找隐藏的进程和启动项」+「解禁杀毒软件」两个脚本逻辑整理合并而来
# 用法: powershell -NoProfile -ExecutionPolicy Bypass -File 威胁清理.ps1 -Mode scan|kill [-LogFile path]

param(
    [string]$Mode = 'scan',
    [string]$LogFile = '',
    [string]$Backup = '',
    [Alias('WhatIf')]
    [switch]$DryRun
)

[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$OutputEncoding = [System.Text.Encoding]::UTF8

$script:LogPath = $LogFile

function L([string]$Message) {
    $line = (Get-Date -Format 'HH:mm:ss.fff') + '  ' + $Message
    Write-Output $line
    if ($script:LogPath) {
        try { Add-Content -LiteralPath $script:LogPath -Value $line -Encoding UTF8 } catch { }
    }
}

function Test-Admin {
    $id = [System.Security.Principal.WindowsIdentity]::GetCurrent()
    $p = New-Object System.Security.Principal.WindowsPrincipal($id)
    return $p.IsInRole([System.Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Get-HiddenExecutables {
    $paths = @()
    try {
        $paths = Get-CimInstance Win32_Process -ErrorAction SilentlyContinue |
            Where-Object { $_.ExecutablePath } |
            Select-Object -ExpandProperty ExecutablePath -Unique
    } catch {
        $paths = Get-WmiObject Win32_Process -ErrorAction SilentlyContinue |
            Where-Object { $_.ExecutablePath } |
            Select-Object -ExpandProperty ExecutablePath -Unique
    }
    $result = @()
    foreach ($p in $paths) {
        if ($p -and (Test-Path -LiteralPath $p -PathType Leaf)) {
            $attr = (Get-Item -LiteralPath $p -Force -ErrorAction SilentlyContinue).Attributes
            if (($attr -band [System.IO.FileAttributes]::Hidden) -or ($attr -band [System.IO.FileAttributes]::System)) {
                $result += $p
            }
        }
    }
    return @($result)
}

function Extract-ExePath([string]$data) {
    $data = $data.Trim()
    if ($data -match '"([^"]+)"') { return $Matches[1].Trim() }
    $first = ($data -split '\s+')[0]
    return $first.TrimEnd(')')
}

function Get-HiddenStartupItems {
    $keys = @(
        'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run',
        'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run',
        'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run'
    )
    $result = @()
    foreach ($k in $keys) {
        if (Test-Path $k) {
            $item = Get-Item $k
            foreach ($name in $item.Property) {
                try { $data = [string](Get-ItemProperty -Path $k -Name $name).$name } catch { continue }
                $exe = Extract-ExePath $data
                if ($exe -and (Test-Path -LiteralPath $exe -PathType Leaf)) {
                    $attr = (Get-Item -LiteralPath $exe -Force -ErrorAction SilentlyContinue).Attributes
                    if (($attr -band [System.IO.FileAttributes]::Hidden) -or ($attr -band [System.IO.FileAttributes]::System)) {
                        $result += "$name  =>  $exe"
                    }
                }
            }
        }
    }
    return @($result)
}

function Get-SuspiciousServices {
    $result = @()
    $servicesRoot = 'HKLM:\SYSTEM\CurrentControlSet\Services'
    $keys = Get-ChildItem $servicesRoot -ErrorAction SilentlyContinue
    foreach ($k in $keys) {
        try {
            $ip = $k.GetValue('ImagePath')
        } catch { continue }
        if ($ip) {
            $s = [string]$ip
            if ($s -match 'cmd\s+/c|powershell|pwsh|wscript|cscript|mshta') {
                $result += "$($k.PSChildName)  =>  $s"
            }
        }
    }
    return @($result)
}

# 读取与 MhyunBox / 布防守护共用的特征库（样本库），做样本比对
function Get-SignatureHits {
    $hits = @()
    $sigFile = Join-Path $PSScriptRoot '特征库.txt'
    if (-not (Test-Path $sigFile)) { return $hits }
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
            $name = $k.PSChildName
            $img = $k.GetValue('ImagePath')
            if (($name -imatch $rx) -or ($img -and ([string]$img -imatch $rx))) {
                $hits += "服务命中特征库: $name => $img"
            }
        }
    }
    return @($hits)
}

# 汇总"应做网络阻断"的可执行文件路径（特征库进程名命中 + 可疑服务/服务关键字命中的非系统 exe）
function Get-BlockCandidates {
    $paths = @()
    $sigFile = Join-Path $PSScriptRoot '特征库.txt'
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
                $exe = Extract-ExePath $img
                if ($exe -and (Test-Path -LiteralPath $exe -PathType Leaf) -and -not $exe.StartsWith($env:WINDIR, [System.StringComparison]::OrdinalIgnoreCase)) { $paths += $exe }
            }
        }
    }
    foreach ($s in Get-SuspiciousServices) {
        $img = ($s -split '  =>  ')[1].Trim()
        $exe = Extract-ExePath $img
        if ($exe -and (Test-Path -LiteralPath $exe -PathType Leaf) -and -not $exe.StartsWith($env:WINDIR, [System.StringComparison]::OrdinalIgnoreCase)) { $paths += $exe }
    }
    return @($paths | Where-Object { $_ } | Sort-Object -Unique)
}

# 按"程序路径"添加防火墙出站阻断规则（MhyunBox_阻断_*）：换池子/换 IP 也无法联网
function Invoke-FirewallBlock([string[]]$paths) {
    if ($paths.Count -eq 0) { return 0 }
    $added = 0
    foreach ($exe in $paths) {
        $name = 'MhyunBox_阻断_' + ([System.IO.Path]::GetFileName($exe) -replace '[^a-zA-Z0-9_]', '_')
        if (Get-NetFirewallRule -DisplayName $name -ErrorAction SilentlyContinue) { continue }
        try {
            New-NetFirewallRule -DisplayName $name -Direction Outbound -Action Block -Program $exe -Profile Any -ErrorAction Stop | Out-Null
            L "[防火墙阻断] $exe"
            $added++
        } catch {
            L "[阻断失败] $exe : $($_.Exception.Message)"
        }
    }
    return $added
}

# 系统关键 DLL 必须仍带微软有效签名；被木马改过的 DLL 会失去签名 → 直接可查
function Get-SystemDllTampering {
    $bad = @()
    $sysDir = Join-Path $env:WINDIR 'System32'
    $criticalDlls = @('ntdll.dll','kernel32.dll','kernelbase.dll','user32.dll','gdi32.dll','ws2_32.dll','wininet.dll','dnsapi.dll','shell32.dll','advapi32.dll','ole32.dll','rpcrt4.dll','version.dll','winhttp.dll','wtsapi32.dll','crypt32.dll','cryptbase.dll','samlib.dll')
    foreach ($d in $criticalDlls) {
        $f = Join-Path $sysDir $d
        if (-not (Test-Path -LiteralPath $f)) { continue }
        $st = (Get-AuthenticodeSignature -LiteralPath $f -ErrorAction SilentlyContinue).Status
        if ($st -ne 'Valid') { $bad += "$d ($st)" }
    }
    return @($bad)
}

# 关键进程里出现了来自用户可写目录的无签名 DLL → DLL 注入嫌疑
function Get-InjectedModules {
    $hits = @()
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
                        $hits += "$($proc.ProcessName)(PID $($proc.Id)) -> $fp"
                    }
                }
            } catch { }
        }
    }
    return @($hits)
}

function Clear-WdacPolicy {
    $policy = 'C:\Windows\System32\CodeIntegrity\SiPolicy.p7b'
    $active = 'C:\Windows\System32\CodeIntegrity\CiPolicies\Active'
    try {
        takeown /f $policy 2>&1 | Out-Null
        icacls $policy /grant administrators:F 2>&1 | Out-Null
        Remove-Item -LiteralPath $policy -Force -ErrorAction SilentlyContinue
        Remove-Item -LiteralPath $active -Recurse -Force -ErrorAction SilentlyContinue
        L '[完成] 已清空 WDAC 策略（杀毒软件应可正常启动）'
    } catch {
        L "[警告] 清空 WDAC 策略失败: $($_.Exception.Message)"
    }
}

function Remove-OldSilverfoxKernel {
    $drivers = @('vafdska', 'MiniFilterDrv', 'vmservice', 'MicrosoftSoftware2ShadowCop4yProvider')
    foreach ($d in $drivers) {
        reg delete "HKLM\SYSTEM\ControlSet001\Services\$d" /f 2>&1 | Out-Null
    }
    Remove-Item -LiteralPath 'C:\Windows\System32\wow64log.dll' -Force -ErrorAction SilentlyContinue
    L '[完成] 已清理旧版银狐驱动与 wow64log.dll'
}

function Disable-RootTasks {
    try {
        $suspicious = 0
        $tasks = @(Get-ScheduledTask -TaskPath '\' -ErrorAction SilentlyContinue)
        foreach ($t in $tasks) {
            if ($t.TaskName -match 'Mhyun') {
                L "[跳过-自用任务] $($t.TaskName)"
                continue
            }
            $hit = $false
            foreach ($a in $t.Actions) {
                if ($a.Execute -match '(?i)\b(powershell|pwsh|wscript|cscript|mshta|rundll32|regsvr32|cmd)\.exe\b' -or $a.Arguments -match '(?i)(https?://|\.ps1\b|\.bat\b|\.vbs\b|\.jse\b|\.vbe\b|-enc\b|-encodedcommand\b|frombase64string|downloadstring)') {
                    $hit = $true
                    break
                }
            }
            if ($hit) {
                try {
                    $t | Disable-ScheduledTask -ErrorAction SilentlyContinue | Out-Null
                    L "[禁用可疑计划任务] $($t.TaskName)"
                    $suspicious++
                } catch { }
            }
        }
        L "[完成] 已禁用可疑计划任务 $suspicious 项（仅禁用命令可疑的任务，其余保留）"
    } catch {
        L "[警告] 禁用计划任务失败: $($_.Exception.Message)"
    }
}

function Invoke-HiddenProcessKill {
    $whitelist = @(
        'explorer.exe', 'svchost.exe', 'lsass.exe', 'winlogon.exe', 'csrss.exe',
        'services.exe', 'dllhost.exe', 'smss.exe', 'dwm.exe', 'wininit.exe',
        'conhost.exe', 'cmd.exe', 'powershell.exe', 'regedit.exe', 'taskmgr.exe'
    )
    $ifeoRoot = 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options'
    $killed = 0
    foreach ($p in Get-HiddenExecutables) {
        $exe = Split-Path -Leaf $p
        if ($whitelist -contains $exe.ToLower()) {
            L "[跳过-系统进程] $p"
            continue
        }
        L "[发现隐藏进程] $p"
        L "[注册表劫持+终止] 写入 $exe 的 IFEO 项"
        try {
            New-Item -Path "$ifeoRoot\$exe" -Force -ErrorAction SilentlyContinue | Out-Null
            New-ItemProperty -Path "$ifeoRoot\$exe" -Name 'Debugger' -Value 'SYSTEM-RAMOS-ZDY' -PropertyType String -Force | Out-Null
            taskkill /f /im $exe 2>&1 | Out-Null
            $killed++
        } catch {
            L "[处理失败] $p : $($_.Exception.Message)"
        }
    }
    L "[完成] 隐藏进程处理完毕，共劫持/终止 $killed 个"
}

function Invoke-ServiceDisable {
    $disabled = 0
    foreach ($s in Get-SuspiciousServices) {
        $name = ($s -split '  =>  ')[0].Trim()
        L "[发现可疑服务] $name"
        try {
            Set-ItemProperty -Path "HKLM:\SYSTEM\CurrentControlSet\Services\$name" -Name Start -Value 4 -Type DWord -Force -ErrorAction Stop
            L "[成功禁用] $name Start=4"
            $disabled++
        } catch {
            L "[禁用失败] $name : $($_.Exception.Message)"
        }
    }
    L "[完成] 可疑服务处理完毕，共禁用 $disabled 个"
}

# ============ 白加黑(DLL 侧加载) / 持久化 清理 ============
# 以下逻辑合并自 Remove-Malware.ps1（白加黑木马/挖矿族清理），并保留原综合扫描能力。

# 本机已确认的银狐"白加黑"木马 IOC
$MalwareIoc = @{
    Dirs = @(
        'C:\Users\Public\Q2J89u',
        'C:\Users\Public\1pKYZw',
        'C:\Program Files (x86)\4vh7uD',
        'C:\Program Files (x86)\vQHs2',
        'C:\ProgramData\b113fdc7'
    )
    Exe = 'C:\ProgramData\c2a21762.exe'
    NamePattern = '^(f7de3fc1|c2a21762|gzy0New2|E5zU7L|rsp82V|oVk6VA)$'
    Tasks = @(
        'maUqQ',
        'O4O4Q',
        'MicrosoftEdgeUpdateTaskUA Task-S-1-5-18 w4hU6',
        'MicrosoftEdgeUpdateTaskUA Task-S-1-5-18 wpXvc'
    )
    RunValues = @('Tencent SecurityHealth','Alibaba SecurityHealtha','Alibaba SecurityHealthb')
}

$RunKeys = @(
    'HKLM:\Software\Microsoft\Windows\CurrentVersion\Run',
    'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run',
    'HKLM:\Software\Wow6432Node\Microsoft\Windows\CurrentVersion\Run'
)

# 常见矿池/C2 端口（已剔除 1080/1081/2222 等常被合法代理/VPN 使用的端口，降低误伤）
$script:C2Ports = @(3333,3334,3335,3357,4444,5555,6666,7777,8888,9999,14433,14444,45700,45560,55555)

# 检测本机已知 IOC 是否仍然存在
function Get-MalwareIocHits {
    $hits = @()
    foreach ($d in $MalwareIoc.Dirs) { if (Test-Path -LiteralPath $d) { $hits += "白加黑载荷目录: $d" } }
    if (Test-Path -LiteralPath $MalwareIoc.Exe) { $hits += "白加黑载荷程序: $($MalwareIoc.Exe)" }
    try {
        Get-CimInstance Win32_Process -ErrorAction SilentlyContinue | Where-Object {
            ($_.ExecutablePath -and (($MalwareIoc.Dirs | Where-Object { $_.ExecutablePath -like "$_*" }) | Select-Object -First 1)) -or
            ($_.ExecutablePath -ieq $MalwareIoc.Exe) -or
            (($_.Name -replace '\.exe$','') -match $MalwareIoc.NamePattern)
        } | ForEach-Object { $hits += "白加黑恶意进程: $($_.Name) (PID $($_.ProcessId), $($_.ExecutablePath))" }
    } catch { }
    foreach ($t in $MalwareIoc.Tasks) {
        if (Get-ScheduledTask -TaskName $t -TaskPath '\' -ErrorAction SilentlyContinue) { $hits += "恶意计划任务: $t" }
    }
    foreach ($rk in $RunKeys) {
        if (-not (Test-Path $rk)) { continue }
        foreach ($n in $MalwareIoc.RunValues) {
            if (Get-ItemProperty -Path $rk -Name $n -ErrorAction SilentlyContinue) { $hits += "伪造启动项: [$rk] $n" }
        }
    }
    return @($hits)
}

# 通用白加黑检测：用户可写目录下，签名有效的 EXE 旁存在"未签名的常见侧加载 DLL"或同名 DLL
function Get-WhiteBlackDlls {
    $hits = @()
    $sideloadNames = @('version.dll','winhttp.dll','wininet.dll','dbghelp.dll','dxgi.dll','d3d8.dll','d3d9.dll','d3d11.dll','dinput8.dll','uxtheme.dll','textinputframework.dll','msimg32.dll','wtsapi32.dll','profapi.dll','cryptbase.dll','samlib.dll','dwmapi.dll','wldap32.dll','ws2_32.dll','dnsapi.dll','ole32.dll','user32.dll','shell32.dll','crypt32.dll','comctl32.dll')
    $roots = @(
        (Join-Path $env:PUBLIC ''),
        $env:ProgramData,
        ${env:ProgramFiles(x86)},
        (Join-Path $env:LOCALAPPDATA 'Temp')
    ) | Where-Object { $_ -and (Test-Path -LiteralPath $_) } | Sort-Object -Unique
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    foreach ($root in $roots) {
        if ($sw.Elapsed.TotalSeconds -gt 20) { break }
        $exes = @(Get-ChildItem -LiteralPath $root -Filter *.exe -File -Recurse -Depth 2 -ErrorAction SilentlyContinue -Force | Select-Object -First 150)
        foreach ($exe in $exes) {
            if ($sw.Elapsed.TotalSeconds -gt 20) { break }
            $candidates = @()
            $sameDll = Join-Path $exe.DirectoryName ([System.IO.Path]::GetFileNameWithoutExtension($exe.Name) + '.dll')
            if (Test-Path -LiteralPath $sameDll) { $candidates += $sameDll }
            foreach ($dn in $sideloadNames) {
                $p = Join-Path $exe.DirectoryName $dn
                if (Test-Path -LiteralPath $p) { $candidates += $p }
            }
            if ($candidates.Count -eq 0) { continue }
            $exeSig = Get-AuthenticodeSignature -LiteralPath $exe.FullName -ErrorAction SilentlyContinue
            if (-not $exeSig -or $exeSig.Status -ne 'Valid' -or -not $exeSig.SignerCertificate) { continue }
            foreach ($cand in ($candidates | Sort-Object -Unique)) {
                $dllSig = Get-AuthenticodeSignature -LiteralPath $cand -ErrorAction SilentlyContinue
                if ($dllSig -and $dllSig.Status -eq 'Valid') { continue }
                $subject = $exeSig.SignerCertificate.Subject
                if ($subject -match '^CN=([^,]+)') { $subject = $Matches[1] }
                $hits += "白加黑嫌疑: $($exe.Name)($subject) 旁存在未签名 $([System.IO.Path]::GetFileName($cand))  [目录 $($exe.DirectoryName)]"
            }
        }
    }
    return @($hits | Sort-Object -Unique)
}

function Backup-MalwarePersistence([string]$BackupDir) {
    New-Item -ItemType Directory -Force -Path $BackupDir | Out-Null
    foreach ($rk in @('HKLM\Software\Microsoft\Windows\CurrentVersion\Run','HKCU\Software\Microsoft\Windows\CurrentVersion\Run')) {
        try { reg export $rk (Join-Path $BackupDir (($rk -replace '[\\/]','_') + '.reg')) /y 2>&1 | Out-Null } catch { }
    }
    foreach ($t in $MalwareIoc.Tasks) {
        if (-not (Get-ScheduledTask -TaskName $t -TaskPath '\' -ErrorAction SilentlyContinue)) { continue }
        try {
            $safe = ($t -replace '[\\/:*?"<>| ]','_')
            $taskXml = [string](Export-ScheduledTask -TaskName $t -TaskPath '\' -ErrorAction Stop)
            [IO.File]::WriteAllText((Join-Path $BackupDir ("task_" + $safe + ".xml")), $taskXml, (New-Object System.Text.UTF8Encoding($false)))
        } catch { }
    }
    foreach ($t in @(Get-SuspiciousNamedTasks | Where-Object { $_.Missing })) {
        try {
            $safe = (($t.Path + $t.Name) -replace '[\\/:*?"<>| ]','_')
            $taskXml = [string](Export-ScheduledTask -TaskName $t.Name -TaskPath $t.Path -ErrorAction Stop)
            [IO.File]::WriteAllText((Join-Path $BackupDir ("gen_" + $safe + ".xml")), $taskXml, (New-Object System.Text.UTF8Encoding($false)))
        } catch { }
    }
    L "[备份] 已备份启动项/计划任务到 $BackupDir"
}

# 备份所有非微软计划任务（含启用状态），供一键还原时恢复被禁用/删除的任务
function Backup-ScheduledTasks([string]$BackupDir) {
    $tdir = Join-Path $BackupDir 'tasks'
    New-Item -ItemType Directory -Force -Path $tdir | Out-Null
    $list = @()
    foreach ($t in (Get-ScheduledTask -ErrorAction SilentlyContinue)) {
        if (-not $t -or $t.TaskPath -like '\Microsoft\*') { continue }
        $safe = (($t.TaskPath + $t.TaskName) -replace '[\\/:*?"<>| ]','_')
        try {
            $xml = Export-ScheduledTask -TaskName $t.TaskName -TaskPath $t.TaskPath -ErrorAction Stop
            if ($xml) {
                [IO.File]::WriteAllText((Join-Path $tdir ($safe + '.xml')), [string]$xml, (New-Object System.Text.UTF8Encoding($false)))
                $list += @{ name = $t.TaskName; path = $t.TaskPath; enabled = ($t.State -ne 'Disabled'); xml = ($safe + '.xml') }
            }
        } catch { }
    }
    $tasksJson = [string]($list | ConvertTo-Json -Depth 6)
    [IO.File]::WriteAllText((Join-Path $BackupDir 'tasks.json'), $tasksJson, (New-Object System.Text.UTF8Encoding($false)))
    L "[备份] 已备份计划任务 $($list.Count) 项（含启用状态）到 $tdir"
}

function Invoke-KillIocProcesses {
    for ($i = 0; $i -lt 6; $i++) {
        $procs = Get-CimInstance Win32_Process -ErrorAction SilentlyContinue | Where-Object {
            $ep = $_.ExecutablePath
            ($ep -and @($MalwareIoc.Dirs | Where-Object { $ep.ToLower().StartsWith($_.ToLower()) }).Count -gt 0) -or
            ($ep -and $ep -ieq $MalwareIoc.Exe) -or
            (($_.Name -replace '\.exe$','') -match $MalwareIoc.NamePattern)
        }
        if (-not $procs) { break }
        foreach ($p in $procs) {
            try { Stop-Process -Id $p.ProcessId -Force -ErrorAction Stop; L "[已终止] PID=$($p.ProcessId) $($p.Name)" } catch { }
        }
        Start-Sleep -Milliseconds 300
    }
}

# DENY-ACL 绕过删除法：夺取所有权 → 移除 Users DENY ACE → 恢复继承 → 去隐藏/系统/只读属性 → 删除
function Invoke-AclBypassDelete([string]$t) {
    if (-not (Test-Path -LiteralPath $t)) { L "[已不存在] $t"; return }
    cmd /c "takeown /F `"$t`" /R /D Y >nul 2>&1"
    cmd /c "icacls `"$t`" /remove:d *S-1-5-32-545 /T /C /Q >nul 2>&1"
    cmd /c "icacls `"$t`" /remove:d *S-1-1-0 /T /C /Q >nul 2>&1"
    cmd /c "icacls `"$t`" /inheritance:e /T /C /Q >nul 2>&1"
    if (Test-Path -LiteralPath $t -PathType Container) { cmd /c "attrib -H -S -R `"$t\*`" /S /D >nul 2>&1" }
    try {
        Remove-Item -LiteralPath $t -Recurse -Force -ErrorAction Stop
        L "[已删除] $t"
    } catch {
        cmd /c "rd /s /q `"$t`" 2>nul"
        if (Test-Path -LiteralPath $t) { L "[删除失败-仍存在] $t" } else { L "[已删除] $t" }
    }
}

# 删除 IOC 文件/目录（处理木马添加的 DENY ACL 权限）
function Invoke-RemoveIocFiles {
    foreach ($t in @($MalwareIoc.Dirs + $MalwareIoc.Exe)) {
        Invoke-AclBypassDelete $t
    }
}

function Remove-MalwareIocPersistence {
    foreach ($t in $MalwareIoc.Tasks) {
        if (-not (Get-ScheduledTask -TaskName $t -TaskPath '\' -ErrorAction SilentlyContinue)) { continue }
        schtasks /Delete /TN "$t" /F 2>$null | Out-Null
        if (Get-ScheduledTask -TaskName $t -TaskPath '\' -ErrorAction SilentlyContinue) { L "[任务删除失败] $t" } else { L "[已删除任务] $t" }
    }
    foreach ($rk in $RunKeys) {
        if (-not (Test-Path $rk)) { continue }
        foreach ($n in $MalwareIoc.RunValues) {
            if (Get-ItemProperty -Path $rk -Name $n -ErrorAction SilentlyContinue) {
                Remove-ItemProperty -Path $rk -Name $n -ErrorAction SilentlyContinue
                L "[已删除伪造启动项] [$rk] $n"
            }
        }
    }
}

# ============ 泛化特征（Remove-Malware.ps1 手法泛化，与银狐硬编码 IOC 共存）============
# 规则：随机混合大小写英文名(5-10位) + 位于 Public / Program Files (x86) / ProgramData 等非常规目录
#       + 无有效公司签名 +（进程侧）高 CPU 或有外联（矿池/远程）
# 目录形态：C:\Users\Public\<随机短名>\ 、Program Files (x86)\<随机短名>\ 、C:\ProgramData\<hex>\ 、C:\ProgramData\<hex>.exe
$script:RandomDirWhitelist = @('Desktop','Documents','Downloads','Music','Pictures','Videos','AccountPictures','Libraries')

# 判定"随机混合大小写短名"：仅字母数字 5-10 位，大小写混排且第 2 位起仍出现大写（排除首字母大写的正常单词）
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

# 目录内载荷判定：存在无有效签名的可执行脚本/exe/dll → 'unsigned'；目录为空 → 'empty'（残留壳）；全部签名有效 → $null（合法，跳过）
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

# 随机名载荷目录扫描（Public / Program Files (x86) 随机短名目录；ProgramData hex 目录）
# 防误报：内置系统目录白名单；"空目录残留"仅对 hex 名目录生效，随机混合大小写目录必须含无签名载荷
function Get-RandomPayloadDirs {
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
            $hit = $false; $why = ''; $isHex = ($d.Name -match '^[0-9a-fA-F]{6,12}$')
            if ($r.hex -and $isHex) { $hit = $true; $why = 'ProgramData hex 名目录' }
            if (-not $hit -and (Test-RandomMixedName $d.Name) -and ($script:RandomDirWhitelist -notcontains $d.Name) -and ($sysDirs -notcontains $d.Name)) { $hit = $true; $why = '随机混合大小写名目录' }
            if (-not $hit) { continue }
            $kind = Test-DirHasUnsignedPayload $d.FullName
            if ($kind -eq 'empty') {
                if ($isHex) { $out += [pscustomobject]@{ Path = $d.FullName; Reason = ($why + '/空目录残留') } }
            } elseif ($kind) { $out += [pscustomobject]@{ Path = $d.FullName; Reason = ($why + '/含无签名载荷') } }
        }
    }
    return @($out)
}

# ProgramData 下的 hex 名 .exe 文件（如 c2a21762.exe）
function Get-RandomPayloadFiles {
    $out = @()
    if (-not $env:ProgramData -or -not (Test-Path -LiteralPath $env:ProgramData)) { return $out }
    foreach ($f in @(Get-ChildItem -LiteralPath $env:ProgramData -File -Force -ErrorAction SilentlyContinue)) {
        if ($f.Name -notmatch '^[0-9a-fA-F]{6,12}\.exe$') { continue }
        $sig = Get-AuthenticodeSignature -LiteralPath $f.FullName -ErrorAction SilentlyContinue
        if (-not $sig -or $sig.Status -ne 'Valid') { $out += [pscustomobject]@{ Path = $f.FullName; Reason = 'ProgramData hex 名可执行文件（无有效签名）' } }
    }
    return @($out)
}

# 泛化：伪造 "Tencent/Alibaba SecurityHealth" 启动项 —— Run 值名含 SecurityHealth 但未指向系统正版组件
function Get-FakeSecurityHealthRunHits {
    $hits = @()
    $legit = @('securityhealthsystray.exe','securityhealthservice.exe')
    foreach ($rk in $RunKeys) {
        if (-not (Test-Path $rk)) { continue }
        $it = Get-Item -Path $rk -ErrorAction SilentlyContinue
        if (-not $it) { continue }
        foreach ($n in $it.Property) {
            if ($n -notmatch '(?i)securityhealth') { continue }
            $val = ''
            try { $val = [string](Get-ItemProperty -Path $rk -Name $n -ErrorAction SilentlyContinue).$n } catch { }
            $exe = Extract-ExePath $val
            if ($exe) { $exe = [Environment]::ExpandEnvironmentVariables($exe) }
            $ok = $false
            foreach ($l in $legit) { if ($exe -and ($exe -ieq (Join-Path $env:WINDIR "System32\$l"))) { $ok = $true; break } }
            if (-not $ok) { $hits += "[$rk] $n = $val" }
        }
    }
    return @($hits)
}

function Remove-FakeSecurityHealthRun {
    $n = 0
    foreach ($h in @(Get-FakeSecurityHealthRunHits)) {
        if ($h -notmatch '^\[(.+?)\] (.+?) = ') { continue }
        $rk = $Matches[1]; $valName = $Matches[2]
        try { Remove-ItemProperty -Path $rk -Name $valName -Force -ErrorAction Stop; L "[已删除伪造SecurityHealth启动项] [$rk] $valName"; $n++ } catch { L "[删除失败-伪造SecurityHealth] [$rk] $valName" }
    }
    L "[完成] 伪造 SecurityHealth 启动项处理 $n 项"
}

# 泛化：随机 5-10 位混合大小写命名的计划任务 + 伪装 MicrosoftEdgeUpdateTaskUA 的任务
# 安全规则：目标 exe 确实不存在（Missing=$true）才可删除；目标存在只报告不删（防误删正规任务）
# 注意：本函数不调用 L()，避免 Write-Output 污染返回流
function Get-SuspiciousNamedTasks {
    $out = @()
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
            $exe = Resolve-Exec $raw
            if ($exe -and $exe -match '^[A-Za-z]:\\' -and -not (Test-Path -LiteralPath $exe)) { $missing = $true }
        }
        $out += [pscustomobject]@{ Name = $t.TaskName; Path = $t.TaskPath; Tag = $tag; Raw = ($raws -join ' | '); Missing = $missing }
    }
    return @($out)
}

function Remove-SuspiciousNamedTasks {
    $n = 0
    foreach ($t in @(Get-SuspiciousNamedTasks | Where-Object { $_.Missing })) {
        try {
            schtasks /Delete /TN "$($t.Name)" /F 2>$null | Out-Null
            if (Get-ScheduledTask -TaskName $t.Name -TaskPath $t.Path -ErrorAction SilentlyContinue) { L "[任务删除失败] $($t.Name)" }
            else { L "[已删除任务-$($t.Tag)] $($t.Name)"; $n++ }
        } catch { L "[任务删除失败] $($t.Name) : $($_.Exception.Message)" }
    }
    L "[完成] 随机命名/伪装 Edge 任务处理 $n 个"
}

# 清除泛化随机名载荷：先多轮终止其进程，再按 DENY-ACL 绕过法删除
function Invoke-RemoveRandomPayloadDirs {
    $items = @(Get-RandomPayloadDirs) + @(Get-RandomPayloadFiles)
    foreach ($item in $items) {
        $p = $item.Path
        if (-not (Test-Path -LiteralPath $p)) { continue }
        L "[泛化载荷-清理] $($item.Path) [$($item.Reason)]"
        for ($i = 0; $i -lt 3; $i++) {
            $procs = @(Get-CimInstance Win32_Process -ErrorAction SilentlyContinue | Where-Object {
                $ep = $_.ExecutablePath
                $ep -and ($ep.ToLower().StartsWith($p.ToLower()))
            })
            if ($procs.Count -eq 0) { break }
            foreach ($pr in $procs) { try { Stop-Process -Id $pr.ProcessId -Force -ErrorAction Stop; L "[已终止-泛化载荷] PID=$($pr.ProcessId) $($pr.Name)" } catch { } }
            Start-Sleep -Milliseconds 300
        }
        Invoke-AclBypassDelete $p
    }
    L "[完成] 泛化随机名载荷清理 $($items.Count) 项"
}

# 清理失效启动项 / 失效计划任务（安全清理，仅针对目标确实不存在的项）
function Remove-BrokenPersistence {
    function Get-StartupExe([string]$val) {
        if ([string]::IsNullOrWhiteSpace($val)) { return $null }
        $v = $val.Trim()
        if ($v.StartsWith('"')) { $end = $v.IndexOf('"',1); if ($end -gt 1) { return $v.Substring(1,$end-1) } }
        $m = [regex]::Match($v, '^([A-Za-z]:\\[^"]*?\.exe)')
        if ($m.Success) { return $m.Groups[1].Value }
        return $null
    }
    foreach ($rk in $RunKeys) {
        if (-not (Test-Path $rk)) { continue }
        foreach ($p in (Get-ItemProperty -Path $rk).PSObject.Properties) {
            if ($p.Name -like 'PS*') { continue }
            $exe = Get-StartupExe ([string]$p.Value)
            if (-not $exe) { continue }
            $exe = [Environment]::ExpandEnvironmentVariables($exe)
            if ($exe -match '^[A-Za-z]:\\' -and -not (Test-Path -LiteralPath $exe)) {
                L "[失效启动项] [$rk] $($p.Name) -> $($p.Value)"
                try { Remove-ItemProperty -Path $rk -Name $p.Name -ErrorAction Stop; L "[已清理失效启动项] $($p.Name)" } catch { }
            }
        }
    }
    Get-ScheduledTask -ErrorAction SilentlyContinue | ForEach-Object {
        $t = $_
        if ($t.TaskPath -like '\Microsoft\*') { return }
        foreach ($a in $t.Actions) {
            $raw = [string]$a.Execute
            if (-not $raw) { continue }
            $exe = [Environment]::ExpandEnvironmentVariables($raw.Trim('"'))
            if ($exe -match '^[A-Za-z]:\\' -and -not (Test-Path -LiteralPath $exe)) {
                L "[失效计划任务] [$($t.TaskPath)$($t.TaskName)] -> $raw"
                try { Unregister-ScheduledTask -TaskName $t.TaskName -TaskPath $t.TaskPath -Confirm:$false -ErrorAction Stop; L "[已清理失效任务] $($t.TaskName)" } catch { }
                break
            }
        }
    }
}

# 恢复 Windows Defender（解除木马对杀软的策略/服务封锁）
function Restore-WindowsDefender {
    foreach ($k in @('HKLM:\SOFTWARE\Policies\Microsoft\Windows Defender','HKLM:\SOFTWARE\Policies\Microsoft\Windows Defender\Real-Time Protection')) {
        if (Test-Path $k) {
            foreach ($v in @('DisableAntiSpyware','DisableAntiVirus','DisableRealtimeMonitoring','DisableBehaviorMonitoring','DisableIOAVProtection','DisableScriptScanning')) {
                Remove-ItemProperty -Path $k -Name $v -ErrorAction SilentlyContinue
            }
        }
    }
    foreach ($v in @('DisableAntiSpyware','DisableAntiVirus')) {
        Remove-ItemProperty -Path 'HKLM:\SOFTWARE\Microsoft\Windows Defender' -Name $v -ErrorAction SilentlyContinue
    }
    try { Set-Service -Name WinDefend -StartupType Automatic -ErrorAction SilentlyContinue } catch { }
    foreach ($s in @('WinDefend','WdNisSvc','SecurityHealthService','wscsvc')) {
        try { Start-Service $s -ErrorAction SilentlyContinue } catch { }
    }
    Start-Sleep -Seconds 2
    try {
        Set-MpPreference -DisableRealtimeMonitoring $false -ErrorAction SilentlyContinue
        Update-MpSignature -ErrorAction SilentlyContinue
    } catch { }
    L '[完成] 已尝试恢复 Windows Defender（解除策略/服务封锁）'
}

# ============ 乱码/随机英文名 + 高占用/高网络 行为检测 ============
# 银狐类木马常以"随机大小写字母(约 5-7 位)组合"命名，占用极高、外联频繁。
$script:SecurityVendors = @(
    '360','360safe','360.cn','qihoo','qq.com','tencent','pcmgr','duba','kingsoft','liebao',
    'huorong','sogou','rising','baidu','hao123','2345','kaspersky','bitdefender','avast','avira',
    'eset','drweb','emsisoft','mcafee','symantec','norton','microsoft','windowsupdate','defender',
    'msftconnecttest','msftncsi','nvidia','adobe','google','gstatic','apple','cloudflare','virustotal'
)

function Test-VendorHost([string]$hostName) {
    if (-not $hostName) { return $false }
    $h = $hostName.ToLower()
    foreach ($v in $script:SecurityVendors) { if ($h -like "*$v*") { return $true } }
    return $false
}

# 判定"乱码/随机"名：乱码替换符/方框字符；或含数字的大小写混排随机串；或无元音短随机串
function Test-GarbledName([string]$name) {
    if ([string]::IsNullOrWhiteSpace($name)) { return $false }
    if ($name -match "\uFFFD|锟斤拷|ï¿½|ÿþ|\u25A0|\u25A1|\u25AA|\u25AB") { return $true }
    try { $base = [System.IO.Path]::GetFileNameWithoutExtension($name) } catch { return $false }
    if ([string]::IsNullOrEmpty($base)) { return $false }
    $normal = 0
    foreach ($ch in $base.ToCharArray()) {
        if ([char]::IsLetterOrDigit($ch) -or $ch -eq '_' -or $ch -eq '-' -or $ch -eq ' ') { $normal++ }
    }
    if (($normal / [Math]::Max(1, $base.Length)) -lt 0.5) { return $true }
    if ($base -notmatch '^[A-Za-z0-9]{5,12}$') { return $false }
    $hasDigit = $base -match '[0-9]'
    $hasLetter = $base -match '[A-Za-z]'
    if (-not $hasLetter) { return $false }
    # 随机名的典型特征：大小写不规则（非首字母处也出现大写）
    $irregular = $false
    for ($i = 1; $i -lt $base.Length; $i++) { if ([char]::IsUpper($base[$i])) { $irregular = $true; break } }
    if (-not $irregular) { return $false }
    $letters = 0
    foreach ($ch in $base.ToCharArray()) { if ([char]::IsLetter($ch)) { $letters++ } }
    $vowels = ([regex]::Matches($base, '[aeiouAEIOU]')).Count
    if ($letters -gt 0 -and ($vowels * 2) -gt $letters) { return $false }
    if ($hasDigit) { return $true }
    if ($vowels -eq 0) { return $true }
    return $false
}

function Get-SuspiciousBehaviorObjects {
    $out = @()
    $procs = @(Get-Process -ErrorAction SilentlyContinue)
    $t1 = @{}
    foreach ($p in $procs) { try { $t1[$p.Id] = $p.TotalProcessorTime.TotalMilliseconds } catch { } }
    Start-Sleep -Milliseconds 800
    $cpuCount = [Math]::Max(1, [Environment]::ProcessorCount)
    $writable = @($env:PUBLIC, $env:ProgramData, (Join-Path $env:LOCALAPPDATA ''), $env:APPDATA, $env:TEMP)
    $sysWhitelist = @('system','idle','registry','memcompression','svchost','csrss','services','lsass','winlogon','smss','wininit','dwm','explorer','fontdrvhost','sihost','ctfmon','taskhostw','runtimebroker','spoolsv','searchindexer','securityhealthservice','msmpeng','nissrv','audiodg','conhost')
    foreach ($p in $procs) {
        try { $path = $p.Path } catch { $path = $null }
        if (-not $path) { continue }
        if ($sysWhitelist -contains $p.ProcessName.ToLower()) { continue }
        $garbled = Test-GarbledName $p.ProcessName
        if (-not $garbled) { continue }
        $low = $path.ToLower()
        $inWritable = $false
        foreach ($w in $writable) { if ($w -and $low.StartsWith($w.ToLower())) { $inWritable = $true; break } }
        $cpu = 0.0
        try {
            if ($t1.ContainsKey($p.Id)) { $p.Refresh(); $d = $p.TotalProcessorTime.TotalMilliseconds - $t1[$p.Id]; $cpu = [Math]::Round(($d / 800.0) / $cpuCount * 100.0, 1) }
        } catch { }
        $conns = 0
        try { $conns = @(Get-NetTCPConnection -OwningProcess $p.Id -ErrorAction SilentlyContinue).Count } catch { }
        $sig = Get-AuthenticodeSignature -LiteralPath $path -ErrorAction SilentlyContinue
        $msSigned = ($sig -and $sig.Status -eq 'Valid' -and $sig.SignerCertificate -and ($sig.SignerCertificate.Subject -match 'Microsoft'))
        if ($msSigned) { continue }
        $unsigned = (-not $sig) -or ($sig.Status -ne 'Valid')
        $reasons = @('乱码/随机名')
        if ($inWritable) { $reasons += '用户可写目录' }
        if ($unsigned) { $reasons += '无有效签名' }
        if ($cpu -ge 40) { $reasons += "CPU ${cpu}%" }
        if ($conns -ge 15) { $reasons += "连接 $conns" }
        if ($inWritable -or $unsigned -or $cpu -ge 40 -or $conns -ge 15) {
            $out += [pscustomobject]@{ Name = $p.ProcessName; Id = $p.Id; Cpu = $cpu; Conns = $conns; Path = $path; Reasons = ($reasons -join '/') }
        }
    }
    return @($out)
}

function Get-BehaviorSuspiciousProcesses {
    return @(Get-SuspiciousBehaviorObjects | ForEach-Object { "$($_.Name) (PID $($_.Id)) CPU $($_.Cpu)% 连接 $($_.Conns) [$($_.Reasons)] => $($_.Path)" })
}

function Invoke-KillBehaviorProcesses {
    $n = 0
    for ($round = 0; $round -lt 4; $round++) {
        $objs = @(Get-SuspiciousBehaviorObjects)
        if ($objs.Count -eq 0) { break }
        foreach ($o in $objs) {
            L "[行为-可疑] $($o.Name) (PID $($o.Id)) CPU $($o.Cpu)% 连接 $($o.Conns) [$($o.Reasons)] => $($o.Path)"
            try { Stop-Process -Id $o.Id -Force -ErrorAction Stop; L "[已终止] $($o.Name)" } catch { }
            if ($o.Path) { Invoke-FirewallBlock @($o.Path) | Out-Null }
            if ($o.Path -and (Test-Path -LiteralPath $o.Path)) {
                cmd /c "takeown /F `"$($o.Path)`" >nul 2>&1"
                cmd /c "icacls `"$($o.Path)`" /inheritance:e /T /C /Q >nul 2>&1"
                try { Remove-Item -LiteralPath $o.Path -Force -ErrorAction Stop; L "[已删除] $($o.Path)" } catch { L "[删除失败] $($o.Path)" }
            }
            $n++
        }
        Start-Sleep -Milliseconds 400
    }
    L "[完成] 行为可疑进程处理 $n 个"
}

# 伪造官方安全软件：文件信息声称为 360/腾讯/火绒/金山/Defender 等，但无有效签名
function Get-FakeSecuritySoftware {
    $hits = @()
    $dirs = @((Join-Path $env:PUBLIC ''), $env:ProgramData, (Join-Path $env:LOCALAPPDATA ''), $env:APPDATA, $env:TEMP) | Where-Object { $_ -and (Test-Path -LiteralPath $_) } | Sort-Object -Unique
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    foreach ($d in $dirs) {
        if ($sw.Elapsed.TotalSeconds -gt 15) { break }
        foreach ($f in @(Get-ChildItem -LiteralPath $d -Filter *.exe -File -Recurse -Depth 3 -ErrorAction SilentlyContinue -Force | Select-Object -First 200)) {
            if ($sw.Elapsed.TotalSeconds -gt 15) { break }
            $vi = $null
            try { $vi = $f.VersionInfo } catch { }
            if (-not $vi) { continue }
            $joined = "$($vi.ProductName) $($vi.FileDescription) $($vi.CompanyName)"
            if ($joined -match '360|腾讯|QQ|电脑管家|火绒|huorong|金山|毒霸|kingsoft|瑞星|rising|安全卫士|安全中心|security center|defender|antivirus|杀毒') {
                $sig = Get-AuthenticodeSignature -LiteralPath $f.FullName -ErrorAction SilentlyContinue
                if (-not $sig -or $sig.Status -ne 'Valid') {
                    $hits += "伪造安全软件: $($f.FullName) [产品=$($vi.ProductName) 厂商=$($vi.CompanyName) 签名=$($sig.Status)]"
                }
            }
        }
    }
    return @($hits | Sort-Object -Unique)
}

function Invoke-RemoveFakeSecurity {
    $n = 0
    foreach ($h in @(Get-FakeSecuritySoftware)) {
        $path = $null
        if ($h -match '伪造安全软件: (.+?) \[') { $path = $Matches[1] }
        if (-not $path) { continue }
        L "[伪造安全软件-可疑] $path"
        Get-Process -ErrorAction SilentlyContinue | Where-Object { try { $_.Path -eq $path } catch { $false } } | ForEach-Object { try { Stop-Process -Id $_.Id -Force -ErrorAction Stop } catch { } }
        Invoke-FirewallBlock @($path) | Out-Null
        try { Remove-Item -LiteralPath $path -Force -ErrorAction Stop; L "[已删除] $path"; $n++ } catch { L "[删除失败] $path" }
    }
    L "[完成] 伪造安全软件处理 $n 个"
}

# 安全厂商 HOSTS 劫持（指向 127.0.0.1 / 0.0.0.0，导致无法访问 360、QQ管家等下载地址）
function Get-HostsTampering {
    $hits = @()
    foreach ($f in @((Join-Path $env:WINDIR 'System32\drivers\etc\hosts'), (Join-Path $env:WINDIR 'System32\drivers\etc\hosts.ics'))) {
        if (-not (Test-Path -LiteralPath $f)) { continue }
        $i = 0
        foreach ($line in (Get-Content -LiteralPath $f -ErrorAction SilentlyContinue)) {
            $i++
            $t = $line.Trim()
            if (-not $t -or $t.StartsWith('#')) { continue }
            if ($t -match '^\s*[0-9a-fA-F:\.]+\s+(\S+)') {
                if (Test-VendorHost $Matches[1]) { $hits += "HOSTS 劫持[${f}:${i}]: $t" }
            }
        }
    }
    return @($hits)
}

function Repair-HostsFile([string]$BackupDir) {
    $hosts = Join-Path $env:WINDIR 'System32\drivers\etc\hosts'
    if (-not (Test-Path -LiteralPath $hosts)) { return }
    try { Copy-Item -LiteralPath $hosts -Destination (Join-Path $BackupDir 'hosts.bak') -Force -ErrorAction SilentlyContinue } catch { }
    $kept = New-Object System.Collections.Generic.List[string]
    $removed = 0
    foreach ($line in (Get-Content -LiteralPath $hosts -ErrorAction SilentlyContinue)) {
        $t = $line.Trim()
        if ($t -and -not $t.StartsWith('#') -and ($t -match '^\s*[0-9a-fA-F:\.]+\s+(\S+)') -and (Test-VendorHost $Matches[1])) {
            L "[HOSTS 已移除] $line"
            $removed++
            continue
        }
        $kept.Add($line) | Out-Null
    }
    if ($removed -gt 0) {
        try {
            Set-Content -LiteralPath $hosts -Value $kept -Encoding ASCII -Force
            L "[完成] HOSTS 已清理 $removed 条安全厂商劫持"
            cmd /c "ipconfig /flushdns >nul 2>&1"
            L "[完成] 已刷新 DNS 缓存"
        } catch { L "[警告] 写入 HOSTS 失败: $($_.Exception.Message)" }
    } else { L "[完成] HOSTS 无安全厂商劫持" }
}

# IFEO 映像劫持（Debugger）——木马常借此让 360/Defender/QQ管家 打不开
function Get-IfeoHijacks {
    $hits = @()
    $root = 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options'
    foreach ($k in (Get-ChildItem $root -ErrorAction SilentlyContinue)) {
        $dbg = $k.GetValue('Debugger')
        if ($dbg) {
            $extra = ''
            if (Test-VendorHost $k.PSChildName) { $extra = '（针对安全软件/厂商，疑似劫持）' }
            $hits += "IFEO 劫持: $($k.PSChildName) -> Debugger=$dbg $extra"
        }
    }
    return @($hits)
}

function Repair-IfeoHijacks {
    $root = 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options'
    $n = 0
    foreach ($k in (Get-ChildItem $root -ErrorAction SilentlyContinue)) {
        $dbg = $k.GetValue('Debugger')
        if (-not $dbg) { continue }
        if ((Test-VendorHost $k.PSChildName) -or ($dbg -match 'SYSTEM-RAMOS-ZDY') -or (Test-GarbledName $k.PSChildName)) {
            try { Remove-ItemProperty -Path $k.PSPath -Name 'Debugger' -Force -ErrorAction Stop; L "[已清除 IFEO 劫持] $($k.PSChildName)"; $n++ } catch { }
        }
    }
    L "[完成] 已清除 IFEO 劫持 $n 项"
}

# 网络劫持：系统代理指向本机回环、netsh 端口转发
function Get-NetworkHijack {
    $hits = @()
    try {
        $is = Get-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Internet Settings' -ErrorAction SilentlyContinue
        if ($is -and $is.ProxyEnable -eq 1 -and $is.ProxyServer -match '^\s*(127\.0\.0\.1|localhost|0\.0\.0\.0)') {
            $hits += "系统代理指向本机(疑似劫持): $($is.ProxyServer)"
        }
    } catch { }
    try {
        $pp = @(cmd /c "netsh interface portproxy show all 2>nul")
        foreach ($line in $pp) {
            if ($line -match '\d{1,3}(\.\d{1,3}){3}\s+\d+\s+\d{1,3}(\.\d{1,3}){3}\s+\d+') { $hits += "netsh 端口代理: $($line.Trim())" }
        }
    } catch { }
    return @($hits)
}

function Restore-NetworkSettings {
    try {
        $is = Get-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Internet Settings' -ErrorAction SilentlyContinue
        if ($is -and $is.ProxyEnable -eq 1 -and $is.ProxyServer -match '^\s*(127\.0\.0\.1|localhost|0\.0\.0\.0)') {
            Set-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Internet Settings' -Name 'ProxyEnable' -Value 0 -Type DWord -ErrorAction SilentlyContinue
            Remove-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Internet Settings' -Name 'ProxyServer' -ErrorAction SilentlyContinue
            L '[完成] 已关闭指向本机的系统代理'
        }
    } catch { }
    try {
        $pp = @(cmd /c "netsh interface portproxy show all 2>nul")
        $has = $false
        foreach ($line in $pp) { if ($line -match '\d{1,3}(\.\d{1,3}){3}\s+\d+\s+\d{1,3}(\.\d{1,3}){3}\s+\d+') { $has = $true } }
        if ($has) { cmd /c "netsh interface portproxy reset >nul 2>&1"; L '[完成] 已清除 netsh 端口代理' }
    } catch { }
}

# ============ 全面持久化 / 无文件 / 驱动 / C2 / 规则 检测（增强） ============

function Test-WritablePath([string]$p) {
    if (-not $p) { return $false }
    $low = $p.ToLower()
    $wl = @($env:PUBLIC, $env:ProgramData, (Join-Path $env:LOCALAPPDATA ''), $env:APPDATA, $env:TEMP, (Join-Path $env:WINDIR 'Temp'), (Join-Path $env:USERPROFILE 'Downloads'), (Join-Path $env:USERPROFILE 'Desktop'))
    foreach ($w in $wl) { if ($w -and $low.StartsWith($w.ToLower())) { return $true } }
    return $false
}

function Resolve-Exec([string]$p) {
    if (-not $p) { return $null }
    $exe = Extract-ExePath $p
    if (-not $exe) { return $null }
    $exe = [Environment]::ExpandEnvironmentVariables($exe)
    if ($exe -match '^\\SystemRoot') { $exe = $exe -replace '^\\SystemRoot', $env:WINDIR }
    elseif ($exe -match '^System32\\') { $exe = Join-Path $env:WINDIR $exe }
    elseif ($exe -match '^SysWOW64\\') { $exe = Join-Path $env:WINDIR $exe }
    return $exe
}

# 可疑可执行文件：乱码/随机名，或位于用户可写目录且非微软有效签名
function Test-SuspiciousExec([string]$p) {
    $exe = Resolve-Exec $p
    if (-not $exe -or -not ($exe -match '^[A-Za-z]:\\')) { return $false }
    if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) { return $false }
    if ($exe.ToLower().StartsWith($env:WINDIR.ToLower())) { return $false }
    if (Test-GarbledName ([System.IO.Path]::GetFileName($exe))) { return $true }
    if (Test-WritablePath $exe) {
        $sig = Get-AuthenticodeSignature -LiteralPath $exe -ErrorAction SilentlyContinue
        if ($sig -and $sig.Status -eq 'Valid' -and $sig.SignerCertificate -and $sig.SignerCertificate.Subject -match 'Microsoft') { return $false }
        return $true
    }
    return $false
}

# 更窄的"可疑落地目录"（不含普通 AppData\Local\Programs 等正规安装目录）
function Test-SuspiciousDir([string]$p) {
    if (-not $p) { return $false }
    $low = $p.ToLower()
    foreach ($w in @($env:PUBLIC, $env:ProgramData, (Join-Path $env:WINDIR 'Temp'), $env:TEMP)) { if ($w -and $low.StartsWith($w.ToLower())) { return $true } }
    return $false
}

# 1) Winlogon / AppInit_DLLs / AppCertDlls / ShellServiceObjectDelayLoad / 屏幕保护
function Get-WinlogonPersistence {
    $hits = @()
    $wl = 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon'
    $defs = @{ Userinit = 'c:\windows\system32\userinit.exe'; Shell = 'explorer.exe' }
    foreach ($v in @('Userinit','Shell','AppInit_DLLs')) {
        $val = $null
        try { $val = [string](Get-ItemProperty -Path $wl -Name $v -ErrorAction SilentlyContinue).$v } catch { }
        if ([string]::IsNullOrWhiteSpace($val)) { continue }
        $trim = $val.Trim().TrimEnd(',').Trim()
        if ($defs.ContainsKey($v) -and ($trim -ieq $defs[$v])) { continue }
        $reason = '数值被修改'
        if (Test-SuspiciousExec $val) { $reason = '指向可写目录/乱码程序' }
        $hits += "Winlogon $v = $val [$reason]"
    }
    foreach ($k in @('HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Windows','HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows NT\CurrentVersion\Windows')) {
        $ad = $null
        try { $ad = [string](Get-ItemProperty -Path $k -Name 'AppInit_DLLs' -ErrorAction SilentlyContinue).AppInit_DLLs } catch { }
        if ($ad -and $ad.Trim()) { $hits += "AppInit_DLLs [$k] = $($ad.Trim())" }
    }
    $appcert = 'HKLM:\SYSTEM\CurrentControlSet\Control\Session Manager\AppCertDlls'
    if (Test-Path $appcert) {
        $item = Get-Item $appcert
        foreach ($n in $item.Property) {
            if ($n -like 'PS*') { continue }
            try { $hits += "AppCertDlls [$n] = $((Get-ItemProperty -Path $appcert -Name $n).$n)" } catch { }
        }
    }
    foreach ($sso in @('HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\ShellServiceObjects','HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\SharedTaskScheduler')) {
        if (-not (Test-Path $sso)) { continue }
        $item = Get-Item $sso
        foreach ($n in $item.Property) {
            if ($n -like 'PS*') { continue }
            try { $hits += "ShellServiceObjectDelayLoad[$sso] $n = $((Get-ItemProperty -Path $sso -Name $n).$n)" } catch { }
        }
    }
    try {
        $scr = [string](Get-ItemProperty -Path 'HKCU:\Control Panel\Desktop' -Name 'SCRNSAVE.EXE' -ErrorAction SilentlyContinue).'SCRNSAVE.EXE'
        if ($scr -and (Test-SuspiciousExec $scr)) { $hits += "屏幕保护程序 SCRNSAVE.EXE = $scr" }
    } catch { }
    return @($hits)
}

# 1) Run / RunOnce 各 hive + Active Setup + Startup 文件夹
function Get-AllRunPersistence {
    $hits = @()
    $runKeys = @(
        'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run',
        'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce',
        'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run',
        'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\RunOnce',
        'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run',
        'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce'
    )
    foreach ($rk in $runKeys) {
        if (-not (Test-Path $rk)) { continue }
        $it = Get-Item -Path $rk -ErrorAction SilentlyContinue
        if (-not $it) { continue }
        foreach ($n in $it.Property) {
            if ($n -like 'PS*') { continue }
            $val = [string](Get-ItemProperty -Path $rk -Name $n -ErrorAction SilentlyContinue).$n
            if (Test-SuspiciousExec $val) { $hits += "启动项[$rk] $n = $val" }
        }
    }
    try {
        foreach ($sid in (Get-ChildItem 'Registry::HKEY_USERS' -ErrorAction SilentlyContinue)) {
            if ($sid.PSChildName -in @('.DEFAULT','S-1-5-18','S-1-5-19','S-1-5-20')) { continue }
            foreach ($sub in @('SOFTWARE\Microsoft\Windows\CurrentVersion\Run','SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce')) {
                $k = Join-Path $sid.PSPath $sub
                $it = Get-Item -Path $k -ErrorAction SilentlyContinue
                if (-not $it) { continue }
                foreach ($n in $it.Property) {
                    if ($n -like 'PS*') { continue }
                    $val = [string](Get-ItemProperty -Path $k -Name $n -ErrorAction SilentlyContinue).$n
                    if (Test-SuspiciousExec $val) { $hits += "启动项[HKU\$($sid.PSChildName)] $n = $val" }
                }
            }
        }
    } catch { }
    foreach ($as in @('HKLM:\SOFTWARE\Microsoft\Active Setup\Installed Components','HKLM:\SOFTWARE\WOW6432Node\Microsoft\Active Setup\Installed Components')) {
        if (-not (Test-Path $as)) { continue }
        foreach ($c in (Get-ChildItem $as -ErrorAction SilentlyContinue)) {
            $stub = [string]$c.GetValue('StubPath')
            if ($stub -and (Test-SuspiciousExec $stub)) { $hits += "Active Setup[$($c.PSChildName)] StubPath = $stub" }
        }
    }
    $sh = $null
    try { $sh = New-Object -ComObject WScript.Shell } catch { }
    foreach ($d in @([Environment]::GetFolderPath('Startup'), [Environment]::GetFolderPath('CommonStartup'))) {
        if (-not $d -or -not (Test-Path -LiteralPath $d)) { continue }
        foreach ($f in @(Get-ChildItem -LiteralPath $d -Force -ErrorAction SilentlyContinue)) {
            if ($f.Extension -ieq '.lnk' -and $sh) {
                $t = $null; try { $t = $sh.CreateShortcut($f.FullName).TargetPath } catch { }
                if ($t -and (Test-SuspiciousExec $t)) { $hits += "启动文件夹: $($f.FullName) -> $t" }
            } elseif ($f.Extension -in @('.exe','.bat','.cmd','.vbs','.ps1','.js','.url','.pif','.scr')) {
                $hits += "启动文件夹: $($f.FullName)"
            }
        }
    }
    return @($hits)
}

# 2) WMI 永久事件订阅（无文件持久化）
function Get-WmiEventSubscriptions {
    $hits = @()
    $consumers = @()
    try { $consumers = @(Get-CimInstance -Namespace root\subscription -ClassName __EventConsumer -ErrorAction SilentlyContinue) } catch { }
    foreach ($c in $consumers) {
        $cls = $c.CimClass.CimClassName
        if ($cls -in @('CommandLineEventConsumer','ActiveScriptEventConsumer')) {
            $detail = ''
            if ($cls -eq 'CommandLineEventConsumer') { $detail = ("$($c.ExecutablePath) $($c.CommandLineTemplate)").Trim() } else { $detail = [string]$c.ScriptText }
            if ($detail.Length -gt 200) { $detail = $detail.Substring(0,200) }
            $hits += "WMI 持久化[$cls] $($c.Name) => $($detail.Trim())"
        }
    }
    return @($hits)
}

# 安全取文件名（注册表值可能含非法路径字符，直接 GetFileName 会抛异常）
function Get-FileNameSafe([string]$p) {
    try { return [System.IO.Path]::GetFileName($p) } catch { return $null }
}

# 3) COM 劫持：HKCU/HKLM Classes\CLSID 的 InprocServer32/LocalServer32 指向用户可写目录
function Get-ComHijacks {
    $hits = @()
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    foreach ($root in @('HKCU:\Software\Classes\CLSID','HKLM:\Software\Classes\CLSID')) {
        if (-not (Test-Path $root)) { continue }
        foreach ($sub in (Get-ChildItem $root -ErrorAction SilentlyContinue)) {
            if ($sw.Elapsed.TotalSeconds -gt 15) { break }
            foreach ($srv in @('InprocServer32','LocalServer32')) {
                $pk = Join-Path $sub.PSPath $srv
                if (-not (Test-Path $pk)) { continue }
                $dll = $null
                try { $dll = (Get-Item -LiteralPath $pk -ErrorAction SilentlyContinue).GetValue('') } catch { }
                if (-not $dll) { continue }
                $dll = [Environment]::ExpandEnvironmentVariables(([string]$dll).Trim('"'))
                $resolved = Resolve-Exec ([string]$dll)
                if (-not $resolved) { $resolved = $dll }
                if ($resolved -match '^[A-Za-z]:\\' -and (Test-WritablePath $resolved) -and ((Test-GarbledName (Get-FileNameSafe $resolved)) -or (Test-SuspiciousDir $resolved))) {
                    $valid = $false
                    if (Test-Path -LiteralPath $resolved -PathType Leaf) { $sg = Get-AuthenticodeSignature -LiteralPath $resolved -ErrorAction SilentlyContinue; if ($sg -and $sg.Status -eq 'Valid') { $valid = $true } }
                    if (-not $valid) { $hits += "COM 劫持[$($sub.PSChildName) $srv] => $resolved" }
                }
            }
        }
    }
    return @($hits | Select-Object -First 100)
}
function Fix-ComHijacks([string]$BackupDir) {
    $n = 0
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    foreach ($root in @('HKCU:\Software\Classes\CLSID','HKLM:\Software\Classes\CLSID')) {
        if (-not (Test-Path $root)) { continue }
        foreach ($sub in (Get-ChildItem $root -ErrorAction SilentlyContinue)) {
            if ($sw.Elapsed.TotalSeconds -gt 15) { break }
            foreach ($srv in @('InprocServer32','LocalServer32')) {
                $pk = Join-Path $sub.PSPath $srv
                if (-not (Test-Path $pk)) { continue }
                $dll = $null
                try { $dll = (Get-Item -LiteralPath $pk -ErrorAction SilentlyContinue).GetValue('') } catch { }
                if (-not $dll) { continue }
                $dll = [Environment]::ExpandEnvironmentVariables(([string]$dll).Trim('"'))
                $resolved = Resolve-Exec ([string]$dll)
                if (-not $resolved) { $resolved = $dll }
                if ($resolved -match '^[A-Za-z]:\\' -and (Test-WritablePath $resolved) -and ((Test-GarbledName (Get-FileNameSafe $resolved)) -or (Test-SuspiciousDir $resolved))) {
                    $valid = $false
                    if (Test-Path -LiteralPath $resolved -PathType Leaf) { $sg = Get-AuthenticodeSignature -LiteralPath $resolved -ErrorAction SilentlyContinue; if ($sg -and $sg.Status -eq 'Valid') { $valid = $true } }
                    if ($valid) { continue }
                    $safe = ($sub.PSPath -replace '[^A-Za-z0-9_]','_')
                    $bf = Join-Path $BackupDir ("com_" + $safe + ".reg")
                    cmd /c "reg export `"$($pk -replace '^Microsoft\.PowerShell\.Core\\Registry::','' -replace '^HKEY_','HK' -replace '_HKEY','HKEY')`" `"$bf`" /y >nul 2>&1"
                    try { Remove-Item -LiteralPath $pk -Recurse -Force -ErrorAction Stop; L "[已清除 COM 劫持] $($sub.PSChildName) $srv => $resolved"; $n++ } catch { }
                }
            }
        }
    }
    L "[完成] COM 劫持处理 $n 项"
}

# 4) 无文件载荷（脚本宿主 / 编码命令）
function Get-FilelessPayloads {
    $hits = @()
    $pats = @(
        'mshta(\.exe)?\s+(https?://|javascript:|vbscript:)',
        'rundll32(\.exe)?\s+\S*(temp|appdata|public|programdata|downloads|desktop)\S*\.dll',
        'regsvr32(\.exe)?\s+.*(scrobj|\.sct|/i:http)',
        'wmic(\.exe)?\s+.*(process\s+call\s+create|/node:)',
        'powershell(\.exe)?\s+.*(-enc\b|-encodedcommand|frombase64string|downloadstring|\biex\b|invoke-expression)',
        'certutil(\.exe)?\s+.*(-urlcache|-decode|-decodehex)',
        'bitsadmin(\.exe)?\s+.*(/transfer|/addfile)',
        'installutil(\.exe)?\s+',
        'msbuild(\.exe)?\s+.*\.(xml|proj)',
        'wscript(\.exe)?\s+.*\.(jse|vbe)\b'
    )
    foreach ($p in @(Get-CimInstance Win32_Process -ErrorAction SilentlyContinue)) {
        if ($p.ProcessId -eq $PID) { continue }
        $cl = [string]$p.CommandLine
        if (-not $cl) { continue }
        foreach ($pat in $pats) {
            if ($cl -imatch $pat) { $hits += "无文件载荷: $($p.Name) (PID $($p.ProcessId)) => $cl"; break }
        }
    }
    return @($hits)
}

function Invoke-KillFilelessProcesses {
    $n = 0
    foreach ($h in @(Get-FilelessPayloads)) {
        if ($h -match 'PID (\d+)') {
            $id = [int]$Matches[1]
            if ($id -eq $PID) { continue }
            try { Stop-Process -Id $id -Force -ErrorAction Stop; L "[已终止-无文件载荷] PID=$id"; $n++ } catch { }
        }
    }
    L "[完成] 无文件载荷进程处理 $n 个"
}

# 5) 快捷方式劫持
function Get-ShortcutHijacks {
    $hits = @()
    $dirs = @(
        [Environment]::GetFolderPath('Desktop'),
        [Environment]::GetFolderPath('CommonDesktopDirectory'),
        [Environment]::GetFolderPath('StartMenu'),
        [Environment]::GetFolderPath('CommonStartMenu')
    ) | Where-Object { $_ -and (Test-Path -LiteralPath $_) } | Sort-Object -Unique
    $sh = $null
    try { $sh = New-Object -ComObject WScript.Shell } catch { return $hits }
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    foreach ($d in $dirs) {
        foreach ($lnk in @(Get-ChildItem -LiteralPath $d -Recurse -Filter *.lnk -File -ErrorAction SilentlyContinue)) {
            if ($sw.Elapsed.TotalSeconds -gt 15) { break }
            $sc = $null
            try { $sc = $sh.CreateShortcut($lnk.FullName) } catch { continue }
            $target = [string]$sc.TargetPath
            $arg = [string]$sc.Arguments
            $reason = $null
            if ($arg -match 'https?://|javascript:|\.sct|scrobj') { $reason = '参数含远程/脚本链接' }
            elseif ($target -match '(mshta|rundll32|regsvr32|wscript|cscript|certutil)\.exe$' -and -not ($target.ToLower().StartsWith($env:WINDIR.ToLower()))) { $reason = '目标为脚本宿主' }
            elseif ($target -and (Test-GarbledName ([System.IO.Path]::GetFileName($target)))) { $reason = '目标为乱码/随机名' }
            if ($reason) { $hits += "快捷方式可疑: $($lnk.FullName) => $target $arg [$reason]" }
        }
    }
    return @($hits)
}

# 6) 可疑驱动 / BYOVD
function Get-SuspiciousDrivers {
    $hits = @()
    $knownVuln = @('gdrv','gdrv2','iqvw64e','rtcore64','winring0','winring0x64','eneio','enetechio','asio','asio2','asio3','kprocesshacker','msio','physmem','dbutil','dbutil_2_3','capcom','speedfan','cpuz','ntiolib','atszio','gpcidrv','glckio2','nsepin','winio')
    $svcRoot = 'HKLM:\SYSTEM\CurrentControlSet\Services'
    foreach ($k in (Get-ChildItem $svcRoot -ErrorAction SilentlyContinue)) {
        $type = $k.GetValue('Type')
        if ($type -ne 1 -and $type -ne 2) { continue }
        $name = $k.PSChildName
        $img = [string]$k.GetValue('ImagePath')
        $exe = Resolve-Exec $img
        if ($exe -and $exe -match '^[A-Za-z]:\\' -and (Test-WritablePath $exe)) { $hits += "可疑驱动服务(可写目录): $name => $img" }
        if ($knownVuln -contains $name.ToLower()) { $hits += "已知易受攻击驱动(BYOVD 风险): $name => $img" }
    }
    return @($hits)
}

# 7) 出站 C2 / 矿池连接
function Get-C2Connections {
    $hits = @()
    $minePorts = $script:C2Ports
    $conns = @()
    try { $conns = @(Get-NetTCPConnection -State Established -ErrorAction SilentlyContinue) } catch { return $hits }
    foreach ($c in $conns) {
        if ($minePorts -contains [int]$c.RemotePort) {
            $pp = Get-Process -Id $c.OwningProcess -ErrorAction SilentlyContinue
            $pn = if ($pp) { $pp.ProcessName } else { "PID $($c.OwningProcess)" }
            $hits += "疑似矿池/C2 连接: $pn (PID $($c.OwningProcess)) -> $($c.RemoteAddress):$($c.RemotePort)"
        }
    }
    return @($hits)
}

function Invoke-BlockC2 {
    $n = 0
    $minePorts = $script:C2Ports
    $conns = @()
    try { $conns = @(Get-NetTCPConnection -State Established -ErrorAction SilentlyContinue) } catch { return }
    $seen = @{}
    foreach ($c in $conns) {
        if ($minePorts -contains [int]$c.RemotePort) {
            $pp = Get-Process -Id $c.OwningProcess -ErrorAction SilentlyContinue
            if (-not $pp) { continue }
            $path = $null; try { $path = $pp.Path } catch { }
            if ($path -and -not $seen.ContainsKey($path)) {
                $seen[$path] = $true
                Invoke-FirewallBlock @($path) | Out-Null
                try { Stop-Process -Id $pp.Id -Force -ErrorAction Stop; L "[已终止-C2] $($pp.ProcessName) -> $($c.RemoteAddress):$($c.RemotePort)"; $n++ } catch { }
            }
        }
    }
    L "[完成] C2/矿池连接处理 $n 个"
}

# 8) 临时/回收站载荷落地
function Get-PayloadDrops {
    $hits = @()
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $dirs = @($env:TEMP, (Join-Path $env:LOCALAPPDATA 'Temp'), (Join-Path $env:PUBLIC ''), (Join-Path $env:ProgramData ''))
    foreach ($d in ($dirs | Where-Object { $_ -and (Test-Path -LiteralPath $_) } | Sort-Object -Unique)) {
        if ($sw.Elapsed.TotalSeconds -gt 15) { break }
        foreach ($f in @(Get-ChildItem -LiteralPath $d -File -Recurse -Depth 2 -ErrorAction SilentlyContinue -Force | Where-Object { $_.Extension -in @('.exe','.dll','.scr','.bat','.cmd','.vbs','.js') } | Select-Object -First 200)) {
            if ($sw.Elapsed.TotalSeconds -gt 15) { break }
            if (Test-GarbledName $f.Name) {
                $sig = Get-AuthenticodeSignature -LiteralPath $f.FullName -ErrorAction SilentlyContinue
                if (-not ($sig -and $sig.Status -eq 'Valid')) { $hits += "载荷落地(乱码名): $($f.FullName)" }
            }
        }
    }
    try {
        $rb = Join-Path $env:SystemDrive '$Recycle.Bin'
        foreach ($f in @(Get-ChildItem -LiteralPath $rb -File -Recurse -Depth 2 -Force -ErrorAction SilentlyContinue | Where-Object { $_.Extension -in @('.exe','.dll') } | Select-Object -First 50)) {
            $hits += "回收站载荷: $($f.FullName)"
        }
    } catch { }
    return @($hits | Sort-Object -Unique)
}

function Remove-PayloadDrops {
    $n = 0
    foreach ($h in @(Get-PayloadDrops)) {
        $p = $null
        if ($h -match '^载荷落地\(乱码名\): (.+)$') { $p = $Matches[1] }
        if (-not $p -or -not (Test-Path -LiteralPath $p)) { continue }
        try { Remove-Item -LiteralPath $p -Force -ErrorAction Stop; L "[已删除-载荷落地] $p"; $n++ } catch { L "[删除失败-载荷落地] $p" }
    }
    L "[完成] 载荷落地处理 $n 个"
}

# 10) 自定义规则库（YARA-lite：命令行/路径正则）
function Get-CustomRuleHits {
    $hits = @()
    $ruleFile = Join-Path $PSScriptRoot '规则库.txt'
    if (-not (Test-Path -LiteralPath $ruleFile)) { return $hits }
    $section = ''; $cmdRx = @(); $pathRx = @()
    foreach ($line in Get-Content -LiteralPath $ruleFile -Encoding UTF8) {
        $t = $line.Trim()
        if ($t.StartsWith('[')) { $section = $t; continue }
        if (-not $t -or $t.StartsWith('#')) { continue }
        switch ($section) {
            '[命令行正则]' { $cmdRx += $t }
            '[路径正则]'   { $pathRx += $t }
        }
    }
    if ($cmdRx.Count -gt 0) {
        foreach ($p in @(Get-CimInstance Win32_Process -ErrorAction SilentlyContinue)) {
            $cl = [string]$p.CommandLine
            if (-not $cl) { continue }
            foreach ($rx in $cmdRx) { try { if ($cl -imatch $rx) { $hits += "规则-命令行: $($p.Name) (PID $($p.ProcessId)) => $cl"; break } } catch { } }
        }
    }
    if ($pathRx.Count -gt 0) {
        $sw = [System.Diagnostics.Stopwatch]::StartNew()
        foreach ($d in @($env:TEMP, (Join-Path $env:PUBLIC ''), $env:APPDATA, (Join-Path $env:ProgramData ''))) {
            if (-not (Test-Path -LiteralPath $d)) { continue }
            foreach ($f in @(Get-ChildItem -LiteralPath $d -File -Recurse -Depth 2 -ErrorAction SilentlyContinue -Force | Select-Object -First 300)) {
                if ($sw.Elapsed.TotalSeconds -gt 12) { break }
                foreach ($rx in $pathRx) { try { if ($f.FullName -imatch $rx) { $hits += "规则-路径: $($f.FullName)"; break } } catch { } }
            }
        }
    }
    return @($hits | Select-Object -First 200)
}

# 可选：云查杀（仅当设置环境变量 VT_API_KEY 时启用）
function Get-CloudHashHits([string[]]$paths) {
    $hits = @()
    $key = $env:VT_API_KEY
    if (-not $key -or -not $paths) { return $hits }
    foreach ($p in ($paths | Where-Object { $_ } | Select-Object -First 20 | Sort-Object -Unique)) {
        if (-not (Test-Path -LiteralPath $p -PathType Leaf)) { continue }
        try {
            $h = (Get-FileHash -LiteralPath $p -Algorithm SHA256).Hash.ToLower()
            $r = Invoke-RestMethod -Uri "https://www.virustotal.com/api/v3/files/$h" -Headers @{ 'x-apikey' = $key } -TimeoutSec 15 -ErrorAction Stop
            $mal = [int]$r.data.attributes.last_analysis_stats.malicious
            if ($mal -gt 0) { $hits += "云查杀命中(VT $mal 引擎判黑): $p [$h]" }
        } catch { }
    }
    return $hits
}

# ============ 系统状态备份 / 一键还原 ============
$script:RestoreManifest = 'state.json'

function New-SystemRestorePoint([string]$desc) {
    try {
        Enable-ComputerRestore -Drive "$env:SystemDrive\" -ErrorAction SilentlyContinue
        Checkpoint-Computer -Description $desc -RestorePointType MODIFY_SETTINGS -ErrorAction Stop
        L "[还原点] 已创建系统还原点：$desc"
    } catch {
        L "[还原点] 未能创建系统还原点（可能被禁用或 24 小时内已创建）"
    }
}

function Backup-SystemState([string]$BackupDir) {
    New-Item -ItemType Directory -Force -Path $BackupDir | Out-Null
    $regKeys = @(
        'HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon',
        'HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Windows',
        'HKLM\SOFTWARE\WOW6432Node\Microsoft\Windows NT\CurrentVersion\Windows',
        'HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\AppCertDlls',
        'HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\SharedTaskScheduler',
        'HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Run',
        'HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce',
        'HKLM\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run',
        'HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\Run',
        'HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options',
        'HKCU\Software\Microsoft\Windows\CurrentVersion\Internet Settings',
        'HKLM\SOFTWARE\Policies\Microsoft\Windows Defender'
    )
    $i = 0
    foreach ($rk in $regKeys) {
        $i++
        $file = Join-Path $BackupDir ("reg_" + $i.ToString('00') + "_" + ($rk -replace '[\\/:*?"<>| ]','_') + ".reg")
        cmd /c "reg export `"$rk`" `"$file`" /y >nul 2>&1"
    }
    $svc = @()
    foreach ($s in (Get-SuspiciousServices)) {
        $name = ($s -split '  =>  ')[0].Trim()
        try { $start = (Get-ItemProperty -Path "HKLM:\SYSTEM\CurrentControlSet\Services\$name" -Name Start -ErrorAction SilentlyContinue).Start; if ($null -ne $start) { $svc += @{ name = $name; start = [int]$start } } } catch { }
    }
    $hosts = Join-Path $env:WINDIR 'System32\drivers\etc\hosts'
    $hasHosts = $false
    if (Test-Path -LiteralPath $hosts) { Copy-Item -LiteralPath $hosts -Destination (Join-Path $BackupDir 'hosts.bak') -Force -ErrorAction SilentlyContinue; $hasHosts = $true }
    $proxy = $null
    try { $is = Get-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Internet Settings' -ErrorAction SilentlyContinue; $proxy = @{ Enable = $is.ProxyEnable; Server = [string]$is.ProxyServer; Override = [string]$is.ProxyOverride } } catch { }
    $manifest = [ordered]@{
        created  = (Get-Date -Format 'yyyy-MM-dd HH:mm:ss')
        services = $svc
        hosts    = $hasHosts
        proxy    = $proxy
    }
    $manifestJson = [string]($manifest | ConvertTo-Json -Depth 6)
    [IO.File]::WriteAllText((Join-Path $BackupDir $script:RestoreManifest), $manifestJson, (New-Object System.Text.UTF8Encoding($false)))
    # 记录本次清理的原始行，便于人工核对
    L "[备份] 系统状态（注册表/服务/hosts/代理）已备份到 $BackupDir（可用 -Mode restore 一键还原）"
    return $BackupDir
}

function Restore-SystemState([string]$BackupDir) {
    if (-not $BackupDir) {
        $desk = [Environment]::GetFolderPath('Desktop')
        $deskBak = @(Get-ChildItem -LiteralPath $desk -Directory -Filter 'malware_backup_*' -ErrorAction SilentlyContinue | Sort-Object Name -Descending)
        if ($deskBak.Count -gt 0) { $BackupDir = $deskBak[0].FullName }
        else {
            $root = Join-Path $env:LOCALAPPDATA 'MhyunBox\MalwareBackup'
            if (-not (Test-Path -LiteralPath $root)) { L '[还原] 未找到任何备份目录'; return }
            $BackupDir = (Get-ChildItem -LiteralPath $root -Directory | Sort-Object Name -Descending | Select-Object -First 1).FullName
        }
    }
    if (-not $BackupDir -or -not (Test-Path -LiteralPath $BackupDir)) { L "[还原] 备份目录不存在: $BackupDir"; return }
    L "== 开始从备份还原系统状态: $BackupDir =="
    foreach ($f in @(Get-ChildItem -LiteralPath $BackupDir -Filter '*.reg' -File -ErrorAction SilentlyContinue)) {
        cmd /c "reg import `"$($f.FullName)`" >nul 2>&1"
        L "[还原注册表] $($f.Name)"
    }
    $manifestPath = Join-Path $BackupDir $script:RestoreManifest
    if (Test-Path -LiteralPath $manifestPath) {
        try {
            $m = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
            foreach ($s in $m.services) {
                try { Set-ItemProperty -Path "HKLM:\SYSTEM\CurrentControlSet\Services\$($s.name)" -Name Start -Value ([int]$s.start) -Type DWord -Force -ErrorAction Stop; L "[还原服务] $($s.name) Start=$($s.start)" } catch { }
            }
            if ($m.proxy) {
                try {
                    Set-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Internet Settings' -Name 'ProxyEnable' -Value ([int]$m.proxy.Enable) -Type DWord -ErrorAction SilentlyContinue
                    if ($m.proxy.Server) { Set-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Internet Settings' -Name 'ProxyServer' -Value $m.proxy.Server -ErrorAction SilentlyContinue }
                } catch { }
            }
        } catch { L "[还原] state.json 解析失败，已跳过服务/代理还原" }
    }
    $hostsBak = Join-Path $BackupDir 'hosts.bak'
    if (Test-Path -LiteralPath $hostsBak) { try { Copy-Item -LiteralPath $hostsBak -Destination (Join-Path $env:WINDIR 'System32\drivers\etc\hosts') -Force -ErrorAction Stop; L '[还原] 已恢复 hosts'; cmd /c "ipconfig /flushdns >nul 2>&1" } catch { } }
    Restore-ScheduledTasks $BackupDir
    $fwRemoved = 0
    foreach ($r in @(Get-NetFirewallRule -ErrorAction SilentlyContinue | Where-Object { $_.DisplayName -like 'MhyunBox_阻断_*' -or $_.DisplayName -like 'MhyunBox_断网_*' })) {
        try { Remove-NetFirewallRule -DisplayName $r.DisplayName -ErrorAction Stop; $fwRemoved++ } catch { }
    }
    if ($fwRemoved -gt 0) { L "[还原] 已移除 $fwRemoved 条 MhyunBox 防火墙规则" }
    L '== 系统状态还原完成 =='
}

# 还原期间恢复被禁用/删除的计划任务
function Restore-ScheduledTasks([string]$BackupDir) {
    $jf = Join-Path $BackupDir 'tasks.json'
    if (-not (Test-Path -LiteralPath $jf)) { return }
    try { $list = Get-Content -LiteralPath $jf -Raw -Encoding UTF8 | ConvertFrom-Json } catch { return }
    $n = 0
    foreach ($it in @($list)) {
        try {
            $existing = Get-ScheduledTask -TaskName $it.name -TaskPath $it.path -ErrorAction SilentlyContinue
            if (-not $existing) {
                $xf = Join-Path (Join-Path $BackupDir 'tasks') $it.xml
                if (Test-Path -LiteralPath $xf) {
                    Register-ScheduledTask -Xml (Get-Content -LiteralPath $xf -Raw -Encoding UTF8) -TaskName $it.name -TaskPath $it.path -Force -ErrorAction Stop | Out-Null
                    $n++
                }
            } elseif ($it.enabled) {
                Enable-ScheduledTask -TaskName $it.name -TaskPath $it.path -ErrorAction Stop | Out-Null
                $n++
            }
        } catch { }
    }
    L "[还原] 已恢复计划任务 $n 项"
}

function Fix-WinlogonPersistence {
    $n = 0
    $wl = 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon'
    foreach ($v in @('Userinit','Shell')) {
        $val = $null
        try { $val = [string](Get-ItemProperty -Path $wl -Name $v -ErrorAction SilentlyContinue).$v } catch { }
        if ([string]::IsNullOrWhiteSpace($val)) { continue }
        $default = if ($v -eq 'Userinit') { 'C:\Windows\system32\userinit.exe,' } else { 'explorer.exe' }
        $def = if ($v -eq 'Userinit') { 'c:\windows\system32\userinit.exe' } else { 'explorer.exe' }
        if ($val.Trim().TrimEnd(',').Trim() -ieq $def) { continue }
        try { Set-ItemProperty -Path $wl -Name $v -Value $default -Force -ErrorAction Stop; L "[已还原 Winlogon $v] -> $default（原值: $val）"; $n++ } catch { }
    }
    foreach ($k in @('HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Windows','HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows NT\CurrentVersion\Windows')) {
        try {
            $ad = [string](Get-ItemProperty -Path $k -Name 'AppInit_DLLs' -ErrorAction SilentlyContinue).AppInit_DLLs
            if ($ad -and $ad.Trim()) {
                Set-ItemProperty -Path $k -Name 'AppInit_DLLs' -Value '' -Force -ErrorAction SilentlyContinue
                Set-ItemProperty -Path $k -Name 'LoadAppInit_DLLs' -Value 0 -Type DWord -Force -ErrorAction SilentlyContinue
                L "[已清空 AppInit_DLLs] $k（原值: $ad）"; $n++
            }
        } catch { }
    }
    $appcert = 'HKLM:\SYSTEM\CurrentControlSet\Control\Session Manager\AppCertDlls'
    if (Test-Path $appcert) {
        foreach ($prop in (Get-Item $appcert).Property) {
            if ($prop -like 'PS*') { continue }
            try { Remove-ItemProperty -Path $appcert -Name $prop -Force -ErrorAction Stop; L "[已删除 AppCertDlls] $prop"; $n++ } catch { }
        }
    }
    L "[完成] Winlogon/AppInit/AppCert 还原 $n 项"
}

function Remove-MaliciousWmiSubscription {
    $n = 0
    try {
        $consumers = @(Get-CimInstance -Namespace root\subscription -ClassName __EventConsumer -ErrorAction SilentlyContinue)
        foreach ($c in $consumers) {
            $cls = $c.CimClass.CimClassName
            if ($cls -notin @('CommandLineEventConsumer','ActiveScriptEventConsumer')) { continue }
            L "[清除 WMI 持久化] $cls $($c.Name)"
            try {
                @(Get-CimInstance -Namespace root\subscription -ClassName __FilterToConsumerBinding -ErrorAction SilentlyContinue) |
                    Where-Object { $_.Consumer -match [regex]::Escape($c.Name) } |
                    Remove-CimInstance -ErrorAction SilentlyContinue
            } catch { }
            try { $c | Remove-CimInstance -ErrorAction SilentlyContinue; $n++ } catch { }
        }
    } catch { }
    L "[完成] WMI 持久化处理 $n 项"
}

function Invoke-DisableSuspiciousDrivers {
    $n = 0
    foreach ($h in @(Get-SuspiciousDrivers)) {
        if ($h -match '可疑驱动服务\(可写目录\): (\S+)') {
            $name = $Matches[1]
            try { Set-ItemProperty -Path "HKLM:\SYSTEM\CurrentControlSet\Services\$name" -Name Start -Value 4 -Type DWord -Force -ErrorAction Stop; L "[已禁用可疑驱动] $name"; $n++ } catch { }
        }
    }
    L "[完成] 可疑驱动禁用 $n 项"
}

# ============ 主流程 ============
$isAdmin = Test-Admin
L '=============================================='
L '== 银狐木马综合威胁清理工具 启动 =='
L "== 模式：$(if ($Mode -eq 'kill') { '扫描+清理' } elseif ($Mode -eq 'restore') { '还原系统状态' } else { '仅扫描' })"
L "== 管理员权限：$isAdmin"
L '=============================================='

if ($DryRun) {
    $Mode = 'scan'
    L '[干跑模式] 已启用 -WhatIf/-DryRun：仅扫描并逐项报告将要执行的清理动作，不终止进程/不删除文件/不修改注册表'
}

if ($Mode -eq 'restore') {
    if (-not $isAdmin) {
        L '【错误】还原操作需要管理员权限，请以管理员身份运行！'
    } else {
        Restore-SystemState $Backup
    }
    L ''
    L '== 威胁清理完成 =='
    return
}

L ''
L '—— 扫描 1/20：隐藏进程（H/S 属性）——'
$hiddenProcs = @(Get-HiddenExecutables)
if ($hiddenProcs.Count -eq 0) {
    L '未发现隐藏进程'
} else {
    foreach ($h in $hiddenProcs) { L "[隐藏进程-可疑] $h" }
}
L "共发现隐藏进程 $($hiddenProcs.Count) 个"

L ''
L '—— 扫描 2/20：隐藏启动项 ——'
$hiddenStart = @(Get-HiddenStartupItems)
if ($hiddenStart.Count -eq 0) {
    L '未发现隐藏启动项'
} else {
    foreach ($h in $hiddenStart) { L "[隐藏启动项-可疑] $h" }
}
L "共发现隐藏启动项 $($hiddenStart.Count) 个"

L ''
L '—— 扫描 3/20：可疑服务 ——'
$suspServices = @(Get-SuspiciousServices)
if ($suspServices.Count -eq 0) {
    L '未发现可疑服务'
} else {
    foreach ($s in $suspServices) { L "[木马特征-服务] $s" }
}
L "共发现可疑服务 $($suspServices.Count) 个"

L ''
L '—— 扫描 4/20：特征库样本比对 ——'
$sigHits = @(Get-SignatureHits)
if ($sigHits.Count -eq 0) {
    L '未命中特征库样本'
} else {
    foreach ($h in $sigHits) { L "[特征库-可疑] $h" }
}
L "特征库命中 $($sigHits.Count) 项"

$blockCands = @(Get-BlockCandidates)
if ($blockCands.Count -gt 0) {
    L ''
    L '—— 网络阻断建议（这些程序不应联网）——'
    foreach ($b in $blockCands) { L "[建议阻断] $b" }
    if ($Mode -eq 'scan') { L '提示: 运行「威胁清理（扫描+清理）」会自动为上述程序添加防火墙出站阻断规则（MhyunBox_阻断_*），届时更换矿池 IP 也无法联网。' }
}

L ''
L '—— 扫描 5/20：系统 DLL 完整性 / 注入检测 ——'
$badDlls = @(Get-SystemDllTampering)
if ($badDlls.Count -eq 0) {
    L '系统关键 DLL 签名全部正常'
} else {
    foreach ($b in $badDlls) { L "[系统DLL-可疑] 签名异常（可能被木马注入/篡改）: $b" }
}
L "系统 DLL 异常 $($badDlls.Count) 项"
$injMods = @(Get-InjectedModules)
if ($injMods.Count -eq 0) {
    L '未发现可疑 DLL 注入'
} else {
    foreach ($m in $injMods) { L "[DLL注入-可疑] $m" }
}
L "可疑 DLL 注入 $($injMods.Count) 项"

L ''
L '—— 扫描 6/20：白加黑（DLL 侧加载）检测 ——'
$whiteBlack = @(Get-WhiteBlackDlls)
if ($whiteBlack.Count -eq 0) {
    L '未发现白加黑（签名程序 + 未签名 DLL 侧加载）'
} else {
    foreach ($w in $whiteBlack) { L "[白加黑-可疑] $w" }
}
L "白加黑嫌疑 $($whiteBlack.Count) 项"

L ''
L '—— 扫描 7/20：已知银狐木马 IOC（白加黑载荷 / 伪造启动项 / 恶意任务）——'
$iocHits = @(Get-MalwareIocHits)
if ($iocHits.Count -eq 0) {
    L '未发现已知银狐木马 IOC'
} else {
    foreach ($h in $iocHits) { L "[IOC-可疑] $h" }
}
L "IOC 命中 $($iocHits.Count) 项"
$genDirs = @(Get-RandomPayloadDirs)
$genFiles = @(Get-RandomPayloadFiles)
$genPayload = @($genDirs + $genFiles)
foreach ($g in $genPayload) { L "[泛化载荷-可疑] $($g.Path) [$($g.Reason)]" }
L "泛化随机名载荷（Public/PF86/ProgramData）命中 $($genPayload.Count) 项"
$genRun = @(Get-FakeSecurityHealthRunHits)
foreach ($g in $genRun) { L "[伪造SecurityHealth-可疑] $g" }
L "伪造 SecurityHealth 启动项命中 $($genRun.Count) 项"
$genTasks = @(Get-SuspiciousNamedTasks)
$genTasksMissing = @($genTasks | Where-Object { $_.Missing })
foreach ($g in $genTasksMissing) { L "[泛化计划任务-可疑] $($g.Tag): $($g.Path)$($g.Name) -> $($g.Raw)（目标 exe 不存在）" }
foreach ($g in ($genTasks | Where-Object { -not $_.Missing })) { L "[仅报告-$($g.Tag)] $($g.Name) 目标存在，不删除: $($g.Raw)" }
L "随机命名/伪装 Edge 任务命中 $($genTasksMissing.Count) 项"

L ''
L '—— 扫描 8/20：乱码/随机英文名 + 高占用/高网络 行为检测 ——'
$behavior = @(Get-BehaviorSuspiciousProcesses)
if ($behavior.Count -eq 0) {
    L '未发现乱码/随机名的高占用进程'
} else {
    foreach ($b in $behavior) { L "[行为-可疑] $b" }
}
L "行为可疑进程 $($behavior.Count) 个"

L ''
L '—— 扫描 9/20：安全厂商 HOSTS 劫持 ——'
$hostsHits = @(Get-HostsTampering)
if ($hostsHits.Count -eq 0) {
    L 'HOSTS 未被劫持'
} else {
    foreach ($h in $hostsHits) { L "[HOSTS-可疑] $h" }
}
L "HOSTS 劫持 $($hostsHits.Count) 项"

L ''
L '—— 扫描 10/20：伪造官方安全软件 / IFEO 劫持 ——'
$fakeSec = @(Get-FakeSecuritySoftware)
foreach ($fs in $fakeSec) { L "[伪造安全软件-可疑] $fs" }
L "伪造安全软件 $($fakeSec.Count) 项"
$ifeo = @(Get-IfeoHijacks)
foreach ($ifh in $ifeo) { L "[IFEO劫持-可疑] $ifh" }
L "IFEO 劫持 $($ifeo.Count) 项"

L ''
L '—— 扫描 11/20：网络劫持（系统代理 / 端口转发）——'
$netHij = @(Get-NetworkHijack)
if ($netHij.Count -eq 0) {
    L '未发现网络劫持'
} else {
    foreach ($nh in $netHij) { L "[网络劫持-可疑] $nh" }
}
L "网络劫持 $($netHij.Count) 项"

L ''
L '—— 扫描 12/20：全面启动项 / 持久化（Winlogon / AppInit_DLLs / AppCertDlls / Run / ActiveSetup / 启动夹）——'
$winlogon = @(Get-WinlogonPersistence)
foreach ($w in $winlogon) { L "[持久化-可疑] $w" }
L "Winlogon/AppInit/AppCert 可疑 $($winlogon.Count) 项"
$allRun = @(Get-AllRunPersistence)
foreach ($r in $allRun) { L "[持久化-可疑] $r" }
L "启动项/启动夹可疑 $($allRun.Count) 项"

L ''
L '—— 扫描 13/20：WMI 永久事件订阅（无文件持久化）——'
$wmiHits = @(Get-WmiEventSubscriptions)
if ($wmiHits.Count -eq 0) { L '未发现 WMI 事件订阅' } else { foreach ($w in $wmiHits) { L "[WMI持久化-可疑] $w" } }
L "WMI 事件订阅 $($wmiHits.Count) 项"

L ''
L '—— 扫描 14/20：COM 劫持（InprocServer32/LocalServer32）——'
$comHits = @(Get-ComHijacks)
if ($comHits.Count -eq 0) { L '未发现 COM 劫持' } else { foreach ($c in $comHits) { L "[COM劫持-可疑] $c" } }
L "COM 劫持 $($comHits.Count) 项"

L ''
L '—— 扫描 15/20：无文件载荷（mshta/rundll32/regsvr32/wmic/powershell -enc/certutil）——'
$fileless = @(Get-FilelessPayloads)
if ($fileless.Count -eq 0) { L '未发现无文件载荷' } else { foreach ($fl in $fileless) { L "[无文件载荷-可疑] $fl" } }
L "无文件载荷 $($fileless.Count) 项"

L ''
L '—— 扫描 16/20：快捷方式（.lnk）劫持 ——'
$lnkHits = @(Get-ShortcutHijacks)
if ($lnkHits.Count -eq 0) { L '未发现快捷方式劫持' } else { foreach ($l in $lnkHits) { L "[快捷方式-可疑] $l" } }
L "快捷方式劫持 $($lnkHits.Count) 项"

L ''
L '—— 扫描 17/20：可疑驱动 / BYOVD ——'
$drvHits = @(Get-SuspiciousDrivers)
if ($drvHits.Count -eq 0) { L '未发现可疑驱动' } else { foreach ($d in $drvHits) { L "[驱动-可疑] $d" } }
L "可疑驱动 $($drvHits.Count) 项"

L ''
L '—— 扫描 18/20：出站 C2 / 矿池连接 ——'
$c2Hits = @(Get-C2Connections)
if ($c2Hits.Count -eq 0) { L '未发现矿池/C2 出站连接' } else { foreach ($c in $c2Hits) { L "[C2-可疑] $c" } }
L "C2/矿池连接 $($c2Hits.Count) 项"

L ''
L '—— 扫描 19/20：临时目录 / 回收站 载荷落地 ——'
$dropHits = @(Get-PayloadDrops)
if ($dropHits.Count -eq 0) { L '未发现载荷落地' } else { foreach ($d in $dropHits) { L "[载荷落地-可疑] $d" } }
L "载荷落地 $($dropHits.Count) 项"

L ''
L '—— 扫描 20/20：自定义规则库（规则库.txt）——'
$ruleHits = @(Get-CustomRuleHits)
if ($ruleHits.Count -eq 0) { L '未命中自定义规则' } else { foreach ($r in $ruleHits) { L "[规则-可疑] $r" } }
L "规则命中 $($ruleHits.Count) 项"

if ($Mode -eq 'kill') {
    if (-not $isAdmin) {
        L '【错误】清理操作需要管理员权限，请以管理员身份运行！'
    } else {
        L ''
        L '== 开始清理 =='
        $backupDir = Join-Path ([Environment]::GetFolderPath('Desktop')) ("malware_backup_" + (Get-Date -Format 'yyyyMMdd_HHmmss'))
        New-SystemRestorePoint 'MhyunBox 威胁清理前'
        Backup-SystemState $backupDir
        Backup-MalwarePersistence $backupDir
        Backup-ScheduledTasks $backupDir
        Repair-IfeoHijacks
        Clear-WdacPolicy
        Remove-OldSilverfoxKernel
        Disable-RootTasks
        Fix-WinlogonPersistence
        Remove-MaliciousWmiSubscription
        Fix-ComHijacks $backupDir
        Invoke-HiddenProcessKill
        Invoke-ServiceDisable
        Invoke-DisableSuspiciousDrivers
        Invoke-KillFilelessProcesses
        Invoke-KillIocProcesses
        Invoke-RemoveIocFiles
        Remove-MalwareIocPersistence
        Remove-FakeSecurityHealthRun
        Invoke-RemoveRandomPayloadDirs
        Remove-SuspiciousNamedTasks
        Remove-BrokenPersistence
        Restore-WindowsDefender
        Invoke-KillBehaviorProcesses
        Invoke-RemoveFakeSecurity
        Invoke-BlockC2
        Repair-HostsFile $backupDir
        Restore-NetworkSettings
        Remove-PayloadDrops
        $addedBlock = Invoke-FirewallBlock $blockCands
        if ($addedBlock -gt 0) { L "[完成] 已为 $addedBlock 个程序添加防火墙出站阻断（更换矿池 IP 也无法联网）" }
        L ''
        if ($badDlls.Count -gt 0) {
            L '【提示】检测到系统 DLL 签名异常，这类被注入的"删不掉、关不掉"的系统 DLL 无法用常规方式清理，'
            L '请按顺序处理：1) 立即断网；2) 重启后以管理员运行 DISM /Online /Cleanup-Image /RestoreHealth；'
            L '3) 运行 SFC /Scannow 修复；4) 再安装火绒/360 做全盘扫描。必要时使用 PE 环境 + 离线专杀工具。'
        }
        L '== 清理完成 =='
        L '建议：重启电脑后安装火绒或 360 杀毒进行全盘扫描，彻底清除残留。'
    }
}

if ($Mode -eq 'kill') {
    L ''
    L '—— 清理后复核 ——'
    $after = @(Get-MalwareIocHits)
    $afterHosts = @(Get-HostsTampering)
    $afterIfeo = @(Get-IfeoHijacks)
    $afterBehavior = @(Get-BehaviorSuspiciousProcesses)
    $afterFake = @(Get-FakeSecuritySoftware)
    $afterWmi = @(Get-WmiEventSubscriptions)
    $afterCom = @(Get-ComHijacks)
    $afterPersistence = @(Get-WinlogonPersistence) + @(Get-AllRunPersistence)
    $afterDrivers = @(Get-SuspiciousDrivers)
    $afterC2 = @(Get-C2Connections)
    $totalLeft = $after.Count + $afterHosts.Count + $afterIfeo.Count + $afterBehavior.Count + $afterFake.Count + $afterWmi.Count + $afterCom.Count + $afterPersistence.Count + $afterDrivers.Count + $afterC2.Count
    foreach ($a in $after) { L "[复核-仍存在IOC] $a" }
    foreach ($a in $afterHosts) { L "[复核-HOSTS仍劫持] $a" }
    foreach ($a in $afterIfeo) { L "[复核-IFEO仍劫持] $a" }
    foreach ($a in $afterBehavior) { L "[复核-行为仍可疑] $a" }
    foreach ($a in $afterFake) { L "[复核-伪造安全软件仍在] $a" }
    foreach ($a in $afterWmi) { L "[复核-WMI持久化仍在] $a" }
    foreach ($a in $afterCom) { L "[复核-COM劫持仍在] $a" }
    foreach ($a in $afterPersistence) { L "[复核-持久化仍在] $a" }
    foreach ($a in $afterDrivers) { L "[复核-可疑驱动仍在] $a" }
    foreach ($a in $afterC2) { L "[复核-C2连接仍在] $a" }
    if ($totalLeft -eq 0) {
        L '[复核] 已知威胁（IOC/行为/HOSTS/IFEO/WMI/COM/持久化/驱动/C2/伪造安全软件）均已清除'
    } else {
        L '【提示】仍有残留：建议重启后再运行一次清理，或用内置 360系统急救箱 / 火绒专杀做全盘扫描。'
    }
}

L ''
L '== 威胁清理完成 =='
