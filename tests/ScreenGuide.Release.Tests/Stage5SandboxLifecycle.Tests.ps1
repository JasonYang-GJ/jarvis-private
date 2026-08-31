$ErrorActionPreference = 'Stop'

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$contractScript = Join-Path $repoRoot 'scripts\Test-Stage5SandboxLifecycleContract.ps1'
$hostScript = Join-Path $repoRoot 'scripts\New-Stage5SandboxLifecycle.ps1'
$bootstrapScript = Join-Path $repoRoot 'scripts\Invoke-Stage5SandboxLifecycle.ps1'
$budgetScript = Join-Path $repoRoot 'scripts\Stage5LifecycleExecutionBudget.ps1'
$testRoot = Join-Path ([IO.Path]::GetTempPath()) ('YuanshuStage5Lifecycle-Tests-' + [Guid]::NewGuid().ToString('N'))

function Assert-True([bool]$condition, [string]$message) {
    if (-not $condition) { throw $message }
}

function Assert-Equal($expected, $actual, [string]$message) {
    if ($expected -ne $actual) { throw "$message Expected=[$expected] Actual=[$actual]" }
}

function Write-Json([string]$path, $value) {
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($path)) | Out-Null
    [IO.File]::WriteAllText(
        $path,
        (($value | ConvertTo-Json -Depth 30).Replace("`r`n", "`n") + "`n"),
        [Text.UTF8Encoding]::new($false))
}

function Copy-Json($value) {
    return (($value | ConvertTo-Json -Depth 30) | ConvertFrom-Json)
}

function Invoke-Contract([string]$mode, $facts, [string]$caseName) {
    $caseRoot = Join-Path $testRoot $caseName
    $factsPath = Join-Path $caseRoot 'facts.json'
    $resultPath = Join-Path $caseRoot 'result.json'
    $wsbPath = Join-Path $caseRoot 'lifecycle.wsb'
    $planPath = Join-Path $caseRoot 'plan.json'
    Write-Json $factsPath $facts
    $output = @(& powershell.exe -NoProfile -ExecutionPolicy Bypass -File $contractScript `
        -Mode $mode -FactsPath $factsPath -ResultPath $resultPath `
        -WsbOutputPath $wsbPath -PlanOutputPath $planPath 2>&1)
    return [pscustomobject]@{
        ExitCode = $LASTEXITCODE
        Result = if (Test-Path -LiteralPath $resultPath) { Get-Content -Raw -LiteralPath $resultPath | ConvertFrom-Json } else { $null }
        WsbPath = $wsbPath
        PlanPath = $planPath
        Output = $output -join "`n"
    }
}

function Assert-Failure($run, [string]$code) {
    Assert-True ($run.ExitCode -ne 0) "Failure $code must return nonzero."
    Assert-Equal 'BLOCKED' $run.Result.status "Failure $code must fail closed."
    Assert-Equal $code $run.Result.errorCode "Failure $code must be stable."
    Assert-True (-not (Test-Path -LiteralPath $run.WsbPath)) "Failure $code must not emit a .wsb."
}

