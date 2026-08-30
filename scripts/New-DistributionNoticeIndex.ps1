param(
    [Parameter(Mandatory = $true)]
    [string]$PayloadManifestPath,

    [Parameter(Mandatory = $true)]
    [string]$BundleManifestPath,

    [Parameter(Mandatory = $true)]
    [string]$OutputPath,

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
    if (-not $resolvedOutputPath.StartsWith($approvedRoot, [StringComparison]::OrdinalIgnoreCase)) {
        Write-ResultAndExit $false 'distribution_notice_output_out_of_bounds' @('output_out_of_bounds') 0 0 0 1
    }
    if ((Test-PathChainHasReparsePoint $resolvedApprovedRoot) -or
        (Test-PathChainHasReparsePoint $resolvedPayloadManifestPath) -or
        (Test-PathChainHasReparsePoint $resolvedBundleManifestPath) -or
        (Test-PathChainHasReparsePoint $resolvedOutputPath)) {
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
    foreach ($component in @($bundleManifest.components)) {
        if ([string]$component.artifactScope -ne 'installedPayload') { continue }
        $componentId = [string]$component.componentId
        $sourceKind = [string]$component.payloadSourceKind
        $payloadComponentId = [string]$component.payloadComponentId
        $version = [string]$component.version
        if ([string]::IsNullOrWhiteSpace($componentId) -or
            [string]::IsNullOrWhiteSpace($sourceKind) -or
            [string]::IsNullOrWhiteSpace($payloadComponentId) -or
            [string]::IsNullOrWhiteSpace($version) -or
            -not $componentIds.Add($componentId)) {
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
        $recordsByPath.Add($payloadPath, [ordered]@{
            artifactScope = 'installedPayload'
            payloadPath = $payloadPath
            componentId = [string]$component.componentId
            version = [string]$component.version
            licenseNoticePaths = @()
        })
    }

    $sortedPaths = [string[]]$recordsByPath.Keys
    [Array]::Sort($sortedPaths, [StringComparer]::Ordinal)
    $records = [Collections.Generic.List[object]]::new()
    foreach ($path in $sortedPaths) { $records.Add($recordsByPath[$path]) }

    $sortedExcludedPackages = [string[]]$excludedPackageIds
    [Array]::Sort($sortedExcludedPackages, [StringComparer]::Ordinal)
    $sortedExcludedPrefixes = [string[]]$excludedPathPrefixes
    [Array]::Sort($sortedExcludedPrefixes, [StringComparer]::Ordinal)
    $index = [ordered]@{
        schemaVersion = 1
        bundleStatus = 'BLOCKED'
        records = $records.ToArray()
        exclusionEvidence = [ordered]@{
            packageIdsAbsentFromPayload = $sortedExcludedPackages
            pathPrefixesAbsentFromPayload = $sortedExcludedPrefixes
        }
    }

    $json = ($index | ConvertTo-Json -Depth 30).Replace("`r`n", "`n") + "`n"
    $outputDirectory = [IO.Path]::GetDirectoryName($resolvedOutputPath)
    [IO.Directory]::CreateDirectory($outputDirectory) | Out-Null
    $temporaryPath = $resolvedOutputPath + '.tmp'
    [IO.File]::WriteAllText($temporaryPath, $json, [Text.UTF8Encoding]::new($false))
    Move-Item -LiteralPath $temporaryPath -Destination $resolvedOutputPath -Force

    Write-ResultAndExit $true $null @() $payloadCount $excludedPackageIds.Count $excludedPathPrefixes.Count 0
}
catch {
    Write-ResultAndExit $false 'distribution_notice_index_invalid' @('generation_failed') 0 0 0 1
}
