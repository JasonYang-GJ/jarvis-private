param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[0-9a-f]{40}$')]
    [string]$ExpectedSourceSha,

    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[0-9a-f]{40}$')]
    [string]$ExpectedParentSha,

    [Parameter(Mandatory = $true)]
    [ValidateSet('0.7.0')]
    [string]$ReleaseVersion,

    [Parameter(Mandatory = $true)]
    [string]$AttributionContractPath,

    [switch]$SkipTests
)

$ErrorActionPreference = 'Stop'
$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$artifactsRoot = Join-Path $repoRoot 'artifacts'
$publishRoot = Join-Path $artifactsRoot 'publish\win-x64'
$clientStage = Join-Path $artifactsRoot 'staging\client'
$hostStage = Join-Path $artifactsRoot 'staging\host'
$distributionStagingRoot = Join-Path $artifactsRoot 'staging'
$distributionInnoInclude = Join-Path $distributionStagingRoot 'distribution-notice-files.iss'
$candidateRoot = Join-Path $artifactsRoot (Join-Path 'candidates\v0.7.0' $ExpectedSourceSha)
$candidateEvidenceRoot = Join-Path $candidateRoot 'evidence'
$offlineNuGetConfig = Join-Path $candidateRoot 'offline-nuget.config'
$solution = Join-Path $repoRoot 'ScreenGuide.slnx'
$clientProject = Join-Path $repoRoot 'src\ScreenGuide.DesktopClient\ScreenGuide.DesktopClient.csproj'
$hostProject = Join-Path $repoRoot 'src\ScreenGuide.DesktopHost\ScreenGuide.DesktopHost.csproj'
$identityValidator = Join-Path $repoRoot 'scripts\Test-ReleaseCandidateIdentity.ps1'
$attributionGenerator = Join-Path $repoRoot 'scripts\New-ReleaseCandidateAttribution.ps1'
$distributionBundleValidator = Join-Path $repoRoot 'scripts\Test-DistributionNoticeBundle.ps1'
$candidateInstallerName = (-join @([char]0x5143, [char]0x67A2)) + '-V0.7.0-' +
    (-join @([char]0x5019, [char]0x9009, [char]0x5B89, [char]0x88C5, [char]0x5305)) + '.exe'

