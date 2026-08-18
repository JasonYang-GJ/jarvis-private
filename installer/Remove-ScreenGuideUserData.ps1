param(
    [switch]$ConfirmDeletion
)

$dataRoot = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'ScreenGuide\V01'
$expectedParent = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'ScreenGuide'

if (-not $ConfirmDeletion) {
    $answer = Read-Host "这会永久删除任务历史、设置和本机日志。请输入 DELETE 确认"
    if ($answer -cne 'DELETE') {
        Write-Host '已取消，未删除任何数据。'
        exit 0
    }
}

$fullTarget = [System.IO.Path]::GetFullPath($dataRoot)
$fullParent = [System.IO.Path]::GetFullPath($expectedParent)
if ((Split-Path -Parent $fullTarget) -ne $fullParent -or (Split-Path -Leaf $fullTarget) -ne 'V01') {
    throw "拒绝删除非预期目录：$fullTarget"
}

Get-Process -Name 'ScreenGuide.DesktopClient','ScreenGuide.DesktopHost' -ErrorAction SilentlyContinue |
    Stop-Process -Force
if (Test-Path -LiteralPath $fullTarget) {
    Remove-Item -LiteralPath $fullTarget -Recurse -Force
}

Write-Host '元枢用户数据已删除。'
