# MhyunBox 挖矿断网防护脚本
# 原理：不再按"矿池 IP"封禁（木马换 IP 就绕过），改为「默认阻断全部出站 + 白名单放行」。
# 木马无论换到哪个矿池 IP/域名，都无法建立连接；同时按程序/服务精确放行白名单条目。
# 用法: powershell -NoProfile -ExecutionPolicy Bypass -File 断网防护.ps1 -Mode on|off|status [-LogFile path]

param(
    [string]$Mode = 'status',
    [string]$LogFile = ''
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

# 规则命名：隔离规则 MhyunBox_断网_*，特征库命中自动阻断 MhyunBox_阻断_*
function Get-MhyunRules([string]$prefix) {
    try {
        return @(Get-NetFirewallRule -PolicyStore ActiveStore -ErrorAction SilentlyContinue |
            Where-Object { $_.DisplayName -like "$prefix*" })
    } catch {
        return @()
    }
}

function Remove-MhyunRules([string]$prefix) {
    $rules = Get-MhyunRules $prefix
    $removed = 0
    foreach ($r in $rules) {
        try {
            & netsh.exe advfirewall firewall delete rule name="$($r.DisplayName)" 2>&1 | Out-Null
            $removed++
        } catch {
            L "[警告] 删除防火墙规则失败: $($r.DisplayName)"
        }
    }
    return $removed
}

# 读取白名单文件，返回 (@(service=..), @(program=..), @(port=..))
function Read-Whitelist {
    $whitelistFile = Join-Path $PSScriptRoot '断网白名单.txt'
    $services = @(); $programs = @(); $ports = @()
    if (-not (Test-Path -LiteralPath $whitelistFile)) { return @($services, $programs, $ports) }
    foreach ($raw in Get-Content -LiteralPath $whitelistFile -Encoding UTF8) {
        $line = $raw.Trim()
        if (-not $line -or $line.StartsWith('#')) { continue }
        if ($line -imatch '^service:(\S+)$') {
            $services += $Matches[1]
        } elseif ($line -imatch '^program:(.+)$') {
            $p = $Matches[1].Trim().Trim('"')
            if (Test-Path -LiteralPath $p -PathType Leaf) { $programs += $p }
        } elseif ($line -imatch '^port:(tcp|udp):(\d+)$') {
            $ports += "$($Matches[1]):$($Matches[2])"
        }
    }
    return @($services, $programs, $ports)
}

function Enable-Isolation {
    $wl = Read-Whitelist
    $services = $wl[0]; $programs = $wl[1]; $ports = $wl[2]

    L '== 开启挖矿断网防护：默认阻断全部出站 + 白名单放行 =='

    # 1) 默认阻断出站（TCP/UDP/ICMP），覆盖任意目标 IP/域名
    foreach ($proto in @('TCP', 'UDP', 'ICMPv4', 'ICMPv6')) {
        $name = "MhyunBox_断网_BlockOut_$proto"
        if (-not (Get-NetFirewallRule -DisplayName $name -ErrorAction SilentlyContinue)) {
            New-NetFirewallRule -DisplayName $name -Direction Outbound -Action Block -Protocol $proto -Profile Any -ErrorAction Stop | Out-Null
        }
    }
    L "[完成] 已阻断全部出站（TCP/UDP/ICMPv4/ICMPv6）"

    # 2) 白名单放行
    foreach ($svc in $services) {
        foreach ($port in @('80', '443')) {
            $name = "MhyunBox_断网_Allow_Service_${svc}_$port"
            if (-not (Get-NetFirewallRule -DisplayName $name -ErrorAction SilentlyContinue)) {
                New-NetFirewallRule -DisplayName $name -Direction Outbound -Action Allow -Service $svc -Protocol TCP -RemotePort $port -Profile Any -ErrorAction SilentlyContinue | Out-Null
            }
        }
        L "[放行服务] $svc (TCP 80/443)"
    }
    foreach ($prog in $programs) {
        $tag = [System.IO.Path]::GetFileName($prog) -replace '[^a-zA-Z0-9_]', '_'
        foreach ($proto in @('TCP', 'UDP')) {
            $name = "MhyunBox_断网_Allow_Program_${tag}_$proto"
            if (-not (Get-NetFirewallRule -DisplayName $name -ErrorAction SilentlyContinue)) {
                New-NetFirewallRule -DisplayName $name -Direction Outbound -Action Allow -Program $prog -Protocol $proto -Profile Any -ErrorAction SilentlyContinue | Out-Null
            }
        }
        L "[放行程序] $prog"
    }
    foreach ($pp in $ports) {
        $parts = $pp -split ':'
        $proto = $parts[0]; $port = $parts[1]
        $name = "MhyunBox_断网_Allow_Port_${proto}_$port"
        if (-not (Get-NetFirewallRule -DisplayName $name -ErrorAction SilentlyContinue)) {
            New-NetFirewallRule -DisplayName $name -Direction Outbound -Action Allow -Protocol $proto -RemotePort $port -Profile Any -ErrorAction SilentlyContinue | Out-Null
        }
        L "[放行端口] $proto/$port"
    }

    L '== 挖矿断网防护已开启 =='
    L '提示：换池子/换 IP 的矿机将无法建立连接。如需联网的软件请先加入 断网白名单.txt。'
}

function Disable-Isolation {
    $removed = Remove-MhyunRules 'MhyunBox_断网_'
    L "[完成] 已移除挖矿断网防护规则 $removed 条，出站恢复正常"
    L '注意：特征库命中时自动添加的「MhyunBox_阻断_*」规则仍会保留（可单独在防火墙高级设置删除）。'
    L '== 挖矿断网防护已关闭 =='
}

function Show-Status {
    L '== MhyunBox 挖矿断网防护 状态 =='
    $isolation = @(Get-MhyunRules 'MhyunBox_断网_')
    $autoBlock = @(Get-MhyunRules 'MhyunBox_阻断_')
    if ($isolation.Count -eq 0) {
        L '状态: 未开启'
        L 'IsolationEnabled=0'
    } else {
        L "状态: 已开启（$($isolation.Count) 条规则）"
        L 'IsolationEnabled=1'
        $allows = @($isolation | Where-Object { $_.Action -eq 'Allow' })
        $blocks = @($isolation | Where-Object { $_.Action -eq 'Block' })
        L "  阻断出站: $($blocks.Count) 条"
        L "  白名单放行: $($allows.Count) 条"
    }
    if ($autoBlock.Count -gt 0) {
        foreach ($p in ($autoBlock | ForEach-Object { ($_ | Get-NetFirewallApplicationFilter).Program } | Where-Object { $_ } | Sort-Object -Unique)) {
            if ($p) { L "  自动阻断程序: $p" }
        }
    }
    L "自动阻断规则（特征库命中）: $($autoBlock.Count) 条"
    L "AutoBlockCount=$($autoBlock.Count)"
    L '== 挖矿断网防护 状态查看结束 =='
}

# ============ 主流程 ============
if ($Mode -in @('on', 'off') -and -not (Test-Admin)) {
    # 手动运行且非管理员时，自动提权重跑（应用内由调用方提权，不会二次弹窗）
    $arg = "-NoProfile -ExecutionPolicy Bypass -File `"$PSCommandPath`" -Mode $Mode"
    if ($LogFile) { $arg += " -LogFile `"$LogFile`"" }
    try {
        Start-Process powershell.exe -ArgumentList $arg -Verb RunAs -WindowStyle Hidden | Out-Null
        exit 0
    } catch {
        Write-Output '【已取消】需要管理员权限（UAC）。'
        exit 1
    }
}

switch ($Mode) {
    'on'     { Enable-Isolation }
    'off'    { Disable-Isolation }
    default  { Show-Status }
}

if ($Mode -ne 'status') {
    L '== 挖矿断网防护完成 =='
}

