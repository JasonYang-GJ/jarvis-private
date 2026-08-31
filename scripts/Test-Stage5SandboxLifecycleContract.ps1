param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('Preflight', 'Evidence')]
    [string]$Mode,

    [Parameter(Mandatory = $true)]
    [string]$FactsPath,

    [Parameter(Mandatory = $true)]
    [string]$ResultPath,

    [string]$WsbOutputPath,
    [string]$PlanOutputPath
)

$ErrorActionPreference = 'Stop'

function Write-Json([string]$path, $value) {
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($path))) | Out-Null
    [IO.File]::WriteAllText(
        [IO.Path]::GetFullPath($path),
        (($value | ConvertTo-Json -Depth 30).Replace("`r`n", "`n") + "`n"),
        [Text.UTF8Encoding]::new($false))
}

function Remove-Output([string]$path) {
    if (-not [string]::IsNullOrWhiteSpace($path) -and (Test-Path -LiteralPath $path -PathType Leaf)) {
        Remove-Item -LiteralPath $path -Force
    }
}

function Complete([string]$status, [string]$errorCode, [int]$exitCode, $details) {
    if ($status -ne 'PASS') {
        Remove-Output $WsbOutputPath
        Remove-Output $PlanOutputPath
    }
    $result = [ordered]@{
        contractVersion = 1
        status = $status
        errorCode = $errorCode
        mode = $Mode.ToLowerInvariant()
        finalized = $true
        networkRequests = 0
        providerRequests = 0
        credentialReads = 0
        details = $details
    }
    Write-Json $ResultPath $result
    Write-Output ($result | ConvertTo-Json -Depth 10 -Compress)
    exit $exitCode
}

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

function Is-Sha256([string]$value) {
    return $value -cmatch '^[0-9A-F]{64}$'
}

function Equal-Hash($artifact) {
    return (Is-Sha256 ([string]$artifact.expectedSha256)) -and
        ([string]$artifact.expectedSha256 -ceq [string]$artifact.actualSha256)
}

try {
    $facts = Get-Content -Raw -LiteralPath ([IO.Path]::GetFullPath($FactsPath)) | ConvertFrom-Json
}
catch {
    Complete 'BLOCKED' 's5_lifecycle_contract_invalid' 1 ([ordered]@{ phase = $Mode.ToLowerInvariant() })
}

if ($facts.contractVersion -ne 1) {
    Complete 'BLOCKED' 's5_lifecycle_contract_invalid' 1 ([ordered]@{ phase = $Mode.ToLowerInvariant() })
}

