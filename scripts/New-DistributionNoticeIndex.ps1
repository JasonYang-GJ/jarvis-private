param(
    [Parameter(Mandatory = $true)]
    [string]$PayloadManifestPath,

    [Parameter(Mandatory = $true)]
    [string]$BundleManifestPath,

    [Parameter(Mandatory = $true)]
    [string]$OutputPath,

    [Parameter(Mandatory = $true)]
    [string]$RootNoticeOutputPath,

    [Parameter(Mandatory = $true)]
    [string]$ApprovedOutputRoot
)

$ErrorActionPreference = 'Stop'

function Write-ResultAndExit([bool]$passed, [string]$errorCode, [string[]]$blockers, [int]$payloadCount, [int]$excludedPackageCount, [int]$excludedPathPrefixCount, [int]$exitCode) {
    $result = [ordered]@{
        passed = $passed
        errorCode = $errorCode
        blockers = @($blockers | Sort-Object -Unique)
        payloadCount = $payloadCount
        excludedPackageIdsVerifiedCount = $excludedPackageCount
        excludedPathPrefixesVerifiedCount = $excludedPathPrefixCount
    }
    Write-Output ($result | ConvertTo-Json -Depth 8 -Compress)
    exit $exitCode
}

function Get-SourceIdentity($record, [string]$releaseVersion) {
    if ($null -ne $record.sourcePackage -and
        -not [string]::IsNullOrWhiteSpace([string]$record.sourcePackage.packageId) -and
        -not [string]::IsNullOrWhiteSpace([string]$record.sourcePackage.version)) {
        return [pscustomobject]@{ Kind = 'package'; Id = [string]$record.sourcePackage.packageId; Version = [string]$record.sourcePackage.version }
    }
    if ($null -ne $record.sourceProject -and -not [string]::IsNullOrWhiteSpace([string]$record.sourceProject.projectId)) {
        return [pscustomobject]@{ Kind = 'project'; Id = [string]$record.sourceProject.projectId; Version = $releaseVersion }
    }
    if ($null -ne $record.sourceBuild -and -not [string]::IsNullOrWhiteSpace([string]$record.sourceBuild.projectId)) {
        return [pscustomobject]@{ Kind = 'build'; Id = [string]$record.sourceBuild.projectId; Version = $releaseVersion }
    }
    return $null
}

function Get-CatalogKey([string]$kind, [string]$id, [string]$version) {
    return $kind + [char]0 + $id + [char]0 + $version
}

