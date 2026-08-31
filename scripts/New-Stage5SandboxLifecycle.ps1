param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[0-9a-f]{40}$')]
    [string]$ExpectedSourceSha,

    [Parameter(Mandatory = $true)]
    [string]$OldInstallerPath,

    [Parameter(Mandatory = $true)]
    [string]$CandidateInstallerPath,

    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[0-9A-Fa-f]{64}$')]
    [string]$CandidateInstallerSha256,

    [Parameter(Mandatory = $true)]
    [string]$LifecycleProbeRoot,

    [string]$OutputRoot
)

$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\')
$ownedRoot = if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    Join-Path $tempRoot ('YuanshuStage5Lifecycle-' + [Guid]::NewGuid().ToString('N'))
} else {
    [IO.Path]::GetFullPath($OutputRoot)
}
$oldInstallerExpectedSha = '4683E10CD6C5317EB537681978DB8A77E2DC15041838EF3DCE2DEE47C7C16F95'
$protectedHostProductRoot = Join-Path $env:LOCALAPPDATA 'Programs\YuanshuDesktop'
$protectedHostUninstallKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\{E2B9C242-2965-48BC-B2C6-CF83A2B11953}_is1'
$sandboxExecutable = Join-Path $env:WINDIR 'System32\WindowsSandbox.exe'

function Has-ReparsePoint([string]$path) {
    $current = [IO.Path]::GetFullPath($path)
    while (-not [string]::IsNullOrWhiteSpace($current)) {
        if (Test-Path -LiteralPath $current) {
            if (([IO.File]::GetAttributes($current) -band [IO.FileAttributes]::ReparsePoint) -ne 0) { return $true }
        }
        $parent = [IO.Directory]::GetParent($current)
        if ($null -eq $parent) { break }
        $current = $parent.FullName
    }
    return $false
}

function Write-Json([string]$path, $value) {
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($path)) | Out-Null
    [IO.File]::WriteAllText(
        $path,
        (($value | ConvertTo-Json -Depth 30).Replace("`r`n", "`n") + "`n"),
        [Text.UTF8Encoding]::new($false))
}

$ownedPrefix = $tempRoot + '\'
if (-not $ownedRoot.StartsWith($ownedPrefix, [StringComparison]::OrdinalIgnoreCase) -or
    -not [IO.Path]::GetFileName($ownedRoot).StartsWith('YuanshuStage5Lifecycle-', [StringComparison]::Ordinal) -or
    (Has-ReparsePoint $ownedRoot)) {
    throw 's5_lifecycle_output_root_invalid'
}
foreach ($path in @($OldInstallerPath, $CandidateInstallerPath, $LifecycleProbeRoot)) {
    if (Has-ReparsePoint $path) { throw 's5_lifecycle_reparse_point' }
}

$actualSourceSha = (& git -C $repoRoot rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0) { throw 's5_lifecycle_source_unavailable' }
$sourceStatus = @(& git -C $repoRoot status --porcelain=v1 --untracked-files=all)
if ($LASTEXITCODE -ne 0) { throw 's5_lifecycle_source_unavailable' }
$sourceClean = $sourceStatus.Count -eq 0

$oldInstaller = [IO.Path]::GetFullPath($OldInstallerPath)
$candidateInstaller = [IO.Path]::GetFullPath($CandidateInstallerPath)
$probeRoot = [IO.Path]::GetFullPath($LifecycleProbeRoot)
$probeExecutable = Join-Path $probeRoot 'ScreenGuide.Stage5LifecycleProbe.exe'
$probeManifest = Join-Path $probeRoot 'lifecycle-probe-bundle.json'
$probeValidator = Join-Path $PSScriptRoot 'Test-Stage5LifecycleProbeBundle.ps1'
foreach ($path in @($oldInstaller, $candidateInstaller, $probeExecutable, $probeManifest, $probeValidator)) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw 's5_lifecycle_artifact_missing' }
}

$sandboxFeature = Get-WindowsOptionalFeature -Online -FeatureName 'Containers-DisposableClientVM' -ErrorAction SilentlyContinue
$sandboxAvailable = (Test-Path -LiteralPath $sandboxExecutable -PathType Leaf) -and
    $null -ne $sandboxFeature -and [string]$sandboxFeature.State -eq 'Enabled'
$protectedHostProductRootPresent = Test-Path -LiteralPath $protectedHostProductRoot
$protectedHostUninstallRegistrationPresent = Test-Path -LiteralPath $protectedHostUninstallKey
$protectedHostInstallPresent = $protectedHostProductRootPresent -or $protectedHostUninstallRegistrationPresent

[IO.Directory]::CreateDirectory($ownedRoot) | Out-Null
$inputRoot = Join-Path $ownedRoot 'input'
$evidenceRoot = Join-Path $ownedRoot 'evidence'
$runtimeRoot = Join-Path $ownedRoot 'sandbox-runtime'
foreach ($path in @($inputRoot, $evidenceRoot, $runtimeRoot)) { [IO.Directory]::CreateDirectory($path) | Out-Null }
if ((Has-ReparsePoint $inputRoot) -or (Has-ReparsePoint $evidenceRoot) -or (Has-ReparsePoint $runtimeRoot)) {
    throw 's5_lifecycle_reparse_point'
}

$sourceProbeValidationPath = Join-Path $ownedRoot 'probe-source-validation.json'
$sourceProbeValidationOutput = @(& powershell.exe -NoProfile -ExecutionPolicy Bypass -File $probeValidator `
    -BundleRoot $probeRoot -ResultPath $sourceProbeValidationPath 2>&1)
if ($LASTEXITCODE -ne 0) {
    Write-Output ($sourceProbeValidationOutput -join "`n")
    throw 's5_lifecycle_probe_bundle_invalid'
}
$sourceProbeValidation = Get-Content -Raw -LiteralPath $sourceProbeValidationPath | ConvertFrom-Json

