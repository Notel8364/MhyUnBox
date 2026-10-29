<#
 .SYNOPSIS
    扩展脚本模板 —— 后续新增查杀毒脚本请复制本模板，保持与 App / 工具箱一致的接口
 .DESCRIPTION
    统一约定（与 SilverFoxTool.cs、查杀毒工具箱.ps1 对齐）：
      · 入参固定为 -Mode / -Silent / -LogFile（可增不可减，保证工具箱能调度）
      · L() 统一输出：控制台回传关键行 + 仅 -LogFile 时落盘（默认静默不写文件）
      · 退出码：0=无威胁 / 2=发现威胁 / 1=出错
    新增能力后，记得在『查杀毒工具箱.ps1』的 $Actions 表追加一行映射即可并入总入口。
 .PARAMETER Mode
    脚本工作模式（自定，如 scan / kill / 自定义）。
 .PARAMETER Silent
    静默模式：不刷屏进度，只回传关键结论行。
 .PARAMETER LogFile
    可选：传入后把输出同时追加到该日志文件。
#>
[CmdletBinding()]
param(
    [string]$Mode = 'scan',
    [switch]$Silent,
    [string]$LogFile = ''
)

[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$OutputEncoding = [System.Text.Encoding]::UTF8

$script:LogPath = $LogFile
function L([string]$m) {
    $line = (Get-Date -Format 'HH:mm:ss.fff') + '  ' + $m
    Write-Output $line
    if ($script:LogPath) { try { Add-Content -LiteralPath $script:LogPath -Value $line -Encoding UTF8 } catch { } }
}

# ---------- 在此实现你的检测/处置逻辑 ----------
# 例：
#   switch ($Mode) {
#       'scan' { L '开始扫描…'; ... ; exit 0 }
#       'kill' { L '开始处置…'; ... ; exit 2 }
#   }
L "[模板] 收到 Mode=$Mode Silent=$Silent（本模板仅用于示范接口，未执行实际处置）"
exit 0