function Get-ComponentLicensePaths($component) {
    $paths = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($file in @($component.licenseNoticeFiles)) {
        $path = ([string]$file.path).Replace('\', '/')
        if ([string]::IsNullOrWhiteSpace($path) -or
            -not $path.StartsWith('files/', [StringComparison]::Ordinal) -or
            [IO.Path]::IsPathRooted($path) -or $path.Split('/') -contains '..' -or
            -not $paths.Add($path)) {
            return $null
        }
    }
    if ($paths.Count -eq 0) { return $null }
    $sorted = [string[]]$paths
    [Array]::Sort($sorted, [StringComparer]::Ordinal)
    return $sorted
}

function Test-PathChainHasReparsePoint([string]$path) {
    $current = [IO.Path]::GetFullPath($path)
    while (-not [string]::IsNullOrWhiteSpace($current)) {
        if (Test-Path -LiteralPath $current) {
            $attributes = [IO.File]::GetAttributes($current)
            if (($attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { return $true }
        }
        $parent = [IO.Directory]::GetParent($current)
        if ($null -eq $parent) { break }
        $current = $parent.FullName
    }
    return $false
}

try {
    $resolvedApprovedRoot = [IO.Path]::GetFullPath($ApprovedOutputRoot)
    $resolvedPayloadManifestPath = [IO.Path]::GetFullPath($PayloadManifestPath)
    $resolvedBundleManifestPath = [IO.Path]::GetFullPath($BundleManifestPath)
    $approvedRoot = $resolvedApprovedRoot.TrimEnd('\') + '\'
    $resolvedOutputPath = [IO.Path]::GetFullPath($OutputPath)
    $resolvedRootNoticePath = [IO.Path]::GetFullPath($RootNoticeOutputPath)
    if (-not $resolvedOutputPath.StartsWith($approvedRoot, [StringComparison]::OrdinalIgnoreCase) -or
        -not $resolvedRootNoticePath.StartsWith($approvedRoot, [StringComparison]::OrdinalIgnoreCase) -or
        $resolvedOutputPath -eq $resolvedRootNoticePath) {
        Write-ResultAndExit $false 'distribution_notice_output_out_of_bounds' @('output_out_of_bounds') 0 0 0 1
    }
    if ((Test-PathChainHasReparsePoint $resolvedApprovedRoot) -or
        (Test-PathChainHasReparsePoint $resolvedPayloadManifestPath) -or
        (Test-PathChainHasReparsePoint $resolvedBundleManifestPath) -or
        (Test-PathChainHasReparsePoint $resolvedOutputPath) -or
        (Test-PathChainHasReparsePoint $resolvedRootNoticePath)) {
        Write-ResultAndExit $false 'distribution_notice_reparse_point' @('reparse_point_rejected') 0 0 0 1
    }

    try {
        $payloadManifest = Get-Content -Raw -LiteralPath $resolvedPayloadManifestPath | ConvertFrom-Json
        $bundleManifest = Get-Content -Raw -LiteralPath $resolvedBundleManifestPath | ConvertFrom-Json
    }
    catch {
        Write-ResultAndExit $false 'distribution_notice_index_invalid' @('invalid_json_or_input') 0 0 0 1
    }

    if ($payloadManifest.schemaVersion -ne 1 -or $bundleManifest.schemaVersion -ne 1 -or
        [string]::IsNullOrWhiteSpace([string]$bundleManifest.releaseVersion) -or
        $null -eq $payloadManifest.records -or $null -eq $bundleManifest.components -or
        $null -eq $bundleManifest.exclusions -or $null -eq $bundleManifest.exclusions.packageIds -or
        $null -eq $bundleManifest.exclusions.pathPrefixes) {
        Write-ResultAndExit $false 'distribution_notice_index_invalid' @('invalid_schema') 0 0 0 1
    }

    $catalog = [Collections.Generic.Dictionary[string, object]]::new([StringComparer]::Ordinal)
    $componentIds = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $containerComponents = [Collections.Generic.List[object]]::new()
    foreach ($component in @($bundleManifest.components)) {
        $componentId = [string]$component.componentId
        $version = [string]$component.version
        if ([string]::IsNullOrWhiteSpace($componentId) -or
            [string]::IsNullOrWhiteSpace($version) -or
            -not $componentIds.Add($componentId)) {
            Write-ResultAndExit $false 'distribution_notice_catalog_conflict' @('catalog_conflict') 0 0 0 1
        }
        if ($null -eq (Get-ComponentLicensePaths $component)) {
            Write-ResultAndExit $false 'distribution_notice_catalog_missing' @('license_mapping_missing') 0 0 0 1
        }
        if ([string]$component.artifactScope -eq 'installerContainer') {
            $containerEntry = ([string]$component.containerEntry).Replace('\', '/')
            if ([string]::IsNullOrWhiteSpace($containerEntry) -or [IO.Path]::IsPathRooted($containerEntry) -or
                $containerEntry.Split('/') -contains '..') {
                Write-ResultAndExit $false 'distribution_notice_catalog_conflict' @('catalog_conflict') 0 0 0 1
            }
            $containerComponents.Add($component)
            continue
        }
        if ([string]$component.artifactScope -ne 'installedPayload') {
            Write-ResultAndExit $false 'distribution_notice_catalog_conflict' @('catalog_conflict') 0 0 0 1
        }
        $sourceKind = [string]$component.payloadSourceKind
        $payloadComponentId = [string]$component.payloadComponentId
        if ([string]::IsNullOrWhiteSpace($sourceKind) -or [string]::IsNullOrWhiteSpace($payloadComponentId)) {
            Write-ResultAndExit $false 'distribution_notice_catalog_conflict' @('catalog_conflict') 0 0 0 1
        }
        $catalogKey = Get-CatalogKey $sourceKind $payloadComponentId $version
        if ($catalog.ContainsKey($catalogKey)) {
            Write-ResultAndExit $false 'distribution_notice_catalog_conflict' @('catalog_conflict') 0 0 0 1
        }
        $catalog.Add($catalogKey, $component)
    }

    $excludedPackageIds = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($packageId in @($bundleManifest.exclusions.packageIds)) {
        if ([string]::IsNullOrWhiteSpace([string]$packageId) -or -not $excludedPackageIds.Add([string]$packageId)) {
            Write-ResultAndExit $false 'distribution_notice_index_invalid' @('invalid_exclusion_catalog') 0 0 0 1
        }
    }
    $excludedPathPrefixes = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($pathPrefix in @($bundleManifest.exclusions.pathPrefixes)) {
        $normalizedPrefix = ([string]$pathPrefix).Replace('\', '/')
        if ([string]::IsNullOrWhiteSpace($normalizedPrefix) -or -not $excludedPathPrefixes.Add($normalizedPrefix)) {
            Write-ResultAndExit $false 'distribution_notice_index_invalid' @('invalid_exclusion_catalog') 0 0 0 1
        }
    }

    $recordsByPath = [Collections.Generic.Dictionary[string, object]]::new([StringComparer]::Ordinal)
    $payloadCount = @($payloadManifest.records).Count
    foreach ($payload in @($payloadManifest.records)) {
        $payloadPath = ([string]$payload.frozenPath).Replace('\', '/')
        if ([string]::IsNullOrWhiteSpace($payloadPath) -or $payloadPath.StartsWith('/', [StringComparison]::Ordinal) -or
            [IO.Path]::IsPathRooted($payloadPath) -or $payloadPath.Split('/') -contains '..' -or
            $recordsByPath.ContainsKey($payloadPath)) {
            Write-ResultAndExit $false 'distribution_notice_index_invalid' @('invalid_or_duplicate_payload_path') $payloadCount 0 0 1
        }

        $source = Get-SourceIdentity $payload ([string]$bundleManifest.releaseVersion)
        if ($null -eq $source) {
            Write-ResultAndExit $false 'distribution_notice_catalog_missing' @('catalog_missing') $payloadCount 0 0 1
        }
        if ($source.Kind -eq 'package' -and $excludedPackageIds.Contains($source.Id)) {
            Write-ResultAndExit $false 'distribution_notice_exclusion_violated' @('excluded_package_present') $payloadCount 0 0 1
        }
        foreach ($pathPrefix in $excludedPathPrefixes) {
            if ($payloadPath.StartsWith($pathPrefix, [StringComparison]::OrdinalIgnoreCase)) {
                Write-ResultAndExit $false 'distribution_notice_exclusion_violated' @('excluded_path_present') $payloadCount 0 0 1
            }
        }

        $catalogKey = Get-CatalogKey $source.Kind $source.Id $source.Version
        if (-not $catalog.ContainsKey($catalogKey)) {
            Write-ResultAndExit $false 'distribution_notice_catalog_missing' @('catalog_missing') $payloadCount 0 0 1
        }
        $component = $catalog[$catalogKey]
        $licensePaths = Get-ComponentLicensePaths $component
        $recordsByPath.Add($payloadPath, [ordered]@{
            artifactScope = 'installedPayload'
            payloadPath = $payloadPath
            componentId = [string]$component.componentId
            version = [string]$component.version
            licenseNoticePaths = @($licensePaths)
        })
    }

    $sortedPaths = [string[]]$recordsByPath.Keys
    [Array]::Sort($sortedPaths, [StringComparer]::Ordinal)
    $records = [Collections.Generic.List[object]]::new()
    foreach ($path in $sortedPaths) { $records.Add($recordsByPath[$path]) }
    $containerEntries = [string[]]@($containerComponents | ForEach-Object { [string]$_.containerEntry })
    [Array]::Sort($containerEntries, [StringComparer]::Ordinal)
    foreach ($containerEntry in $containerEntries) {
        $component = @($containerComponents | Where-Object { [string]$_.containerEntry -ceq $containerEntry })[0]
        $records.Add([ordered]@{
            artifactScope = 'installerContainer'
            containerEntry = ([string]$component.containerEntry).Replace('\', '/')
            componentId = [string]$component.componentId
            version = [string]$component.version
            licenseNoticePaths = @(Get-ComponentLicensePaths $component)
        })
    }

    $sortedExcludedPackages = [string[]]$excludedPackageIds
    [Array]::Sort($sortedExcludedPackages, [StringComparer]::Ordinal)
    $sortedExcludedPrefixes = [string[]]$excludedPathPrefixes
    [Array]::Sort($sortedExcludedPrefixes, [StringComparer]::Ordinal)
    $index = [ordered]@{
        schemaVersion = 1
        bundleStatus = [string]$bundleManifest.bundleStatus
        records = $records.ToArray()
        exclusionEvidence = [ordered]@{
            packageIdsAbsentFromPayload = $sortedExcludedPackages
            pathPrefixesAbsentFromPayload = $sortedExcludedPrefixes
        }
    }

    $json = ($index | ConvertTo-Json -Depth 30).Replace("`r`n", "`n") + "`n"
    $noticeLines = [Collections.Generic.List[string]]::new()
    $noticeLines.Add(('Yuanshu ' + [string]$bundleManifest.releaseVersion + ' THIRD-PARTY-NOTICES index'))
    $noticeLines.Add('This file is a deterministic index. Full upstream texts are installed under licenses/<component>/.')
    $noticeLines.Add('Project-authored notices and reference-only terms are identified by their file names; this index is not legal advice.')
    $noticeLines.Add('')
    $noticeComponentIds = [string[]]@($bundleManifest.components | ForEach-Object { [string]$_.componentId })
    [Array]::Sort($noticeComponentIds, [StringComparer]::Ordinal)
    foreach ($componentId in $noticeComponentIds) {
        $component = @($bundleManifest.components | Where-Object { [string]$_.componentId -ceq $componentId })[0]
        $paths = Get-ComponentLicensePaths $component
        $noticeLines.Add(([string]$component.componentId + ' | ' + [string]$component.version + ' | ' + [string]$component.artifactScope + ' | ' + ($paths -join ', ')))
    }
    $rootNotice = ($noticeLines -join "`n") + "`n"
    $outputDirectory = [IO.Path]::GetDirectoryName($resolvedOutputPath)
    $rootNoticeDirectory = [IO.Path]::GetDirectoryName($resolvedRootNoticePath)
    [IO.Directory]::CreateDirectory($outputDirectory) | Out-Null
    [IO.Directory]::CreateDirectory($rootNoticeDirectory) | Out-Null
    $temporaryPath = $resolvedOutputPath + '.tmp'
    $temporaryRootNoticePath = $resolvedRootNoticePath + '.tmp'
    [IO.File]::WriteAllText($temporaryPath, $json, [Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText($temporaryRootNoticePath, $rootNotice, [Text.UTF8Encoding]::new($false))
    Move-Item -LiteralPath $temporaryPath -Destination $resolvedOutputPath -Force
    Move-Item -LiteralPath $temporaryRootNoticePath -Destination $resolvedRootNoticePath -Force

    Write-ResultAndExit $true $null @() $payloadCount $excludedPackageIds.Count $excludedPathPrefixes.Count 0
}
catch {
    Write-ResultAndExit $false 'distribution_notice_index_invalid' @('generation_failed') 0 0 0 1
}
