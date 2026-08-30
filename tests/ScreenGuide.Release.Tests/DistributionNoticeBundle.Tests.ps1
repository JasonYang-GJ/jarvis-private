$ErrorActionPreference = 'Stop'

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$validator = Join-Path $repoRoot 'scripts\Test-DistributionNoticeBundle.ps1'
$testRoot = Join-Path ([IO.Path]::GetTempPath()) ('screen-guide-notice-tests-' + [Guid]::NewGuid().ToString('N'))

function Write-Utf8Json([string]$path, $value) {
    $json = $value | ConvertTo-Json -Depth 20
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($path)) | Out-Null
    [IO.File]::WriteAllText($path, ($json.Replace("`r`n", "`n") + "`n"), [Text.UTF8Encoding]::new($false))
}

function Get-Sha256([string]$path) {
    $bytes = [IO.File]::ReadAllBytes($path)
    $offset = if ($bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF) { 3 } else { 0 }
    $text = [Text.UTF8Encoding]::new($false, $true).GetString($bytes, $offset, $bytes.Length - $offset)
    $normalizedBytes = [Text.UTF8Encoding]::new($false).GetBytes($text.Replace("`r`n", "`n").Replace("`r", "`n"))
    $sha = [Security.Cryptography.SHA256]::Create()
    try { return ([BitConverter]::ToString($sha.ComputeHash($normalizedBytes))).Replace('-', '') }
    finally { $sha.Dispose() }
}

function Assert-True([bool]$condition, [string]$message) {
    if (-not $condition) { throw $message }
}

function Assert-Equal($expected, $actual, [string]$message) {
    if ($expected -ne $actual) { throw "$message Expected=[$expected] Actual=[$actual]" }
}

function New-PassFixture([string]$root) {
    $bundleRoot = Join-Path $root 'bundle'
    $licensePath = Join-Path $bundleRoot 'files\alpha\LICENSE.txt'
    $noticeIndexPath = Join-Path $bundleRoot 'notice-index.json'
    $manifestPath = Join-Path $bundleRoot 'bundle-manifest.json'
    $payloadPath = Join-Path $root 'payload-manifest.json'
    $includePath = Join-Path $root 'distribution-notice-files.iss'

    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($licensePath)) | Out-Null
    [IO.File]::WriteAllText($licensePath, "FAKE LICENSE FOR TESTS ONLY`n", [Text.UTF8Encoding]::new($false))

    $payload = [ordered]@{
        schemaVersion = 1
        records = @(
            [ordered]@{
                frozenPath = 'app/alpha.dll'
                size = 4
                sha256 = ('A' * 64)
                sourcePackage = [ordered]@{ packageId = 'alpha'; version = '1.0.0' }
            }
        )
    }
    Write-Utf8Json $payloadPath $payload

    $noticeIndex = [ordered]@{
        schemaVersion = 1
        records = @(
            [ordered]@{
                payloadPath = 'app/alpha.dll'
                artifactScope = 'installedPayload'
                componentId = 'alpha'
                version = '1.0.0'
                licenseNoticePaths = @('files/alpha/LICENSE.txt')
            },
            [ordered]@{
                containerEntry = 'installer/engine'
                artifactScope = 'installerContainer'
                componentId = 'beta-installer'
                version = '2.0.0'
                licenseNoticePaths = @('files/alpha/LICENSE.txt')
            }
        )
    }
    Write-Utf8Json $noticeIndexPath $noticeIndex

    $manifest = [ordered]@{
        schemaVersion = 1
        bundleId = 'fake-pass-bundle'
        releaseVersion = 'test'
        bundleStatus = 'VERIFIED'
        payloadManifestHashProfile = 'UTF8_NO_BOM_LF_V1'
        payloadManifestSha256 = (Get-Sha256 $payloadPath)
        noticeIndex = [ordered]@{
            path = 'notice-index.json'
            hashProfile = 'UTF8_NO_BOM_LF_V1'
            sha256 = (Get-Sha256 $noticeIndexPath)
        }
        components = @(
            [ordered]@{
                componentId = 'alpha'
                version = '1.0.0'
                artifactScope = 'installedPayload'
                payloadComponentId = 'alpha'
                sourceBinding = [ordered]@{
                    kind = 'test-fixture'
                    reference = 'fake-alpha-source@1.0.0'
                    sha256 = ('B' * 64)
                }
                status = [ordered]@{
                    source = 'VERIFIED'
                    bundling = 'VERIFIED'
                    attribution = 'VERIFIED'
                    notice = 'VERIFIED'
                }
                licenseNoticeFiles = @(
                    [ordered]@{
                        kind = 'LICENSE'
                        path = 'files/alpha/LICENSE.txt'
                        hashProfile = 'UTF8_NO_BOM_LF_V1'
                        sha256 = (Get-Sha256 $licensePath)
                    }
                )
            },
            [ordered]@{
                componentId = 'beta-installer'
                version = '2.0.0'
                artifactScope = 'installerContainer'
                payloadComponentId = 'beta-installer'
                sourceBinding = [ordered]@{
                    kind = 'test-fixture'
                    reference = 'fake-beta-source@2.0.0'
                    sha256 = ('C' * 64)
                }
                status = [ordered]@{
                    source = 'VERIFIED'
                    bundling = 'VERIFIED'
                    attribution = 'VERIFIED'
                    notice = 'VERIFIED'
                }
                licenseNoticeFiles = @(
                    [ordered]@{
                        kind = 'LICENSE'
                        path = 'files/alpha/LICENSE.txt'
                        hashProfile = 'UTF8_NO_BOM_LF_V1'
                        sha256 = (Get-Sha256 $licensePath)
                    }
                )
            }
        )
        remainingBlockers = @()
    }
    Write-Utf8Json $manifestPath $manifest

    return [pscustomobject]@{
        BundleRoot = $bundleRoot
        ManifestPath = $manifestPath
        PayloadPath = $payloadPath
        IncludePath = $includePath
    }
}

