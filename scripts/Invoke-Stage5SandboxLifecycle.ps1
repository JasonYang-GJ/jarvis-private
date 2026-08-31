param(
    [Parameter(Mandatory = $true)]
    [string]$PlanPath,

    [Parameter(Mandatory = $true)]
    [string]$EvidencePath
)

$ErrorActionPreference = 'Stop'
$inputRoot = 'C:\YuanshuLifecycleInput'
$evidenceRoot = 'C:\YuanshuLifecycleEvidence'
$runtimeRoot = Join-Path ([IO.Path]::GetTempPath()) ('YuanshuStage5Lifecycle-' + [Guid]::NewGuid().ToString('N'))
$installRoot = Join-Path $env:LOCALAPPDATA 'Programs\YuanshuDesktop'
$dataRoot = Join-Path $env:LOCALAPPDATA 'ScreenGuideTeacher'
$databasePath = Join-Path $dataRoot 'state\tasking.db'
$existingUninstallKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\{E2B9C242-2965-48BC-B2C6-CF83A2B11953}_is1'
$contractScript = Join-Path $inputRoot 'Test-Stage5SandboxLifecycleContract.ps1'
$budgetScript = Join-Path $inputRoot 'Stage5LifecycleExecutionBudget.ps1'
$diagnosticsScript = Join-Path $inputRoot 'Stage5LifecycleFailureDiagnostics.ps1'
$probeTransportScript = Join-Path $inputRoot 'Stage5LifecycleProbeTransport.ps1'
$probeExecutable = $null
$errorCode = 's5_lifecycle_unexpected_failure'
$hostExecutions = 0
$exitCodes = [Collections.Generic.List[int]]::new()
$installerExitCodes = [Collections.Generic.List[int]]::new()
$installerBudget = $null
if (-not (Test-Path -LiteralPath $diagnosticsScript -PathType Leaf)) { throw 's5_lifecycle_diagnostics_missing' }
. $diagnosticsScript
$diagnostics = New-Stage5LifecycleDiagnosticState

function Throw-Code([string]$code) {
    $script:errorCode = $code
    throw $code
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

function Write-SafeFailure([string]$code) {
    $budgetSnapshot = if ($null -eq $installerBudget) { $null } else { Get-Stage5LifecycleExecutionBudgetSnapshot -Budget $installerBudget }
    $presence = Get-Stage5LifecycleSafePresence -InstallRoot $installRoot -UninstallKey $existingUninstallKey
    [void](Write-Stage5LifecycleFailureEvidence -EvidencePath $EvidencePath -ErrorCode $code `
        -State $diagnostics -InstallerExecutionBudget $budgetSnapshot -Presence $presence)
}

function Invoke-Hidden(
    [string]$file,
    [string[]]$arguments,
    [ValidateSet('installer', 'host', 'probe')] [string]$kind,
    [ValidateSet('InstallOrUpgrade', 'Uninstall')] [string]$installerKind) {
    try {
        $exitCode = Invoke-Stage5LifecycleObservedProcess -State $diagnostics -Budget $installerBudget `
            -FilePath $file -Arguments $arguments -Kind $kind -InstallerKind $installerKind
    }
    catch {
        if ([string]$_.Exception.Message -cmatch '^s5_lifecycle_[a-z0-9_]+$') {
            $script:errorCode = [string]$_.Exception.Message
        }
        throw
    }
    $exitCodes.Add($exitCode)
    if ($kind -eq 'installer') { $installerExitCodes.Add($exitCode) }
    if ($kind -eq 'host') { $script:hostExecutions++ }
}

function Assert-Hash([string]$path, [string]$expected, [string]$code) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf) -or
        (Get-FileHash -Algorithm SHA256 -LiteralPath $path).Hash -cne $expected) {
        Throw-Code $code
    }
}

function Add-ProbeValidation([string]$layer, $validation) {
    try { Add-Stage5LifecycleProbeValidation -State $diagnostics -Layer $layer -Validation $validation }
    catch { Throw-Code 's5_lifecycle_probe_evidence_invalid' }
    if ([string]$validation.status -cne 'PASS') {
        $code = [string]$validation.errorCode
        if ($code -cnotmatch '^s5_lifecycle_[a-z0-9_]+$') { $code = 's5_lifecycle_probe_bundle_invalid' }
        Throw-Code $code
    }
}