if ($Mode -eq 'Preflight') {
    if ([string]$facts.expectedSourceSha -notmatch '^[0-9a-f]{40}$' -or
        [string]$facts.expectedSourceSha -cne [string]$facts.actualSourceSha) {
        Complete 'BLOCKED' 's5_lifecycle_source_sha_mismatch' 1 ([ordered]@{ phase = 'source' })
    }
    if (-not [bool]$facts.sourceClean) {
        Complete 'BLOCKED' 's5_lifecycle_source_dirty' 1 ([ordered]@{ phase = 'source' })
    }
    if (-not (Equal-Hash $facts.oldInstaller)) {
        Complete 'BLOCKED' 's5_lifecycle_old_installer_hash_mismatch' 1 ([ordered]@{ phase = 'artifact' })
    }
    if (-not (Equal-Hash $facts.candidateInstaller)) {
        Complete 'BLOCKED' 's5_lifecycle_candidate_installer_hash_mismatch' 1 ([ordered]@{ phase = 'artifact' })
    }
    if (-not (Equal-Hash $facts.lifecycleProbe)) {
        Complete 'BLOCKED' 's5_lifecycle_probe_hash_mismatch' 1 ([ordered]@{ phase = 'artifact' })
    }
    if ([string]$facts.lifecycleProbe.transportKind -cne 'sealed-zip-v1' -or
        [string]$facts.lifecycleProbe.fileName -cne 'lifecycle-probe-transport.zip' -or
        -not (Is-Sha256 ([string]$facts.lifecycleProbe.manifestSha256)) -or
        [int]$facts.lifecycleProbe.fileCount -lt 7 -or
        [string]$facts.lifecycleProbe.entryPoint -cne 'ScreenGuide.Stage5LifecycleProbe.exe') {
        Complete 'BLOCKED' 's5_lifecycle_probe_bundle_invalid' 1 ([ordered]@{ phase = 'artifact' })
    }
    if (-not [bool]$facts.sandboxAvailable) {
        Complete 'BLOCKED' 's5_lifecycle_sandbox_unavailable' 1 ([ordered]@{ phase = 'host' })
    }
    $protectedHostProperties = @($facts.protectedHostInstall.PSObject.Properties.Name)
    foreach ($requiredProperty in @('present', 'productRootPresent', 'uninstallRegistrationPresent')) {
        if ($protectedHostProperties -cnotcontains $requiredProperty -or
            $facts.protectedHostInstall.$requiredProperty -isnot [bool]) {
            Complete 'BLOCKED' 's5_lifecycle_contract_invalid' 1 ([ordered]@{ phase = 'protected-host' })
        }
    }
    if ([bool]$facts.protectedHostInstall.present -ne
        ([bool]$facts.protectedHostInstall.productRootPresent -or [bool]$facts.protectedHostInstall.uninstallRegistrationPresent)) {
        Complete 'BLOCKED' 's5_lifecycle_contract_invalid' 1 ([ordered]@{ phase = 'protected-host' })
    }
    foreach ($name in @('networking', 'clipboard', 'audioInput', 'videoInput', 'printer')) {
        if ([string]$facts.sandboxPolicy.$name -cne 'Disable') {
            Complete 'BLOCKED' 's5_lifecycle_network_policy_invalid' 1 ([ordered]@{ phase = 'sandbox-policy' })
        }
    }
    if (-not [bool]$facts.mappings.inputReadOnly -or [bool]$facts.mappings.repoMapped) {
        Complete 'BLOCKED' 's5_lifecycle_input_mapping_writable' 1 ([ordered]@{ phase = 'mapping' })
    }
    if ([bool]$facts.mappings.evidenceReadOnly -or
        [string]::IsNullOrWhiteSpace([string]$facts.mappings.inputHostPath) -or
        [string]::IsNullOrWhiteSpace([string]$facts.mappings.evidenceHostPath) -or
        [IO.Path]::GetFullPath([string]$facts.mappings.inputHostPath) -eq [IO.Path]::GetFullPath([string]$facts.mappings.evidenceHostPath)) {
        Complete 'BLOCKED' 's5_lifecycle_evidence_mapping_invalid' 1 ([ordered]@{ phase = 'mapping' })
    }
    if (-not [bool]$facts.cleanup.ownershipConfirmed -or
        [string]::IsNullOrWhiteSpace([string]$facts.cleanup.ownedRoot)) {
        Complete 'BLOCKED' 's5_lifecycle_cleanup_root_invalid' 1 ([ordered]@{ phase = 'cleanup' })
    }
    foreach ($path in @(
        [string]$facts.mappings.inputHostPath,
        [string]$facts.mappings.evidenceHostPath,
        [string]$facts.cleanup.ownedRoot)) {
        if (Has-ReparsePoint $path) {
            Complete 'BLOCKED' 's5_lifecycle_reparse_point' 1 ([ordered]@{ phase = 'path' })
        }
    }
    if ([string]::IsNullOrWhiteSpace($WsbOutputPath) -or [string]::IsNullOrWhiteSpace($PlanOutputPath)) {
        Complete 'BLOCKED' 's5_lifecycle_contract_invalid' 1 ([ordered]@{ phase = 'output' })
    }

    $plan = [ordered]@{
        contractVersion = 1
        mode = 'native'
        sourceSha = [string]$facts.expectedSourceSha
        oldInstaller = [ordered]@{ fileName = [string]$facts.oldInstaller.fileName; sha256 = [string]$facts.oldInstaller.expectedSha256 }
        candidateInstaller = [ordered]@{ fileName = [string]$facts.candidateInstaller.fileName; sha256 = [string]$facts.candidateInstaller.expectedSha256 }
        lifecycleProbe = [ordered]@{
            transportKind = [string]$facts.lifecycleProbe.transportKind
            fileName = [string]$facts.lifecycleProbe.fileName
            sha256 = [string]$facts.lifecycleProbe.expectedSha256
            manifestSha256 = [string]$facts.lifecycleProbe.manifestSha256
            fileCount = [int]$facts.lifecycleProbe.fileCount
            entryPoint = [string]$facts.lifecycleProbe.entryPoint
        }
        oldIdentity = $facts.oldIdentity
        candidateIdentity = $facts.candidateIdentity
        noticeFiles = @(
            'THIRD-PARTY-NOTICES.txt',
            'distribution/bundle-manifest.json',
            'distribution/notice-index.json'
        )
        budgets = [ordered]@{
            installerExecutions = 5
            installOrUpgradeExecutions = 3
            uninstallExecutions = 2
            hostExecutions = 3
            retries = 0
            resends = 0
        }
    }
    Write-Json $PlanOutputPath $plan

    $inputHost = [Security.SecurityElement]::Escape([IO.Path]::GetFullPath([string]$facts.mappings.inputHostPath))
    $evidenceHost = [Security.SecurityElement]::Escape([IO.Path]::GetFullPath([string]$facts.mappings.evidenceHostPath))
    $wsb = @"
<Configuration>
  <Networking>Disable</Networking>
  <ClipboardRedirection>Disable</ClipboardRedirection>
  <AudioInput>Disable</AudioInput>
  <VideoInput>Disable</VideoInput>
  <PrinterRedirection>Disable</PrinterRedirection>
  <vGPU>Disable</vGPU>
  <MappedFolders>
    <MappedFolder><HostFolder>$inputHost</HostFolder><SandboxFolder>C:\YuanshuLifecycleInput</SandboxFolder><ReadOnly>true</ReadOnly></MappedFolder>
    <MappedFolder><HostFolder>$evidenceHost</HostFolder><SandboxFolder>C:\YuanshuLifecycleEvidence</SandboxFolder><ReadOnly>false</ReadOnly></MappedFolder>
  </MappedFolders>
  <LogonCommand>
    <Command>powershell.exe -NoProfile -ExecutionPolicy Bypass -File C:\YuanshuLifecycleInput\Invoke-Stage5SandboxLifecycle.ps1 -PlanPath C:\YuanshuLifecycleInput\lifecycle-plan.json -EvidencePath C:\YuanshuLifecycleEvidence\lifecycle-result.json</Command>
  </LogonCommand>
</Configuration>
"@
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($WsbOutputPath))) | Out-Null
    [IO.File]::WriteAllText([IO.Path]::GetFullPath($WsbOutputPath), $wsb.Replace("`r`n", "`n"), [Text.UTF8Encoding]::new($false))
    Complete 'PASS' $null 0 ([ordered]@{
        wsbGenerated = $true
        planGenerated = $true
        mappings = 2
        hostInstallerExecutions = 0
        protectedHostInstall = [ordered]@{
            present = [bool]$facts.protectedHostInstall.present
            productRootPresent = [bool]$facts.protectedHostInstall.productRootPresent
            uninstallRegistrationPresent = [bool]$facts.protectedHostInstall.uninstallRegistrationPresent
        }
    })
}

