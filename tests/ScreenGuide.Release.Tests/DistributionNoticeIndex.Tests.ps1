$ErrorActionPreference = 'Stop'

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$generator = Join-Path $repoRoot 'scripts\New-DistributionNoticeIndex.ps1'
$testRoot = Join-Path ([IO.Path]::GetTempPath()) ('screen-guide-notice-index-tests-' + [Guid]::NewGuid().ToString('N'))

function Write-Utf8Json([string]$path, $value) {
    $json = $value | ConvertTo-Json -Depth 30
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($path)) | Out-Null
    [IO.File]::WriteAllText($path, ($json.Replace("`r`n", "`n") + "`n"), [Text.UTF8Encoding]::new($false))
}

function Assert-True([bool]$condition, [string]$message) {
    if (-not $condition) { throw $message }
}

function Assert-Equal($expected, $actual, [string]$message) {
    if ($expected -ne $actual) { throw "$message Expected=[$expected] Actual=[$actual]" }
}

function Read-Json([string]$path) {
    return Get-Content -Raw -LiteralPath $path | ConvertFrom-Json
}

function New-GeneratorFixture([string]$root) {
    $payloadPath = Join-Path $root 'payload.json'
    $manifestPath = Join-Path $root 'bundle-manifest.json'
    $approvedRoot = Join-Path $root 'approved'
    $outputPath = Join-Path $approvedRoot 'notice-index.json'
    $rootNoticePath = Join-Path $approvedRoot 'THIRD-PARTY-NOTICES.txt'

    $payload = [ordered]@{
        schemaVersion = 1
        records = @(
            [ordered]@{
                artifactScope = 'installedPayload'
                frozenPath = 'zeta.dll'
                sourceClassification = 'package'
                sourcePackage = [ordered]@{ packageId = 'zeta'; version = '1.0.0' }
            },
            [ordered]@{
                artifactScope = 'installedPayload'
                frozenPath = 'alpha.dll'
                sourceClassification = 'project-owned'
                sourceProject = [ordered]@{ projectId = 'alpha-project' }
            },
            [ordered]@{
                artifactScope = 'installedPayload'
                frozenPath = 'middle.deps.json'
                sourceClassification = 'build-derived'
                sourceBuild = [ordered]@{ projectId = 'middle-project' }
            }
        )
    }
    Write-Utf8Json $payloadPath $payload

    $components = @(
        [ordered]@{ componentId = 'component-alpha'; version = 'test'; artifactScope = 'installedPayload'; payloadSourceKind = 'project'; payloadComponentId = 'alpha-project'; licenseNoticeFiles = @([ordered]@{ path = 'files/alpha/NOTICE.txt' }) },
        [ordered]@{ componentId = 'component-middle'; version = 'test'; artifactScope = 'installedPayload'; payloadSourceKind = 'build'; payloadComponentId = 'middle-project'; licenseNoticeFiles = @([ordered]@{ path = 'files/middle/NOTICE.txt' }) },
        [ordered]@{ componentId = 'component-zeta'; version = '1.0.0'; artifactScope = 'installedPayload'; payloadSourceKind = 'package'; payloadComponentId = 'zeta'; licenseNoticeFiles = @([ordered]@{ path = 'files/zeta/LICENSE.txt' }) },
        [ordered]@{ componentId = 'component-installer'; version = '2.0.0'; artifactScope = 'installerContainer'; containerEntry = 'installer/engine'; licenseNoticeFiles = @([ordered]@{ path = 'files/installer/LICENSE.txt' }) }
    )
    $manifest = [ordered]@{
        schemaVersion = 1
        releaseVersion = 'test'
        bundleStatus = 'BLOCKED'
        components = $components
        exclusions = [ordered]@{
            packageIds = @('zeta.runtime.non-target')
            pathPrefixes = @('models/voice-model/')
        }
    }
    Write-Utf8Json $manifestPath $manifest
    [IO.Directory]::CreateDirectory($approvedRoot) | Out-Null

    return [pscustomobject]@{
        Root = $root
        PayloadPath = $payloadPath
        ManifestPath = $manifestPath
        ApprovedRoot = $approvedRoot
        OutputPath = $outputPath
        RootNoticePath = $rootNoticePath
    }
}