function Get-PairIdentity {
    return Get-Stage5LifecyclePairIdentity -State $diagnostics -InstallRoot $installRoot
}

function Assert-Pair($actual, $expected) {
    try { Assert-Stage5LifecyclePairIdentity -Actual $actual -Expected $expected }
    catch { Throw-Code ([string]$_.Exception.Message) }
}

function Invoke-HostOnce([string]$pipeName) {
    $previousData = $env:SCREEN_GUIDE_DATA_DIRECTORY
    $previousPipe = $env:SCREEN_GUIDE_PIPE_NAME
    try {
        $env:SCREEN_GUIDE_DATA_DIRECTORY = $dataRoot
        $env:SCREEN_GUIDE_PIPE_NAME = $pipeName
        Invoke-Hidden (Join-Path $installRoot 'ScreenGuide.DesktopHost.exe') @('--run-once') host
    }
    finally {
        $env:SCREEN_GUIDE_DATA_DIRECTORY = $previousData
        $env:SCREEN_GUIDE_PIPE_NAME = $previousPipe
    }
}

function Invoke-Probe([string]$database, [int]$expectedSchema, [string]$label) {
    if ([string]::IsNullOrWhiteSpace($probeExecutable)) { Throw-Code 's5_lifecycle_probe_bundle_invalid' }
    $probeOutput = Join-Path $runtimeRoot ($label + '.json')
    Invoke-Hidden $probeExecutable @('--database', $database, '--expected-schema', [string]$expectedSchema, '--output', $probeOutput) probe
    $result = Get-Content -Raw -LiteralPath $probeOutput | ConvertFrom-Json
    if (-not [bool]$result.passed -or $result.schemaVersion -ne $expectedSchema -or -not [bool]$result.integrityOk) {
        Throw-Code 's5_lifecycle_schema_mismatch'
    }
    return $result
}

function Install([string]$installer, [string]$logName) {
    Invoke-Hidden $installer @(
        '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART',
        "/DIR=$installRoot", "/LOG=$(Join-Path $runtimeRoot $logName)") installer InstallOrUpgrade
}

function Uninstall {
    $uninstaller = Join-Path $installRoot 'unins000.exe'
    if (-not (Test-Path -LiteralPath $uninstaller -PathType Leaf)) { Throw-Code 's5_lifecycle_uninstaller_missing' }
    Invoke-Hidden $uninstaller @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART') installer Uninstall
}

function Get-MatchingBackup([string]$directory) {
    $matches = @(Get-ChildItem -LiteralPath $directory -Filter 'tasking.pre-v11-from-v10-*.backup.db' -File -ErrorAction SilentlyContinue)
    if ($matches.Count -eq 0) { Throw-Code 's5_lifecycle_backup_missing' }
    if ($matches.Count -ne 1) { Throw-Code 's5_lifecycle_backup_mismatch' }
    return $matches[0].FullName
}

