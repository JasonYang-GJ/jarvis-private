param(
    [Parameter(Mandatory = $true)]
    [string]$RepositoryRoot,

    [Parameter(Mandatory = $true)]
    [ValidateSet('Identity', 'NormalMigration', 'FailureRollback', 'RejectV8')]
    [string]$Scenario
)

$ErrorActionPreference = 'Stop'
$expectedTagObject = '9fc790ade57fa2d3c18bc5ee84e8dc9e7018aa89'
$expectedSourceCommit = '0a8cd9e164c35b86f67ffd94b9e0f17c312a2576'
$resultPrefix = 'STAGE1_PRE_V8_RESULT '
$ownerToken = [Guid]::NewGuid().ToString('N')
$temporaryBase = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
$temporaryPrefix = $temporaryBase.TrimEnd(
    [IO.Path]::DirectorySeparatorChar,
    [IO.Path]::AltDirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
$ownedRoot = Join-Path $temporaryBase ("screen-guide-p0-05-{0}" -f [Guid]::NewGuid().ToString('N'))
$ownerMarker = Join-Path $ownedRoot '.screen-guide-p0-05-owner'
$failureCode = 'stage1_validation_failed'
$previousDataDirectory = [Environment]::GetEnvironmentVariable('SCREEN_GUIDE_DATA_DIRECTORY', 'Process')
$previousPipeName = [Environment]::GetEnvironmentVariable('SCREEN_GUIDE_PIPE_NAME', 'Process')

function Invoke-QuietChecked {
    param(
        [Parameter(Mandatory = $true)]
        [string]$FilePath,
        [Parameter(Mandatory = $true)]
        [string[]]$Arguments,
        [Parameter(Mandatory = $true)]
        [string]$Code
    )

    $output = & $FilePath @Arguments 2>&1
    if ($LASTEXITCODE -ne 0) {
        $script:failureCode = $Code
        throw [InvalidOperationException]::new($Code)
    }

    return @($output)
}

function Read-RunnerResult {
    param(
        [Parameter(Mandatory = $true)]
        [string[]]$Output
    )

    $lines = @($Output | Where-Object { $_ -is [string] -and $_.StartsWith('STAGE1_DB_COMPAT_RESULT ') })
    if ($lines.Count -ne 1) {
        $script:failureCode = 'runner_result_invalid'
        throw [InvalidOperationException]::new('runner_result_invalid')
    }

    return $lines[0].Substring('STAGE1_DB_COMPAT_RESULT '.Length) | ConvertFrom-Json
}

function Invoke-CompatibilityRunner {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Runner,
        [Parameter(Mandatory = $true)]
        [string[]]$RunnerArguments,
        [Parameter(Mandatory = $true)]
        [string]$Code
    )

    $processArguments = @($Runner) + $RunnerArguments
    return Read-RunnerResult @(Invoke-QuietChecked -FilePath 'dotnet' -Arguments $processArguments -Code $Code)
}

function Find-OfflinePackageCache {
    param(
        [Parameter(Mandatory = $true)]
        [string]$StartPath
    )

    $candidate = [IO.DirectoryInfo]::new([IO.Path]::GetFullPath($StartPath))
    while ($null -ne $candidate) {
        $packageCache = Join-Path $candidate.FullName '.nuget\packages'
        if (Test-Path -LiteralPath (Join-Path $packageCache 'microsoft.data.sqlite\10.0.11') -PathType Container) {
            return $packageCache
        }

        $candidate = $candidate.Parent
    }

    # A repository relocated to another drive no longer has the user's cache among its ancestors.
    # Read an existing cache only; this does not authorize an online restore or a different Stage1 source.
    $sharedCaches = @($env:NUGET_PACKAGES, (Join-Path $env:USERPROFILE '.nuget\packages'))
    foreach ($sharedCache in $sharedCaches) {
        if (-not [string]::IsNullOrWhiteSpace($sharedCache) -and
            (Test-Path -LiteralPath (Join-Path $sharedCache 'microsoft.data.sqlite\10.0.11') -PathType Container)) {
            return [IO.Path]::GetFullPath($sharedCache)
        }
    }
    $script:failureCode = 'offline_package_cache_missing'
    throw [InvalidOperationException]::new('offline_package_cache_missing')
}

