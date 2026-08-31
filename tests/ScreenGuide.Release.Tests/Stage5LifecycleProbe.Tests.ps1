$ErrorActionPreference = 'Stop'

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$project = Join-Path $repoRoot 'tests\ScreenGuide.Stage5LifecycleProbe\ScreenGuide.Stage5LifecycleProbe.csproj'
$probe = Join-Path $repoRoot 'tests\ScreenGuide.Stage5LifecycleProbe\bin\Release\net10.0\win-x64\ScreenGuide.Stage5LifecycleProbe.exe'
$testRoot = Join-Path ([IO.Path]::GetTempPath()) ('YuanshuStage5ProbeTests-' + [Guid]::NewGuid().ToString('N'))

function Assert-True([bool]$condition, [string]$message) {
    if (-not $condition) { throw $message }
}

try {
    [IO.Directory]::CreateDirectory($testRoot) | Out-Null
    & dotnet build $project --configuration Release --no-restore --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Lifecycle probe Release build failed.' }
    if (-not (Test-Path -LiteralPath $probe -PathType Leaf)) { throw 'Lifecycle probe executable missing.' }

    $database = Join-Path $testRoot ('YuanshuStage5ProbeFixture-' + [Guid]::NewGuid().ToString('N') + '.db')
    $passOutput = Join-Path $testRoot 'pass.json'
    $previous = $env:SCREEN_GUIDE_STAGE5_PROBE_TEST_MODE
    try {
        $env:SCREEN_GUIDE_STAGE5_PROBE_TEST_MODE = '1'
        & $probe --database $database --expected-schema 10 --output $passOutput --fixture-schema 10 | Out-Null
    }
    finally { $env:SCREEN_GUIDE_STAGE5_PROBE_TEST_MODE = $previous }
    Assert-True ($LASTEXITCODE -eq 0) 'Exact schema fixture must pass.'
    $pass = Get-Content -Raw -LiteralPath $passOutput | ConvertFrom-Json
    Assert-True ($pass.passed -and $pass.schemaVersion -eq 10 -and $pass.integrityOk) 'Probe PASS evidence must be bounded and exact.'
    Assert-True ([string]$pass.databaseSha256 -match '^[0-9A-F]{64}$') 'Probe must report only a safe database hash.'

    $failOutput = Join-Path $testRoot 'mismatch.json'
    & $probe --database $database --expected-schema 11 --output $failOutput | Out-Null
    Assert-True ($LASTEXITCODE -ne 0) 'Mismatched schema must fail closed.'
    $failure = Get-Content -Raw -LiteralPath $failOutput | ConvertFrom-Json
    Assert-True ($failure.errorCode -eq 's5_lifecycle_probe_schema_mismatch') 'Schema mismatch must use a stable code.'
    Assert-True (-not (($failure | ConvertTo-Json -Depth 10).Contains($testRoot))) 'Probe evidence must not contain local paths.'

    Write-Host 'Stage5 lifecycle probe: Release build + exact schema PASS + mismatch fail-closed passed'
}
finally {
    $resolved = [IO.Path]::GetFullPath($testRoot)
    $prefix = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
    if ($resolved.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase) -and
        [IO.Path]::GetFileName($resolved).StartsWith('YuanshuStage5ProbeTests-', [StringComparison]::Ordinal)) {
        if (Test-Path -LiteralPath $resolved) { Remove-Item -LiteralPath $resolved -Recurse -Force }
    }
}
