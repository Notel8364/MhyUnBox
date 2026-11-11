# MhyUnBox 安装包构建脚本
# 用法：powershell -ExecutionPolicy Bypass -File Installer\build.ps1
#   产物：Installer\dist\MhyUnBox-Setup-<版本>.exe
param(
    [switch]$SkipAppBuild
)

$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot                 # ...\Installer
$root = Split-Path -Parent $here      # 仓库根
$appOut = Join-Path $root 'bin\Release\net8.0-windows10.0.19041.0'
$artifacts = Join-Path $here 'artifacts'
$dist = Join-Path $here 'dist'

function Step($text) { Write-Host "`n=== $text ===" -ForegroundColor Cyan }

# 0. 版本号（与 MhyunBox/Services/AppVersion.cs 保持一致）
$appVersionFile = Join-Path $root 'MhyunBox\Services\AppVersion.cs'
$version = (Select-String -Path $appVersionFile -Pattern 'Display\s*=\s*"v([0-9.]+)"').Matches[0].Groups[1].Value
Step "目标版本 v$version"

# 1. 构建程序本体
if (-not $SkipAppBuild) {
    Step '构建 MhyUnBox 主程序（Release）'
    Push-Location $root
    & dotnet build 'MhyunBox.csproj' -c Release -v m --nologo | Select-Object -Last 4
    if ($LASTEXITCODE -ne 0) { throw '主程序构建失败' }
    Pop-Location
}
if (-not (Test-Path (Join-Path $appOut 'MhyunBox.exe'))) {
    throw "找不到程序输出目录：$appOut"
}

# 2. 打 payload.zip（直接按条目写入，排除日志与调试符号；不建临时目录）
Step '打包程序文件 payload.zip'
$payload = Join-Path $here 'payload.zip'
if ([System.IO.File]::Exists($payload)) { [System.IO.File]::Delete($payload) }
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = [System.IO.Compression.ZipFile]::Open($payload, [System.IO.Compression.ZipArchiveMode]::Create)
try {
    $files = Get-ChildItem $appOut -Recurse -File |
        Where-Object { $_.Extension -ne '.pdb' -and $_.Extension -ne '.log' -and $_.FullName -notmatch 'MhyUnBox_Logs' }
    foreach ($f in $files) {
        $rel = $f.FullName.Substring($appOut.Length + 1).Replace('\', '/')
        [void][System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
            $zip, $f.FullName, $rel, [System.IO.Compression.CompressionLevel]::Optimal)
    }
    "打包条目数：$($files.Count)"
}
finally { $zip.Dispose() }
$payloadMb = [math]::Round((Get-Item $payload).Length / 1MB, 1)
Step "payload.zip 生成完成：$payloadMb MB"

# 3. 构建卸载器（框架依赖单文件，体积小）
Step '构建卸载器 uninstall.exe'
$unOut = Join-Path $artifacts 'uninstaller'
if ([System.IO.Directory]::Exists($unOut)) { [System.IO.Directory]::Delete($unOut, $true) }
& dotnet publish (Join-Path $here 'Uninstaller\Uninstaller.csproj') -c Release -r win-x64 --self-contained false `
    -p:PublishSingleFile=true -p:DebugType=none -p:IncludeNativeLibrariesForSelfExtract=true -o $unOut -v q --nologo
if ($LASTEXITCODE -ne 0) { throw '卸载器构建失败' }
Copy-Item (Join-Path $unOut 'uninstall.exe') (Join-Path $here 'uninstaller.bin') -Force
$unKb = [math]::Round((Get-Item (Join-Path $here 'uninstaller.bin')).Length / 1KB, 0)
Step "卸载器就绪：$unKb KB"

# 4. 构建安装包（自包含单文件）
Step '构建安装包 MhyUnBoxSetup.exe'
if ([System.IO.Directory]::Exists($dist)) { [System.IO.Directory]::Delete($dist, $true) }
& dotnet publish (Join-Path $here 'MhyUnBoxSetup.csproj') -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true `
    -p:DebugType=none -o $dist -v q --nologo
if ($LASTEXITCODE -ne 0) { throw '安装包构建失败' }

$setup = Join-Path $dist 'MhyUnBoxSetup.exe'
$final = Join-Path $dist ("MhyUnBox-Setup-{0}.exe" -f $version)
Copy-Item $setup $final -Force
if ([System.IO.File]::Exists($setup)) { [System.IO.File]::Delete($setup) }
Get-ChildItem $dist -Filter '*.pdb' | ForEach-Object { [System.IO.File]::Delete($_.FullName) }

$setupMb = [math]::Round((Get-Item $final).Length / 1MB, 1)
$sha = (Get-FileHash $final -Algorithm SHA256).Hash.ToLower()
Step '完成'
Write-Host "安装包：$final" -ForegroundColor Green
Write-Host "大小：$setupMb MB" -ForegroundColor Green
Write-Host "SHA256：$sha" -ForegroundColor Green