function Invoke-Validator($fixture) {
    $output = @(& powershell.exe -NoProfile -ExecutionPolicy Bypass -File $validator `
        -ManifestPath $fixture.ManifestPath `
        -BundleRoot $fixture.BundleRoot `
        -PayloadManifestPath $fixture.PayloadPath `
        -InnoIncludePath $fixture.IncludePath 2>&1)
    return [pscustomobject]@{
        ExitCode = $LASTEXITCODE
        Output = ($output -join "`n")
    }
}

function Read-Json([string]$path) {
    return Get-Content -Raw -LiteralPath $path | ConvertFrom-Json
}

function Assert-Failure($fixture, [string]$expectedCode, [string]$expectedBlocker) {
    $run = Invoke-Validator $fixture
    Assert-True ($run.ExitCode -ne 0) 'An incomplete or invalid bundle must fail.'
    $result = $run.Output | ConvertFrom-Json
    Assert-Equal $expectedCode $result.errorCode 'The failure code must be stable.'
    Assert-True (@($result.blockers) -contains $expectedBlocker) "Expected blocker [$expectedBlocker]."
    Assert-True (-not (Test-Path -LiteralPath $fixture.IncludePath)) 'Failure must not emit an Inno include.'
}

try {
    [IO.Directory]::CreateDirectory($testRoot) | Out-Null
    $fixture = New-PassFixture (Join-Path $testRoot 'pass')
    $run = Invoke-Validator $fixture

    Assert-Equal 0 $run.ExitCode 'A complete fake bundle must pass.'
    $result = $run.Output | ConvertFrom-Json
    Assert-True ([bool]$result.passed) 'The result must report passed=true.'
    Assert-Equal 1 $result.payloadCount 'The pass fixture must map one payload.'
    Assert-True (Test-Path -LiteralPath $fixture.IncludePath -PathType Leaf) 'PASS must emit the exact Inno include.'

    $include = Get-Content -Raw -LiteralPath $fixture.IncludePath
    Assert-True ($include.Contains('bundle-manifest.json')) 'The include must collect the manifest.'
    Assert-True ($include.Contains('notice-index.json')) 'The include must collect the notice index.'
    Assert-True ($include.Contains('files\alpha\LICENSE.txt')) 'The include must collect the exact fake license.'
    Assert-True (-not $include.Contains('*')) 'The include must not use wildcards.'
    Assert-True (-not $include.Contains('skipifsourcedoesntexist')) 'The include must not make license files optional.'

    Write-Host 'PASS fixture: passed'

    $missing = New-PassFixture (Join-Path $testRoot 'missing-file')
    Remove-Item -LiteralPath (Join-Path $missing.BundleRoot 'files\alpha\LICENSE.txt') -Force
    Assert-Failure $missing 'distribution_notice_bundle_incomplete' 'license_notice_file_missing:alpha'

    $hashMismatch = New-PassFixture (Join-Path $testRoot 'hash-mismatch')
    [IO.File]::AppendAllText((Join-Path $hashMismatch.BundleRoot 'files\alpha\LICENSE.txt'), 'changed', [Text.UTF8Encoding]::new($false))
    Assert-Failure $hashMismatch 'distribution_notice_bundle_incomplete' 'license_notice_hash_mismatch:alpha'

    $duplicate = New-PassFixture (Join-Path $testRoot 'duplicate-component')
    $duplicateManifest = Read-Json $duplicate.ManifestPath
    $duplicateManifest.components = @($duplicateManifest.components[0], $duplicateManifest.components[0])
    Write-Utf8Json $duplicate.ManifestPath $duplicateManifest
    Assert-Failure $duplicate 'distribution_notice_bundle_invalid' 'duplicate_or_unknown_component'

    $unknown = New-PassFixture (Join-Path $testRoot 'unknown-component')
    $unknownIndexPath = Join-Path $unknown.BundleRoot 'notice-index.json'
    $unknownIndex = Read-Json $unknownIndexPath
    $unknownIndex.records[0].componentId = 'unknown'
    Write-Utf8Json $unknownIndexPath $unknownIndex
    $unknownManifest = Read-Json $unknown.ManifestPath
    $unknownManifest.noticeIndex.sha256 = Get-Sha256 $unknownIndexPath
    Write-Utf8Json $unknown.ManifestPath $unknownManifest
    Assert-Failure $unknown 'distribution_notice_bundle_invalid' 'duplicate_unknown_or_unmapped_component'

    $partial = New-PassFixture (Join-Path $testRoot 'partial-status')
    $partialManifest = Read-Json $partial.ManifestPath
    $partialManifest.components[0].status.notice = 'PARTIAL'
    Write-Utf8Json $partial.ManifestPath $partialManifest
    Assert-Failure $partial 'distribution_notice_bundle_incomplete' 'component_status_not_verified:alpha:notice'

    $missingMapping = New-PassFixture (Join-Path $testRoot 'missing-mapping')
    $missingMappingIndexPath = Join-Path $missingMapping.BundleRoot 'notice-index.json'
    $missingMappingIndex = Read-Json $missingMappingIndexPath
    $missingMappingIndex.records = @()
    Write-Utf8Json $missingMappingIndexPath $missingMappingIndex
    $missingMappingManifest = Read-Json $missingMapping.ManifestPath
    $missingMappingManifest.noticeIndex.sha256 = Get-Sha256 $missingMappingIndexPath
    Write-Utf8Json $missingMapping.ManifestPath $missingMappingManifest
    Assert-Failure $missingMapping 'distribution_notice_bundle_incomplete' 'payload_notice_mapping_incomplete'

    $illegalPath = New-PassFixture (Join-Path $testRoot 'illegal-path')
    $illegalManifest = Read-Json $illegalPath.ManifestPath
    $illegalManifest.components[0].licenseNoticeFiles[0].path = '../outside.txt'
    Write-Utf8Json $illegalPath.ManifestPath $illegalManifest
    Assert-Failure $illegalPath 'distribution_notice_bundle_invalid' 'invalid_license_notice_layout'

    Write-Host 'FAIL-CLOSED fixtures: 7 passed'

    $currentInclude = Join-Path $testRoot 'current-repository\distribution-notice-files.iss'
    $sentinelInstaller = Join-Path $testRoot 'current-repository\release-output.exe'
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($sentinelInstaller)) | Out-Null
    [IO.File]::WriteAllText($sentinelInstaller, 'FAKE INSTALLER SENTINEL - DO NOT EXECUTE', [Text.UTF8Encoding]::new($false))
    $sentinelHashBefore = (Get-FileHash -Algorithm SHA256 -LiteralPath $sentinelInstaller).Hash
    $currentFixture = [pscustomobject]@{
        BundleRoot = Join-Path $repoRoot 'distribution\licenses'
        ManifestPath = Join-Path $repoRoot 'distribution\licenses\bundle-manifest.json'
        PayloadPath = Join-Path $repoRoot 'docs\baselines\V0.6.0_STAGE4_C0_PAYLOAD_ATTRIBUTION.json'
        IncludePath = $currentInclude
    }
    $currentRun = Invoke-Validator $currentFixture
    Assert-True ($currentRun.ExitCode -ne 0) 'The current real bundle must remain blocked.'
    $currentResult = $currentRun.Output | ConvertFrom-Json
    Assert-Equal 'distribution_notice_bundle_incomplete' $currentResult.errorCode 'The current bundle must use the stable incomplete code.'
    Assert-True (@($currentResult.blockers) -contains 'declared_blocker:exact_license_notice_files_not_bundled') 'The current blocker must identify missing exact files.'
    Assert-True (-not (Test-Path -LiteralPath $currentInclude)) 'The current blocked bundle must not emit an Inno include.'
    Assert-Equal $sentinelHashBefore (Get-FileHash -Algorithm SHA256 -LiteralPath $sentinelInstaller).Hash 'Validation must not mutate an existing installer output.'
    Assert-True (-not $currentRun.Output.Contains($repoRoot)) 'Structured gate evidence must not contain an absolute repository path.'

    $releaseScript = Get-Content -Raw -LiteralPath (Join-Path $repoRoot 'scripts\build-desktop-release.ps1')
    $gatePosition = $releaseScript.IndexOf('$distributionGateOutput = @(& powershell.exe', [StringComparison]::Ordinal)
    $restorePosition = $releaseScript.IndexOf('dotnet restore $solution', [StringComparison]::Ordinal)
    $resetPosition = $releaseScript.IndexOf('Reset-BuildDirectory $clientStage', [StringComparison]::Ordinal)
    $isccPosition = $releaseScript.IndexOf('& $iscc ', [StringComparison]::Ordinal)
    Assert-True ($gatePosition -ge 0) 'The release script must invoke the bundle validator.'
    Assert-True ($gatePosition -lt $restorePosition) 'The bundle gate must run before restore.'
    Assert-True ($gatePosition -lt $resetPosition) 'The bundle gate must run before publish directory mutation.'
    Assert-True ($gatePosition -lt $isccPosition) 'The bundle gate must run before installer compilation.'

    $iss = Get-Content -Raw -LiteralPath (Join-Path $repoRoot 'installer\ScreenGuideDesktop.iss')
    Assert-True ($iss.Contains('#include "..\artifacts\staging\distribution-notice-files.iss"')) 'Inno must require the validated generated include.'
    Assert-True (-not $iss.Contains('distribution-notice-files.iss"; Flags: skipifsourcedoesntexist')) 'The distribution include must never be optional.'

    Write-Host 'CURRENT bundle: blocked before installer mutation'
    Write-Host 'RELEASE wiring: passed'
    Write-Host 'TOTAL: 10 targeted cases passed'
}
finally {
    $fullTestRoot = [IO.Path]::GetFullPath($testRoot)
    $tempPrefix = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
    if ($fullTestRoot.StartsWith($tempPrefix, [StringComparison]::OrdinalIgnoreCase) -and
        [IO.Path]::GetFileName($fullTestRoot).StartsWith('screen-guide-notice-tests-', [StringComparison]::Ordinal)) {
        if (Test-Path -LiteralPath $fullTestRoot) { Remove-Item -LiteralPath $fullTestRoot -Recurse -Force }
    }
}