function Remove-OwnedTemporaryRoot {
    $resolvedRoot = [IO.Path]::GetFullPath($ownedRoot)
    if (-not $resolvedRoot.StartsWith($temporaryPrefix, [StringComparison]::OrdinalIgnoreCase) -or
        -not ([IO.Path]::GetFileName($resolvedRoot)).StartsWith('screen-guide-p0-05-', [StringComparison]::Ordinal) -or
        -not (Test-Path -LiteralPath $ownerMarker -PathType Leaf) -or
        (Get-Content -Raw -LiteralPath $ownerMarker) -ne $ownerToken) {
        throw [InvalidOperationException]::new('owned_cleanup_validation_failed')
    }

    Remove-Item -LiteralPath $resolvedRoot -Recurse -Force
}

$result = $null
$exitCode = 1
try {
    $failureCode = 'repository_root_validation_failed'
    $resolvedRepository = [IO.Path]::GetFullPath($RepositoryRoot)
    if (-not (Test-Path -LiteralPath (Join-Path $resolvedRepository 'ScreenGuide.slnx') -PathType Leaf)) {
        $failureCode = 'repository_identity_invalid'
        throw [InvalidOperationException]::new($failureCode)
    }

    $failureCode = 'owned_root_creation_failed'
    New-Item -ItemType Directory -Path $ownedRoot | Out-Null
    Set-Content -LiteralPath $ownerMarker -Value $ownerToken -NoNewline
    [Environment]::SetEnvironmentVariable(
        'SCREEN_GUIDE_DATA_DIRECTORY',
        (Join-Path $ownedRoot 'isolated-data'),
        'Process')
    [Environment]::SetEnvironmentVariable(
        'SCREEN_GUIDE_PIPE_NAME',
        ("ScreenGuide.P0_05.{0}" -f [Guid]::NewGuid().ToString('N')),
        'Process')

    $failureCode = 'tag_lookup_failed'
    $tagOutput = @(Invoke-QuietChecked -FilePath 'git' -Arguments @(
        '-C', $resolvedRepository, 'rev-parse', 'refs/tags/v0.3.0-stage1') -Code 'tag_lookup_failed')
    $tagObject = ([string]$tagOutput[-1]).Trim()
    $sourceOutput = @(Invoke-QuietChecked -FilePath 'git' -Arguments @(
        '-C', $resolvedRepository, 'rev-parse', 'v0.3.0-stage1^{commit}') -Code 'tag_commit_lookup_failed')
    $sourceCommit = ([string]$sourceOutput[-1]).Trim()
    $tagObjectVerified = $tagObject -eq $expectedTagObject
    $sourceCommitVerified = $sourceCommit -eq $expectedSourceCommit
    if (-not $tagObjectVerified -or -not $sourceCommitVerified) {
        $failureCode = 'stage1_identity_mismatch'
        throw [InvalidOperationException]::new($failureCode)
    }

    $runnerRelative = 'tools\ScreenGuide.Stage1DatabaseCompatibilityRunner'
    $currentRunnerProject = Join-Path $resolvedRepository "$runnerRelative\ScreenGuide.Stage1DatabaseCompatibilityRunner.csproj"
    $currentRunnerLock = Join-Path $resolvedRepository "$runnerRelative\packages.lock.json"
    if (-not (Test-Path -LiteralPath $currentRunnerLock -PathType Leaf)) {
        $failureCode = 'runner_lock_missing'
        throw [InvalidOperationException]::new($failureCode)
    }

    $offlinePackageCache = Find-OfflinePackageCache -StartPath $resolvedRepository
    $lockedRestoreArguments = @(
        '--locked-mode',
        '--source', $offlinePackageCache,
        "-p:RestorePackagesPath=$offlinePackageCache",
        '-p:NuGetAudit=false',
        '-nodeReuse:false')
    $currentRestoreArguments = @('restore', $currentRunnerProject) + $lockedRestoreArguments
    $null = Invoke-QuietChecked -FilePath 'dotnet' -Arguments $currentRestoreArguments -Code 'current_runner_restore_failed'
    $null = Invoke-QuietChecked -FilePath 'dotnet' -Arguments @(
        'build', $currentRunnerProject, '-c', 'Release', '--no-restore', '--nologo',
        '-nodeReuse:false') -Code 'current_runner_build_failed'

    $archivePath = Join-Path $ownedRoot 'stage1-source.zip'
    $stage1Root = Join-Path $ownedRoot 'stage1-source'
    New-Item -ItemType Directory -Path $stage1Root | Out-Null
    $null = Invoke-QuietChecked -FilePath 'git' -Arguments @(
        '-C', $resolvedRepository, 'archive', '--format=zip', "--output=$archivePath", $expectedSourceCommit) -Code 'stage1_archive_failed'
    Expand-Archive -LiteralPath $archivePath -DestinationPath $stage1Root

    $stage1RunnerDirectory = Join-Path $stage1Root $runnerRelative
    New-Item -ItemType Directory -Path $stage1RunnerDirectory | Out-Null
    Copy-Item -LiteralPath $currentRunnerProject -Destination $stage1RunnerDirectory
    Copy-Item -LiteralPath (Join-Path $resolvedRepository "$runnerRelative\Program.cs") -Destination $stage1RunnerDirectory
    Copy-Item -LiteralPath $currentRunnerLock -Destination $stage1RunnerDirectory
    $stage1RunnerProject = Join-Path $stage1RunnerDirectory 'ScreenGuide.Stage1DatabaseCompatibilityRunner.csproj'
    $stage1RestoreArguments = @('restore', $stage1RunnerProject) +
        $lockedRestoreArguments +
        @('-p:Stage1CompatibilityBuild=true')
    $null = Invoke-QuietChecked -FilePath 'dotnet' -Arguments $stage1RestoreArguments -Code 'stage1_runner_restore_failed'
    $null = Invoke-QuietChecked -FilePath 'dotnet' -Arguments @(
        'build', $stage1RunnerProject, '-c', 'Release', '--no-restore', '--nologo',
        '-nodeReuse:false', '-p:Stage1CompatibilityBuild=true') -Code 'stage1_runner_build_failed'

    $currentRunner = Join-Path $resolvedRepository "$runnerRelative\bin\Release\net10.0\ScreenGuide.Stage1DatabaseCompatibilityRunner.dll"
    $stage1Runner = Join-Path $stage1RunnerDirectory 'bin\Release\net10.0\ScreenGuide.Stage1DatabaseCompatibilityRunner.dll'
    $currentIdentity = Read-RunnerResult (Invoke-QuietChecked -FilePath 'dotnet' -Arguments @(
        $currentRunner, 'identity') -Code 'current_runner_identity_failed')
    $stage1Identity = Read-RunnerResult (Invoke-QuietChecked -FilePath 'dotnet' -Arguments @(
        $stage1Runner, 'identity') -Code 'stage1_runner_identity_failed')
    if (-not $currentIdentity.success -or -not $stage1Identity.success -or
        $currentIdentity.schemaVersion -ne 8 -or $stage1Identity.schemaVersion -ne 7) {
        $failureCode = 'runner_schema_identity_invalid'
        throw [InvalidOperationException]::new($failureCode)
    }

    if ($Scenario -eq 'Identity') {
        $result = [ordered]@{
            scenario = $Scenario
            success = $true
            errorCode = $null
            exceptionType = $null
            tagObjectVerified = $tagObjectVerified
            sourceCommitVerified = $sourceCommitVerified
            stage1RunnerBuilt = $true
            stage1SchemaVersion = [int]$stage1Identity.schemaVersion
            currentSchemaVersion = [int]$currentIdentity.schemaVersion
        }
    }
    elseif ($Scenario -eq 'NormalMigration') {
        $scenarioRoot = Join-Path $ownedRoot 'normal-migration'
        $databasePath = Join-Path $scenarioRoot 'tasking.db'
        New-Item -ItemType Directory -Path $scenarioRoot | Out-Null
        $commonDatabaseArguments = @(
            '--database', $databasePath,
            '--owned-root', $ownedRoot,
            '--owner-token', $ownerToken)
        $createArguments = @('create-v7') + $commonDatabaseArguments
        $created = Invoke-CompatibilityRunner -Runner $stage1Runner -RunnerArguments $createArguments -Code 'stage1_v7_creation_failed'
        $migrationArguments = @('migrate-v8') + $commonDatabaseArguments
        $migrated = Invoke-CompatibilityRunner -Runner $currentRunner -RunnerArguments $migrationArguments -Code 'current_v8_migration_failed'

        $backupFiles = @(Get-ChildItem -LiteralPath $scenarioRoot -File -Filter 'tasking.pre-v8-from-v7-*.backup.db')
        if ($backupFiles.Count -ne 1) {
            $failureCode = 'pre_v8_backup_count_invalid'
            throw [InvalidOperationException]::new($failureCode)
        }

        $backupArguments = @(
            '--database', $backupFiles[0].FullName,
            '--owned-root', $ownedRoot,
            '--owner-token', $ownerToken)
        $backupReadArguments = @('read-canary') + $backupArguments
        $backupRead = Invoke-CompatibilityRunner -Runner $stage1Runner -RunnerArguments $backupReadArguments -Code 'stage1_backup_read_failed'
        $backupInspectArguments = @('inspect') + $backupArguments
        $backupInspection = Invoke-CompatibilityRunner -Runner $currentRunner -RunnerArguments $backupInspectArguments -Code 'backup_inspection_failed'

        $normalSuccess = $created.success -and
            $migrated.success -and
            $backupRead.success -and
            $backupInspection.success -and
            $backupFiles.Count -eq 1 -and
            [int]$migrated.schemaVersion -eq 8 -and
            [int]$backupRead.schemaVersion -eq 7 -and
            $migrated.integrityOk -and
            $backupInspection.integrityOk -and
            $migrated.canaryReadable -and
            $backupRead.canaryReadable -and
            $migrated.historicalFrozenRouteNull -and
            $migrated.hasAllVersion8Objects -and
            -not $backupInspection.hasAnyVersion8Objects
        if (-not $normalSuccess) {
            $failureCode = 'normal_migration_verification_failed'
            throw [InvalidOperationException]::new($failureCode)
        }

        $result = [ordered]@{
            scenario = $Scenario
            success = $true
            errorCode = $null
            exceptionType = $null
            mainSchemaVersion = [int]$migrated.schemaVersion
            backupSchemaVersion = [int]$backupRead.schemaVersion
            uniqueBackup = $backupFiles.Count -eq 1
            mainIntegrityOk = [bool]$migrated.integrityOk
            backupIntegrityOk = [bool]$backupInspection.integrityOk
            mainCanaryReadable = [bool]$migrated.canaryReadable
            backupCanaryReadableByExactStage1 = [bool]$backupRead.canaryReadable
            historicalFrozenRouteNull = [bool]$migrated.historicalFrozenRouteNull
            v8ObjectsOnlyInMain = [bool]($migrated.hasAllVersion8Objects -and -not $backupInspection.hasAnyVersion8Objects)
        }
    }
    elseif ($Scenario -eq 'FailureRollback') {
        $scenarioRoot = Join-Path $ownedRoot 'failure-rollback'
        $databasePath = Join-Path $scenarioRoot 'tasking.db'
        New-Item -ItemType Directory -Path $scenarioRoot | Out-Null
        $commonDatabaseArguments = @(
            '--database', $databasePath,
            '--owned-root', $ownedRoot,
            '--owner-token', $ownerToken)
        $createArguments = @('create-v7') + $commonDatabaseArguments
        $created = Invoke-CompatibilityRunner -Runner $stage1Runner -RunnerArguments $createArguments -Code 'stage1_failure_fixture_creation_failed'
        $injectArguments = @('inject-v8-conflict') + $commonDatabaseArguments
        $beforeFailure = Invoke-CompatibilityRunner -Runner $currentRunner -RunnerArguments $injectArguments -Code 'migration_conflict_injection_failed'
        $failureArguments = @('migrate-v8-expect-failure') + $commonDatabaseArguments
        $afterFailure = Invoke-CompatibilityRunner -Runner $currentRunner -RunnerArguments $failureArguments -Code 'migration_failure_verification_failed'

        $backupFiles = @(Get-ChildItem -LiteralPath $scenarioRoot -File -Filter 'tasking.pre-v8-from-v7-*.backup.db')
        if ($backupFiles.Count -ne 1) {
            $failureCode = 'failed_migration_backup_count_invalid'
            throw [InvalidOperationException]::new($failureCode)
        }

        $mainReadArguments = @('read-canary') + $commonDatabaseArguments
        $mainRead = Invoke-CompatibilityRunner -Runner $stage1Runner -RunnerArguments $mainReadArguments -Code 'stage1_failed_main_read_failed'
        $backupArguments = @(
            '--database', $backupFiles[0].FullName,
            '--owned-root', $ownedRoot,
            '--owner-token', $ownerToken)
        $backupReadArguments = @('read-canary') + $backupArguments
        $backupRead = Invoke-CompatibilityRunner -Runner $stage1Runner -RunnerArguments $backupReadArguments -Code 'stage1_failed_backup_read_failed'
        $backupInspectArguments = @('inspect') + $backupArguments
        $backupInspection = Invoke-CompatibilityRunner -Runner $currentRunner -RunnerArguments $backupInspectArguments -Code 'failed_backup_inspection_failed'

        $schemaFingerprintUnchanged = $beforeFailure.schemaFingerprint -eq $afterFailure.schemaFingerprint
        $noPartialFrozenRouteColumns = [int]$afterFailure.frozenRouteColumnCount -eq 0
        $noPartialVersion8Indexes = [int]$afterFailure.aiInvocationIndexCount -eq 0
        $preexistingConflictPreserved = [int]$afterFailure.aiInvocationTableCount -eq 1
        $failureSuccess = $created.success -and
            $beforeFailure.success -and
            $afterFailure.success -and
            $afterFailure.migrationFailed -and
            $schemaFingerprintUnchanged -and
            $noPartialFrozenRouteColumns -and
            $noPartialVersion8Indexes -and
            $preexistingConflictPreserved -and
            [int]$afterFailure.schemaVersion -eq 7 -and
            [int]$backupRead.schemaVersion -eq 7 -and
            $afterFailure.integrityOk -and
            $backupInspection.integrityOk -and
            $mainRead.canaryReadable -and
            $backupRead.canaryReadable
        if (-not $failureSuccess) {
            $failureCode = 'failed_migration_atomicity_invalid'
            throw [InvalidOperationException]::new($failureCode)
        }

        $result = [ordered]@{
            scenario = $Scenario
            success = $true
            errorCode = $null
            exceptionType = $null
            migrationFailed = [bool]$afterFailure.migrationFailed
            mainSchemaVersion = [int]$afterFailure.schemaVersion
            backupSchemaVersion = [int]$backupRead.schemaVersion
            schemaFingerprintUnchanged = [bool]$schemaFingerprintUnchanged
            noPartialFrozenRouteColumns = [bool]$noPartialFrozenRouteColumns
            noPartialVersion8Indexes = [bool]$noPartialVersion8Indexes
            preexistingConflictPreserved = [bool]$preexistingConflictPreserved
            mainIntegrityOk = [bool]$afterFailure.integrityOk
            backupIntegrityOk = [bool]$backupInspection.integrityOk
            mainCanaryReadableByExactStage1 = [bool]$mainRead.canaryReadable
            backupCanaryReadableByExactStage1 = [bool]$backupRead.canaryReadable
        }
    }
    elseif ($Scenario -eq 'RejectV8') {
        $sourceRoot = Join-Path $ownedRoot 'reject-v8-source'
        $sourceDatabase = Join-Path $sourceRoot 'tasking.db'
        New-Item -ItemType Directory -Path $sourceRoot | Out-Null
        $sourceArguments = @(
            '--database', $sourceDatabase,
            '--owned-root', $ownedRoot,
            '--owner-token', $ownerToken)
        $createArguments = @('create-v7') + $sourceArguments
        $created = Invoke-CompatibilityRunner -Runner $stage1Runner -RunnerArguments $createArguments -Code 'stage1_rejection_fixture_creation_failed'
        $migrationArguments = @('migrate-v8') + $sourceArguments
        $migrated = Invoke-CompatibilityRunner -Runner $currentRunner -RunnerArguments $migrationArguments -Code 'rejection_fixture_migration_failed'

        $rejectionRoot = Join-Path $ownedRoot 'reject-v8-copy'
        $rejectionDatabase = Join-Path $rejectionRoot 'tasking.db'
        New-Item -ItemType Directory -Path $rejectionRoot | Out-Null
        Copy-Item -LiteralPath $sourceDatabase -Destination $rejectionDatabase
        $rejectionArguments = @(
            'reject-v8',
            '--database', $rejectionDatabase,
            '--owned-root', $ownedRoot,
            '--owner-token', $ownerToken)
        $rejection = Invoke-CompatibilityRunner -Runner $stage1Runner -RunnerArguments $rejectionArguments -Code 'stage1_v8_rejection_failed'

        $rejectionSuccess = $created.success -and
            $migrated.success -and
            $rejection.success -and
            $rejection.stage1RejectedV8 -and
            [int]$rejection.schemaVersion -eq 8 -and
            $rejection.databaseHashUnchanged -and
            $rejection.schemaFingerprintUnchanged -and
            $rejection.canaryUnchanged -and
            -not $rejection.hostStarted -and
            -not $rejection.turnReplayAttempted -and
            -not $rejection.inPlaceDowngradeAttempted
        if (-not $rejectionSuccess) {
            $failureCode = 'stage1_v8_rejection_verification_failed'
            throw [InvalidOperationException]::new($failureCode)
        }

        $result = [ordered]@{
            scenario = $Scenario
            success = $true
            errorCode = $null
            exceptionType = $null
            stage1RejectedV8 = [bool]$rejection.stage1RejectedV8
            schemaVersion = [int]$rejection.schemaVersion
            databaseHashUnchanged = [bool]$rejection.databaseHashUnchanged
            schemaFingerprintUnchanged = [bool]$rejection.schemaFingerprintUnchanged
            canaryUnchanged = [bool]$rejection.canaryUnchanged
            hostNotStarted = [bool](-not $rejection.hostStarted)
            noTurnReplay = [bool](-not $rejection.turnReplayAttempted)
            noInPlaceDowngrade = [bool](-not $rejection.inPlaceDowngradeAttempted)
        }
    }
    else {
        $failureCode = 'scenario_not_implemented'
        throw [InvalidOperationException]::new($failureCode)
    }
    $exitCode = 0
}
catch {
    $result = [ordered]@{
        scenario = $Scenario
        success = $false
        errorCode = $failureCode
        exceptionType = $_.Exception.GetType().Name
        tagObjectVerified = $false
        sourceCommitVerified = $false
        stage1RunnerBuilt = $false
        stage1SchemaVersion = $null
        currentSchemaVersion = $null
    }
}
finally {
    [Environment]::SetEnvironmentVariable('SCREEN_GUIDE_DATA_DIRECTORY', $previousDataDirectory, 'Process')
    [Environment]::SetEnvironmentVariable('SCREEN_GUIDE_PIPE_NAME', $previousPipeName, 'Process')
    if (Test-Path -LiteralPath $ownedRoot -PathType Container) {
        try {
            Remove-OwnedTemporaryRoot
        }
        catch {
            $result = [ordered]@{
                scenario = $Scenario
                success = $false
                errorCode = 'owned_cleanup_failed'
                exceptionType = $_.Exception.GetType().Name
                tagObjectVerified = $false
                sourceCommitVerified = $false
                stage1RunnerBuilt = $false
                stage1SchemaVersion = $null
                currentSchemaVersion = $null
            }
            $exitCode = 1
        }
    }
}

Write-Output ($resultPrefix + ($result | ConvertTo-Json -Compress))
exit $exitCode