try {
    [IO.Directory]::CreateDirectory($testRoot) | Out-Null
    $inputRoot = Join-Path $testRoot 'OwnedInput'
    $evidenceRoot = Join-Path $testRoot 'OwnedEvidence'
    $cleanupRoot = Join-Path $testRoot 'OwnedRuntime'
    foreach ($path in @($inputRoot, $evidenceRoot, $cleanupRoot)) { [IO.Directory]::CreateDirectory($path) | Out-Null }
    $oldInstaller = Join-Path $inputRoot 'old-installer.fake'
    $newInstaller = Join-Path $inputRoot 'new-installer.fake'
    $probe = Join-Path $inputRoot 'probe.fake'
    [IO.File]::WriteAllText($oldInstaller, 'fake old installer', [Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText($newInstaller, 'fake new installer', [Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText($probe, 'fake lifecycle probe', [Text.UTF8Encoding]::new($false))

    $preflight = [ordered]@{
        contractVersion = 1
        expectedSourceSha = '919fef805010395c272f72033c7e653f9b26e768'
        actualSourceSha = '919fef805010395c272f72033c7e653f9b26e768'
        sourceClean = $true
        sandboxAvailable = $true
        existingHostInstall = $false
        oldInstaller = [ordered]@{ fileName = 'old-installer.fake'; expectedSha256 = (Get-FileHash $oldInstaller -Algorithm SHA256).Hash; actualSha256 = (Get-FileHash $oldInstaller -Algorithm SHA256).Hash }
        candidateInstaller = [ordered]@{ fileName = 'new-installer.fake'; expectedSha256 = (Get-FileHash $newInstaller -Algorithm SHA256).Hash; actualSha256 = (Get-FileHash $newInstaller -Algorithm SHA256).Hash }
        lifecycleProbe = [ordered]@{
            fileName = 'probe/lifecycle-probe-bundle.json'
            expectedSha256 = (Get-FileHash $probe -Algorithm SHA256).Hash
            actualSha256 = (Get-FileHash $probe -Algorithm SHA256).Hash
            fileCount = 7
            entryPoint = 'probe/ScreenGuide.Stage5LifecycleProbe.exe'
        }
        mappings = [ordered]@{
            inputHostPath = $inputRoot
            evidenceHostPath = $evidenceRoot
            inputReadOnly = $true
            evidenceReadOnly = $false
            repoMapped = $false
        }
        sandboxPolicy = [ordered]@{ networking = 'Disable'; clipboard = 'Disable'; audioInput = 'Disable'; videoInput = 'Disable'; printer = 'Disable' }
        cleanup = [ordered]@{ ownedRoot = $cleanupRoot; ownershipConfirmed = $true }
        oldIdentity = [ordered]@{ productVersion = '0.5.0+d553e7e9d606037df87d98e99250de5498f5934a'; fileVersion = '0.5.0.0'; schemaVersion = 10 }
        candidateIdentity = [ordered]@{ productVersion = '0.6.0+919fef805010395c272f72033c7e653f9b26e768'; fileVersion = '0.6.0.0'; schemaVersion = 11 }
    }

    $positive = Invoke-Contract Preflight $preflight 'preflight-pass'
    Assert-Equal 0 $positive.ExitCode 'Valid preflight must pass.'
    Assert-Equal 'PASS' $positive.Result.status 'Valid preflight result must be PASS.'
    Assert-True (Test-Path -LiteralPath $positive.WsbPath -PathType Leaf) 'Valid preflight must emit a .wsb.'
    Assert-True (Test-Path -LiteralPath $positive.PlanPath -PathType Leaf) 'Valid preflight must emit a lifecycle plan.'
    $plan = Get-Content -Raw -LiteralPath $positive.PlanPath | ConvertFrom-Json
    Assert-Equal 5 $plan.budgets.installerExecutions 'Lifecycle plan must declare all five installer/uninstaller process starts.'
    Assert-Equal 3 $plan.budgets.installOrUpgradeExecutions 'Lifecycle plan must declare three install/upgrade starts.'
    Assert-Equal 2 $plan.budgets.uninstallExecutions 'Lifecycle plan must declare two uninstall starts.'
    Assert-Equal 7 $plan.lifecycleProbe.fileCount 'Lifecycle plan must bind the full probe bundle file count.'
    $wsb = Get-Content -Raw -LiteralPath $positive.WsbPath
    foreach ($element in @('Networking', 'ClipboardRedirection', 'AudioInput', 'VideoInput', 'PrinterRedirection')) {
        Assert-True ($wsb.Contains("<$element>Disable</$element>")) "$element must be disabled."
    }
    Assert-True ($wsb.Contains('<ReadOnly>true</ReadOnly>')) 'Input mapping must be read-only.'
    Assert-True ($wsb.Contains('<ReadOnly>false</ReadOnly>')) 'Evidence mapping must be the only writable mapping.'
    Assert-True (-not $wsb.Contains($repoRoot)) 'The repository must never be mapped into Sandbox.'

    $mutations = @(
        @('s5_lifecycle_source_sha_mismatch', { param($x) $x.actualSourceSha = '0' * 40 }),
        @('s5_lifecycle_source_dirty', { param($x) $x.sourceClean = $false }),
        @('s5_lifecycle_old_installer_hash_mismatch', { param($x) $x.oldInstaller.actualSha256 = '0' * 64 }),
        @('s5_lifecycle_candidate_installer_hash_mismatch', { param($x) $x.candidateInstaller.actualSha256 = '0' * 64 }),
        @('s5_lifecycle_probe_hash_mismatch', { param($x) $x.lifecycleProbe.actualSha256 = '0' * 64 }),
        @('s5_lifecycle_sandbox_unavailable', { param($x) $x.sandboxAvailable = $false }),
        @('s5_lifecycle_existing_host_install', { param($x) $x.existingHostInstall = $true }),
        @('s5_lifecycle_network_policy_invalid', { param($x) $x.sandboxPolicy.networking = 'Enable' }),
        @('s5_lifecycle_input_mapping_writable', { param($x) $x.mappings.inputReadOnly = $false }),
        @('s5_lifecycle_evidence_mapping_invalid', { param($x) $x.mappings.evidenceReadOnly = $true }),
        @('s5_lifecycle_cleanup_root_invalid', { param($x) $x.cleanup.ownershipConfirmed = $false })
    )
    $case = 0
    foreach ($mutation in $mutations) {
        $copy = Copy-Json $preflight
        & $mutation[1] $copy
        Assert-Failure (Invoke-Contract Preflight $copy ('preflight-fail-' + (++$case))) $mutation[0]
    }

    $target = Join-Path $testRoot 'reparse-target'
    [IO.Directory]::CreateDirectory($target) | Out-Null
    $alias = Join-Path $testRoot 'reparse-input'
    New-Item -ItemType Junction -Path $alias -Target $target | Out-Null
    $reparse = Copy-Json $preflight
    $reparse.mappings.inputHostPath = $alias
    Assert-Failure (Invoke-Contract Preflight $reparse 'preflight-reparse') 's5_lifecycle_reparse_point'

    $evidence = [ordered]@{
        contractVersion = 1
        coordinatorInstance = 'sandbox-lifecycle-v1'
        sourceSha = '919fef805010395c272f72033c7e653f9b26e768'
        phases = [ordered]@{
            cleanState = $true
            oldInstall = $true
            oldSchema = 10
            canaryCreated = $true
            upgradeInstall = $true
            newSchema = 11
            matchingBackupCount = 1
            matchingBackupSchema = 10
            canaryRetainedAfterUpgrade = $true
            noticeLayoutVerified = $true
            missingBackupRefused = $true
            v11HashBeforeMissingBackup = 'A' * 64
            v11HashAfterMissingBackup = 'A' * 64
            matchingBackupRestored = $true
            rollbackSchema = 10
            canaryRetainedAfterRollback = $true
            preservedV11HashBeforeRollback = 'B' * 64
            preservedV11HashAfterRollback = 'B' * 64
            uninstallProgramRemoved = $true
            uninstallRegistrationRemoved = $true
            dataRetained = $true
        }
        oldPair = [ordered]@{ clientProductVersion = '0.5.0+d553e7e9d606037df87d98e99250de5498f5934a'; hostProductVersion = '0.5.0+d553e7e9d606037df87d98e99250de5498f5934a'; fileVersion = '0.5.0.0' }
        newPair = [ordered]@{ clientProductVersion = '0.6.0+919fef805010395c272f72033c7e653f9b26e768'; hostProductVersion = '0.6.0+919fef805010395c272f72033c7e653f9b26e768'; fileVersion = '0.6.0.0' }
        terminal = [ordered]@{ status = 'PASS'; finalized = $true }
        counters = [ordered]@{ networkRequests = 0; providerRequests = 0; credentialReads = 0; retries = 0; resends = 0 }
        execution = [ordered]@{
            installerExecutions = 5
            installOrUpgradeExecutions = 3
            uninstallExecutions = 2
            plannedInstallerExecutions = 5
            plannedInstallOrUpgradeExecutions = 3
            plannedUninstallExecutions = 2
            installerExitCodeCount = 5
        }
    }
    $evidencePass = Invoke-Contract Evidence $evidence 'evidence-pass'
    Assert-Equal 0 $evidencePass.ExitCode 'Valid lifecycle evidence must pass.'
    Assert-Equal 'PASS' $evidencePass.Result.status 'Lifecycle evidence must report PASS.'
    Assert-Equal 0 $evidencePass.Result.networkRequests 'Sanitized evidence must retain hard-zero counters.'
    Assert-True (-not ($evidencePass.Result | ConvertTo-Json -Depth 20).Contains($testRoot)) 'Sanitized evidence must not contain host paths.'

    $evidenceMutations = @(
        @('s5_lifecycle_backup_missing', { param($x) $x.phases.matchingBackupCount = 0 }),
        @('s5_lifecycle_backup_mismatch', { param($x) $x.phases.matchingBackupSchema = 9 }),
        @('s5_lifecycle_mixed_version_pair', { param($x) $x.newPair.hostProductVersion = '0.5.0+wrong' }),
        @('s5_lifecycle_notice_missing', { param($x) $x.phases.noticeLayoutVerified = $false }),
        @('s5_lifecycle_v11_hash_changed', { param($x) $x.phases.v11HashAfterMissingBackup = 'C' * 64 }),
        @('s5_lifecycle_cleanup_failed', { param($x) $x.phases.uninstallProgramRemoved = $false }),
        @('s5_lifecycle_installer_execution_count_mismatch', { param($x) $x.execution.installerExecutions = 4; $x.execution.installOrUpgradeExecutions = 2 }),
        @('s5_lifecycle_installer_execution_budget_exceeded', { param($x) $x.execution.installerExecutions = 6; $x.execution.installOrUpgradeExecutions = 4; $x.execution.installerExitCodeCount = 6 }),
        @('s5_lifecycle_installer_execution_count_mismatch', { param($x) $x.execution.installOrUpgradeExecutions = $null }),
        @('s5_lifecycle_installer_execution_count_mismatch', { param($x) $x.execution.plannedInstallerExecutions = 4 })
    )
    $case = 0
    foreach ($mutation in $evidenceMutations) {
        $copy = Copy-Json $evidence
        & $mutation[1] $copy
        Assert-Failure (Invoke-Contract Evidence $copy ('evidence-fail-' + (++$case))) $mutation[0]
    }

    Assert-True (Test-Path -LiteralPath $budgetScript -PathType Leaf) 'Shared installer execution budget guard must exist.'
    . $budgetScript
    $budget = New-Stage5LifecycleExecutionBudget -InstallerExecutions 5 -InstallOrUpgradeExecutions 3 -UninstallExecutions 2
    1..3 | ForEach-Object { Enter-Stage5LifecycleInstallerExecution -Budget $budget -Kind InstallOrUpgrade }
    1..2 | ForEach-Object { Enter-Stage5LifecycleInstallerExecution -Budget $budget -Kind Uninstall }
    $sixthAttemptCode = $null
    try { Enter-Stage5LifecycleInstallerExecution -Budget $budget -Kind InstallOrUpgrade }
    catch { $sixthAttemptCode = $_.Exception.Message }
    Assert-Equal 's5_lifecycle_installer_execution_budget_exceeded' $sixthAttemptCode 'Sixth installer process attempt must fail before start.'
    $budgetSnapshot = Get-Stage5LifecycleExecutionBudgetSnapshot -Budget $budget
    Assert-Equal 5 $budgetSnapshot.installerExecutions 'Rejected sixth attempt must not increment total.'
    Assert-Equal 3 $budgetSnapshot.installOrUpgradeExecutions 'Rejected sixth attempt must not increment install/upgrade count.'
    Assert-Equal 2 $budgetSnapshot.uninstallExecutions 'Rejected sixth attempt must not increment uninstall count.'

    foreach ($script in @($hostScript, $bootstrapScript)) {
        $text = Get-Content -Raw -LiteralPath $script
        Assert-True (-not $text.Contains('Start-Process WindowsSandbox')) 'Developer harness must not auto-launch Windows Sandbox.'
        Assert-True (-not $text.Contains('http://') -and -not $text.Contains('https://')) 'Lifecycle scripts must not contain network endpoints.'
    }
    $hostText = Get-Content -Raw -LiteralPath $hostScript
    Assert-True ($hostText.Contains('Get-WindowsOptionalFeature')) 'Host preflight must verify Windows Sandbox availability.'
    Assert-True ($hostText.Contains('existingUninstallKey')) 'Host preflight must reject an existing same-AppId installation.'
    Assert-True ($hostText.Contains("'Stage5LifecycleExecutionBudget.ps1'")) 'Host preflight must place the shared budget guard in the read-only input mapping.'
    Assert-True ($hostText.Contains("'Test-Stage5LifecycleProbeBundle.ps1'")) 'Host preflight must validate the complete probe bundle before and after copy.'
    $bootstrapText = Get-Content -Raw -LiteralPath $bootstrapScript
    Assert-True ($bootstrapText.Contains('tasking.pre-v11-from-v10-*.backup.db')) 'Bootstrap must require the matching migration backup.'
    Assert-True ($bootstrapText.Contains('THIRD-PARTY-NOTICES.txt')) 'Bootstrap must verify NOTICE layout.'
    Assert-True ($bootstrapText.Contains('Enter-Stage5LifecycleInstallerExecution -Budget $installerBudget -Kind $installerKind')) 'Installer process entry must invoke the shared budget guard before Start-Process.'
    Assert-True ($bootstrapText.Contains('installer InstallOrUpgrade')) 'Every install/upgrade wrapper must identify its budget kind.'
    Assert-True ($bootstrapText.Contains('installer Uninstall')) 'Every uninstall wrapper must identify its budget kind.'
    Assert-True ($bootstrapText.Contains('probe-bundle-validation.json')) 'Sandbox bootstrap must validate the complete probe bundle before execution.'
    Assert-Equal 1 ([regex]::Matches($bootstrapText, 'Start-Process').Count) 'Bootstrap must retain one counted process-start seam.'

    Write-Host 'Stage5 Sandbox lifecycle contract: 1 preflight pass + 12 preflight failures + 1 evidence pass + 10 evidence failures + sixth-attempt budget guard + static safety checks passed'
}
finally {
    $resolved = [IO.Path]::GetFullPath($testRoot)
    $prefix = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
    if ($resolved.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase) -and
        [IO.Path]::GetFileName($resolved).StartsWith('YuanshuStage5Lifecycle-Tests-', [StringComparison]::Ordinal)) {
        if (Test-Path -LiteralPath $resolved) { Remove-Item -LiteralPath $resolved -Recurse -Force }
    }
}
