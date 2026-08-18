param(
    [switch]$SkipTests
)

$ErrorActionPreference = 'Stop'
$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$artifactsRoot = Join-Path $repoRoot 'artifacts'
$publishRoot = Join-Path $artifactsRoot 'publish\win-x64'
$clientStage = Join-Path $artifactsRoot 'staging\client'
$hostStage = Join-Path $artifactsRoot 'staging\host'
$releaseRoot = Join-Path $artifactsRoot 'release'

function Reset-BuildDirectory([string]$path) {
    $fullPath = [System.IO.Path]::GetFullPath($path)
    $fullArtifacts = [System.IO.Path]::GetFullPath($artifactsRoot)
    $prefix = $fullArtifacts.TrimEnd([System.IO.Path]::DirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
    if (-not $fullPath.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "拒绝清理 artifacts 以外的目录：$fullPath"
    }

    if (Test-Path -LiteralPath $fullPath) {
        Remove-Item -LiteralPath $fullPath -Recurse -Force
    }
    New-Item -ItemType Directory -Path $fullPath -Force | Out-Null
}

if (-not $SkipTests) {
    dotnet test (Join-Path $repoRoot 'ScreenGuide.slnx') --configuration Release
    if ($LASTEXITCODE -ne 0) { throw 'Release tests failed.' }
}

Reset-BuildDirectory $clientStage
Reset-BuildDirectory $hostStage
Reset-BuildDirectory $publishRoot
New-Item -ItemType Directory -Path $releaseRoot -Force | Out-Null

dotnet publish (Join-Path $repoRoot 'src\ScreenGuide.DesktopClient\ScreenGuide.DesktopClient.csproj') `
    --configuration Release --runtime win-x64 --self-contained true `
    -p:PublishSingleFile=false -p:DebugType=None -p:DebugSymbols=false --output $clientStage
if ($LASTEXITCODE -ne 0) { throw 'DesktopClient publish failed.' }

dotnet publish (Join-Path $repoRoot 'src\ScreenGuide.DesktopHost\ScreenGuide.DesktopHost.csproj') `
    --configuration Release --runtime win-x64 --self-contained true `
    -p:PublishSingleFile=false -p:DebugType=None -p:DebugSymbols=false --output $hostStage
if ($LASTEXITCODE -ne 0) { throw 'DesktopHost publish failed.' }

Copy-Item -Path (Join-Path $hostStage '*') -Destination $publishRoot -Recurse -Force
# Client is a Windows Desktop application and carries the full WPF runtime.
# Copy it last so the Host's smaller framework facades cannot overwrite WPF assemblies.
Copy-Item -Path (Join-Path $clientStage '*') -Destination $publishRoot -Recurse -Force

$isccCandidates = @(
    (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'),
    (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
    (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe')
) | Where-Object { $_ -and (Test-Path -LiteralPath $_) }
$iscc = $isccCandidates | Select-Object -First 1
if (-not $iscc) {
    throw '未找到 Inno Setup 6。请先安装 JRSoftware.InnoSetup。'
}

& $iscc (Join-Path $repoRoot 'installer\ScreenGuideDesktop.iss')
if ($LASTEXITCODE -ne 0) { throw 'Installer compilation failed.' }

$installer = Join-Path $releaseRoot '元枢-V0.1.0-安装包.exe'
if (-not (Test-Path -LiteralPath $installer)) { throw 'Installer was not produced.' }
Get-FileHash -Algorithm SHA256 -LiteralPath $installer
Write-Host "Release installer: $installer"
