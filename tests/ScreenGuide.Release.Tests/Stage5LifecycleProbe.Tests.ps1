$ErrorActionPreference = 'Stop'

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$project = Join-Path $repoRoot 'tests\ScreenGuide.Stage5LifecycleProbe\ScreenGuide.Stage5LifecycleProbe.csproj'
$prepareScript = Join-Path $repoRoot 'scripts\New-Stage5LifecycleProbeBundle.ps1'
$validateScript = Join-Path $repoRoot 'scripts\Test-Stage5LifecycleProbeBundle.ps1'
$testRoot = Join-Path ([IO.Path]::GetTempPath()) ('YuanshuStage5ProbeTests-' + [Guid]::NewGuid().ToString('N'))

function Assert-True([bool]$condition, [string]$message) {
    if (-not $condition) { throw $message }
}

function Invoke-Validator([string]$bundleRoot, [string]$resultPath) {
    $output = @(& powershell.exe -NoProfile -ExecutionPolicy Bypass -File $validateScript `
        -BundleRoot $bundleRoot -ResultPath $resultPath 2>&1)
    return [pscustomobject]@{
        ExitCode = $LASTEXITCODE
        Result = if (Test-Path -LiteralPath $resultPath) { Get-Content -Raw -LiteralPath $resultPath | ConvertFrom-Json } else { $null }
        Output = $output -join "`n"
    }
}

