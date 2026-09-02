param(
    [Parameter(Mandatory = $true)]
    [string]$RepositoryRoot,

    [Parameter(Mandatory = $true)]
    [string]$PublishRoot,

    [Parameter(Mandatory = $true)]
    [string]$AttributionContractPath,

    [Parameter(Mandatory = $true)]
    [string]$ExpectedSourceSha,

    [Parameter(Mandatory = $true)]
    [string]$ReleaseVersion,

    [Parameter(Mandatory = $true)]
    [string]$OutputRoot,

    [string]$NuGetPackagesRoot
)

$ErrorActionPreference = 'Stop'

function Complete([bool]$passed, [string]$errorCode, $details, [int]$exitCode) {
    [ordered]@{
        passed = $passed
        errorCode = $errorCode
        details = $details
    } | ConvertTo-Json -Depth 10 -Compress | Write-Output
    exit $exitCode
}

function Get-RawSha256([string]$path) {
    return (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToUpperInvariant()
}

function Get-CanonicalUtf8LfSha256([string]$path) {
    $bytes = [IO.File]::ReadAllBytes($path)
    $offset = if ($bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF) { 3 } else { 0 }
    $text = [Text.UTF8Encoding]::new($false, $true).GetString($bytes, $offset, $bytes.Length - $offset)
    $normalized = $text.Replace("`r`n", "`n").Replace("`r", "`n")
    $sha = [Security.Cryptography.SHA256]::Create()
    try {
        return ([BitConverter]::ToString($sha.ComputeHash([Text.UTF8Encoding]::new($false).GetBytes($normalized)))).Replace('-', '')
    }
    finally { $sha.Dispose() }
}

function Write-Utf8LfJson([string]$path, $value) {
    $json = ($value | ConvertTo-Json -Depth 40).Replace("`r`n", "`n") + "`n"
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($path)) | Out-Null
    [IO.File]::WriteAllText($path, $json, [Text.UTF8Encoding]::new($false))
}