$phases = $facts.phases
if ($null -eq $phases -or $null -eq $facts.oldPair -or $null -eq $facts.newPair -or $null -eq $facts.counters) {
    Complete 'BLOCKED' 's5_lifecycle_evidence_invalid' 1 ([ordered]@{ phase = 'evidence' })
}
if ($phases.matchingBackupCount -eq 0) {
    Complete 'BLOCKED' 's5_lifecycle_backup_missing' 1 ([ordered]@{ phase = 'rollback' })
}
if ($phases.matchingBackupCount -ne 1 -or $phases.matchingBackupSchema -ne 10 -or -not [bool]$phases.matchingBackupRestored) {
    Complete 'BLOCKED' 's5_lifecycle_backup_mismatch' 1 ([ordered]@{ phase = 'rollback' })
}
if ([string]$facts.oldPair.clientProductVersion -cne [string]$facts.oldPair.hostProductVersion -or
    [string]$facts.newPair.clientProductVersion -cne [string]$facts.newPair.hostProductVersion) {
    Complete 'BLOCKED' 's5_lifecycle_mixed_version_pair' 1 ([ordered]@{ phase = 'identity' })
}
if (-not [bool]$phases.noticeLayoutVerified) {
    Complete 'BLOCKED' 's5_lifecycle_notice_missing' 1 ([ordered]@{ phase = 'notice' })
}
if (-not (Is-Sha256 ([string]$phases.v11HashBeforeMissingBackup)) -or
    [string]$phases.v11HashBeforeMissingBackup -cne [string]$phases.v11HashAfterMissingBackup -or
    -not [bool]$phases.missingBackupRefused) {
    Complete 'BLOCKED' 's5_lifecycle_v11_hash_changed' 1 ([ordered]@{ phase = 'rollback-refusal' })
}
if (-not (Is-Sha256 ([string]$phases.preservedV11HashBeforeRollback)) -or
    [string]$phases.preservedV11HashBeforeRollback -cne [string]$phases.preservedV11HashAfterRollback) {
    Complete 'BLOCKED' 's5_lifecycle_v11_hash_changed' 1 ([ordered]@{ phase = 'rollback-preservation' })
}
if (-not [bool]$phases.uninstallProgramRemoved -or -not [bool]$phases.uninstallRegistrationRemoved -or -not [bool]$phases.dataRetained) {
    Complete 'BLOCKED' 's5_lifecycle_cleanup_failed' 1 ([ordered]@{ phase = 'cleanup' })
}
foreach ($required in @(
    'cleanState', 'oldInstall', 'canaryCreated', 'upgradeInstall', 'canaryRetainedAfterUpgrade',
    'canaryRetainedAfterRollback')) {
    if (-not [bool]$phases.$required) {
        Complete 'BLOCKED' 's5_lifecycle_evidence_invalid' 1 ([ordered]@{ phase = 'lifecycle' })
    }
}
if ($phases.oldSchema -ne 10 -or $phases.newSchema -ne 11 -or $phases.rollbackSchema -ne 10 -or
    -not [bool]$facts.terminal.finalized -or [string]$facts.terminal.status -cne 'PASS') {
    Complete 'BLOCKED' 's5_lifecycle_evidence_invalid' 1 ([ordered]@{ phase = 'lifecycle' })
}
foreach ($counter in @('networkRequests', 'providerRequests', 'credentialReads', 'retries', 'resends')) {
    if ([int]$facts.counters.$counter -ne 0) {
        Complete 'BLOCKED' 's5_lifecycle_external_activity_detected' 1 ([ordered]@{ phase = 'counters' })
    }
}