try {
    [IO.Directory]::CreateDirectory($testRoot) | Out-Null
    Assert-True (Test-Path -LiteralPath $prepareScript -PathType Leaf) 'Offline probe bundle preparation script must exist.'
    Assert-True (Test-Path -LiteralPath $validateScript -PathType Leaf) 'Probe bundle validator must exist.'

    $intermediateRoot = Join-Path $testRoot 'fresh-intermediate'
    $bundleRoot = Join-Path $testRoot 'self-contained-bundle'
    $prepareResultPath = Join-Path $testRoot 'prepare-result.json'
    $prepareOutput = @(& powershell.exe -NoProfile -ExecutionPolicy Bypass -File $prepareScript `
        -ProjectPath $project -IntermediateRoot $intermediateRoot -OutputRoot $bundleRoot `
        -ResultPath $prepareResultPath 2>&1)
    Assert-True ($LASTEXITCODE -eq 0) ('Fresh offline self-contained preparation must pass. ' + ($prepareOutput -join "`n"))
    $prepare = Get-Content -Raw -LiteralPath $prepareResultPath | ConvertFrom-Json
    Assert-True ($prepare.status -eq 'PASS' -and $prepare.selfContained -and $prepare.runtimeIdentifier -eq 'win-x64') 'Preparation evidence must prove a self-contained win-x64 bundle.'
    Assert-True ($prepare.networkRequests -eq 0 -and $prepare.remoteSources -eq 0) 'Preparation must prove no network or remote NuGet source.'
    Assert-True ($prepare.fileCount -gt 6) 'Self-contained bundle must contain the runtime, not only one executable.'
    Assert-True (Test-Path -LiteralPath (Join-Path $intermediateRoot 'project.assets.json') -PathType Leaf) 'Fresh locked restore must create project.assets.json in the owned intermediate root.'
    foreach ($required in @('ScreenGuide.Stage5LifecycleProbe.exe', 'hostfxr.dll', 'hostpolicy.dll', 'coreclr.dll', 'System.Private.CoreLib.dll', 'e_sqlite3.dll', 'lifecycle-probe-bundle.json')) {
        Assert-True (Test-Path -LiteralPath (Join-Path $bundleRoot $required) -PathType Leaf) "Self-contained bundle is missing $required."
    }

    $validation = Invoke-Validator $bundleRoot (Join-Path $testRoot 'validate-pass.json')
    Assert-True ($validation.ExitCode -eq 0 -and $validation.Result.status -eq 'PASS') 'Fresh bundle validation must pass.'
    Assert-True ($validation.Result.fileCount -eq $prepare.fileCount) 'Preparation and validation file counts must match.'

    $probe = Join-Path $bundleRoot 'ScreenGuide.Stage5LifecycleProbe.exe'
    $database = Join-Path $testRoot ('YuanshuStage5ProbeFixture-' + [Guid]::NewGuid().ToString('N') + '.db')
    $passOutput = Join-Path $testRoot 'pass.json'
    $previous = $env:SCREEN_GUIDE_STAGE5_PROBE_TEST_MODE
    try {
        $env:SCREEN_GUIDE_STAGE5_PROBE_TEST_MODE = '1'
        & $probe --database $database --expected-schema 10 --output $passOutput --fixture-schema 10 | Out-Null
    }
    finally { $env:SCREEN_GUIDE_STAGE5_PROBE_TEST_MODE = $previous }
    Assert-True ($LASTEXITCODE -eq 0) 'Exact schema fixture must pass from the self-contained bundle.'
    $pass = Get-Content -Raw -LiteralPath $passOutput | ConvertFrom-Json
    Assert-True ($pass.passed -and $pass.schemaVersion -eq 10 -and $pass.integrityOk) 'Probe PASS evidence must be bounded and exact.'

    $failOutput = Join-Path $testRoot 'mismatch.json'
    & $probe --database $database --expected-schema 11 --output $failOutput | Out-Null
    Assert-True ($LASTEXITCODE -ne 0) 'Mismatched schema must fail closed.'
    $failure = Get-Content -Raw -LiteralPath $failOutput | ConvertFrom-Json
    Assert-True ($failure.errorCode -eq 's5_lifecycle_probe_schema_mismatch') 'Schema mismatch must use a stable code.'
    Assert-True (-not (($failure | ConvertTo-Json -Depth 10).Contains($testRoot))) 'Probe evidence must not contain local paths.'

    $tamperedRoot = Join-Path $testRoot 'tampered-bundle'
    Copy-Item -LiteralPath $bundleRoot -Destination $tamperedRoot -Recurse
    [IO.File]::AppendAllText((Join-Path $tamperedRoot 'hostfxr.dll'), 'tamper', [Text.UTF8Encoding]::new($false))
    $tampered = Invoke-Validator $tamperedRoot (Join-Path $testRoot 'validate-tampered.json')
    Assert-True ($tampered.ExitCode -ne 0 -and $tampered.Result.errorCode -eq 's5_lifecycle_probe_bundle_hash_mismatch') 'Tampered runtime file must fail closed.'

    $emptyCache = Join-Path $testRoot 'empty-offline-cache'
    [IO.Directory]::CreateDirectory($emptyCache) | Out-Null
    $missingCacheResult = Join-Path $testRoot 'missing-cache.json'
    $missingCacheOutput = @(& powershell.exe -NoProfile -ExecutionPolicy Bypass -File $prepareScript `
        -ProjectPath $project -IntermediateRoot (Join-Path $testRoot 'missing-cache-intermediate') `
        -OutputRoot (Join-Path $testRoot 'missing-cache-output') -ResultPath $missingCacheResult `
        -OfflinePackageCache $emptyCache 2>&1)
    $missingCache = Get-Content -Raw -LiteralPath $missingCacheResult | ConvertFrom-Json
    Assert-True ($LASTEXITCODE -ne 0 -and $missingCache.errorCode -eq 's5_lifecycle_probe_offline_cache_incomplete') ('Empty cache must fail closed without fallback. ' + ($missingCacheOutput -join "`n"))
    Assert-True (-not (Test-Path -LiteralPath (Join-Path $testRoot 'missing-cache-output\lifecycle-probe-bundle.json'))) 'Failed preparation must not emit a valid-looking bundle manifest.'

    foreach ($script in @($prepareScript, $validateScript)) {
        $text = Get-Content -Raw -LiteralPath $script
        Assert-True (-not $text.Contains('http://') -and -not $text.Contains('https://')) 'Probe bundle scripts must not contain remote sources.'
    }
    Write-Host 'Stage5 lifecycle probe: fresh locked offline restore + self-contained publish + 2 probe cases + bundle tamper/cache failures passed'
}
finally {
    $resolved = [IO.Path]::GetFullPath($testRoot)
    $prefix = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
    if ($resolved.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase) -and
        [IO.Path]::GetFileName($resolved).StartsWith('YuanshuStage5ProbeTests-', [StringComparison]::Ordinal)) {
        if (Test-Path -LiteralPath $resolved) { Remove-Item -LiteralPath $resolved -Recurse -Force }
    }
}
