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

function Invoke-LiveIdentityValidator(
    [string]$validatorPath,
    [string]$repository,
    [string]$contract,
    [string]$sourceSha,
    [string]$parentSha,
    [string]$version = '0.7.0'
) {
    $output = @(& powershell.exe -NoProfile -ExecutionPolicy Bypass -File $validatorPath `
        -RepositoryRoot $repository `
        -ExpectedSourceSha $sourceSha `
        -ExpectedParentSha $parentSha `
        -ReleaseVersion $version `
        -AttributionContractPath $contract 2>&1)
    return [pscustomobject]@{
        ExitCode = $LASTEXITCODE
        Result = ($output -join "`n") | ConvertFrom-Json
    }
}

function Invoke-FixtureGit([string]$repository, [string[]]$arguments) {
    $output = @(& git -C $repository @arguments 2>&1)
    if ($LASTEXITCODE -ne 0) {
        throw "Fixture git command failed: git $($arguments -join ' ')`n$($output -join "`n")"
    }
    return ($output -join "`n").Trim()
}

$positive = Invoke-IdentityValidator $syntheticSourceSha $baselineSha '0.7.0'
Assert-Equal 0 $positive.ExitCode 'Exact V0.7.0 candidate configuration must pass.'
Assert-True $positive.Result.passed 'Positive identity result must report passed=true.'

$wrongVersion = Invoke-IdentityValidator $syntheticSourceSha $baselineSha '0.6.0'
Assert-Equal 1 $wrongVersion.ExitCode 'Legacy release version must fail.'
Assert-Equal 'release_candidate_version_mismatch' $wrongVersion.Result.errorCode 'Legacy version must use the stable mismatch code.'

$configurationOnlyDirectParent = Invoke-IdentityValidator $syntheticSourceSha ('2' * 40) '0.7.0'
Assert-Equal 0 $configurationOnlyDirectParent.ExitCode 'Configuration-only validation must not confuse the candidate baseline with a direct parent.'
Assert-True $configurationOnlyDirectParent.Result.passed 'Configuration-only validation must accept an independently supplied direct parent.'

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

$gitFixtureRoot = Join-Path ([IO.Path]::GetTempPath()) ('yuanshu-release-identity-git-' + [Guid]::NewGuid().ToString('N'))
try {
    $gitFixtureContractRoot = Join-Path $gitFixtureRoot 'distribution\release-candidates\v0.7.0'
    $gitFixtureBundleRoot = Join-Path $gitFixtureRoot 'distribution\licenses'
    $gitFixtureScriptsRoot = Join-Path $gitFixtureRoot 'scripts'
    $gitFixtureInstallerRoot = Join-Path $gitFixtureRoot 'installer'
    [IO.Directory]::CreateDirectory($gitFixtureContractRoot) | Out-Null
    [IO.Directory]::CreateDirectory($gitFixtureBundleRoot) | Out-Null
    [IO.Directory]::CreateDirectory($gitFixtureScriptsRoot) | Out-Null
    [IO.Directory]::CreateDirectory($gitFixtureInstallerRoot) | Out-Null
    Copy-Item -LiteralPath (Join-Path $repoRoot 'Directory.Build.props') -Destination (Join-Path $gitFixtureRoot 'Directory.Build.props')
    Copy-Item -LiteralPath (Join-Path $repoRoot 'installer\ScreenGuideDesktop.iss') -Destination (Join-Path $gitFixtureInstallerRoot 'ScreenGuideDesktop.iss')
    Copy-Item -LiteralPath (Join-Path $repoRoot 'scripts\build-desktop-release.ps1') -Destination (Join-Path $gitFixtureScriptsRoot 'build-desktop-release.ps1')
    Copy-Item -LiteralPath (Join-Path $repoRoot 'distribution\licenses\bundle-manifest.json') -Destination (Join-Path $gitFixtureBundleRoot 'bundle-manifest.json')
    Copy-Item -LiteralPath (Join-Path $repoRoot 'distribution\release-candidates\v0.7.0\PROPRIETARY-NOTICE.txt') -Destination (Join-Path $gitFixtureContractRoot 'PROPRIETARY-NOTICE.txt')

    Invoke-FixtureGit $gitFixtureRoot @('init', '--quiet') | Out-Null
    Invoke-FixtureGit $gitFixtureRoot @('config', 'core.autocrlf', 'false') | Out-Null
    Invoke-FixtureGit $gitFixtureRoot @('config', 'user.name', 'Yuanshu Release Test') | Out-Null
    Invoke-FixtureGit $gitFixtureRoot @('config', 'user.email', 'release-test@invalid.local') | Out-Null
    Invoke-FixtureGit $gitFixtureRoot @('add', '--all') | Out-Null
    Invoke-FixtureGit $gitFixtureRoot @('commit', '--quiet', '-m', 'initial baseline') | Out-Null
    $gitFixtureBaselineSha = Invoke-FixtureGit $gitFixtureRoot @('rev-parse', 'HEAD')

    $gitFixtureContractPath = Join-Path $gitFixtureContractRoot 'attribution-contract.json'
    $gitFixtureValidator = Join-Path $gitFixtureScriptsRoot 'Test-ReleaseCandidateIdentity.ps1'
    Copy-Item -LiteralPath $validator -Destination $gitFixtureValidator
    $gitFixtureContract = Get-Content -Raw -LiteralPath $contractPath | ConvertFrom-Json
    $gitFixtureContract.baselineParentSha = $gitFixtureBaselineSha
    [IO.File]::WriteAllText(
        $gitFixtureContractPath,
        (($gitFixtureContract | ConvertTo-Json -Depth 12).Replace("`r`n", "`n") + "`n"),
        [Text.UTF8Encoding]::new($false))
    Invoke-FixtureGit $gitFixtureRoot @('add', '--all') | Out-Null
    Invoke-FixtureGit $gitFixtureRoot @('commit', '--quiet', '-m', 'release identity commit') | Out-Null

    [IO.File]::WriteAllText((Join-Path $gitFixtureRoot 'product-security-fix.txt'), "safe`n", [Text.UTF8Encoding]::new($false))
    Invoke-FixtureGit $gitFixtureRoot @('add', '--all') | Out-Null
    Invoke-FixtureGit $gitFixtureRoot @('commit', '--quiet', '-m', 'product security fix') | Out-Null

    [IO.File]::AppendAllText($gitFixtureValidator, "`n# validator fix candidate`n", [Text.UTF8Encoding]::new($false))
    Invoke-FixtureGit $gitFixtureRoot @('add', '--all') | Out-Null
    Invoke-FixtureGit $gitFixtureRoot @('commit', '--quiet', '-m', 'validator fix candidate') | Out-Null
    $gitFixtureSourceSha = Invoke-FixtureGit $gitFixtureRoot @('rev-parse', 'HEAD')
    $gitFixtureParentSha = Invoke-FixtureGit $gitFixtureRoot @('rev-parse', 'HEAD^')

    $multiCommit = Invoke-LiveIdentityValidator $gitFixtureValidator $gitFixtureRoot $gitFixtureContractPath $gitFixtureSourceSha $gitFixtureParentSha
    Assert-Equal 0 $multiCommit.ExitCode "A clean multi-commit candidate must accept its initial baseline ancestor and exact direct parent. ErrorCode=$($multiCommit.Result.errorCode)."
    Assert-True $multiCommit.Result.passed 'Multi-commit candidate identity must report passed=true.'

    $wrongLiveParent = Invoke-LiveIdentityValidator $gitFixtureValidator $gitFixtureRoot $gitFixtureContractPath $gitFixtureSourceSha ('2' * 40)
    Assert-Equal 1 $wrongLiveParent.ExitCode 'A direct parent other than HEAD^ must fail.'
    Assert-Equal 'release_candidate_parent_sha_mismatch' $wrongLiveParent.Result.errorCode 'A direct parent mismatch must use the stable parent code.'

    $wrongLiveSource = Invoke-LiveIdentityValidator $gitFixtureValidator $gitFixtureRoot $gitFixtureContractPath ('1' * 40) $gitFixtureParentSha
    Assert-Equal 1 $wrongLiveSource.ExitCode 'A source SHA other than HEAD must fail.'
    Assert-Equal 'release_candidate_source_sha_mismatch' $wrongLiveSource.Result.errorCode 'A source mismatch must use the stable source code.'

    $invalidSourceSha = Invoke-LiveIdentityValidator $gitFixtureValidator $gitFixtureRoot $gitFixtureContractPath 'abc' $gitFixtureParentSha
    Assert-Equal 1 $invalidSourceSha.ExitCode 'A short source SHA must fail.'
    Assert-Equal 'release_candidate_sha_invalid' $invalidSourceSha.Result.errorCode 'An invalid source SHA must use the stable SHA code.'

    $invalidParentSha = Invoke-LiveIdentityValidator $gitFixtureValidator $gitFixtureRoot $gitFixtureContractPath $gitFixtureSourceSha ('A' * 40)
    Assert-Equal 1 $invalidParentSha.ExitCode 'An uppercase direct parent SHA must fail.'
    Assert-Equal 'release_candidate_sha_invalid' $invalidParentSha.Result.errorCode 'An invalid parent SHA must use the stable SHA code.'

    $wrongLiveVersion = Invoke-LiveIdentityValidator $gitFixtureValidator $gitFixtureRoot $gitFixtureContractPath $gitFixtureSourceSha $gitFixtureParentSha '0.6.0'
    Assert-Equal 1 $wrongLiveVersion.ExitCode 'A live legacy release version must fail.'
    Assert-Equal 'release_candidate_version_mismatch' $wrongLiveVersion.Result.errorCode 'A live version mismatch must use the stable version code.'

    $outOfBoundsContract = Invoke-LiveIdentityValidator $gitFixtureValidator $gitFixtureRoot $contractPath $gitFixtureSourceSha $gitFixtureParentSha
    Assert-Equal 1 $outOfBoundsContract.ExitCode 'An attribution contract outside the candidate directory must fail.'
    Assert-Equal 'release_candidate_contract_out_of_bounds' $outOfBoundsContract.Result.errorCode 'An out-of-bounds contract must use the stable bounds code.'

    $legacyStage4Manifest = Join-Path $repoRoot 'docs\baselines\V0.6.0_STAGE4_C0_PAYLOAD_ATTRIBUTION.json'
    $legacyManifestResult = Invoke-LiveIdentityValidator $gitFixtureValidator $gitFixtureRoot $legacyStage4Manifest $gitFixtureSourceSha $gitFixtureParentSha
    Assert-Equal 1 $legacyManifestResult.ExitCode 'The frozen Stage 4 payload manifest must not be accepted as a candidate contract.'
    Assert-Equal 'release_candidate_contract_out_of_bounds' $legacyManifestResult.Result.errorCode 'The frozen Stage 4 manifest must fail at the candidate contract boundary.'

    $dirtyPath = Join-Path $gitFixtureRoot 'untracked-dirty-file.txt'
    [IO.File]::WriteAllText($dirtyPath, "dirty`n", [Text.UTF8Encoding]::new($false))
    try {
        $dirtyResult = Invoke-LiveIdentityValidator $gitFixtureValidator $gitFixtureRoot $gitFixtureContractPath $gitFixtureSourceSha $gitFixtureParentSha
        Assert-Equal 1 $dirtyResult.ExitCode 'A dirty candidate repository must fail.'
        Assert-Equal 'release_candidate_git_not_clean' $dirtyResult.Result.errorCode 'A dirty repository must use the stable clean-tree code.'
    }
    finally {
        if (Test-Path -LiteralPath $dirtyPath) { Remove-Item -LiteralPath $dirtyPath -Force }
    }

    $tamperFixtureRoot = Join-Path ([IO.Path]::GetTempPath()) ('yuanshu-release-identity-tamper-' + [Guid]::NewGuid().ToString('N'))
    try {
        $cloneOutput = @(& git -c core.autocrlf=false clone --quiet --local --no-hardlinks $gitFixtureRoot $tamperFixtureRoot 2>&1)
        if ($LASTEXITCODE -ne 0) { throw "Fixture clone failed:`n$($cloneOutput -join "`n")" }
        Invoke-FixtureGit $tamperFixtureRoot @('config', 'core.autocrlf', 'false') | Out-Null
        Invoke-FixtureGit $tamperFixtureRoot @('config', 'user.name', 'Yuanshu Release Test') | Out-Null
        Invoke-FixtureGit $tamperFixtureRoot @('config', 'user.email', 'release-test@invalid.local') | Out-Null
        $tamperContractPath = Join-Path $tamperFixtureRoot 'distribution\release-candidates\v0.7.0\attribution-contract.json'
        $tamperValidator = Join-Path $tamperFixtureRoot 'scripts\Test-ReleaseCandidateIdentity.ps1'
        $unrelatedTree = Invoke-FixtureGit $tamperFixtureRoot @('write-tree')
        $unrelatedBaselineSha = Invoke-FixtureGit $tamperFixtureRoot @('commit-tree', $unrelatedTree, '-m', 'unrelated baseline')
        $tamperedContract = Get-Content -Raw -LiteralPath $tamperContractPath | ConvertFrom-Json
        $tamperedContract.baselineParentSha = $unrelatedBaselineSha
        [IO.File]::WriteAllText(
            $tamperContractPath,
            (($tamperedContract | ConvertTo-Json -Depth 12).Replace("`r`n", "`n") + "`n"),
            [Text.UTF8Encoding]::new($false))
        Invoke-FixtureGit $tamperFixtureRoot @('add', '--all') | Out-Null
        Invoke-FixtureGit $tamperFixtureRoot @('commit', '--quiet', '-m', 'tamper candidate baseline') | Out-Null
        $tamperSourceSha = Invoke-FixtureGit $tamperFixtureRoot @('rev-parse', 'HEAD')
        $tamperParentSha = Invoke-FixtureGit $tamperFixtureRoot @('rev-parse', 'HEAD^')

        $nonAncestorBaseline = Invoke-LiveIdentityValidator $tamperValidator $tamperFixtureRoot $tamperContractPath $tamperSourceSha $tamperParentSha
        Assert-Equal 1 $nonAncestorBaseline.ExitCode 'A real commit outside the candidate ancestry must fail.'
        Assert-Equal 'release_candidate_baseline_not_ancestor' $nonAncestorBaseline.Result.errorCode 'A non-ancestor candidate baseline must use the stable ancestry code.'

        $tamperedContract.contractId = 'tampered-contract-id'
        [IO.File]::WriteAllText(
            $tamperContractPath,
            (($tamperedContract | ConvertTo-Json -Depth 12).Replace("`r`n", "`n") + "`n"),
            [Text.UTF8Encoding]::new($false))
        Invoke-FixtureGit $tamperFixtureRoot @('add', '--all') | Out-Null
        Invoke-FixtureGit $tamperFixtureRoot @('commit', '--quiet', '-m', 'tamper candidate contract') | Out-Null
        $tamperedContractSourceSha = Invoke-FixtureGit $tamperFixtureRoot @('rev-parse', 'HEAD')
        $tamperedContractParentSha = Invoke-FixtureGit $tamperFixtureRoot @('rev-parse', 'HEAD^')
        $tamperedContractResult = Invoke-LiveIdentityValidator $tamperValidator $tamperFixtureRoot $tamperContractPath $tamperedContractSourceSha $tamperedContractParentSha
        Assert-Equal 1 $tamperedContractResult.ExitCode 'A structurally tampered attribution contract must fail.'
        Assert-Equal 'release_candidate_contract_mismatch' $tamperedContractResult.Result.errorCode 'A structural contract tamper must use the stable contract code.'
    }
    finally {
        if (Test-Path -LiteralPath $tamperFixtureRoot) { Remove-Item -LiteralPath $tamperFixtureRoot -Recurse -Force }
    }
}
finally {
    if (Test-Path -LiteralPath $gitFixtureRoot) { Remove-Item -LiteralPath $gitFixtureRoot -Recurse -Force }
}

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

Write-Host 'RELEASE CANDIDATE IDENTITY: targeted checks passed'
