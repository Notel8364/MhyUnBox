# MhyunBox 系统急救箱：一键下载并启动 360 系统急救箱（SuperKiller）
# 下载直链（已验证）: https://down.360safe.com/SuperKiller.exe
#   —— 360 官方 CDN down.360safe.com，文件带 Qihoo 360 有效 Authenticode 签名，约 2.6MB（v5.1.x）
# 官方页面: https://www.360.cn/killer/360compkill.html | https://www.360.cn/jijiuxiang/
# 用法: powershell -NoProfile -ExecutionPolicy Bypass -File 系统急救箱.ps1 [-Force] [-NoLaunch]
#   -Force     已存在 bin\SuperKiller.exe 时强制重新下载
#   -NoLaunch  下载/校验完成后不自动启动
# 退出码: 0=成功（或无需下载）  1=下载/校验/提权失败

param(
    [switch]$Force,
    [switch]$NoLaunch
)

[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$ErrorActionPreference = 'Continue'

$Url = 'https://down.360safe.com/SuperKiller.exe'
$MinSize = 1MB
$OfficialPages = @('https://www.360.cn/killer/360compkill.html','https://www.360.cn/jijiuxiang/')

function L([string]$m) { Write-Output ((Get-Date -Format 'HH:mm:ss.fff') + '  ' + $m) }

function Test-Admin {
    $id = [System.Security.Principal.WindowsIdentity]::GetCurrent()
    $p = New-Object System.Security.Principal.WindowsPrincipal($id)
    return $p.IsInRole([System.Security.Principal.WindowsBuiltInRole]::Administrator)
}

# 管理员自提权（下载目录可能位于 Program Files，启动急救箱也需要管理员）
if (-not (Test-Admin)) {
    L '需要管理员权限，正在请求 UAC 提权...'
    $argList = @('-NoProfile','-ExecutionPolicy','Bypass','-File',"`"$PSCommandPath`"")
    if ($Force) { $argList += '-Force' }
    if ($NoLaunch) { $argList += '-NoLaunch' }
    try { Start-Process -FilePath 'powershell.exe' -Verb RunAs -ArgumentList $argList | Out-Null; exit 0 }
    catch { L '【失败】未获得管理员授权（UAC 被拒绝）。'; exit 1 }
}

$bin = Join-Path $PSScriptRoot 'bin'
New-Item -ItemType Directory -Force -Path $bin | Out-Null
$target = Join-Path $bin 'SuperKiller.exe'

# 校验：存在 + 大小 >= 1MB + Qihoo 360 有效签名
function Test-SuperKillerValid([string]$path) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { return $false }
    try { if ((Get-Item -LiteralPath $path).Length -lt $MinSize) { return $false } } catch { return $false }
    $sig = Get-AuthenticodeSignature -LiteralPath $path -ErrorAction SilentlyContinue
    if (-not $sig -or $sig.Status -ne 'Valid') { return $false }
    if ($sig.SignerCertificate.Subject -notmatch 'Qihoo 360') { return $false }
    return $true
}

$needDownload = $true
if ((Test-Path -LiteralPath $target) -and -not $Force) {
    if (Test-SuperKillerValid $target) {
        L "[已存在] $target 校验通过（Qihoo 360 有效签名），跳过下载"
        $needDownload = $false
    } else {
        L '[提示] 已存在文件校验不通过，将重新下载'
    }
}

if ($needDownload) {
    L '== 开始下载 360 系统急救箱 =='
    L "来源直链: $Url （360 官方 CDN down.360safe.com）"
    try { [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12 } catch { }
    $tmp = "$target.download"
    Remove-Item -LiteralPath $tmp -Force -ErrorAction SilentlyContinue
    $ok = $false
    try {
        Invoke-WebRequest -Uri $Url -OutFile $tmp -UseBasicParsing -TimeoutSec 300 -ErrorAction Stop
        $ok = $true
        L '[下载方式] Invoke-WebRequest'
    } catch { L "[警告] Invoke-WebRequest 下载失败: $($_.Exception.Message)" }
    if (-not $ok) {
        try {
            Start-BitsTransfer -Source $Url -Destination $tmp -ErrorAction Stop
            $ok = $true
            L '[下载方式] BITS'
        } catch { L "[警告] BITS 下载失败: $($_.Exception.Message)" }
    }
    if (-not $ok -and (Get-Command curl.exe -ErrorAction SilentlyContinue)) {
        cmd /c "curl.exe -L --retry 2 -o `"$tmp`" `"$Url`" >nul 2>&1"
        if ((Test-Path -LiteralPath $tmp) -and ((Get-Item -LiteralPath $tmp).Length -gt $MinSize)) { $ok = $true; L '[下载方式] curl.exe' }
        else { L '[警告] curl.exe 下载失败' }
    }
    if (-not $ok -or -not (Test-Path -LiteralPath $tmp)) {
        L '【失败】下载失败。请手动从官方页面下载：'
        foreach ($pg in $OfficialPages) { L "  官方页: $pg" }
        exit 1
    }
    $size = (Get-Item -LiteralPath $tmp).Length
    L "下载完成，大小: $size 字节（参考值约 2595768 字节）"
    if (-not (Test-SuperKillerValid $tmp)) {
        L '【失败】文件校验不通过（大小异常或非 Qihoo 360 有效签名），已删除可疑文件。'
        L '请手动从官方页面下载，或检查网络是否被劫持：'
        foreach ($pg in $OfficialPages) { L "  官方页: $pg" }
        Remove-Item -LiteralPath $tmp -Force -ErrorAction SilentlyContinue
        exit 1
    }
    Move-Item -LiteralPath $tmp -Destination $target -Force
    $vi = (Get-Item -LiteralPath $target).VersionInfo
    L "[校验通过] $($vi.FileDescription) 版本 $($vi.FileVersion)（Qihoo 360 有效签名）"
}

if ($NoLaunch) { L '[完成] -NoLaunch 已指定，不启动急救箱。'; exit 0 }

L "== 启动 360 系统急救箱: $target =="
try {
    Start-Process -FilePath $target -WorkingDirectory $bin
    L '[完成] 已启动。首次运行会释放引擎，请按界面提示进行全盘查杀。'
} catch {
    L "【失败】启动失败: $($_.Exception.Message)"
    L '若 exe 被杀软误删，可尝试同目录 360急救箱\ 文件夹内的备用运行方式（bat/改名为 .com）。'
    exit 1
}
exit 0
