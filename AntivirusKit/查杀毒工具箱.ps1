<#
 .SYNOPSIS
    MhyunBox 查杀毒工具箱 —— 统一调度入口（合并全部单兵脚本，便于后续追加）
 .DESCRIPTION
    本脚本是 AntivirusKit 的“总入口”，把分散在多个 .ps1 中的能力用 -Action 统一调度，
    内部按需调用同目录的具体实现脚本，接口、日志风格与 App 侧 SilverFoxTool.cs 保持一致：
      · 默认静默：仅向控制台回传关键行（便于 App 捕获到日志面板），不弹窗、不强制写文件；
      · 仅当显式传入 -LogFile 时才落盘日志文件（满足“隐藏不写日志”的要求）；
      · 退出的内涵与具体脚本一致（0=无威胁 / 2=发现威胁 / 1=出错）。
    要新增能力时，只需在下面 $Actions 表后追加一项并写一个同目录脚本即可，无需改 App。
 .PARAMETER Action
    scan / kill / evo-scan / guard-install / guard-uninstall / guard-status /
    emergency-stop / emergency-standard / emergency-deep / emergency-restore /
    netguard-on / netguard-off / rescue / status
 .PARAMETER LogFile
    可选：传入后把输出同时追加到该日志文件。
 .PARAMETER Force
    跳过脚本内部二次确认（程序化调用时使用）。
#>
[CmdletBinding()]
param(
    [ValidateSet('scan','kill','evo-scan','guard-install','guard-uninstall','guard-status',
                 'emergency-stop','emergency-standard','emergency-deep','emergency-restore',
                 'emergency-status','netguard-on','netguard-off','rescue','status')]
    [string]$Action = 'status',
    [string]$LogFile = '',
    [switch]$Force
)

[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$OutputEncoding = [System.Text.Encoding]::UTF8

$script:LogPath = $LogFile
function L([string]$m) {
    $line = (Get-Date -Format 'HH:mm:ss.fff') + '  ' + $m
    Write-Output $line
    if ($script:LogPath) { try { Add-Content -LiteralPath $script:LogPath -Value $line -Encoding UTF8 } catch { } }
}

$DirSelf = $PSScriptRoot
if (-not $DirSelf) { $DirSelf = Split-Path -Parent $MyInvocation.MyCommand.Definition }
function Script([string]$name) { Join-Path $DirSelf $name }

# 把 Action 映射到“具体脚本 + 启动参数”
$Actions = @{
    'scan'                = @{ File = '杀毒扫描.ps1';          Args = @('-AutoRun') }
    'kill'                = @{ File = '威胁清理.ps1';          Args = @('-Mode','kill') }
    'evo-scan'            = @{ File = '威胁进化扫描.ps1';       Args = @('-Mode','scan','-Silent') }
    'guard-install'       = @{ File = '命令守卫.ps1';          Args = @('-Mode','guard-install','-Silent') }
    'guard-uninstall'     = @{ File = '命令守卫.ps1';          Args = @('-Mode','guard-uninstall','-Silent') }
    'guard-status'        = @{ File = '命令守卫.ps1';          Args = @('-Mode','guard-status') }
    'emergency-stop'      = @{ File = '应急响应.ps1';          Args = @('-Mode','stop','-Silent','-Force') }
    'emergency-standard'  = @{ File = '应急响应.ps1';          Args = @('-Mode','standard','-Silent','-Force') }
    'emergency-deep'      = @{ File = '应急响应.ps1';          Args = @('-Mode','deep','-Silent','-Force') }
    'emergency-restore'   = @{ File = '应急响应.ps1';          Args = @('-Mode','restore','-Silent','-Force') }
    'emergency-status'    = @{ File = '应急响应.ps1';          Args = @('-Mode','status') }
    'netguard-on'         = @{ File = '断网防护.ps1';          Args = @('-Mode','on') }
    'netguard-off'        = @{ File = '断网防护.ps1';          Args = @('-Mode','off') }
    'rescue'              = @{ File = '系统急救箱.ps1';        Args = @() }
    'status'              = @{ File = '应急响应.ps1';          Args = @('-Mode','status') }
}

if (-not $Actions.ContainsKey($Action)) {
    L "[错误] 未知动作: $Action （可用: $($Actions.Keys -join ', ')）"
    exit 1
}

$entry = $Actions[$Action]
$ps1 = Script $entry.File
if (-not (Test-Path -LiteralPath $ps1)) {
    L "[错误] 未找到实现脚本: $ps1"
    exit 1
}

$argList = @('-NoProfile','-ExecutionPolicy','Bypass','-File',$ps1) + $entry.Args
if ($Force) { $argList += '-Force' }   # 部分脚本已内置 -Force，重复传参无害
if ($script:LogPath) { $argList += @('-LogFile', $script:LogPath) }

L "== 调用 [$Action] -> $($entry.File) =="
try {
    & powershell.exe @argList
    exit $LASTEXITCODE
} catch {
    L "[错误] 执行失败: $_"
    exit 1
}
