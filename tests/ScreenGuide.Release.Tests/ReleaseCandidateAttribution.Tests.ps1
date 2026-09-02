$ErrorActionPreference = 'Stop'

$sourceRepoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$fixtureRoot = Join-Path ([IO.Path]::GetTempPath()) ('yuanshu-candidate-attribution-' + [Guid]::NewGuid().ToString('N'))
$releaseVersion = '0.7.0'
$sourceSha = '1111111111111111111111111111111111111111'

function Assert-True([bool]$condition, [string]$message) {
    if (-not $condition) { throw $message }
}

function Assert-Equal($expected, $actual, [string]$message) {
    if ($expected -cne $actual) { throw "$message Expected=[$expected] Actual=[$actual]" }
}

function Write-Utf8Lf([string]$path, [string]$text) {
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($path)) | Out-Null
    [IO.File]::WriteAllText($path, $text.Replace("`r`n", "`n"), [Text.UTF8Encoding]::new($false))
}

function Write-Json([string]$path, $value) {
    Write-Utf8Lf $path (($value | ConvertTo-Json -Depth 30).Replace("`r`n", "`n") + "`n")
}

function Get-Sha([string]$path) {
    return (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToUpperInvariant()
}

function New-LicenseComponent([string]$componentId, [string]$version, [string]$scope, [string]$kind, [string]$payloadId, [string]$path, [string]$sha) {
    $component = [ordered]@{
        componentId = $componentId
        version = $version
        artifactScope = $scope
        sourceBinding = [ordered]@{
            kind = 'fixture'
            reference = 'fixture/' + $componentId
            path = $path
            hashProfile = 'UTF8_NO_BOM_LF_V1'
            sha256 = $sha
            rawSha256 = $sha
            sourceUrls = @('PROJECT_AUTHORED')
        }
        status = [ordered]@{ source = 'VERIFIED'; bundling = 'VERIFIED'; attribution = 'VERIFIED'; notice = 'VERIFIED' }
        licenseNoticeFiles = @([ordered]@{
            kind = 'NOTICE'
            path = $path
            hashProfile = 'UTF8_NO_BOM_LF_V1'
            sha256 = $sha
            rawSha256 = $sha
            sourceUrls = @('PROJECT_AUTHORED')
        })
    }
    if (-not [string]::IsNullOrWhiteSpace($kind)) {
        $component.payloadSourceKind = $kind
        $component.payloadComponentId = $payloadId
    }
    return $component
}

function Invoke-Generator([string]$publishRoot, [string]$outputRoot, [string]$contractPath, [string]$nugetRoot) {
    $generator = Join-Path $fixtureRoot 'scripts\New-ReleaseCandidateAttribution.ps1'
    $previousPreference = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $output = @(& powershell.exe -NoProfile -ExecutionPolicy Bypass -File $generator `
            -RepositoryRoot $fixtureRoot `
            -PublishRoot $publishRoot `
            -AttributionContractPath $contractPath `
            -ExpectedSourceSha $sourceSha `
            -ReleaseVersion $releaseVersion `
            -OutputRoot $outputRoot `
            -NuGetPackagesRoot $nugetRoot 2>&1)
        $exitCode = $LASTEXITCODE
    }
    finally { $ErrorActionPreference = $previousPreference }
    return [pscustomobject]@{ ExitCode = $exitCode; Raw = ($output -join "`n"); Result = (($output[-1] | Out-String) | ConvertFrom-Json) }
}

try {
    $scriptsRoot = Join-Path $fixtureRoot 'scripts'
    $bundleRoot = Join-Path $fixtureRoot 'distribution\licenses'
    $candidateDefinitionRoot = Join-Path $fixtureRoot 'distribution\release-candidates\v0.7.0'
    $publishRoot = Join-Path $fixtureRoot 'publish'
    $nugetRoot = Join-Path $fixtureRoot 'nuget'
    [IO.Directory]::CreateDirectory($scriptsRoot) | Out-Null
    [IO.Directory]::CreateDirectory($bundleRoot) | Out-Null
    [IO.Directory]::CreateDirectory($candidateDefinitionRoot) | Out-Null
    [IO.Directory]::CreateDirectory($publishRoot) | Out-Null
    Copy-Item -LiteralPath (Join-Path $sourceRepoRoot 'scripts\New-ReleaseCandidateAttribution.ps1') -Destination $scriptsRoot
    Copy-Item -LiteralPath (Join-Path $sourceRepoRoot 'scripts\New-DistributionNoticeIndex.ps1') -Destination $scriptsRoot

    $projectNotice = Join-Path $bundleRoot 'files\project\NOTICE.txt'
    $packageNotice = Join-Path $bundleRoot 'files\package-alpha\LICENSE.txt'
    $installerNotice = Join-Path $bundleRoot 'files\installer\LICENSE.txt'
    Write-Utf8Lf $projectNotice "fixture project notice`n"
    Write-Utf8Lf $packageNotice "fixture package license`n"
    Write-Utf8Lf $installerNotice "fixture installer license`n"
    Write-Utf8Lf (Join-Path $bundleRoot 'notice-index.json') "{}`n"
    Write-Utf8Lf (Join-Path $bundleRoot 'THIRD-PARTY-NOTICES.txt') "fixture old notice`n"

    $components = @(
        (New-LicenseComponent 'payload-build-ScreenGuide.DesktopClient' '0.6.0' 'installedPayload' 'build' 'ScreenGuide.DesktopClient' 'files/project/NOTICE.txt' (Get-Sha $projectNotice)),
        (New-LicenseComponent 'payload-build-ScreenGuide.DesktopHost' '0.6.0' 'installedPayload' 'build' 'ScreenGuide.DesktopHost' 'files/project/NOTICE.txt' (Get-Sha $projectNotice)),
        (New-LicenseComponent 'payload-project-ScreenGuide.Core' '0.6.0' 'installedPayload' 'project' 'ScreenGuide.Core' 'files/project/NOTICE.txt' (Get-Sha $projectNotice)),
        (New-LicenseComponent 'payload-project-runtime-prompts' '0.6.0' 'installedPayload' 'project' 'runtime-prompts' 'files/project/NOTICE.txt' (Get-Sha $projectNotice)),
        (New-LicenseComponent 'payload-package-Package.Alpha' '1.0.0' 'installedPayload' 'package' 'Package.Alpha' 'files/package-alpha/LICENSE.txt' (Get-Sha $packageNotice)),
        (New-LicenseComponent 'fixture-installer' '1.0.0' 'installerContainer' $null $null 'files/installer/LICENSE.txt' (Get-Sha $installerNotice))
    )
    $components[5].containerEntry = 'installer/fixture'
    $baseManifest = [ordered]@{
        schemaVersion = 1
        bundleId = 'fixture-v0.6.0'
        releaseVersion = '0.6.0'
        bundleStatus = 'VERIFIED'
        payloadManifestHashProfile = 'UTF8_NO_BOM_LF_V1'
        payloadManifestSha256 = '0' * 64
        noticeIndex = [ordered]@{ path = 'notice-index.json'; hashProfile = 'UTF8_NO_BOM_LF_V1'; sha256 = '0' * 64 }
        components = $components
        exclusions = [ordered]@{ packageIds = @(); pathPrefixes = @() }
        rootNotice = [ordered]@{ path = 'THIRD-PARTY-NOTICES.txt'; hashProfile = 'UTF8_NO_BOM_LF_V1'; sha256 = '0' * 64; rawSha256 = '0' * 64 }
    }
    $baseManifestPath = Join-Path $bundleRoot 'bundle-manifest.json'
    Write-Json $baseManifestPath $baseManifest

    $candidateNoticePath = Join-Path $candidateDefinitionRoot 'PROPRIETARY-NOTICE.txt'
    Write-Utf8Lf $candidateNoticePath "fixture V0.7.0 candidate notice`n"
    $contract = [ordered]@{
        schemaVersion = 1
        contractId = 'fixture-v0.7.0'
        candidateStatus = 'INTERNAL_CANDIDATE_ONLY'
        releaseVersion = $releaseVersion
        baselineParentSha = '2' * 40
        generationMode = 'PUBLISH_OUTPUT_EXACT_SOURCE_MAPPING_V1'
        baseBundleManifest = [ordered]@{ path = 'distribution/licenses/bundle-manifest.json'; rawSha256 = Get-Sha $baseManifestPath }
        candidateNotice = [ordered]@{
            sourcePath = 'distribution/release-candidates/v0.7.0/PROPRIETARY-NOTICE.txt'
            bundlePath = 'files/yuanshu-v0.7.0-candidate/PROPRIETARY-NOTICE.txt'
            hashProfile = 'UTF8_NO_BOM_LF_V1'
            sha256 = Get-Sha $candidateNoticePath
        }
        forbiddenPayloadManifestPaths = @('docs/baselines/V0.6.0_STAGE4_C0_PAYLOAD_ATTRIBUTION.json')
        expectedProjectAndBuildComponentIds = @(
            'payload-build-ScreenGuide.DesktopClient',
            'payload-build-ScreenGuide.DesktopHost',
            'payload-project-ScreenGuide.Core',
            'payload-project-runtime-prompts'
        )
        expectedPackageComponentCount = 1
        expectedInstallerContainerComponentIds = @('fixture-installer')
    }
    $contractPath = Join-Path $candidateDefinitionRoot 'attribution-contract.json'
    Write-Json $contractPath $contract

    Write-Utf8Lf (Join-Path $publishRoot 'ScreenGuide.Core.dll') 'fixture project binary'
    Write-Utf8Lf (Join-Path $publishRoot 'ScreenGuide.DesktopClient.exe') 'fixture apphost'
    $deps = [ordered]@{ libraries = [ordered]@{ 'ScreenGuide.Core/0.7.0' = [ordered]@{ type = 'project' } } }
    Write-Json (Join-Path $publishRoot 'ScreenGuide.DesktopClient.deps.json') $deps
    Write-Json (Join-Path $publishRoot 'ScreenGuide.DesktopHost.deps.json') $deps
    Write-Utf8Lf (Join-Path $fixtureRoot 'prompts\runtime\fixture.md') "fixture prompt`n"
    Write-Utf8Lf (Join-Path $publishRoot 'prompts\runtime\fixture.md') "fixture prompt`n"
    $packageSource = Join-Path $nugetRoot 'package.alpha\1.0.0\lib\net10.0\Package.Alpha.dll'
    Write-Utf8Lf $packageSource 'fixture package binary'
    Write-Utf8Lf (Join-Path $publishRoot 'Package.Alpha.dll') 'fixture package binary'

    $positive = Invoke-Generator $publishRoot (Join-Path $fixtureRoot 'artifacts\positive') $contractPath $nugetRoot
    Assert-Equal 0 $positive.ExitCode ('Exact candidate attribution fixture must pass. Raw=' + [string]$positive.Raw)
    Assert-True $positive.Result.passed 'Exact candidate attribution result must report passed=true.'
    Assert-Equal 6 ([int]$positive.Result.details.publishFileCount) 'Every fixture payload must be mapped.'
    $payload = Get-Content -Raw -LiteralPath ([string]$positive.Result.details.payloadManifestPath) | ConvertFrom-Json
    Assert-Equal $sourceSha ([string]$payload.sourceCommit) 'Generated payload attribution must bind exact source SHA.'
    Assert-Equal $releaseVersion ([string]$payload.releaseVersion) 'Generated payload attribution must bind candidate version.'
    Assert-Equal 6 @($payload.records).Count 'Generated payload attribution must contain every publish file.'
    $generatedBundle = Get-Content -Raw -LiteralPath ([string]$positive.Result.details.candidateBundleManifestPath) | ConvertFrom-Json
    Assert-Equal $releaseVersion ([string]$generatedBundle.releaseVersion) 'Generated bundle must use candidate release version.'
    Assert-True (([string]$generatedBundle.components[0].sourceBinding.reference).Contains($sourceSha)) 'Candidate project source binding must include exact source SHA.'

    Write-Utf8Lf (Join-Path $publishRoot 'Package.Alpha.dll') 'tampered package binary'
    $tampered = Invoke-Generator $publishRoot (Join-Path $fixtureRoot 'artifacts\tampered') $contractPath $nugetRoot
    Assert-Equal 1 $tampered.ExitCode 'Unmapped/tampered payload must fail.'
    Assert-Equal 'release_candidate_payload_source_unmapped' ([string]$tampered.Result.errorCode) 'Tampered payload must use the stable fail-closed code.'

    Write-Host 'RELEASE CANDIDATE ATTRIBUTION: 11 targeted checks passed'
}
finally {
    if (Test-Path -LiteralPath $fixtureRoot) { Remove-Item -LiteralPath $fixtureRoot -Recurse -Force }
}
