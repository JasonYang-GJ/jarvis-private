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
$errorCode = 's5_lifecycle_unexpected_failure'
$hostExecutions = 0
$exitCodes = [Collections.Generic.List[int]]::new()
$installerExitCodes = [Collections.Generic.List[int]]::new()
$installerBudget = $null

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
    $result = [ordered]@{
        contractVersion = 1
        status = 'BLOCKED'
        errorCode = $code
        finalized = $true
        networkRequests = 0
        providerRequests = 0
        credentialReads = 0
        installerExecutionBudget = $budgetSnapshot
        hostExecutions = $hostExecutions
    }
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($EvidencePath)) | Out-Null
    [IO.File]::WriteAllText(
        $EvidencePath,
        (($result | ConvertTo-Json -Depth 10).Replace("`r`n", "`n") + "`n"),
        [Text.UTF8Encoding]::new($false))
}

function Invoke-Hidden(
    [string]$file,
    [string[]]$arguments,
    [ValidateSet('installer', 'host', 'probe')] [string]$kind,
    [ValidateSet('InstallOrUpgrade', 'Uninstall')] [string]$installerKind) {
    if ($kind -eq 'installer') {
        if ($null -eq $installerBudget -or [string]::IsNullOrWhiteSpace($installerKind)) {
            Throw-Code 's5_lifecycle_installer_execution_count_mismatch'
        }
        try { Enter-Stage5LifecycleInstallerExecution -Budget $installerBudget -Kind $installerKind }
        catch { Throw-Code $_.Exception.Message }
    }
    $process = Start-Process -FilePath $file -ArgumentList $arguments -Wait -PassThru -WindowStyle Hidden
    $exitCodes.Add($process.ExitCode)
    if ($kind -eq 'installer') { $installerExitCodes.Add($process.ExitCode) }
    if ($kind -eq 'host') { $script:hostExecutions++ }
    if ($process.ExitCode -ne 0) { Throw-Code ('s5_lifecycle_' + $kind + '_failed') }
}

function Assert-Hash([string]$path, [string]$expected, [string]$code) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf) -or
        (Get-FileHash -Algorithm SHA256 -LiteralPath $path).Hash -cne $expected) {
        Throw-Code $code
    }
}

function Get-PairIdentity {
    $client = Join-Path $installRoot 'ScreenGuide.DesktopClient.exe'
    $host = Join-Path $installRoot 'ScreenGuide.DesktopHost.exe'
    foreach ($path in @($client, $host)) {
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { Throw-Code 's5_lifecycle_mixed_version_pair' }
    }
    $clientInfo = [Diagnostics.FileVersionInfo]::GetVersionInfo($client)
    $hostInfo = [Diagnostics.FileVersionInfo]::GetVersionInfo($host)
    return [ordered]@{
        clientProductVersion = $clientInfo.ProductVersion.Trim()
        hostProductVersion = $hostInfo.ProductVersion.Trim()
        clientFileVersion = $clientInfo.FileVersion.Trim()
        hostFileVersion = $hostInfo.FileVersion.Trim()
    }
}