function Invoke-Generator($fixture, [string]$outputPath) {
    $output = @(& powershell.exe -NoProfile -ExecutionPolicy Bypass -File $generator `
        -PayloadManifestPath $fixture.PayloadPath `
        -BundleManifestPath $fixture.ManifestPath `
        -OutputPath $outputPath `
        -RootNoticeOutputPath $fixture.RootNoticePath `
        -ApprovedOutputRoot $fixture.ApprovedRoot 2>&1)
    return [pscustomobject]@{
        ExitCode = $LASTEXITCODE
        Output = ($output -join "`n")
    }
}

function Assert-GeneratorFailure($fixture, [string]$outputPath, [string]$expectedCode, [string]$expectedBlocker) {
    $run = Invoke-Generator $fixture $outputPath
    Assert-True ($run.ExitCode -ne 0) 'Invalid generation input must fail.'
    $result = $run.Output | ConvertFrom-Json
    Assert-Equal $expectedCode $result.errorCode 'Generator failure code must be stable.'
    Assert-True (@($result.blockers) -contains $expectedBlocker) "Expected generator blocker [$expectedBlocker]."
    Assert-True (-not (Test-Path -LiteralPath $outputPath)) 'Failure must not emit an index.'
    Assert-True (-not (Test-Path -LiteralPath $fixture.RootNoticePath)) 'Failure must not emit a root notice.'
}

