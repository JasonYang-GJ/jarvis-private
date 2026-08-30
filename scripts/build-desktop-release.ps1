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
$solution = Join-Path $repoRoot 'ScreenGuide.slnx'
$clientProject = Join-Path $repoRoot 'src\ScreenGuide.DesktopClient\ScreenGuide.DesktopClient.csproj'
$hostProject = Join-Path $repoRoot 'src\ScreenGuide.DesktopHost\ScreenGuide.DesktopHost.csproj'
$distributionBundleRoot = Join-Path $repoRoot 'distribution\licenses'
$distributionBundleManifest = Join-Path $distributionBundleRoot 'bundle-manifest.json'
$distributionPayloadManifest = Join-Path $repoRoot 'docs\baselines\V0.6.0_STAGE4_C0_PAYLOAD_ATTRIBUTION.json'
$distributionInnoInclude = Join-Path $artifactsRoot 'staging\distribution-notice-files.iss'
$distributionBundleValidator = Join-Path $repoRoot 'scripts\Test-DistributionNoticeBundle.ps1'

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

$distributionGateOutput = @(& powershell.exe -NoProfile -ExecutionPolicy Bypass `
    -File $distributionBundleValidator `
    -ManifestPath $distributionBundleManifest `
    -BundleRoot $distributionBundleRoot `
    -PayloadManifestPath $distributionPayloadManifest `
    -InnoIncludePath $distributionInnoInclude 2>&1)
$distributionGateExitCode = $LASTEXITCODE
$distributionGateOutput | ForEach-Object { Write-Output $_ }
if ($distributionGateExitCode -ne 0) {
    $distributionGateCode = 'distribution_notice_bundle_invalid'
    try {
        $distributionGateResult = ($distributionGateOutput -join "`n") | ConvertFrom-Json
        if (-not [string]::IsNullOrWhiteSpace([string]$distributionGateResult.errorCode)) {
            $distributionGateCode = [string]$distributionGateResult.errorCode
        }
    }
    catch {
        # Keep the stable generic gate code; never copy parser details or content into the release error.
    }
    Write-Output $distributionGateCode
    exit 1
}

dotnet restore $solution --locked-mode --disable-parallel --nologo `
    -p:NuGetAudit=false --ignore-failed-sources
if ($LASTEXITCODE -ne 0) { throw 'Locked release restore failed.' }

if (-not $SkipTests) {
    # IPC and single-instance tests share current-user Windows resources, so keep
    # release verification sequential to prevent cross-assembly pipe collisions.
    dotnet test $solution --configuration Release --no-restore --maxcpucount:1
    if ($LASTEXITCODE -ne 0) { throw 'Release tests failed.' }
}

Reset-BuildDirectory $clientStage
Reset-BuildDirectory $hostStage
Reset-BuildDirectory $publishRoot
New-Item -ItemType Directory -Path $releaseRoot -Force | Out-Null

dotnet restore $clientProject --locked-mode --disable-parallel --nologo `
    -p:RuntimeIdentifier=win-x64 -p:SelfContained=true -p:NuGetAudit=false --ignore-failed-sources
if ($LASTEXITCODE -ne 0) { throw 'DesktopClient win-x64 locked restore failed.' }

dotnet restore $hostProject --locked-mode --disable-parallel --nologo `
    -p:RuntimeIdentifier=win-x64 -p:SelfContained=true -p:NuGetAudit=false --ignore-failed-sources
if ($LASTEXITCODE -ne 0) { throw 'DesktopHost win-x64 locked restore failed.' }

dotnet publish $clientProject `
    --configuration Release --runtime win-x64 --self-contained true --no-restore `
    -p:RestoreLockedMode=true -p:PublishSingleFile=false -p:DebugType=None -p:DebugSymbols=false --output $clientStage
if ($LASTEXITCODE -ne 0) { throw 'DesktopClient publish failed.' }

dotnet publish $hostProject `
    --configuration Release --runtime win-x64 --self-contained true --no-restore `
    -p:RestoreLockedMode=true -p:PublishSingleFile=false -p:DebugType=None -p:DebugSymbols=false --output $hostStage
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

$installer = Join-Path $releaseRoot '元枢-V0.6.0-安装包.exe'
if (-not (Test-Path -LiteralPath $installer)) { throw 'Installer was not produced.' }
Get-FileHash -Algorithm SHA256 -LiteralPath $installer
Write-Host "Release installer: $installer"
