<#
 .SYNOPSIS
    一致性自检 —— 校验本工具箱与 MhyunBox App 侧 (SilverFoxTool.cs) 引用是否对齐
 .DESCRIPTION
    SilverFoxTool.cs 通过固定文件名引用 KillTools/ 下的脚本。本脚本把那一串文件名
    在本包目录中逐一核对，确保「App 侧」与「独立脚本包」两边不会因改名/漏带而失配。
    用法: powershell -NoProfile -ExecutionPolicy Bypass -File 一致性自检.ps1
#>
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$OutputEncoding = [System.Text.Encoding]::UTF8

$DirSelf = $PSScriptRoot
if (-not $DirSelf) { $DirSelf = Split-Path -Parent $MyInvocation.MyCommand.Definition }

# 与 MhyunBox\Services\SilverFoxTool.cs 中的 *ScriptPath / *Path 保持一致
$Expected = @(
    '威胁清理.ps1',
    '断网防护.ps1',
    '杀毒扫描.ps1',
    '系统急救箱.ps1',
    '威胁进化扫描.ps1',
    '命令守卫.ps1',
    '应急响应.ps1',
    '特征库.txt',
    '规则库.txt',
    '断网白名单.txt'
)

$missing = @()
foreach ($f in $Expected) {
    $p = Join-Path $DirSelf $f
    if (Test-Path -LiteralPath $p) {
        Write-Output ('[OK]   ' + $f)
    } else {
        Write-Output ('[MISS] ' + $f)
        $missing += $f
    }
}

Write-Output ''
Write-Output '[提示] App 另引用但未随包分发的外部程序（需自行放入本目录或 KillTools/）：'
Write-Output '      火绒恶性木马专杀工具.exe  (HuorongKillerPath)'
Write-Output '      HEU_KMS_Activator_v42.3.0.exe (KmsActivatorPath)'

if ($missing.Count -eq 0) {
    Write-Output ''
    Write-Output '自检通过：本包与 App 侧引用完全一致。'
    exit 0
} else {
    Write-Output ''
    Write-Output ('自检失败：缺少 ' + $missing.Count + ' 个文件，请补齐后再打包。')
    exit 1
}
