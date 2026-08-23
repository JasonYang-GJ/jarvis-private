param(
    [string]$InstallerPath,
    [string]$ExpectedFileVersion = '0.2.1.0'
)

$ErrorActionPreference = 'Stop'

$token = [Guid]::NewGuid().ToString('N')
$tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
$testRoot = [IO.Path]::GetFullPath((Join-Path $tempRoot "ScreenGuideInstallAcceptance-$token"))
$installRoot = Join-Path $testRoot 'Program'
$dataRoot = Join-Path $testRoot 'UserData'
$defaultSetup = Join-Path $PSScriptRoot '..\artifacts\release\元枢-V0.2.1-安装包.exe'
$setup = [IO.Path]::GetFullPath($(if ($InstallerPath) { $InstallerPath } else { $defaultSetup }))

function Invoke-HiddenProcess([string]$file, [string[]]$arguments) {
    $process = Start-Process -FilePath $file -ArgumentList $arguments -Wait -PassThru -WindowStyle Hidden
    if ($process.ExitCode -ne 0) {
        throw "$file failed with exit code $($process.ExitCode)."
    }
    return $process.ExitCode
}

function Assert-SafeTestRoot {
    $prefix = $tempRoot.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if (-not $testRoot.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase) -or
        (Split-Path -Leaf $testRoot) -notlike 'ScreenGuideInstallAcceptance-*') {
        throw "拒绝操作非预期临时目录：$testRoot"
    }
}

Assert-SafeTestRoot
if (-not (Test-Path -LiteralPath $setup)) {
    throw "安装包不存在：$setup"
}

New-Item -ItemType Directory -Path $testRoot -Force | Out-Null
try {
    $installExit = Invoke-HiddenProcess $setup @(
        '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART',
        "/DIR=$installRoot", "/LOG=$(Join-Path $testRoot 'install.log')")
    foreach ($file in 'ScreenGuide.DesktopClient.exe', 'ScreenGuide.DesktopHost.exe', 'unins000.exe') {
        if (-not (Test-Path -LiteralPath (Join-Path $installRoot $file))) {
            throw "安装后缺少文件：$file"
        }
    }

    foreach ($file in 'ScreenGuide.DesktopClient.exe', 'ScreenGuide.DesktopHost.exe') {
        $actualVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $installRoot $file)).FileVersion
        if ($actualVersion -ne $ExpectedFileVersion) {
            throw "安装后版本不符：$file 预期 $ExpectedFileVersion，实际 $actualVersion。"
        }
    }

    $previousData = $env:SCREEN_GUIDE_DATA_DIRECTORY
    $previousPipe = $env:SCREEN_GUIDE_PIPE_NAME
    try {
        $env:SCREEN_GUIDE_DATA_DIRECTORY = $dataRoot
        $env:SCREEN_GUIDE_PIPE_NAME = "ScreenGuide.InstallTest.$token"
        $hostExit = Invoke-HiddenProcess (Join-Path $installRoot 'ScreenGuide.DesktopHost.exe') @('--run-once')
    }
    finally {
        $env:SCREEN_GUIDE_DATA_DIRECTORY = $previousData
        $env:SCREEN_GUIDE_PIPE_NAME = $previousPipe
    }

    $database = Join-Path $dataRoot 'state\tasking.db'
    if (-not (Test-Path -LiteralPath $database)) {
        throw '安装版 Host 未能初始化隔离的任务数据库。'
    }

    $sentinel = Join-Path $dataRoot 'reinstall-history-sentinel.txt'
    Set-Content -LiteralPath $sentinel -Value 'history-retention-validation'
    $uninstallExit = Invoke-HiddenProcess (Join-Path $installRoot 'unins000.exe') @(
        '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART')
    if (-not (Test-Path -LiteralPath $sentinel)) {
        throw '卸载错误删除了用户数据。'
    }

    $reinstallExit = Invoke-HiddenProcess $setup @(
        '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART',
        "/DIR=$installRoot", "/LOG=$(Join-Path $testRoot 'reinstall.log')")
    if (-not (Test-Path -LiteralPath $sentinel)) {
        throw '重装后历史数据未保留。'
    }

    $finalUninstallExit = Invoke-HiddenProcess (Join-Path $installRoot 'unins000.exe') @(
        '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART')

    [PSCustomObject]@{
        InstallExit = $installExit
        HostExit = $hostExit
        UninstallExit = $uninstallExit
        ReinstallExit = $reinstallExit
        FinalUninstallExit = $finalUninstallExit
        DatabaseInitialized = Test-Path -LiteralPath $database
        HistoryRetained = Test-Path -LiteralPath $sentinel
    }
}
finally {
    Assert-SafeTestRoot
    if (Test-Path -LiteralPath $testRoot) {
        Remove-Item -LiteralPath $testRoot -Recurse -Force
    }
}
