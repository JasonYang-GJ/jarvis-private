$ErrorActionPreference = 'Stop'

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$validator = Join-Path $repoRoot 'scripts\Test-ReleaseCandidateIdentity.ps1'
$contractPath = Join-Path $repoRoot 'distribution\release-candidates\v0.7.0\attribution-contract.json'
$baselineSha = '02c158d210569b1ce5ab5e3182084f5bf6486236'
$syntheticSourceSha = '1111111111111111111111111111111111111111'

function Assert-True([bool]$condition, [string]$message) {
    if (-not $condition) { throw $message }
}

function Assert-Equal($expected, $actual, [string]$message) {
    if ($expected -cne $actual) { throw "$message Expected=[$expected] Actual=[$actual]" }
}

function Invoke-IdentityValidator([string]$sourceSha, [string]$parentSha, [string]$version, [string]$repository = $repoRoot, [string]$contract = $contractPath) {
    $output = @(& powershell.exe -NoProfile -ExecutionPolicy Bypass -File $validator `
        -RepositoryRoot $repository `
        -ExpectedSourceSha $sourceSha `
        -ExpectedParentSha $parentSha `
        -ReleaseVersion $version `
        -AttributionContractPath $contract `
        -ConfigurationOnly 2>&1)
    return [pscustomobject]@{
        ExitCode = $LASTEXITCODE
        Result = ($output -join "`n") | ConvertFrom-Json
    }
}

$positive = Invoke-IdentityValidator $syntheticSourceSha $baselineSha '0.7.0'
Assert-Equal 0 $positive.ExitCode 'Exact V0.7.0 candidate configuration must pass.'
Assert-True $positive.Result.passed 'Positive identity result must report passed=true.'

$wrongVersion = Invoke-IdentityValidator $syntheticSourceSha $baselineSha '0.6.0'
Assert-Equal 1 $wrongVersion.ExitCode 'Legacy release version must fail.'
Assert-Equal 'release_candidate_version_mismatch' $wrongVersion.Result.errorCode 'Legacy version must use the stable mismatch code.'

$wrongParent = Invoke-IdentityValidator $syntheticSourceSha ('2' * 40) '0.7.0'
Assert-Equal 1 $wrongParent.ExitCode 'Wrong baseline parent must fail.'
Assert-Equal 'release_candidate_contract_mismatch' $wrongParent.Result.errorCode 'Wrong parent must fail against the candidate contract.'

$releaseScript = Get-Content -Raw -LiteralPath (Join-Path $repoRoot 'scripts\build-desktop-release.ps1')
Assert-True (-not $releaseScript.Contains('V0.6.0_STAGE4_C0_PAYLOAD_ATTRIBUTION.json')) 'Candidate release must never reference the frozen Stage 4 payload manifest.'
Assert-True (-not $releaseScript.Contains('元枢-V0.6.0-安装包.exe')) 'Candidate release must never accept the frozen Stage 4 installer name.'
Assert-True ($releaseScript.Contains('[string]$ExpectedSourceSha')) 'Expected source SHA must be an explicit release input.'
Assert-True ($releaseScript.Contains('[string]$ExpectedParentSha')) 'Expected parent SHA must be an explicit release input.'
Assert-True ($releaseScript.Contains('[string]$AttributionContractPath')) 'Attribution contract must be an explicit release input.'
Assert-True ($releaseScript.Contains('--configfile $offlineNuGetConfig')) 'Every restore path must use the source-cleared offline NuGet configuration.'
Assert-Equal 3 ([regex]::Matches($releaseScript, '--configfile \$offlineNuGetConfig').Count) 'All three restore operations must be offline-bound.'
Assert-True ($releaseScript.Contains('-p:SourceRevisionId=$ExpectedSourceSha')) 'Publish must bind InformationalVersion to the authorized exact SHA.'
Assert-True ($releaseScript.Contains('Release candidate build changed the Git working tree.')) 'Release build must fail if Git is not clean after packaging.'
Assert-True ($releaseScript.Contains("([string]`$installerInfo.FileVersion).Trim()")) 'Installer FileVersion validation must normalize Win32 fixed-width padding.'
Assert-True ($releaseScript.Contains("([string]`$installerInfo.ProductVersion).Trim()")) 'Installer ProductVersion validation must normalize Win32 fixed-width padding.'