function Assert-PathInside([string]$root, [string]$path, [string]$errorCode) {
    $resolvedRoot = [IO.Path]::GetFullPath($root).TrimEnd('\') + '\'
    $resolvedPath = [IO.Path]::GetFullPath($path)
    if (-not $resolvedPath.StartsWith($resolvedRoot, [StringComparison]::OrdinalIgnoreCase)) {
        throw [InvalidOperationException]::new($errorCode)
    }
}

function Get-RelativePath([string]$root, [string]$path) {
    $resolvedRoot = [IO.Path]::GetFullPath($root).TrimEnd('\') + '\'
    $resolvedPath = [IO.Path]::GetFullPath($path)
    if (-not $resolvedPath.StartsWith($resolvedRoot, [StringComparison]::OrdinalIgnoreCase)) {
        throw [InvalidOperationException]::new('release_candidate_relative_path_out_of_bounds')
    }
    return $resolvedPath.Substring($resolvedRoot.Length)
}

function Assert-SetEqual([string[]]$expected, [string[]]$actual, [string]$errorCode) {
    $difference = @(Compare-Object -ReferenceObject @($expected | Sort-Object) -DifferenceObject @($actual | Sort-Object) -CaseSensitive)
    if ($difference.Count -ne 0) { throw [InvalidOperationException]::new($errorCode) }
}

try {
    if ($ReleaseVersion -cne '0.7.0' -or $ExpectedSourceSha -notmatch '^[0-9a-f]{40}$') {
        throw [InvalidOperationException]::new('release_candidate_attribution_identity_invalid')
    }

    $repoRoot = [IO.Path]::GetFullPath($RepositoryRoot).TrimEnd('\')
    $publishRoot = [IO.Path]::GetFullPath($PublishRoot).TrimEnd('\')
    $outputRoot = [IO.Path]::GetFullPath($OutputRoot).TrimEnd('\')
    $artifactsRoot = [IO.Path]::GetFullPath((Join-Path $repoRoot 'artifacts')).TrimEnd('\')
    Assert-PathInside $artifactsRoot $outputRoot 'release_candidate_attribution_output_out_of_bounds'
    if (-not (Test-Path -LiteralPath $publishRoot -PathType Container)) {
        throw [InvalidOperationException]::new('release_candidate_publish_missing')
    }

    try { $contract = Get-Content -Raw -LiteralPath $AttributionContractPath | ConvertFrom-Json }
    catch { throw [InvalidOperationException]::new('release_candidate_contract_invalid') }
    if ($contract.schemaVersion -ne 1 -or
        [string]$contract.releaseVersion -cne $ReleaseVersion -or
        [string]$contract.generationMode -cne 'PUBLISH_OUTPUT_EXACT_SOURCE_MAPPING_V1') {
        throw [InvalidOperationException]::new('release_candidate_contract_mismatch')
    }
    if (@($contract.forbiddenPayloadManifestPaths) -contains ([string]$contract.baseBundleManifest.path)) {
        throw [InvalidOperationException]::new('release_candidate_legacy_manifest_fallback')
    }

    $baseBundleManifestPath = [IO.Path]::GetFullPath((Join-Path $repoRoot ([string]$contract.baseBundleManifest.path)))
    $baseBundleRoot = [IO.Path]::GetDirectoryName($baseBundleManifestPath)
    $candidateNoticeSource = [IO.Path]::GetFullPath((Join-Path $repoRoot ([string]$contract.candidateNotice.sourcePath)))
    if ((Get-RawSha256 $baseBundleManifestPath) -cne ([string]$contract.baseBundleManifest.rawSha256).ToUpperInvariant()) {
        throw [InvalidOperationException]::new('release_candidate_base_bundle_hash_mismatch')
    }
    if ([string]$contract.candidateNotice.hashProfile -cne 'UTF8_NO_BOM_LF_V1' -or
        (Get-CanonicalUtf8LfSha256 $candidateNoticeSource) -cne ([string]$contract.candidateNotice.sha256).ToUpperInvariant()) {
        throw [InvalidOperationException]::new('release_candidate_notice_hash_mismatch')
    }
    $baseBundle = Get-Content -Raw -LiteralPath $baseBundleManifestPath | ConvertFrom-Json

    $projectAndBuildComponents = @($baseBundle.components | Where-Object { [string]$_.payloadSourceKind -in @('project', 'build') })
    Assert-SetEqual @($contract.expectedProjectAndBuildComponentIds) @($projectAndBuildComponents | ForEach-Object { [string]$_.componentId }) 'release_candidate_component_contract_mismatch'
    $packageComponents = @($baseBundle.components | Where-Object { [string]$_.payloadSourceKind -eq 'package' })
    if ($packageComponents.Count -ne [int]$contract.expectedPackageComponentCount) {
        throw [InvalidOperationException]::new('release_candidate_package_catalog_mismatch')
    }
    $containerComponents = @($baseBundle.components | Where-Object { [string]$_.artifactScope -eq 'installerContainer' })
    Assert-SetEqual @($contract.expectedInstallerContainerComponentIds) @($containerComponents | ForEach-Object { [string]$_.componentId }) 'release_candidate_container_contract_mismatch'

    $publishFiles = @(Get-ChildItem -LiteralPath $publishRoot -File -Recurse | Sort-Object FullName)
    if ($publishFiles.Count -eq 0) { throw [InvalidOperationException]::new('release_candidate_publish_empty') }

    $projectComponentsById = [Collections.Generic.Dictionary[string, object]]::new([StringComparer]::Ordinal)
    $buildComponentsById = [Collections.Generic.Dictionary[string, object]]::new([StringComparer]::Ordinal)
    foreach ($component in $projectAndBuildComponents) {
        $id = [string]$component.payloadComponentId
        if ([string]$component.payloadSourceKind -eq 'project') { $projectComponentsById.Add($id, $component) }
        else { $buildComponentsById.Add($id, $component) }
    }

    $depsLibraries = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($depsName in @('ScreenGuide.DesktopClient.deps.json', 'ScreenGuide.DesktopHost.deps.json')) {
        $depsPath = Join-Path $publishRoot $depsName
        if (-not (Test-Path -LiteralPath $depsPath -PathType Leaf)) {
            throw [InvalidOperationException]::new('release_candidate_deps_missing')
        }
        $deps = Get-Content -Raw -LiteralPath $depsPath | ConvertFrom-Json
        foreach ($property in $deps.libraries.PSObject.Properties) { [void]$depsLibraries.Add([string]$property.Name) }
    }

    if ([string]::IsNullOrWhiteSpace($NuGetPackagesRoot)) {
        $NuGetPackagesRoot = if (-not [string]::IsNullOrWhiteSpace($env:NUGET_PACKAGES)) {
            $env:NUGET_PACKAGES
        } else {
            Join-Path ([Environment]::GetFolderPath('UserProfile')) '.nuget\packages'
        }
    }
    $nugetRoot = [IO.Path]::GetFullPath($NuGetPackagesRoot).TrimEnd('\')
    if (-not (Test-Path -LiteralPath $nugetRoot -PathType Container)) {
        throw [InvalidOperationException]::new('release_candidate_nuget_cache_missing')
    }

    $neededLeafNames = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($file in $publishFiles) { [void]$neededLeafNames.Add($file.Name) }
    $packageHashIndex = [Collections.Generic.Dictionary[string, object]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($component in $packageComponents) {
        $packageId = [string]$component.payloadComponentId
        $packageVersion = [string]$component.version
        $packageRoot = Join-Path (Join-Path $nugetRoot $packageId.ToLowerInvariant()) $packageVersion.ToLowerInvariant()
        if (-not (Test-Path -LiteralPath $packageRoot -PathType Container)) { continue }
        foreach ($sourceFile in Get-ChildItem -LiteralPath $packageRoot -File -Recurse) {
            if (-not $neededLeafNames.Contains($sourceFile.Name)) { continue }
            $hash = Get-RawSha256 $sourceFile.FullName
            if (-not $packageHashIndex.ContainsKey($hash)) {
                $packageHashIndex.Add($hash, [Collections.Generic.List[object]]::new())
            }
            $sourceRelative = (Get-RelativePath $packageRoot $sourceFile.FullName).Replace('\', '/')
            $packageHashIndex[$hash].Add([pscustomobject]@{
                identityKey = $packageId.ToLowerInvariant() + '/' + $packageVersion.ToLowerInvariant()
                packageId = $packageId
                version = $packageVersion
                sourcePath = 'nuget-cache/' + $packageId.ToLowerInvariant() + '/' + $packageVersion.ToLowerInvariant() + '/' + $sourceRelative
            })
        }
    }

    $records = [Collections.Generic.List[object]]::new()
    foreach ($file in $publishFiles) {
        $relativePath = (Get-RelativePath $publishRoot $file.FullName).Replace('\', '/')
        $sha256 = Get-RawSha256 $file.FullName
        $sourcePackage = $null
        $sourceProject = $null
        $sourceBuild = $null
        $classification = $null
        $evidenceKind = $null

        if ($relativePath.StartsWith('prompts/runtime/', [StringComparison]::Ordinal)) {
            if (-not $projectComponentsById.ContainsKey('runtime-prompts')) {
                throw [InvalidOperationException]::new('release_candidate_prompt_component_missing')
            }
            $sourcePath = Join-Path $repoRoot $relativePath.Replace('/', '\')
            if (-not (Test-Path -LiteralPath $sourcePath -PathType Leaf) -or (Get-RawSha256 $sourcePath) -cne $sha256) {
                throw [InvalidOperationException]::new('release_candidate_prompt_source_mismatch')
            }
            $sourceProject = [ordered]@{
                projectId = 'runtime-prompts'
                sourcePath = $relativePath
                sourceCommit = $ExpectedSourceSha
                sourceFileSha256 = $sha256
            }
            $classification = 'project-owned'
            $evidenceKind = 'SOURCE_FILE_SHA256_EQUAL'
        }
        elseif ($file.Name -match '^(ScreenGuide\..+)\.dll$' -and $projectComponentsById.ContainsKey($Matches[1])) {
            $projectId = $Matches[1]
            if (-not $depsLibraries.Contains($projectId + '/' + $ReleaseVersion)) {
                throw [InvalidOperationException]::new('release_candidate_project_deps_mismatch')
            }
            $sourceProject = [ordered]@{
                projectId = $projectId
                sourceCommit = $ExpectedSourceSha
                dependencyIdentity = $projectId + '/' + $ReleaseVersion
            }
            $classification = 'project-owned'
            $evidenceKind = 'DEPS_PROJECT_ENTRY_AND_OUTPUT_IDENTITY'
        }
        elseif ($file.Name -match '^(ScreenGuide\.Desktop(?:Client|Host))\.(exe|deps\.json|runtimeconfig\.json)$' -and $buildComponentsById.ContainsKey($Matches[1])) {
            $projectId = $Matches[1]
            $sourceBuild = [ordered]@{
                projectId = $projectId
                sourceCommit = $ExpectedSourceSha
                artifactKind = $Matches[2]
            }
            $classification = 'build-derived'
            $evidenceKind = 'PUBLISH_OUTPUT_AND_EXACT_SOURCE_COMMIT'
        }
        else {
            if (-not $packageHashIndex.ContainsKey($sha256)) {
                throw [InvalidOperationException]::new('release_candidate_payload_source_unmapped')
            }
            $identityGroups = @($packageHashIndex[$sha256] | Group-Object -Property identityKey)
            if ($identityGroups.Count -ne 1) {
                throw [InvalidOperationException]::new('release_candidate_payload_source_ambiguous')
            }
            $match = @($identityGroups[0].Group | Sort-Object sourcePath)[0]
            $sourcePackage = [ordered]@{
                packageId = [string]$match.packageId
                version = [string]$match.version
                sourcePath = [string]$match.sourcePath
                sourceFileSha256 = $sha256
                hashEquality = 'VERIFIED'
            }
            $classification = 'package'
            $evidenceKind = 'PACKAGE_FILE_SHA256_EQUALS_PUBLISHED_FILE'
        }

        $records.Add([ordered]@{
            artifactScope = 'installedPayload'
            frozenPath = $relativePath
            size = [long]$file.Length
            sha256 = $sha256
            mappingStatus = 'mapped'
            sourceClassification = $classification
            evidenceKind = $evidenceKind
            sourcePackage = $sourcePackage
            sourceProject = $sourceProject
            sourceBuild = $sourceBuild
        })
    }

    [IO.Directory]::CreateDirectory($outputRoot) | Out-Null
    $payloadManifestPath = Join-Path $outputRoot 'payload-attribution.json'
    $payloadManifest = [ordered]@{
        schemaVersion = 1
        evidenceId = 'v0.7.0-candidate-payload-attribution'
        candidateStatus = 'INTERNAL_CANDIDATE_ONLY'
        releaseVersion = $ReleaseVersion
        sourceCommit = $ExpectedSourceSha
        publishFileCount = $publishFiles.Count
        generationMode = 'PUBLISH_OUTPUT_EXACT_SOURCE_MAPPING_V1'
        records = $records.ToArray()
    }
    Write-Utf8LfJson $payloadManifestPath $payloadManifest

    $candidateBundleRoot = Join-Path $outputRoot 'bundle'
    [IO.Directory]::CreateDirectory($candidateBundleRoot) | Out-Null
    Copy-Item -Path (Join-Path $baseBundleRoot '*') -Destination $candidateBundleRoot -Recurse -Force
    $candidateNoticeBundlePath = Join-Path $candidateBundleRoot ([string]$contract.candidateNotice.bundlePath).Replace('/', '\')
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($candidateNoticeBundlePath)) | Out-Null
    Copy-Item -LiteralPath $candidateNoticeSource -Destination $candidateNoticeBundlePath -Force
    $candidateNoticeSha = Get-CanonicalUtf8LfSha256 $candidateNoticeBundlePath
    $candidateNoticeRawSha = Get-RawSha256 $candidateNoticeBundlePath

    $candidateBundleManifestPath = Join-Path $candidateBundleRoot 'bundle-manifest.json'
    $candidateBundle = Get-Content -Raw -LiteralPath $baseBundleManifestPath | ConvertFrom-Json
    $candidateBundle.bundleId = 'yuanshu-v0.7.0-candidate-distribution-notice-bundle'
    $candidateBundle.releaseVersion = $ReleaseVersion
    $candidateBundle.payloadManifestSha256 = Get-CanonicalUtf8LfSha256 $payloadManifestPath
    foreach ($component in @($candidateBundle.components | Where-Object { [string]$_.payloadSourceKind -in @('project', 'build') })) {
        $component.version = $ReleaseVersion
        $component.sourceBinding.reference = 'candidate/' + [string]$component.payloadSourceKind + '/' + [string]$component.payloadComponentId + '@' + $ReleaseVersion + '+' + $ExpectedSourceSha
        $component.sourceBinding.path = [string]$contract.candidateNotice.bundlePath
        $component.sourceBinding.hashProfile = 'UTF8_NO_BOM_LF_V1'
        $component.sourceBinding.sha256 = $candidateNoticeSha
        $component.sourceBinding.rawSha256 = $candidateNoticeRawSha
        $component.sourceBinding.sourceUrls = @('PROJECT_AUTHORED')
        $component.licenseNoticeFiles = @([pscustomobject][ordered]@{
            kind = 'NOTICE'
            path = [string]$contract.candidateNotice.bundlePath
            hashProfile = 'UTF8_NO_BOM_LF_V1'
            sha256 = $candidateNoticeSha
            rawSha256 = $candidateNoticeRawSha
            sourceUrls = @('PROJECT_AUTHORED')
        })
    }
    Write-Utf8LfJson $candidateBundleManifestPath $candidateBundle

    $noticeIndexPath = Join-Path $candidateBundleRoot 'notice-index.json'
    $rootNoticePath = Join-Path $candidateBundleRoot 'THIRD-PARTY-NOTICES.txt'
    $noticeGenerator = Join-Path $repoRoot 'scripts\New-DistributionNoticeIndex.ps1'
    $noticeOutput = @(& powershell.exe -NoProfile -ExecutionPolicy Bypass -File $noticeGenerator `
        -PayloadManifestPath $payloadManifestPath `
        -BundleManifestPath $candidateBundleManifestPath `
        -OutputPath $noticeIndexPath `
        -RootNoticeOutputPath $rootNoticePath `
        -ApprovedOutputRoot $candidateBundleRoot 2>&1)
    if ($LASTEXITCODE -ne 0) {
        throw [InvalidOperationException]::new('release_candidate_notice_index_generation_failed')
    }
    $noticeResult = ($noticeOutput -join "`n") | ConvertFrom-Json
    if (-not $noticeResult.passed) {
        throw [InvalidOperationException]::new('release_candidate_notice_index_generation_failed')
    }

    $candidateBundle.noticeIndex.sha256 = Get-CanonicalUtf8LfSha256 $noticeIndexPath
    $candidateBundle.rootNotice.sha256 = Get-CanonicalUtf8LfSha256 $rootNoticePath
    $candidateBundle.rootNotice.rawSha256 = Get-RawSha256 $rootNoticePath
    Write-Utf8LfJson $candidateBundleManifestPath $candidateBundle

    Complete $true $null ([ordered]@{
        sourceCommit = $ExpectedSourceSha
        releaseVersion = $ReleaseVersion
        publishFileCount = $publishFiles.Count
        payloadManifestPath = $payloadManifestPath
        candidateBundleRoot = $candidateBundleRoot
        candidateBundleManifestPath = $candidateBundleManifestPath
    }) 0
}
catch {
    $code = [string]$_.Exception.Message
    if ($code -notmatch '^release_candidate_[a-z0-9_]+$') { $code = 'release_candidate_attribution_failed' }
    Complete $false $code ([ordered]@{ sourceCommit = $ExpectedSourceSha; releaseVersion = $ReleaseVersion }) 1
}