try {
    Set-Stage5LifecyclePhase $diagnostics 'plan-validation'
    $resolvedPlan = [IO.Path]::GetFullPath($PlanPath)
    $resolvedEvidence = [IO.Path]::GetFullPath($EvidencePath)
    if (-not $resolvedPlan.StartsWith($inputRoot + '\', [StringComparison]::OrdinalIgnoreCase) -or
        -not $resolvedEvidence.StartsWith($evidenceRoot + '\', [StringComparison]::OrdinalIgnoreCase) -or
        (Has-ReparsePoint $resolvedPlan) -or (Has-ReparsePoint $resolvedEvidence)) {
        Throw-Code 's5_lifecycle_path_invalid'
    }
    $plan = Get-Content -Raw -LiteralPath $resolvedPlan | ConvertFrom-Json
    if ($plan.contractVersion -ne 1 -or [string]$plan.mode -cne 'native' -or
        [string]$plan.lifecycleProbe.transportKind -cne 'sealed-zip-v1' -or
        [string]$plan.lifecycleProbe.fileName -cne 'lifecycle-probe-transport.zip' -or
        [string]$plan.lifecycleProbe.sha256 -cnotmatch '^[0-9A-F]{64}$' -or
        [string]$plan.lifecycleProbe.manifestSha256 -cnotmatch '^[0-9A-F]{64}$' -or
        [int]$plan.lifecycleProbe.fileCount -lt 7 -or
        [string]$plan.lifecycleProbe.entryPoint -cne 'ScreenGuide.Stage5LifecycleProbe.exe' -or
        $plan.budgets.installerExecutions -ne 5 -or
        $plan.budgets.installOrUpgradeExecutions -ne 3 -or
        $plan.budgets.uninstallExecutions -ne 2 -or
        $plan.budgets.retries -ne 0 -or $plan.budgets.resends -ne 0) {
        Throw-Code 's5_lifecycle_plan_invalid'
    }
    if (-not (Test-Path -LiteralPath $budgetScript -PathType Leaf) -or (Has-ReparsePoint $budgetScript)) {
        Throw-Code 's5_lifecycle_plan_invalid'
    }
    if (-not (Test-Path -LiteralPath $probeTransportScript -PathType Leaf) -or (Has-ReparsePoint $probeTransportScript)) {
        Throw-Code 's5_lifecycle_probe_transport_invalid'
    }
    . $budgetScript
    . $probeTransportScript
    try {
        $installerBudget = New-Stage5LifecycleExecutionBudget `
            -InstallerExecutions ([int]$plan.budgets.installerExecutions) `
            -InstallOrUpgradeExecutions ([int]$plan.budgets.installOrUpgradeExecutions) `
            -UninstallExecutions ([int]$plan.budgets.uninstallExecutions)
    }
    catch { Throw-Code $_.Exception.Message }
    [IO.Directory]::CreateDirectory($runtimeRoot) | Out-Null
    Set-Stage5LifecyclePhase $diagnostics 'sandbox-clean-validation'
    if ((Test-Path -LiteralPath $installRoot) -or (Test-Path -LiteralPath $existingUninstallKey) -or (Test-Path -LiteralPath $dataRoot)) {
        Throw-Code 's5_lifecycle_sandbox_not_clean'
    }
    Set-Stage5LifecyclePhase $diagnostics 'artifact-validation'
    $oldInstaller = Join-Path $inputRoot ([string]$plan.oldInstaller.fileName)
    $candidateInstaller = Join-Path $inputRoot ([string]$plan.candidateInstaller.fileName)
    $probeTransportMapped = Join-Path $inputRoot ([string]$plan.lifecycleProbe.fileName)
    Assert-Hash $oldInstaller ([string]$plan.oldInstaller.sha256) 's5_lifecycle_old_installer_hash_mismatch'
    Assert-Hash $candidateInstaller ([string]$plan.candidateInstaller.sha256) 's5_lifecycle_candidate_installer_hash_mismatch'
    Set-Stage5LifecyclePhase $diagnostics 'probe-bundle-validation'
    $mappedValidation = Test-Stage5LifecycleProbeTransport -ArchivePath $probeTransportMapped `
        -ExpectedArchiveSha256 ([string]$plan.lifecycleProbe.sha256) `
        -ExpectedManifestSha256 ([string]$plan.lifecycleProbe.manifestSha256) `
        -ExpectedFileCount ([int]$plan.lifecycleProbe.fileCount)
    Add-ProbeValidation 'transport-mapped' $mappedValidation

    $probeTransportLocal = Join-Path $runtimeRoot 'lifecycle-probe-transport.zip'
    try { Copy-Item -LiteralPath $probeTransportMapped -Destination $probeTransportLocal }
    catch {
        $copyFailure = New-Stage5ProbeValidationResult 'BLOCKED' 's5_lifecycle_probe_transport_copy_failed' 'transport-copy' `
            ([int]$mappedValidation.declaredCount) 0 0 0 ([string]$mappedValidation.archiveSha256) `
            ([string]$mappedValidation.manifestSha256) $false
        Add-ProbeValidation 'transport-local' $copyFailure
    }
    $localValidation = Test-Stage5LifecycleProbeTransport -ArchivePath $probeTransportLocal `
        -ExpectedArchiveSha256 ([string]$plan.lifecycleProbe.sha256) `
        -ExpectedManifestSha256 ([string]$plan.lifecycleProbe.manifestSha256) `
        -ExpectedFileCount ([int]$plan.lifecycleProbe.fileCount)
    Add-ProbeValidation 'transport-local' $localValidation

    $probeExpandedRoot = Join-Path $runtimeRoot 'probe-expanded'
    $expandedValidation = Expand-Stage5LifecycleProbeTransport -ArchivePath $probeTransportLocal `
        -DestinationRoot $probeExpandedRoot -ApprovedRoot $runtimeRoot `
        -ExpectedArchiveSha256 ([string]$plan.lifecycleProbe.sha256) `
        -ExpectedManifestSha256 ([string]$plan.lifecycleProbe.manifestSha256) `
        -ExpectedFileCount ([int]$plan.lifecycleProbe.fileCount)
    Add-ProbeValidation 'expanded-bundle' $expandedValidation

    $probeExecutable = Join-Path $probeExpandedRoot ([string]$plan.lifecycleProbe.entryPoint)
    $entryPointValid = (Test-Path -LiteralPath $probeExecutable -PathType Leaf) -and
        -not (Test-Stage5ProbeReparsePoint $probeExecutable)
    $entryValidation = New-Stage5ProbeValidationResult `
        $(if ($entryPointValid) { 'PASS' } else { 'BLOCKED' }) `
        $(if ($entryPointValid) { $null } else { 's5_lifecycle_probe_entrypoint_invalid' }) `
        'entrypoint' ([int]$expandedValidation.declaredCount) ([int]$expandedValidation.actualCount) `
        0 0 ([string]$expandedValidation.archiveSha256) ([string]$expandedValidation.manifestSha256) $entryPointValid
    Add-ProbeValidation 'entrypoint' $entryValidation

    Set-Stage5LifecyclePhase $diagnostics 'old-install'
    Install $oldInstaller 'install-v0.5.log'
    Set-Stage5LifecyclePhase $diagnostics 'old-pair-validation'
    $oldPair = Get-PairIdentity
    Assert-Pair $oldPair $plan.oldIdentity
    Set-Stage5LifecyclePhase $diagnostics 'old-host-start'
    Invoke-HostOnce ('Yuanshu.Stage5.Old.' + [Guid]::NewGuid().ToString('N'))
    Set-Stage5LifecyclePhase $diagnostics 'old-schema-validation'
    $oldSchema = Invoke-Probe $databasePath 10 'schema-v10-before-upgrade'
    Set-Stage5LifecyclePhase $diagnostics 'canary-create'
    $canaryPath = Join-Path $dataRoot 'stage5-lifecycle-canary.txt'
    [IO.File]::WriteAllText($canaryPath, 'yuanshu-stage5-lifecycle-canary-v1', [Text.UTF8Encoding]::new($false))
    $canaryHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $canaryPath).Hash

    Set-Stage5LifecyclePhase $diagnostics 'candidate-upgrade'
    Install $candidateInstaller 'upgrade-v0.6.log'
    Set-Stage5LifecyclePhase $diagnostics 'candidate-pair-validation'
    $newPair = Get-PairIdentity
    Assert-Pair $newPair $plan.candidateIdentity
    Set-Stage5LifecyclePhase $diagnostics 'candidate-host-start'
    Invoke-HostOnce ('Yuanshu.Stage5.New.' + [Guid]::NewGuid().ToString('N'))
    Set-Stage5LifecyclePhase $diagnostics 'candidate-schema-validation'
    $newSchema = Invoke-Probe $databasePath 11 'schema-v11-after-upgrade'
    Set-Stage5LifecyclePhase $diagnostics 'backup-validation'
    $backup = Get-MatchingBackup (Split-Path -Parent $databasePath)
    $backupSchema = Invoke-Probe $backup 10 'schema-v10-backup'
    if ((Get-FileHash -Algorithm SHA256 -LiteralPath $canaryPath).Hash -cne $canaryHash) {
        Throw-Code 's5_lifecycle_canary_changed'
    }
    Set-Stage5LifecyclePhase $diagnostics 'notice-validation'
    $requiredNoticeFiles = @(
        'THIRD-PARTY-NOTICES.txt',
        'distribution/bundle-manifest.json',
        'distribution/notice-index.json'
    )
    foreach ($required in $requiredNoticeFiles) {
        if (@($plan.noticeFiles) -cnotcontains $required) { Throw-Code 's5_lifecycle_notice_missing' }
    }
    foreach ($relative in @($plan.noticeFiles)) {
        $noticePath = Join-Path $installRoot ([string]$relative).Replace('/', '\')
        if (-not (Test-Path -LiteralPath $noticePath -PathType Leaf)) { Throw-Code 's5_lifecycle_notice_missing' }
    }

    Set-Stage5LifecyclePhase $diagnostics 'rollback-refusal-validation'
    $v11HashBeforeMissing = (Get-FileHash -Algorithm SHA256 -LiteralPath $databasePath).Hash
    $emptyBackupRoot = Join-Path $runtimeRoot 'missing-backup-case'
    [IO.Directory]::CreateDirectory($emptyBackupRoot) | Out-Null
    $missingRefused = $false
    try { [void](Get-MatchingBackup $emptyBackupRoot) }
    catch {
        if ($script:errorCode -ceq 's5_lifecycle_backup_missing') { $missingRefused = $true; $script:errorCode = 's5_lifecycle_unexpected_failure' }
        else { throw }
    }
    $v11HashAfterMissing = (Get-FileHash -Algorithm SHA256 -LiteralPath $databasePath).Hash
    if (-not $missingRefused -or $v11HashBeforeMissing -cne $v11HashAfterMissing) { Throw-Code 's5_lifecycle_v11_hash_changed' }

    Set-Stage5LifecyclePhase $diagnostics 'preserve-v11'
    $preservedRoot = Join-Path $dataRoot 'stage5-preserved-v11'
    [IO.Directory]::CreateDirectory($preservedRoot) | Out-Null
    $preservedV11 = Join-Path $preservedRoot 'tasking.v11.preserved.db'
    Copy-Item -LiteralPath $databasePath -Destination $preservedV11
    $preservedBefore = (Get-FileHash -Algorithm SHA256 -LiteralPath $preservedV11).Hash
    Set-Stage5LifecyclePhase $diagnostics 'candidate-uninstall'
    Uninstall
    Set-Stage5LifecyclePhase $diagnostics 'restore-v10-backup'
    Copy-Item -LiteralPath $backup -Destination $databasePath -Force
    [void](Invoke-Probe $databasePath 10 'schema-v10-restored')
    Set-Stage5LifecyclePhase $diagnostics 'rollback-install'
    Install $oldInstaller 'rollback-v0.5.log'
    Set-Stage5LifecyclePhase $diagnostics 'rollback-pair-validation'
    $rollbackPair = Get-PairIdentity
    Assert-Pair $rollbackPair $plan.oldIdentity
    Set-Stage5LifecyclePhase $diagnostics 'rollback-host-start'
    Invoke-HostOnce ('Yuanshu.Stage5.Rollback.' + [Guid]::NewGuid().ToString('N'))
    Set-Stage5LifecyclePhase $diagnostics 'rollback-schema-validation'
    [void](Invoke-Probe $databasePath 10 'schema-v10-after-rollback')
    $preservedAfter = (Get-FileHash -Algorithm SHA256 -LiteralPath $preservedV11).Hash
    if ($preservedBefore -cne $preservedAfter -or (Get-FileHash -Algorithm SHA256 -LiteralPath $canaryPath).Hash -cne $canaryHash) {
        Throw-Code 's5_lifecycle_v11_hash_changed'
    }

    Set-Stage5LifecyclePhase $diagnostics 'final-uninstall'
    Uninstall
    $programRemoved = -not (Test-Path -LiteralPath $installRoot)
    $registrationRemoved = -not (Test-Path -LiteralPath $existingUninstallKey)
    $dataRetained = (Test-Path -LiteralPath $databasePath) -and (Test-Path -LiteralPath $preservedV11)

    Set-Stage5LifecyclePhase $diagnostics 'terminal-validation'
    $executionBudget = Get-Stage5LifecycleExecutionBudgetSnapshot -Budget $installerBudget
    if ([int]$executionBudget.installerExecutions -ne 5 -or
        [int]$executionBudget.installOrUpgradeExecutions -ne 3 -or
        [int]$executionBudget.uninstallExecutions -ne 2 -or
        $installerExitCodes.Count -ne 5) {
        Throw-Code 's5_lifecycle_installer_execution_count_mismatch'
    }
    $executionBudget.installerExitCodeCount = $installerExitCodes.Count
    $facts = [ordered]@{
        contractVersion = 1
        coordinatorInstance = 'sandbox-lifecycle-v1'
        sourceSha = [string]$plan.sourceSha
        phases = [ordered]@{
            cleanState = $true; oldInstall = $true; oldSchema = 10; canaryCreated = $true
            upgradeInstall = $true; newSchema = 11; matchingBackupCount = 1; matchingBackupSchema = 10
            canaryRetainedAfterUpgrade = $true; noticeLayoutVerified = $true; missingBackupRefused = $missingRefused
            v11HashBeforeMissingBackup = $v11HashBeforeMissing; v11HashAfterMissingBackup = $v11HashAfterMissing
            matchingBackupRestored = $true; rollbackSchema = 10; canaryRetainedAfterRollback = $true
            preservedV11HashBeforeRollback = $preservedBefore; preservedV11HashAfterRollback = $preservedAfter
            uninstallProgramRemoved = $programRemoved; uninstallRegistrationRemoved = $registrationRemoved; dataRetained = $dataRetained
        }
        oldPair = [ordered]@{ clientProductVersion = $oldPair.clientProductVersion; hostProductVersion = $oldPair.hostProductVersion; fileVersion = $oldPair.clientFileVersion }
        newPair = [ordered]@{ clientProductVersion = $newPair.clientProductVersion; hostProductVersion = $newPair.hostProductVersion; fileVersion = $newPair.clientFileVersion }
        terminal = [ordered]@{ status = 'PASS'; finalized = $true }
        counters = [ordered]@{ networkRequests = 0; providerRequests = 0; credentialReads = 0; retries = 0; resends = 0 }
        execution = [ordered]@{
            installerExecutions = $executionBudget.installerExecutions
            installOrUpgradeExecutions = $executionBudget.installOrUpgradeExecutions
            uninstallExecutions = $executionBudget.uninstallExecutions
            plannedInstallerExecutions = $executionBudget.plannedInstallerExecutions
            plannedInstallOrUpgradeExecutions = $executionBudget.plannedInstallOrUpgradeExecutions
            plannedUninstallExecutions = $executionBudget.plannedUninstallExecutions
            installerExitCodeCount = $executionBudget.installerExitCodeCount
            hostExecutions = $hostExecutions
            exitCodes = $exitCodes.ToArray()
        }
    }
    Set-Stage5LifecyclePhase $diagnostics 'evidence-validation'
    $factsPath = Join-Path $runtimeRoot 'lifecycle-facts.json'
    [IO.File]::WriteAllText($factsPath, (($facts | ConvertTo-Json -Depth 30).Replace("`r`n", "`n") + "`n"), [Text.UTF8Encoding]::new($false))
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $contractScript -Mode Evidence -FactsPath $factsPath -ResultPath $resolvedEvidence
    exit $LASTEXITCODE
}
catch {
    if ($errorCode -ceq 's5_lifecycle_unexpected_failure') {
        if ([string]$_.Exception.Message -cmatch '^s5_lifecycle_[a-z0-9_]+$') {
            $errorCode = [string]$_.Exception.Message
        } else {
            $errorCode = Get-Stage5LifecyclePhaseFailureCode ([string]$diagnostics.CurrentPhase)
        }
    }
    Write-SafeFailure $errorCode
    exit 1
}
finally {
    try { Remove-Stage5LifecycleOwnedRuntime $runtimeRoot }
    catch { }
}