$oldCopy = Join-Path $inputRoot 'yuanshu-v0.5.0-installer.exe'
$candidateCopy = Join-Path $inputRoot 'yuanshu-v0.6.0-candidate-installer.exe'
Copy-Item -LiteralPath $oldInstaller -Destination $oldCopy
Copy-Item -LiteralPath $candidateInstaller -Destination $candidateCopy
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Invoke-Stage5SandboxLifecycle.ps1') -Destination (Join-Path $inputRoot 'Invoke-Stage5SandboxLifecycle.ps1')
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Test-Stage5SandboxLifecycleContract.ps1') -Destination (Join-Path $inputRoot 'Test-Stage5SandboxLifecycleContract.ps1')
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Stage5LifecycleExecutionBudget.ps1') -Destination (Join-Path $inputRoot 'Stage5LifecycleExecutionBudget.ps1')
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Stage5LifecycleFailureDiagnostics.ps1') -Destination (Join-Path $inputRoot 'Stage5LifecycleFailureDiagnostics.ps1')
Copy-Item -LiteralPath $probeValidator -Destination (Join-Path $inputRoot 'Test-Stage5LifecycleProbeBundle.ps1')
Copy-Item -LiteralPath $probeRoot -Destination (Join-Path $inputRoot 'probe') -Recurse

$copiedProbeRoot = Join-Path $inputRoot 'probe'
$copiedProbeValidationPath = Join-Path $ownedRoot 'probe-copy-validation.json'
$copiedProbeValidationOutput = @(& powershell.exe -NoProfile -ExecutionPolicy Bypass -File $probeValidator `
    -BundleRoot $copiedProbeRoot -ResultPath $copiedProbeValidationPath 2>&1)
if ($LASTEXITCODE -ne 0) {
    Write-Output ($copiedProbeValidationOutput -join "`n")
    throw 's5_lifecycle_probe_bundle_invalid'
}
$copiedProbeValidation = Get-Content -Raw -LiteralPath $copiedProbeValidationPath | ConvertFrom-Json
if ([int]$copiedProbeValidation.fileCount -ne [int]$sourceProbeValidation.fileCount -or
    [string]$copiedProbeValidation.details.manifestSha256 -cne [string]$sourceProbeValidation.details.manifestSha256) {
    throw 's5_lifecycle_probe_bundle_hash_mismatch'
}

$facts = [ordered]@{
    contractVersion = 1
    expectedSourceSha = $ExpectedSourceSha
    actualSourceSha = $actualSourceSha
    sourceClean = $sourceClean
    sandboxAvailable = $sandboxAvailable
    protectedHostInstall = [ordered]@{
        present = $protectedHostInstallPresent
        productRootPresent = $protectedHostProductRootPresent
        uninstallRegistrationPresent = $protectedHostUninstallRegistrationPresent
    }
    oldInstaller = [ordered]@{ fileName = [IO.Path]::GetFileName($oldCopy); expectedSha256 = $oldInstallerExpectedSha; actualSha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $oldCopy).Hash }
    candidateInstaller = [ordered]@{ fileName = [IO.Path]::GetFileName($candidateCopy); expectedSha256 = $CandidateInstallerSha256.ToUpperInvariant(); actualSha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $candidateCopy).Hash }
    lifecycleProbe = [ordered]@{
        fileName = 'probe/lifecycle-probe-bundle.json'
        expectedSha256 = [string]$sourceProbeValidation.details.manifestSha256
        actualSha256 = [string]$copiedProbeValidation.details.manifestSha256
        fileCount = [int]$sourceProbeValidation.fileCount
        entryPoint = 'probe/ScreenGuide.Stage5LifecycleProbe.exe'
    }
    mappings = [ordered]@{ inputHostPath = $inputRoot; evidenceHostPath = $evidenceRoot; inputReadOnly = $true; evidenceReadOnly = $false; repoMapped = $false }
    sandboxPolicy = [ordered]@{ networking = 'Disable'; clipboard = 'Disable'; audioInput = 'Disable'; videoInput = 'Disable'; printer = 'Disable' }
    cleanup = [ordered]@{ ownedRoot = $runtimeRoot; ownershipConfirmed = $true }
    oldIdentity = [ordered]@{ productVersion = '0.5.0+d553e7e9d606037df87d98e99250de5498f5934a'; fileVersion = '0.5.0.0'; schemaVersion = 10 }
    candidateIdentity = [ordered]@{ productVersion = "0.6.0+$ExpectedSourceSha"; fileVersion = '0.6.0.0'; schemaVersion = 11 }
}

$factsPath = Join-Path $ownedRoot 'host-preflight-facts.json'
$planPath = Join-Path $inputRoot 'lifecycle-plan.json'
$resultPath = Join-Path $evidenceRoot 'host-preflight-result.json'
$wsbPath = Join-Path $ownedRoot 'yuanshu-stage5-lifecycle.wsb'
Write-Json $factsPath $facts
$output = @(& powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'Test-Stage5SandboxLifecycleContract.ps1') `
    -Mode Preflight -FactsPath $factsPath -ResultPath $resultPath -WsbOutputPath $wsbPath -PlanOutputPath $planPath 2>&1)
if ($LASTEXITCODE -ne 0) {
    Write-Output ($output -join "`n")
    exit $LASTEXITCODE
}

# Deliberately do not launch WindowsSandbox.exe. The user/QA must inspect and launch the generated .wsb separately.
Write-Output ($output -join "`n")