function Assert-Pair($actual, $expected) {
    if ([string]$actual.clientProductVersion -cne [string]$expected.productVersion -or
        [string]$actual.hostProductVersion -cne [string]$expected.productVersion -or
        [string]$actual.clientFileVersion -cne [string]$expected.fileVersion -or
        [string]$actual.hostFileVersion -cne [string]$expected.fileVersion) {
        Throw-Code 's5_lifecycle_mixed_version_pair'
    }
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
    $probe = Join-Path $inputRoot 'probe\ScreenGuide.Stage5LifecycleProbe.exe'
    $probeOutput = Join-Path $runtimeRoot ($label + '.json')
    Invoke-Hidden $probe @('--database', $database, '--expected-schema', [string]$expectedSchema, '--output', $probeOutput) probe
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
    $resolvedPlan = [IO.Path]::GetFullPath($PlanPath)
    $resolvedEvidence = [IO.Path]::GetFullPath($EvidencePath)
    if (-not $resolvedPlan.StartsWith($inputRoot + '\', [StringComparison]::OrdinalIgnoreCase) -or
        -not $resolvedEvidence.StartsWith($evidenceRoot + '\', [StringComparison]::OrdinalIgnoreCase) -or
        (Has-ReparsePoint $resolvedPlan) -or (Has-ReparsePoint $resolvedEvidence)) {
        Throw-Code 's5_lifecycle_path_invalid'
    }
    $plan = Get-Content -Raw -LiteralPath $resolvedPlan | ConvertFrom-Json
    if ($plan.contractVersion -ne 1 -or [string]$plan.mode -cne 'native' -or
        $plan.budgets.installerExecutions -ne 5 -or
        $plan.budgets.installOrUpgradeExecutions -ne 3 -or
        $plan.budgets.uninstallExecutions -ne 2 -or
        $plan.budgets.retries -ne 0 -or $plan.budgets.resends -ne 0) {
        Throw-Code 's5_lifecycle_plan_invalid'
    }
    if (-not (Test-Path -LiteralPath $budgetScript -PathType Leaf) -or (Has-ReparsePoint $budgetScript)) {
        Throw-Code 's5_lifecycle_plan_invalid'
    }
    . $budgetScript
    try {
        $installerBudget = New-Stage5LifecycleExecutionBudget `
            -InstallerExecutions ([int]$plan.budgets.installerExecutions) `
            -InstallOrUpgradeExecutions ([int]$plan.budgets.installOrUpgradeExecutions) `
            -UninstallExecutions ([int]$plan.budgets.uninstallExecutions)
    }
    catch { Throw-Code $_.Exception.Message }
    [IO.Directory]::CreateDirectory($runtimeRoot) | Out-Null
    if ((Test-Path -LiteralPath $installRoot) -or (Test-Path -LiteralPath $existingUninstallKey) -or (Test-Path -LiteralPath $dataRoot)) {
        Throw-Code 's5_lifecycle_sandbox_not_clean'
    }
    $oldInstaller = Join-Path $inputRoot ([string]$plan.oldInstaller.fileName)
    $candidateInstaller = Join-Path $inputRoot ([string]$plan.candidateInstaller.fileName)
    $probe = Join-Path $inputRoot ([string]$plan.lifecycleProbe.fileName).Replace('/', '\')
    Assert-Hash $oldInstaller ([string]$plan.oldInstaller.sha256) 's5_lifecycle_old_installer_hash_mismatch'
    Assert-Hash $candidateInstaller ([string]$plan.candidateInstaller.sha256) 's5_lifecycle_candidate_installer_hash_mismatch'
    Assert-Hash $probe ([string]$plan.lifecycleProbe.sha256) 's5_lifecycle_probe_hash_mismatch'

    Install $oldInstaller 'install-v0.5.log'
    $oldPair = Get-PairIdentity
    Assert-Pair $oldPair $plan.oldIdentity
    Invoke-HostOnce ('Yuanshu.Stage5.Old.' + [Guid]::NewGuid().ToString('N'))
    $oldSchema = Invoke-Probe $databasePath 10 'schema-v10-before-upgrade'
    $canaryPath = Join-Path $dataRoot 'stage5-lifecycle-canary.txt'
    [IO.File]::WriteAllText($canaryPath, 'yuanshu-stage5-lifecycle-canary-v1', [Text.UTF8Encoding]::new($false))
    $canaryHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $canaryPath).Hash

    Install $candidateInstaller 'upgrade-v0.6.log'
    $newPair = Get-PairIdentity
    Assert-Pair $newPair $plan.candidateIdentity
    Invoke-HostOnce ('Yuanshu.Stage5.New.' + [Guid]::NewGuid().ToString('N'))
    $newSchema = Invoke-Probe $databasePath 11 'schema-v11-after-upgrade'
    $backup = Get-MatchingBackup (Split-Path -Parent $databasePath)
    $backupSchema = Invoke-Probe $backup 10 'schema-v10-backup'
    if ((Get-FileHash -Algorithm SHA256 -LiteralPath $canaryPath).Hash -cne $canaryHash) {
        Throw-Code 's5_lifecycle_canary_changed'
    }
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

    $preservedRoot = Join-Path $dataRoot 'stage5-preserved-v11'
    [IO.Directory]::CreateDirectory($preservedRoot) | Out-Null
    $preservedV11 = Join-Path $preservedRoot 'tasking.v11.preserved.db'
    Copy-Item -LiteralPath $databasePath -Destination $preservedV11
    $preservedBefore = (Get-FileHash -Algorithm SHA256 -LiteralPath $preservedV11).Hash
    Uninstall
    Copy-Item -LiteralPath $backup -Destination $databasePath -Force
    [void](Invoke-Probe $databasePath 10 'schema-v10-restored')
    Install $oldInstaller 'rollback-v0.5.log'
    $rollbackPair = Get-PairIdentity
    Assert-Pair $rollbackPair $plan.oldIdentity
    Invoke-HostOnce ('Yuanshu.Stage5.Rollback.' + [Guid]::NewGuid().ToString('N'))
    [void](Invoke-Probe $databasePath 10 'schema-v10-after-rollback')
    $preservedAfter = (Get-FileHash -Algorithm SHA256 -LiteralPath $preservedV11).Hash
    if ($preservedBefore -cne $preservedAfter -or (Get-FileHash -Algorithm SHA256 -LiteralPath $canaryPath).Hash -cne $canaryHash) {
        Throw-Code 's5_lifecycle_v11_hash_changed'
    }

    Uninstall
    $programRemoved = -not (Test-Path -LiteralPath $installRoot)
    $registrationRemoved = -not (Test-Path -LiteralPath $existingUninstallKey)
    $dataRetained = (Test-Path -LiteralPath $databasePath) -and (Test-Path -LiteralPath $preservedV11)

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
    $factsPath = Join-Path $runtimeRoot 'lifecycle-facts.json'
    [IO.File]::WriteAllText($factsPath, (($facts | ConvertTo-Json -Depth 30).Replace("`r`n", "`n") + "`n"), [Text.UTF8Encoding]::new($false))
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $contractScript -Mode Evidence -FactsPath $factsPath -ResultPath $resolvedEvidence
    exit $LASTEXITCODE
}
catch {
    Write-SafeFailure $errorCode
    exit 1
}
finally {
    $resolvedRuntime = [IO.Path]::GetFullPath($runtimeRoot)
    $tempPrefix = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
    if ($resolvedRuntime.StartsWith($tempPrefix, [StringComparison]::OrdinalIgnoreCase) -and
        [IO.Path]::GetFileName($resolvedRuntime).StartsWith('YuanshuStage5Lifecycle-', [StringComparison]::Ordinal)) {
        if (Test-Path -LiteralPath $resolvedRuntime) { Remove-Item -LiteralPath $resolvedRuntime -Recurse -Force }
    }
}
