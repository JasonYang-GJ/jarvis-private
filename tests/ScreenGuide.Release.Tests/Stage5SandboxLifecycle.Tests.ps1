$ErrorActionPreference = 'Stop'

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$contractScript = Join-Path $repoRoot 'scripts\Test-Stage5SandboxLifecycleContract.ps1'
$hostScript = Join-Path $repoRoot 'scripts\New-Stage5SandboxLifecycle.ps1'
$bootstrapScript = Join-Path $repoRoot 'scripts\Invoke-Stage5SandboxLifecycle.ps1'
$budgetScript = Join-Path $repoRoot 'scripts\Stage5LifecycleExecutionBudget.ps1'
$diagnosticsScript = Join-Path $repoRoot 'scripts\Stage5LifecycleFailureDiagnostics.ps1'
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
    Assert-True (Test-Path -LiteralPath $diagnosticsScript -PathType Leaf) 'Shared lifecycle failure diagnostics must exist.'
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
        protectedHostInstall = [ordered]@{
            present = $false
            productRootPresent = $false
            uninstallRegistrationPresent = $false
        }
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
    Assert-Equal 2 ([regex]::Matches($wsb, '<MappedFolder>').Count) 'Sandbox must contain only the owned input and evidence mappings.'
    foreach ($forbiddenHostState in @('Programs\YuanshuDesktop', 'ScreenGuideTeacher', '{E2B9C242-2965-48BC-B2C6-CF83A2B11953}_is1')) {
        Assert-True (-not $wsb.Contains($forbiddenHostState)) 'Host product, data, and uninstall state must never be mapped into Sandbox.'
    }

    $protectedHostFacts = Copy-Json $preflight
    $protectedHostFacts.protectedHostInstall.present = $true
    $protectedHostFacts.protectedHostInstall.productRootPresent = $true
    $protectedHostFacts.protectedHostInstall.uninstallRegistrationPresent = $true
    $protectedHostFacts | Add-Member -NotePropertyName existingHostInstall -NotePropertyValue $true
    $protectedHost = Invoke-Contract Preflight $protectedHostFacts 'preflight-protected-host-install'
    Assert-Equal 0 $protectedHost.ExitCode 'An existing protected Host installation must not block isolated Sandbox preflight.'
    Assert-Equal 'PASS' $protectedHost.Result.status 'Protected Host installation must remain informational.'
    Assert-True (-not [bool]$positive.Result.details.protectedHostInstall.present) 'Absent Host installation must be recorded as protected informational state.'
    Assert-Equal 0 $positive.Result.details.hostInstallerExecutions 'Host preflight must execute zero installers.'
    Assert-True ([bool]$protectedHost.Result.details.protectedHostInstall.present) 'Protected Host installation presence must be recorded without paths or contents.'
    Assert-Equal 0 $protectedHost.Result.details.hostInstallerExecutions 'Protected Host preflight must execute zero installers.'
    $protectedHostWsb = Get-Content -Raw -LiteralPath $protectedHost.WsbPath
    $protectedHostPlan = Get-Content -Raw -LiteralPath $protectedHost.PlanPath
    Assert-True (-not $protectedHostPlan.Contains('protectedHostInstall')) 'Protected Host state must not enter the Sandbox lifecycle plan.'
    Assert-Equal 2 ([regex]::Matches($protectedHostWsb, '<MappedFolder>').Count) 'Protected Host preflight must retain exactly two owned mappings.'
    foreach ($forbiddenHostState in @('Programs\YuanshuDesktop', 'ScreenGuideTeacher', '{E2B9C242-2965-48BC-B2C6-CF83A2B11953}_is1')) {
        Assert-True (-not $protectedHostWsb.Contains($forbiddenHostState)) 'Protected Host state must not enter the generated Sandbox configuration.'
    }

    $mutations = @(
        @('s5_lifecycle_source_sha_mismatch', { param($x) $x.actualSourceSha = '0' * 40 }),
        @('s5_lifecycle_source_dirty', { param($x) $x.sourceClean = $false }),
        @('s5_lifecycle_old_installer_hash_mismatch', { param($x) $x.oldInstaller.actualSha256 = '0' * 64 }),
        @('s5_lifecycle_candidate_installer_hash_mismatch', { param($x) $x.candidateInstaller.actualSha256 = '0' * 64 }),
        @('s5_lifecycle_probe_hash_mismatch', { param($x) $x.lifecycleProbe.actualSha256 = '0' * 64 }),
        @('s5_lifecycle_sandbox_unavailable', { param($x) $x.sandboxAvailable = $false }),
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

    . $diagnosticsScript
    $emptyPresence = [ordered]@{
        installRootPresent = $false
        clientPresent = $false
        hostPresent = $false
        uninstallRegistrationPresent = $false
    }

    $identityRoot = Join-Path $testRoot 'identity-fixture'
    [IO.Directory]::CreateDirectory($identityRoot) | Out-Null
    [IO.File]::WriteAllText((Join-Path $identityRoot 'ScreenGuide.DesktopClient.exe'), 'fake-client')
    [IO.File]::WriteAllText((Join-Path $identityRoot 'ScreenGuide.DesktopHost.exe'), 'fake-host')
    $expectedPair = [ordered]@{
        productVersion = '0.5.0+d553e7e9d606037df87d98e99250de5498f5934a'
        fileVersion = '0.5.0.0'
    }
    $validVersionReader = {
        param([string]$path)
        return [pscustomobject]@{
            ProductVersion = '0.5.0+d553e7e9d606037df87d98e99250de5498f5934a'
            FileVersion = '0.5.0.0'
        }
    }
    $identityBudget = New-Stage5LifecycleExecutionBudget -InstallerExecutions 5 -InstallOrUpgradeExecutions 3 -UninstallExecutions 2
    $identityState = New-Stage5LifecycleDiagnosticState
    Set-Stage5LifecyclePhase $identityState 'old-pair-validation'
    $validPair = Get-Stage5LifecyclePairIdentity -State $identityState -InstallRoot $identityRoot -VersionReader $validVersionReader
    Assert-Stage5LifecyclePairIdentity -Actual $validPair -Expected $expectedPair
    Assert-Equal $expectedPair.productVersion $validPair.clientProductVersion 'Expected Client ProductVersion must remain exact.'
    Assert-Equal $expectedPair.productVersion $validPair.hostProductVersion 'Expected Host ProductVersion must remain exact.'
    Assert-Equal $expectedPair.fileVersion $validPair.clientFileVersion 'Expected Client FileVersion must remain exact.'
    Assert-Equal $expectedPair.fileVersion $validPair.hostFileVersion 'Expected Host FileVersion must remain exact.'
    $identityBudgetAfterRead = Get-Stage5LifecycleExecutionBudgetSnapshot $identityBudget
    Assert-Equal 0 $identityBudgetAfterRead.installerExecutions 'Pair identity inspection must not consume installer execution budget.'

    $missingProductState = New-Stage5LifecycleDiagnosticState
    Set-Stage5LifecyclePhase $missingProductState 'old-pair-validation'
    $missingProductCode = $null
    try {
        [void](Get-Stage5LifecyclePairIdentity -State $missingProductState -InstallRoot $identityRoot -VersionReader {
            param([string]$path)
            return [pscustomobject]@{ ProductVersion = $null; FileVersion = '0.5.0.0' }
        })
    }
    catch { $missingProductCode = $_.Exception.Message }
    Assert-Equal 's5_lifecycle_pair_product_version_missing' $missingProductCode 'Missing ProductVersion must fail closed without a raw null exception.'

    $missingFileState = New-Stage5LifecycleDiagnosticState
    Set-Stage5LifecyclePhase $missingFileState 'old-pair-validation'
    $missingFileCode = $null
    try {
        [void](Get-Stage5LifecyclePairIdentity -State $missingFileState -InstallRoot $identityRoot -VersionReader {
            param([string]$path)
            return [pscustomobject]@{ ProductVersion = '0.5.0+d553e7e9d606037df87d98e99250de5498f5934a'; FileVersion = $null }
        })
    }
    catch { $missingFileCode = $_.Exception.Message }
    Assert-Equal 's5_lifecycle_pair_file_version_missing' $missingFileCode 'Missing FileVersion must fail closed without a raw null exception.'

    $invalidIdentityState = New-Stage5LifecycleDiagnosticState
    Set-Stage5LifecyclePhase $invalidIdentityState 'old-pair-validation'
    $invalidIdentityCode = $null
    try {
        [void](Get-Stage5LifecyclePairIdentity -State $invalidIdentityState -InstallRoot $identityRoot -VersionReader {
            param([string]$path)
            return [pscustomobject]@{ ProductVersion = 'RAW_INVALID_VERSION_SENTINEL'; FileVersion = '0.5.0.0' }
        })
    }
    catch { $invalidIdentityCode = $_.Exception.Message }
    Assert-Equal 's5_lifecycle_pair_identity_invalid' $invalidIdentityCode 'Invalid version text must fail closed without entering evidence.'

    $mismatchState = New-Stage5LifecycleDiagnosticState
    Set-Stage5LifecyclePhase $mismatchState 'old-pair-validation'
    $mismatchPair = Get-Stage5LifecyclePairIdentity -State $mismatchState -InstallRoot $identityRoot -VersionReader {
        param([string]$path)
        $product = if ([IO.Path]::GetFileName($path) -ceq 'ScreenGuide.DesktopHost.exe') {
            '0.5.0+aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa'
        } else {
            '0.5.0+d553e7e9d606037df87d98e99250de5498f5934a'
        }
        return [pscustomobject]@{ ProductVersion = $product; FileVersion = '0.5.0.0' }
    }
    $mismatchCode = $null
    try { Assert-Stage5LifecyclePairIdentity -Actual $mismatchPair -Expected $expectedPair }
    catch { $mismatchCode = $_.Exception.Message }
    Assert-Equal 's5_lifecycle_mixed_version_pair' $mismatchCode 'Strict pair mismatch must retain the existing stable error.'

    $readerFailureState = New-Stage5LifecycleDiagnosticState
    Set-Stage5LifecyclePhase $readerFailureState 'old-pair-validation'
    $readerFailureCode = $null
    try {
        [void](Get-Stage5LifecyclePairIdentity -State $readerFailureState -InstallRoot $identityRoot -VersionReader {
            param([string]$path)
            throw 'RAW_VERSION_READER_SENTINEL'
        })
    }
    catch { $readerFailureCode = $_.Exception.Message }
    Assert-Equal 's5_lifecycle_pair_metadata_unreadable' $readerFailureCode 'Version reader exception must map to a stable safe failure.'

    $pairDiagnosticPaths = [Collections.Generic.List[string]]::new()
    foreach ($pairFailure in @(
        @($missingProductState, $missingProductCode),
        @($missingFileState, $missingFileCode),
        @($invalidIdentityState, $invalidIdentityCode),
        @($mismatchState, $mismatchCode),
        @($readerFailureState, $readerFailureCode))) {
        $pairDiagnosticPath = Join-Path $testRoot ('pair-' + [Guid]::NewGuid().ToString('N') + '.json')
        [void](Write-Stage5LifecycleFailureEvidence -EvidencePath $pairDiagnosticPath -ErrorCode ([string]$pairFailure[1]) `
            -State $pairFailure[0] -InstallerExecutionBudget (Get-Stage5LifecycleExecutionBudgetSnapshot $identityBudget) -Presence $emptyPresence)
        $pairDiagnosticPaths.Add($pairDiagnosticPath)
    }
    $pairDiagnosticText = ($pairDiagnosticPaths | ForEach-Object { Get-Content -Raw -LiteralPath $_ }) -join ''
    foreach ($sensitive in @($identityRoot, 'RAW_INVALID_VERSION_SENTINEL', 'RAW_VERSION_READER_SENTINEL')) {
        Assert-True (-not $pairDiagnosticText.Contains($sensitive)) 'Pair evidence must not contain paths, invalid metadata, or raw exceptions.'
    }
    $missingProductEvidence = Get-Content -Raw -LiteralPath $pairDiagnosticPaths[0] | ConvertFrom-Json
    Assert-Equal $false $missingProductEvidence.pairIdentities[0].client.productVersionPresent 'Missing ProductVersion must be represented only as a safe boolean.'
    Assert-Equal '0.5.0.0' $missingProductEvidence.pairIdentities[0].client.fileVersion 'A valid safe FileVersion may remain in failure evidence.'
    $mismatchEvidence = Get-Content -Raw -LiteralPath $pairDiagnosticPaths[3] | ConvertFrom-Json
    Assert-Equal $expectedPair.productVersion $mismatchEvidence.pairIdentities[0].client.productVersion 'Failure evidence may retain only a strictly valid Client ProductVersion.'
    Assert-Equal '0.5.0+aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa' $mismatchEvidence.pairIdentities[0].host.productVersion 'Failure evidence may retain only a strictly valid Host ProductVersion.'
    Assert-Equal $expectedPair.fileVersion $mismatchEvidence.pairIdentities[0].client.fileVersion 'Failure evidence may retain only a strictly valid Client FileVersion.'
    Assert-Equal $expectedPair.fileVersion $mismatchEvidence.pairIdentities[0].host.fileVersion 'Failure evidence may retain only a strictly valid Host FileVersion.'

    $launchState = New-Stage5LifecycleDiagnosticState
    Set-Stage5LifecyclePhase $launchState 'old-install'
    $launchBudget = New-Stage5LifecycleExecutionBudget -InstallerExecutions 5 -InstallOrUpgradeExecutions 3 -UninstallExecutions 2
    $launchCode = $null
    try {
        [void](Invoke-Stage5LifecycleObservedProcess -State $launchState -Budget $launchBudget `
            -FilePath 'C:\fixture\installer.exe' -Arguments @('/fixture', 'SUPER_SECRET_ARGUMENT') `
            -Kind installer -InstallerKind InstallOrUpgrade -StartProcessCommand { throw 'RAW_EXCEPTION_SENTINEL' })
    }
    catch { $launchCode = $_.Exception.Message }
    Assert-Equal 's5_lifecycle_installer_launch_failed' $launchCode 'Start-Process exception must map to a stable launch failure.'
    $launchEvidencePath = Join-Path $testRoot 'diagnostics-launch-failure.json'
    [void](Write-Stage5LifecycleFailureEvidence -EvidencePath $launchEvidencePath -ErrorCode $launchCode `
        -State $launchState -InstallerExecutionBudget (Get-Stage5LifecycleExecutionBudgetSnapshot $launchBudget) -Presence $emptyPresence)
    $launchEvidence = Get-Content -Raw -LiteralPath $launchEvidencePath | ConvertFrom-Json
    Assert-Equal 1 $launchEvidence.installerExecutionBudget.installerExecutions 'Launch failure must preserve the pre-start installer attempt.'
    Assert-Equal 'old-install' $launchEvidence.processes[0].phase 'Launch failure process evidence must retain the stable phase.'
    Assert-Equal 'installer' $launchEvidence.processes[0].processKind 'Launch failure process evidence must retain the safe process kind.'
    Assert-Equal 'InstallOrUpgrade' $launchEvidence.processes[0].installerKind 'Launch failure process evidence must retain the installer kind.'
    Assert-True (-not [bool]$launchEvidence.processes[0].processStarted -and -not [bool]$launchEvidence.processes[0].processExited) 'Launch failure must not claim process start or exit.'
    Assert-True ($null -eq $launchEvidence.processes[0].exitCode) 'Launch failure must not invent an exit code.'

    $nonzeroState = New-Stage5LifecycleDiagnosticState
    Set-Stage5LifecyclePhase $nonzeroState 'old-install'
    $nonzeroBudget = New-Stage5LifecycleExecutionBudget -InstallerExecutions 5 -InstallOrUpgradeExecutions 3 -UninstallExecutions 2
    $nonzeroCode = $null
    try {
        [void](Invoke-Stage5LifecycleObservedProcess -State $nonzeroState -Budget $nonzeroBudget `
            -FilePath 'C:\fixture\installer.exe' -Arguments @('/fixture') -Kind installer -InstallerKind InstallOrUpgrade `
            -StartProcessCommand { return [pscustomobject]@{ ExitCode = 23 } })
    }
    catch { $nonzeroCode = $_.Exception.Message }
    Assert-Equal 's5_lifecycle_installer_failed' $nonzeroCode 'Nonzero installer exit must use a stable installer failure.'
    $nonzeroEvidencePath = Join-Path $testRoot 'diagnostics-nonzero.json'
    [void](Write-Stage5LifecycleFailureEvidence -EvidencePath $nonzeroEvidencePath -ErrorCode $nonzeroCode `
        -State $nonzeroState -InstallerExecutionBudget (Get-Stage5LifecycleExecutionBudgetSnapshot $nonzeroBudget) -Presence $emptyPresence)
    $nonzeroEvidence = Get-Content -Raw -LiteralPath $nonzeroEvidencePath | ConvertFrom-Json
    Assert-True ([bool]$nonzeroEvidence.processes[0].processStarted -and [bool]$nonzeroEvidence.processes[0].processExited) 'Nonzero exit must prove process start and exit.'
    Assert-Equal 23 $nonzeroEvidence.processes[0].exitCode 'Nonzero exit evidence must retain only the numeric exit code.'

    $pairState = New-Stage5LifecycleDiagnosticState
    Set-Stage5LifecyclePhase $pairState 'old-install'
    $pairBudget = New-Stage5LifecycleExecutionBudget -InstallerExecutions 5 -InstallOrUpgradeExecutions 3 -UninstallExecutions 2
    [void](Invoke-Stage5LifecycleObservedProcess -State $pairState -Budget $pairBudget `
        -FilePath 'C:\fixture\installer.exe' -Arguments @('/fixture') -Kind installer -InstallerKind InstallOrUpgrade `
        -StartProcessCommand { return [pscustomobject]@{ ExitCode = 0 } })
    Set-Stage5LifecyclePhase $pairState 'old-pair-validation'
    $pairCode = Get-Stage5LifecyclePhaseFailureCode $pairState.CurrentPhase
    Assert-Equal 's5_lifecycle_old_pair_validation_failed' $pairCode 'Unclassified post-install pair failure must retain a stable stage code.'
    $fixtureInstallRoot = Join-Path $testRoot 'fixture-installed-product'
    [IO.Directory]::CreateDirectory($fixtureInstallRoot) | Out-Null
    [IO.File]::WriteAllText((Join-Path $fixtureInstallRoot 'ScreenGuide.DesktopClient.exe'), 'fake-client')
    $pairPresence = Get-Stage5LifecycleSafePresence -InstallRoot $fixtureInstallRoot `
        -UninstallKey 'HKCU:\Software\YuanshuStage5LifecycleTests\Missing'
    $pairEvidencePath = Join-Path $testRoot 'diagnostics-pair-failure.json'
    [void](Write-Stage5LifecycleFailureEvidence -EvidencePath $pairEvidencePath -ErrorCode $pairCode `
        -State $pairState -InstallerExecutionBudget (Get-Stage5LifecycleExecutionBudgetSnapshot $pairBudget) -Presence $pairPresence)
    $pairEvidence = Get-Content -Raw -LiteralPath $pairEvidencePath | ConvertFrom-Json
    Assert-Equal 'old-pair-validation' $pairEvidence.phase 'Pair failure evidence must identify the stable phase.'
    Assert-True ([bool]$pairEvidence.presence.installRootPresent -and [bool]$pairEvidence.presence.clientPresent) 'Pair failure evidence must retain safe presence booleans.'
    Assert-True (-not [bool]$pairEvidence.presence.hostPresent -and -not [bool]$pairEvidence.presence.uninstallRegistrationPresent) 'Pair failure evidence must expose missing pair state without paths.'
    Assert-Equal 0 $pairEvidence.processes[0].exitCode 'Pair failure must preserve the successful installer exit before validation failed.'

    $diagnosticText = (Get-Content -Raw $launchEvidencePath) + (Get-Content -Raw $nonzeroEvidencePath) + (Get-Content -Raw $pairEvidencePath)
    foreach ($sensitive in @($testRoot, 'C:\fixture\installer.exe', 'SUPER_SECRET_ARGUMENT', 'RAW_EXCEPTION_SENTINEL')) {
        Assert-True (-not $diagnosticText.Contains($sensitive)) 'Failure evidence must not contain paths, arguments, secrets, or raw exceptions.'
    }
    $cleanupFixture = Join-Path ([IO.Path]::GetTempPath()) ('YuanshuStage5Lifecycle-' + [Guid]::NewGuid().ToString('N'))
    [IO.Directory]::CreateDirectory($cleanupFixture) | Out-Null
    [IO.File]::WriteAllText((Join-Path $cleanupFixture 'temporary.txt'), 'temporary')
    Remove-Stage5LifecycleOwnedRuntime $cleanupFixture
    Assert-True (-not (Test-Path -LiteralPath $cleanupFixture)) 'Owned runtime cleanup must remove only the owned runtime root.'
    Assert-True (Test-Path -LiteralPath $pairEvidencePath -PathType Leaf) 'Failure evidence must be finalized before owned runtime cleanup.'

    foreach ($script in @($hostScript, $bootstrapScript, $diagnosticsScript)) {
        $text = Get-Content -Raw -LiteralPath $script
        Assert-True (-not $text.Contains('Start-Process WindowsSandbox')) 'Developer harness must not auto-launch Windows Sandbox.'
        Assert-True (-not $text.Contains('http://') -and -not $text.Contains('https://')) 'Lifecycle scripts must not contain network endpoints.'
    }
    $hostText = Get-Content -Raw -LiteralPath $hostScript
    Assert-True ($hostText.Contains('Get-WindowsOptionalFeature')) 'Host preflight must verify Windows Sandbox availability.'
    Assert-True ($hostText.Contains('protectedHostInstall')) 'Host preflight must record an existing same-AppId installation as protected informational state.'
    Assert-Equal 0 ([regex]::Matches($hostText, 'Start-Process').Count) 'Host harness must execute zero installer or Sandbox processes.'
    Assert-True ($hostText.Contains("'Stage5LifecycleExecutionBudget.ps1'")) 'Host preflight must place the shared budget guard in the read-only input mapping.'
    Assert-True ($hostText.Contains("'Stage5LifecycleFailureDiagnostics.ps1'")) 'Host preflight must place shared failure diagnostics in the read-only input mapping.'
    Assert-True ($hostText.Contains("'Test-Stage5LifecycleProbeBundle.ps1'")) 'Host preflight must validate the complete probe bundle before and after copy.'
    $contractText = Get-Content -Raw -LiteralPath $contractScript
    Assert-True (-not $contractText.Contains('s5_lifecycle_existing_host_install')) 'Existing Host installation must not remain a preflight blocker.'
    $bootstrapText = Get-Content -Raw -LiteralPath $bootstrapScript
    Assert-True ($bootstrapText.Contains('tasking.pre-v11-from-v10-*.backup.db')) 'Bootstrap must require the matching migration backup.'
    Assert-True ($bootstrapText.Contains('THIRD-PARTY-NOTICES.txt')) 'Bootstrap must verify NOTICE layout.'
    Assert-True ($bootstrapText.Contains('Invoke-Stage5LifecycleObservedProcess')) 'Every lifecycle process must use the shared observed process seam.'
    Assert-True ($bootstrapText.Contains('Get-Stage5LifecyclePairIdentity')) 'Every lifecycle pair check must use the null-safe identity seam.'
    Assert-True ($bootstrapText.Contains('Assert-Stage5LifecyclePairIdentity')) 'Every lifecycle pair comparison must use the strict identity seam.'
    Assert-True (-not $bootstrapText.Contains('ProductVersion.Trim()') -and -not $bootstrapText.Contains('FileVersion.Trim()')) 'Bootstrap must not call Trim on nullable version metadata.'
    Assert-True ($bootstrapText.Contains('Get-Stage5LifecyclePhaseFailureCode')) 'Bootstrap must normalize unclassified failures through the stable phase code.'
    Assert-True ($bootstrapText.IndexOf('Write-SafeFailure $errorCode', [StringComparison]::Ordinal) -lt $bootstrapText.LastIndexOf('finally {', [StringComparison]::Ordinal)) 'Failure evidence must be finalized before cleanup begins.'
    Assert-True ($bootstrapText.Contains('installer InstallOrUpgrade')) 'Every install/upgrade wrapper must identify its budget kind.'
    Assert-True ($bootstrapText.Contains('installer Uninstall')) 'Every uninstall wrapper must identify its budget kind.'
    Assert-True ($bootstrapText.Contains('probe-bundle-validation.json')) 'Sandbox bootstrap must validate the complete probe bundle before execution.'
    Assert-Equal 0 ([regex]::Matches($bootstrapText, 'Start-Process').Count) 'Bootstrap must not bypass the shared observed process seam.'
    $diagnosticsText = Get-Content -Raw -LiteralPath $diagnosticsScript
    Assert-Equal 1 ([regex]::Matches($diagnosticsText, 'Start-Process').Count) 'Diagnostics must own the single process-start seam.'

    Write-Host 'Stage5 Sandbox lifecycle contract: 25 existing scenarios + 3 process diagnostics + 6 pair-identity cases + evidence-before-cleanup + static safety checks passed'
}
finally {
    $resolved = [IO.Path]::GetFullPath($testRoot)
    $prefix = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
    if ($resolved.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase) -and
        [IO.Path]::GetFileName($resolved).StartsWith('YuanshuStage5Lifecycle-Tests-', [StringComparison]::Ordinal)) {
        if (Test-Path -LiteralPath $resolved) { Remove-Item -LiteralPath $resolved -Recurse -Force }
    }
}
