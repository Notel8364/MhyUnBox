@echo off
chcp 65001 >nul
setlocal
rem MhyunBox 启动器（精简版，不含 .NET 8 运行时）
rem 检查 .NET 8 桌面运行时是否已安装，未安装则提示下载
set "RUNTIME_FOUND="
if exist "%ProgramFiles%\dotnet\shared\Microsoft.WindowsDesktop.App\8.0.*" set "RUNTIME_FOUND=1"
if not defined RUNTIME_FOUND if exist "%ProgramFiles(x86)%\dotnet\shared\Microsoft.WindowsDesktop.App\8.0.*" set "RUNTIME_FOUND=1"
if not defined RUNTIME_FOUND if exist "%LOCALAPPDATA%\Microsoft\dotnet\shared\Microsoft.WindowsDesktop.App\8.0.*" set "RUNTIME_FOUND=1"
if not defined RUNTIME_FOUND (
    echo.
    echo [提示] 未检测到 .NET 8 桌面运行时（.NET Desktop Runtime 8.0）。
    echo 首次运行需要先安装运行库，正在为你打开官方下载页面…
    echo 安装完成后，重新双击本文件即可启动 MhyunBox。
    echo.
    start "" "https://dotnet.microsoft.com/zh-cn/download/dotnet/8.0"
    pause
    exit /b 1
)
start "" "%~dp0MhyunBox.exe"