$requiredExecutionProperties = @(
    'installerExecutions',
    'installOrUpgradeExecutions',
    'uninstallExecutions',
    'plannedInstallerExecutions',
    'plannedInstallOrUpgradeExecutions',
    'plannedUninstallExecutions',
    'installerExitCodeCount')
$executionPropertyNames = @($facts.execution.PSObject.Properties.Name)
foreach ($requiredProperty in $requiredExecutionProperties) {
    if ($executionPropertyNames -cnotcontains $requiredProperty) {
        Complete 'BLOCKED' 's5_lifecycle_installer_execution_count_mismatch' 1 ([ordered]@{ phase = 'execution-budget' })
    }
}
if ([int]$facts.execution.installerExecutions -gt 5 -or
    [int]$facts.execution.installOrUpgradeExecutions -gt 3 -or
    [int]$facts.execution.uninstallExecutions -gt 2) {
    Complete 'BLOCKED' 's5_lifecycle_installer_execution_budget_exceeded' 1 ([ordered]@{ phase = 'execution-budget' })
}
if ([int]$facts.execution.installerExecutions -ne 5 -or
    [int]$facts.execution.installOrUpgradeExecutions -ne 3 -or
    [int]$facts.execution.uninstallExecutions -ne 2 -or
    [int]$facts.execution.installerExecutions -ne ([int]$facts.execution.installOrUpgradeExecutions + [int]$facts.execution.uninstallExecutions) -or
    [int]$facts.execution.plannedInstallerExecutions -ne 5 -or
    [int]$facts.execution.plannedInstallOrUpgradeExecutions -ne 3 -or
    [int]$facts.execution.plannedUninstallExecutions -ne 2 -or
    [int]$facts.execution.installerExitCodeCount -ne [int]$facts.execution.installerExecutions) {
    Complete 'BLOCKED' 's5_lifecycle_installer_execution_count_mismatch' 1 ([ordered]@{ phase = 'execution-budget' })
}

Complete 'PASS' $null 0 ([ordered]@{
    sourceSha = [string]$facts.sourceSha
    oldSchema = 10
    newSchema = 11
    rollbackSchema = 10
    matchingBackupCount = 1
    noticeLayoutVerified = $true
    dataRetained = $true
    oldPair = $facts.oldPair
    newPair = $facts.newPair
    v11DatabaseSha256 = [string]$phases.v11HashBeforeMissingBackup
    preservedV11DatabaseSha256 = [string]$phases.preservedV11HashAfterRollback
    execution = $facts.execution
})