try {
    [IO.Directory]::CreateDirectory($testRoot) | Out-Null
    $fixture = New-GeneratorFixture (Join-Path $testRoot 'deterministic')
    $first = Invoke-Generator $fixture $fixture.OutputPath

    Assert-Equal 0 $first.ExitCode 'A complete explicit catalog must generate an index.'
    $result = $first.Output | ConvertFrom-Json
    Assert-True ([bool]$result.passed) 'Generator must report passed=true.'
    Assert-Equal 3 $result.payloadCount 'All payload rows must be mapped.'
    Assert-Equal 1 $result.excludedPackageIdsVerifiedCount 'The explicit non-target package exclusion must be verified.'
    Assert-Equal 1 $result.excludedPathPrefixesVerifiedCount 'The explicit voice-model prefix exclusion must be verified.'

    $index = Get-Content -Raw -LiteralPath $fixture.OutputPath | ConvertFrom-Json
    Assert-Equal 4 @($index.records).Count 'Generated index must contain every payload row and explicit installer-container entry.'
    Assert-Equal 'alpha.dll' $index.records[0].payloadPath 'Records must use deterministic ordinal path order.'
    Assert-Equal 'component-alpha' $index.records[0].componentId 'Project source must use its explicit catalog component.'
    Assert-Equal 'component-middle' $index.records[1].componentId 'Build source must use its explicit catalog component.'
    Assert-Equal 'component-zeta' $index.records[2].componentId 'Package source must use its explicit catalog component.'
    Assert-Equal 'files/alpha/NOTICE.txt' $index.records[0].licenseNoticePaths[0] 'Generator must copy only the component explicit NOTICE mapping.'
    Assert-Equal 'installerContainer' $index.records[3].artifactScope 'Installer entry must retain its scope.'
    Assert-Equal 'installer/engine' $index.records[3].containerEntry 'Installer entry must retain its exact container path.'
    Assert-Equal 'files/installer/LICENSE.txt' $index.records[3].licenseNoticePaths[0] 'Installer entry must use only its component material.'
    Assert-True (Test-Path -LiteralPath $fixture.RootNoticePath -PathType Leaf) 'Generator must emit the human-readable root notice.'
    $rootNotice = Get-Content -Raw -LiteralPath $fixture.RootNoticePath
    Assert-True ($rootNotice.Contains('component-alpha | test | installedPayload | files/alpha/NOTICE.txt')) 'Root notice must index component/version/scope/material without copying material text.'

    $firstBytes = [IO.File]::ReadAllBytes($fixture.OutputPath)
    $firstNoticeBytes = [IO.File]::ReadAllBytes($fixture.RootNoticePath)
    $secondPath = Join-Path $fixture.ApprovedRoot 'notice-index-second.json'
    $fixture.RootNoticePath = Join-Path $fixture.ApprovedRoot 'THIRD-PARTY-NOTICES-second.txt'
    $second = Invoke-Generator $fixture $secondPath
    Assert-Equal 0 $second.ExitCode 'Repeated generation must pass.'
    Assert-True ([Linq.Enumerable]::SequenceEqual($firstBytes, [IO.File]::ReadAllBytes($secondPath))) 'Repeated generation must be byte-for-byte deterministic.'
    Assert-True ([Linq.Enumerable]::SequenceEqual($firstNoticeBytes, [IO.File]::ReadAllBytes($fixture.RootNoticePath))) 'Repeated root notice generation must be byte-for-byte deterministic.'

    Write-Host 'GENERATOR deterministic fixture: passed'

    $missingCatalog = New-GeneratorFixture (Join-Path $testRoot 'missing-catalog')
    $missingManifest = Read-Json $missingCatalog.ManifestPath
    $missingManifest.components = @($missingManifest.components | Where-Object { $_.componentId -ne 'component-zeta' })
    Write-Utf8Json $missingCatalog.ManifestPath $missingManifest
    Assert-GeneratorFailure $missingCatalog $missingCatalog.OutputPath 'distribution_notice_catalog_missing' 'catalog_missing'

    $conflictingCatalog = New-GeneratorFixture (Join-Path $testRoot 'conflicting-catalog')
    $conflictingManifest = Read-Json $conflictingCatalog.ManifestPath
    $conflictingManifest.components = @($conflictingManifest.components) + @($conflictingManifest.components[0])
    Write-Utf8Json $conflictingCatalog.ManifestPath $conflictingManifest
    Assert-GeneratorFailure $conflictingCatalog $conflictingCatalog.OutputPath 'distribution_notice_catalog_conflict' 'catalog_conflict'

    $exclusionViolation = New-GeneratorFixture (Join-Path $testRoot 'exclusion-violation')
    $excludedPayload = Read-Json $exclusionViolation.PayloadPath
    $excludedPayload.records[0].sourcePackage.packageId = 'zeta.runtime.non-target'
    Write-Utf8Json $exclusionViolation.PayloadPath $excludedPayload
    Assert-GeneratorFailure $exclusionViolation $exclusionViolation.OutputPath 'distribution_notice_exclusion_violated' 'excluded_package_present'

    $outputEscape = New-GeneratorFixture (Join-Path $testRoot 'output-escape')
    $outsideOutput = Join-Path $outputEscape.Root 'outside\notice-index.json'
    Assert-GeneratorFailure $outputEscape $outsideOutput 'distribution_notice_output_out_of_bounds' 'output_out_of_bounds'

    $reparseOutput = New-GeneratorFixture (Join-Path $testRoot 'reparse-output')
    $reparseTarget = Join-Path $reparseOutput.Root 'reparse-target'
    [IO.Directory]::CreateDirectory($reparseTarget) | Out-Null
    $reparseLink = Join-Path $reparseOutput.ApprovedRoot 'linked'
    New-Item -ItemType Junction -Path $reparseLink -Target $reparseTarget | Out-Null
    $reparseOutputPath = Join-Path $reparseLink 'notice-index.json'
    Assert-GeneratorFailure $reparseOutput $reparseOutputPath 'distribution_notice_reparse_point' 'reparse_point_rejected'

    Write-Host 'GENERATOR fail-closed fixtures: 5 passed'
}
finally {
    $fullTestRoot = [IO.Path]::GetFullPath($testRoot)
    $tempPrefix = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
    if ($fullTestRoot.StartsWith($tempPrefix, [StringComparison]::OrdinalIgnoreCase) -and
        [IO.Path]::GetFileName($fullTestRoot).StartsWith('screen-guide-notice-index-tests-', [StringComparison]::Ordinal)) {
        if (Test-Path -LiteralPath $fullTestRoot) { Remove-Item -LiteralPath $fullTestRoot -Recurse -Force }
    }
}