function Reset-BuildDirectory([string]$path) {
    $fullPath = [System.IO.Path]::GetFullPath($path)
    $fullArtifacts = [System.IO.Path]::GetFullPath($artifactsRoot)
    $prefix = $fullArtifacts.TrimEnd([System.IO.Path]::DirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
    if (-not $fullPath.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to reset a directory outside artifacts: $fullPath"
    }

    if (Test-Path -LiteralPath $fullPath) {
        Remove-Item -LiteralPath $fullPath -Recurse -Force
    }
    New-Item -ItemType Directory -Path $fullPath -Force | Out-Null
}

function Invoke-Checked([string]$failureMessage, [scriptblock]$operation) {
    & $operation
    if ($LASTEXITCODE -ne 0) { throw $failureMessage }
}

function Get-PublishTreeSha256([string]$root) {
    $lines = Get-ChildItem -LiteralPath $root -File -Recurse | Sort-Object FullName | ForEach-Object {
        $relative = Get-RelativePath $root $_.FullName
        $hash = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToUpperInvariant()
        $relative + '|' + [string]$_.Length + '|' + $hash
    }
    $bytes = [Text.UTF8Encoding]::new($false).GetBytes(($lines -join "`n") + "`n")
    $sha = [Security.Cryptography.SHA256]::Create()
    try { return ([BitConverter]::ToString($sha.ComputeHash($bytes))).Replace('-', '') }
    finally { $sha.Dispose() }
}

function Get-RelativePath([string]$root, [string]$path) {
    $resolvedRoot = [IO.Path]::GetFullPath($root).TrimEnd('\') + '\'
    $resolvedPath = [IO.Path]::GetFullPath($path)
    if (-not $resolvedPath.StartsWith($resolvedRoot, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Release candidate relative path is outside the repository.'
    }
    return $resolvedPath.Substring($resolvedRoot.Length).Replace('\', '/')
}

$identityOutput = @(& powershell.exe -NoProfile -ExecutionPolicy Bypass -File $identityValidator `
    -RepositoryRoot $repoRoot `
    -ExpectedSourceSha $ExpectedSourceSha `
    -ExpectedParentSha $ExpectedParentSha `
    -ReleaseVersion $ReleaseVersion `
    -AttributionContractPath $AttributionContractPath 2>&1)
if ($LASTEXITCODE -ne 0) {
    $identityOutput | ForEach-Object { Write-Output $_ }
    throw 'Release candidate identity preflight failed.'
}
$identityResult = ($identityOutput -join "`n") | ConvertFrom-Json
if (-not $identityResult.passed) { throw 'Release candidate identity preflight failed.' }

Reset-BuildDirectory $candidateRoot
Reset-BuildDirectory $clientStage
Reset-BuildDirectory $hostStage
Reset-BuildDirectory $publishRoot
New-Item -ItemType Directory -Path $distributionStagingRoot -Force | Out-Null

$offlineConfigText = @'
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
  </packageSources>
</configuration>
'@
[IO.File]::WriteAllText($offlineNuGetConfig, $offlineConfigText.Replace("`r`n", "`n") + "`n", [Text.UTF8Encoding]::new($false))

$previousTelemetry = $env:DOTNET_CLI_TELEMETRY_OPTOUT
$previousFirstTime = $env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE
$previousRevocation = $env:NUGET_CERT_REVOCATION_MODE
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:NUGET_CERT_REVOCATION_MODE = 'offline'
try {
    Invoke-Checked 'Locked offline release restore failed.' {
        dotnet restore $solution --locked-mode --disable-parallel --nologo `
            --configfile $offlineNuGetConfig -p:NuGetAudit=false
    }

    if (-not $SkipTests) {
        Invoke-Checked 'Release tests failed.' {
            dotnet test $solution --configuration Release --no-restore --maxcpucount:1
        }
    }

    Invoke-Checked 'DesktopClient win-x64 locked offline restore failed.' {
        dotnet restore $clientProject --locked-mode --disable-parallel --nologo `
            --configfile $offlineNuGetConfig `
            -p:RuntimeIdentifier=win-x64 -p:SelfContained=true -p:NuGetAudit=false
    }
    Invoke-Checked 'DesktopHost win-x64 locked offline restore failed.' {
        dotnet restore $hostProject --locked-mode --disable-parallel --nologo `
            --configfile $offlineNuGetConfig `
            -p:RuntimeIdentifier=win-x64 -p:SelfContained=true -p:NuGetAudit=false
    }

    Invoke-Checked 'DesktopClient publish failed.' {
        dotnet publish $clientProject `
            --configuration Release --runtime win-x64 --self-contained true --no-restore `
            -p:RestoreLockedMode=true -p:PublishSingleFile=false -p:DebugType=None -p:DebugSymbols=false `
            -p:SourceRevisionId=$ExpectedSourceSha -p:IncludeSourceRevisionInInformationalVersion=true `
            --output $clientStage
    }
    Invoke-Checked 'DesktopHost publish failed.' {
        dotnet publish $hostProject `
            --configuration Release --runtime win-x64 --self-contained true --no-restore `
            -p:RestoreLockedMode=true -p:PublishSingleFile=false -p:DebugType=None -p:DebugSymbols=false `
            -p:SourceRevisionId=$ExpectedSourceSha -p:IncludeSourceRevisionInInformationalVersion=true `
            --output $hostStage
    }
}
finally {
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = $previousTelemetry
    $env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = $previousFirstTime
    $env:NUGET_CERT_REVOCATION_MODE = $previousRevocation
}

Copy-Item -Path (Join-Path $hostStage '*') -Destination $publishRoot -Recurse -Force
# Client is a Windows Desktop application and carries the full WPF runtime.
# Copy it last so the Host's smaller framework facades cannot overwrite WPF assemblies.
Copy-Item -Path (Join-Path $clientStage '*') -Destination $publishRoot -Recurse -Force

$expectedProductVersion = $ReleaseVersion + '+' + $ExpectedSourceSha
$expectedFileVersion = $ReleaseVersion + '.0'
$clientExe = Join-Path $publishRoot 'ScreenGuide.DesktopClient.exe'
$hostExe = Join-Path $publishRoot 'ScreenGuide.DesktopHost.exe'
$clientInfo = [Diagnostics.FileVersionInfo]::GetVersionInfo($clientExe)
$hostInfo = [Diagnostics.FileVersionInfo]::GetVersionInfo($hostExe)
if ($clientInfo.ProductVersion -cne $expectedProductVersion -or $hostInfo.ProductVersion -cne $expectedProductVersion -or
    $clientInfo.FileVersion -cne $expectedFileVersion -or $hostInfo.FileVersion -cne $expectedFileVersion) {
    throw 'Release candidate Client/Host binary identity mismatch.'
}

$attributionOutput = @(& powershell.exe -NoProfile -ExecutionPolicy Bypass -File $attributionGenerator `
    -RepositoryRoot $repoRoot `
    -PublishRoot $publishRoot `
    -AttributionContractPath $AttributionContractPath `
    -ExpectedSourceSha $ExpectedSourceSha `
    -ReleaseVersion $ReleaseVersion `
    -OutputRoot $candidateEvidenceRoot 2>&1)
if ($LASTEXITCODE -ne 0) {
    $attributionOutput | ForEach-Object { Write-Output $_ }
    throw 'Release candidate attribution generation failed.'
}
$attributionResult = ($attributionOutput -join "`n") | ConvertFrom-Json
if (-not $attributionResult.passed) { throw 'Release candidate attribution generation failed.' }

$candidateBundleRoot = [string]$attributionResult.details.candidateBundleRoot
$candidateBundleManifest = [string]$attributionResult.details.candidateBundleManifestPath
$candidatePayloadManifest = [string]$attributionResult.details.payloadManifestPath
$distributionGateOutput = @(& powershell.exe -NoProfile -ExecutionPolicy Bypass `
    -File $distributionBundleValidator `
    -ManifestPath $candidateBundleManifest `
    -BundleRoot $candidateBundleRoot `
    -PayloadManifestPath $candidatePayloadManifest `
    -ApprovedStagingRoot $distributionStagingRoot `
    -InnoIncludePath $distributionInnoInclude 2>&1)
if ($LASTEXITCODE -ne 0) {
    $distributionGateOutput | ForEach-Object { Write-Output $_ }
    throw 'Release candidate distribution notice bundle failed.'
}
$distributionResult = ($distributionGateOutput -join "`n") | ConvertFrom-Json
if (-not $distributionResult.passed) { throw 'Release candidate distribution notice bundle failed.' }

$isccCandidates = @(
    (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'),
    (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
    (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe')
) | Where-Object { $_ -and (Test-Path -LiteralPath $_) }
$iscc = $isccCandidates | Select-Object -First 1
if (-not $iscc) { throw 'Inno Setup 6 was not found.' }
$innoUninstaller = Join-Path ([IO.Path]::GetDirectoryName($iscc)) 'unins000.exe'
if (-not (Test-Path -LiteralPath $innoUninstaller -PathType Leaf)) {
    throw 'Cannot verify Inno Setup 6.7.3 because the version anchor is missing.'
}
$innoProductVersion = ([Diagnostics.FileVersionInfo]::GetVersionInfo($innoUninstaller).ProductVersion).Trim()
if ($innoProductVersion -cne '6.7.3') {
    throw "Inno Setup version mismatch. Expected 6.7.3; actual $innoProductVersion."
}

Invoke-Checked 'Installer compilation failed.' {
    & $iscc "/O$candidateRoot" (Join-Path $repoRoot 'installer\ScreenGuideDesktop.iss')
}

$installer = Join-Path $candidateRoot $candidateInstallerName
if (-not (Test-Path -LiteralPath $installer -PathType Leaf)) { throw 'Installer was not produced.' }
$installerInfo = [Diagnostics.FileVersionInfo]::GetVersionInfo($installer)
$installerFileVersion = ([string]$installerInfo.FileVersion).Trim()
$installerProductVersion = ([string]$installerInfo.ProductVersion).Trim()
if ($installerFileVersion -cne $expectedFileVersion -or $installerProductVersion -cne $ReleaseVersion) {
    throw 'Release candidate installer identity mismatch.'
}

$publishFiles = @(Get-ChildItem -LiteralPath $publishRoot -File -Recurse)
$publishBytes = [long](($publishFiles | Measure-Object -Property Length -Sum).Sum)
$releaseIdentityPath = Join-Path $candidateRoot 'release-identity.json'
$releaseIdentity = [ordered]@{
    schemaVersion = 1
    candidateStatus = 'INTERNAL_CANDIDATE_ONLY'
    releaseVersion = $ReleaseVersion
    sourceCommit = $ExpectedSourceSha
    parentCommit = $ExpectedParentSha
    client = [ordered]@{
        path = Get-RelativePath $repoRoot $clientExe
        fileVersion = $clientInfo.FileVersion
        productVersion = $clientInfo.ProductVersion
        size = (Get-Item -LiteralPath $clientExe).Length
        sha256 = (Get-FileHash -LiteralPath $clientExe -Algorithm SHA256).Hash.ToUpperInvariant()
    }
    host = [ordered]@{
        path = Get-RelativePath $repoRoot $hostExe
        fileVersion = $hostInfo.FileVersion
        productVersion = $hostInfo.ProductVersion
        size = (Get-Item -LiteralPath $hostExe).Length
        sha256 = (Get-FileHash -LiteralPath $hostExe -Algorithm SHA256).Hash.ToUpperInvariant()
    }
    installer = [ordered]@{
        path = Get-RelativePath $repoRoot $installer
        fileVersion = $installerFileVersion
        productVersion = $installerProductVersion
        size = (Get-Item -LiteralPath $installer).Length
        sha256 = (Get-FileHash -LiteralPath $installer -Algorithm SHA256).Hash.ToUpperInvariant()
    }
    publishTree = [ordered]@{
        path = Get-RelativePath $repoRoot $publishRoot
        fileCount = $publishFiles.Count
        totalBytes = $publishBytes
        treeSha256 = Get-PublishTreeSha256 $publishRoot
    }
    attribution = [ordered]@{
        contractPath = Get-RelativePath $repoRoot $AttributionContractPath
        payloadManifestPath = Get-RelativePath $repoRoot $candidatePayloadManifest
        payloadManifestSha256 = (Get-FileHash -LiteralPath $candidatePayloadManifest -Algorithm SHA256).Hash.ToUpperInvariant()
        payloadCount = [int]$distributionResult.payloadCount
        bundleManifestPath = Get-RelativePath $repoRoot $candidateBundleManifest
    }
}
$releaseIdentityJson = ($releaseIdentity | ConvertTo-Json -Depth 12).Replace("`r`n", "`n") + "`n"
[IO.File]::WriteAllText($releaseIdentityPath, $releaseIdentityJson, [Text.UTF8Encoding]::new($false))

$finalStatus = @(& git -C $repoRoot status --porcelain=v1 --untracked-files=all 2>$null)
if ($LASTEXITCODE -ne 0 -or $finalStatus.Count -ne 0) {
    throw 'Release candidate build changed the Git working tree.'
}

[ordered]@{
    passed = $true
    sourceCommit = $ExpectedSourceSha
    parentCommit = $ExpectedParentSha
    releaseVersion = $ReleaseVersion
    candidateRoot = $candidateRoot
    publishRoot = $publishRoot
    releaseIdentityPath = $releaseIdentityPath
    installerPath = $installer
    publishFileCount = $publishFiles.Count
    publishTotalBytes = $publishBytes
} | ConvertTo-Json -Compress | Write-Output