$installer = Get-Content -Raw -LiteralPath (Join-Path $repoRoot 'installer\ScreenGuideDesktop.iss')
Assert-True ($installer.Contains('#define MyAppVersion "0.7.0"')) 'Installer version must be V0.7.0.'
Assert-True ($installer.Contains('OutputBaseFilename=元枢-V0.7.0-候选安装包')) 'Installer file name must visibly identify the candidate.'
Assert-True ($installer.Contains('AppId={{E2B9C242-2965-48BC-B2C6-CF83A2B11953}')) 'Candidate must retain the existing upgrade AppId.'

$contract = Get-Content -Raw -LiteralPath $contractPath | ConvertFrom-Json
Assert-Equal '0.7.0' ([string]$contract.releaseVersion) 'Candidate attribution contract version must be explicit.'
Assert-Equal $baselineSha ([string]$contract.baselineParentSha) 'Candidate attribution contract must bind the approved baseline parent.'
Assert-Equal 'PUBLISH_OUTPUT_EXACT_SOURCE_MAPPING_V1' ([string]$contract.generationMode) 'Candidate attribution must be generated from current publish output.'
Assert-True (@($contract.forbiddenPayloadManifestPaths) -contains 'docs/baselines/V0.6.0_STAGE4_C0_PAYLOAD_ATTRIBUTION.json') 'Frozen manifest fallback must be explicitly forbidden.'

$fixtureRoot = Join-Path ([IO.Path]::GetTempPath()) ('yuanshu-release-identity-' + [Guid]::NewGuid().ToString('N'))
try {
    $fixtureContractRoot = Join-Path $fixtureRoot 'distribution\release-candidates\v0.7.0'
    $fixtureBundleRoot = Join-Path $fixtureRoot 'distribution\licenses'
    $fixtureScriptsRoot = Join-Path $fixtureRoot 'scripts'
    $fixtureInstallerRoot = Join-Path $fixtureRoot 'installer'
    [IO.Directory]::CreateDirectory($fixtureContractRoot) | Out-Null
    [IO.Directory]::CreateDirectory($fixtureBundleRoot) | Out-Null
    [IO.Directory]::CreateDirectory($fixtureScriptsRoot) | Out-Null
    [IO.Directory]::CreateDirectory($fixtureInstallerRoot) | Out-Null
    Copy-Item -LiteralPath (Join-Path $repoRoot 'Directory.Build.props') -Destination (Join-Path $fixtureRoot 'Directory.Build.props')
    Copy-Item -LiteralPath (Join-Path $repoRoot 'installer\ScreenGuideDesktop.iss') -Destination (Join-Path $fixtureInstallerRoot 'ScreenGuideDesktop.iss')
    Copy-Item -LiteralPath (Join-Path $repoRoot 'scripts\build-desktop-release.ps1') -Destination (Join-Path $fixtureScriptsRoot 'build-desktop-release.ps1')
    Copy-Item -LiteralPath (Join-Path $repoRoot 'distribution\licenses\bundle-manifest.json') -Destination (Join-Path $fixtureBundleRoot 'bundle-manifest.json')
    Copy-Item -LiteralPath (Join-Path $repoRoot 'distribution\release-candidates\v0.7.0\PROPRIETARY-NOTICE.txt') -Destination (Join-Path $fixtureContractRoot 'PROPRIETARY-NOTICE.txt')
    Copy-Item -LiteralPath $contractPath -Destination (Join-Path $fixtureContractRoot 'attribution-contract.json')

    [IO.File]::AppendAllText((Join-Path $fixtureBundleRoot 'bundle-manifest.json'), "`n", [Text.UTF8Encoding]::new($false))
    $tampered = Invoke-IdentityValidator $syntheticSourceSha $baselineSha '0.7.0' $fixtureRoot (Join-Path $fixtureContractRoot 'attribution-contract.json')
    Assert-Equal 1 $tampered.ExitCode 'Tampered base manifest must fail.'
    Assert-Equal 'release_candidate_base_bundle_hash_mismatch' $tampered.Result.errorCode 'Manifest tamper must have a stable fail-closed code.'
}
finally {
    if (Test-Path -LiteralPath $fixtureRoot) { Remove-Item -LiteralPath $fixtureRoot -Recurse -Force }
}

Write-Host 'RELEASE CANDIDATE IDENTITY: 18 targeted checks passed'
