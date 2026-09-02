param(
    [Parameter(Mandatory = $true)]
    [string]$ManifestPath,

    [Parameter(Mandatory = $true)]
    [string]$BundleRoot,

    [Parameter(Mandatory = $true)]
    [string]$PayloadManifestPath,

    [Parameter(Mandatory = $true)]
    [string]$ApprovedStagingRoot,

    [Parameter(Mandatory = $true)]
    [string]$InnoIncludePath
)

$ErrorActionPreference = 'Stop'

function Write-ResultAndExit([bool]$passed, [string]$errorCode, [string[]]$blockers, [int]$payloadCount, [int]$exitCode) {
    $result = [ordered]@{
        passed = $passed
        errorCode = $errorCode
        blockers = @($blockers | Sort-Object -Unique)
        payloadCount = $payloadCount
    }
    Write-Output ($result | ConvertTo-Json -Depth 8 -Compress)
    exit $exitCode
}

function Test-RelativeBundlePath([string]$path) {
    if ([string]::IsNullOrWhiteSpace($path)) { return $false }
    if ([IO.Path]::IsPathRooted($path)) { return $false }
    $normalized = $path.Replace('\', '/')
    if ($normalized.StartsWith('/', [StringComparison]::Ordinal) -or
        $normalized.Split('/') -contains '..') { return $false }
    return $true
}

function Resolve-BundleFile([string]$root, [string]$relativePath) {
    if (-not (Test-RelativeBundlePath $relativePath)) {
        throw [InvalidOperationException]::new('invalid_relative_path')
    }
    $fullRoot = [IO.Path]::GetFullPath($root).TrimEnd('\') + '\'
    $candidate = [IO.Path]::GetFullPath((Join-Path $root $relativePath.Replace('/', '\')))
    if (-not $candidate.StartsWith($fullRoot, [StringComparison]::OrdinalIgnoreCase)) {
        throw [InvalidOperationException]::new('invalid_relative_path')
    }
    return $candidate
}

function Get-FileSha256([string]$path) {
    return (Get-FileHash -Algorithm SHA256 -LiteralPath $path).Hash.ToUpperInvariant()
}

function Get-CanonicalUtf8LfSha256([string]$path) {
    $bytes = [IO.File]::ReadAllBytes($path)
    $offset = if ($bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF) { 3 } else { 0 }
    $strictUtf8 = [Text.UTF8Encoding]::new($false, $true)
    $text = $strictUtf8.GetString($bytes, $offset, $bytes.Length - $offset)
    $normalized = $text.Replace("`r`n", "`n").Replace("`r", "`n")
    $normalizedBytes = [Text.UTF8Encoding]::new($false).GetBytes($normalized)
    $sha = [Security.Cryptography.SHA256]::Create()
    try {
        return ([BitConverter]::ToString($sha.ComputeHash($normalizedBytes))).Replace('-', '')
    }
    finally {
        $sha.Dispose()
    }
}

function Test-Sha256([string]$value) {
    return -not [string]::IsNullOrWhiteSpace($value) -and $value -match '^[0-9A-Fa-f]{64}$'
}

function Test-PinnedSourceUrl([string]$value) {
    if ($value -eq 'PROJECT_AUTHORED') { return $true }
    $uri = $null
    if (-not [Uri]::TryCreate($value, [UriKind]::Absolute, [ref]$uri) -or $uri.Scheme -ne 'https') { return $false }
    if ($uri.Host -eq 'raw.githubusercontent.com') {
        if ($uri.AbsolutePath -match '/(?:main|master)/') { return $false }
        return $uri.AbsolutePath -match '/(?:v\d+\.\d+(?:\.\d+)?|is-\d+_\d+_\d+|[0-9a-f]{40})/'
    }
    return $true
}

function Test-SourceUrls($values) {
    $urls = @($values)
    if ($urls.Count -eq 0) { return $false }
    foreach ($url in $urls) {
        if (-not (Test-PinnedSourceUrl ([string]$url))) { return $false }
    }
    return $true
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

function Get-ProfiledSha256([string]$path, [string]$profile) {
    if ($profile -eq 'RAW_BYTES_SHA256') { return Get-FileSha256 $path }
    if ($profile -eq 'UTF8_NO_BOM_LF_V1') { return Get-CanonicalUtf8LfSha256 $path }
    throw [InvalidOperationException]::new('invalid_hash_profile')
}

function Get-PayloadSourceIdentity($payload) {
    if ($null -ne $payload.sourcePackage -and -not [string]::IsNullOrWhiteSpace([string]$payload.sourcePackage.packageId)) {
        return [pscustomobject]@{ Kind = 'package'; Id = [string]$payload.sourcePackage.packageId }
    }
    if ($null -ne $payload.sourceProject -and -not [string]::IsNullOrWhiteSpace([string]$payload.sourceProject.projectId)) {
        return [pscustomobject]@{ Kind = 'project'; Id = [string]$payload.sourceProject.projectId }
    }
    if ($null -ne $payload.sourceBuild -and -not [string]::IsNullOrWhiteSpace([string]$payload.sourceBuild.projectId)) {
        return [pscustomobject]@{ Kind = 'build'; Id = [string]$payload.sourceBuild.projectId }
    }
    return $null
}

try {
    $resolvedBundleRoot = [IO.Path]::GetFullPath($BundleRoot)
    $resolvedManifestPath = [IO.Path]::GetFullPath($ManifestPath)
    $resolvedPayloadPath = [IO.Path]::GetFullPath($PayloadManifestPath)
    $resolvedStagingRoot = [IO.Path]::GetFullPath($ApprovedStagingRoot)
    $resolvedIncludePath = [IO.Path]::GetFullPath($InnoIncludePath)
    $bundlePrefix = $resolvedBundleRoot.TrimEnd('\') + '\'
    $stagingPrefix = $resolvedStagingRoot.TrimEnd('\') + '\'
    if (-not $resolvedManifestPath.StartsWith($bundlePrefix, [StringComparison]::OrdinalIgnoreCase)) {
        Write-ResultAndExit $false 'distribution_notice_bundle_invalid' @('manifest_outside_bundle') 0 1
    }
    if (-not $resolvedIncludePath.StartsWith($stagingPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        Write-ResultAndExit $false 'distribution_notice_output_out_of_bounds' @('output_out_of_bounds') 0 1
    }
    if ((Test-PathChainHasReparsePoint $resolvedBundleRoot) -or
        (Test-PathChainHasReparsePoint $resolvedManifestPath) -or
        (Test-PathChainHasReparsePoint $resolvedPayloadPath) -or
        (Test-PathChainHasReparsePoint $resolvedStagingRoot) -or
        (Test-PathChainHasReparsePoint $resolvedIncludePath)) {
        Write-ResultAndExit $false 'distribution_notice_reparse_point' @('reparse_point_rejected') 0 1
    }

    if (-not (Test-Path -LiteralPath $resolvedManifestPath -PathType Leaf) -or
        -not (Test-Path -LiteralPath $resolvedPayloadPath -PathType Leaf)) {
        Write-ResultAndExit $false 'distribution_notice_bundle_invalid' @('required_file_missing') 0 1
    }

    try {
        $manifest = Get-Content -Raw -LiteralPath $resolvedManifestPath | ConvertFrom-Json
        $payloadManifest = Get-Content -Raw -LiteralPath $resolvedPayloadPath | ConvertFrom-Json
    }
    catch {
        Write-ResultAndExit $false 'distribution_notice_bundle_invalid' @('invalid_json') 0 1
    }

    if ($manifest.schemaVersion -ne 1 -or $payloadManifest.schemaVersion -ne 1 -or
        $null -eq $manifest.noticeIndex -or $null -eq $manifest.rootNotice -or $null -eq $manifest.components -or
        $null -eq $payloadManifest.records) {
        Write-ResultAndExit $false 'distribution_notice_bundle_invalid' @('invalid_schema') 0 1
    }

    $payloadCount = @($payloadManifest.records).Count
    $blockers = [Collections.Generic.List[string]]::new()

    if ([string]$manifest.payloadManifestHashProfile -ne 'UTF8_NO_BOM_LF_V1' -or
        -not (Test-Sha256 ([string]$manifest.payloadManifestSha256)) -or
        (Get-CanonicalUtf8LfSha256 $resolvedPayloadPath) -ne ([string]$manifest.payloadManifestSha256).ToUpperInvariant()) {
        $blockers.Add('payload_manifest_hash_mismatch')
    }

    try { $rootNoticePath = Resolve-BundleFile $resolvedBundleRoot ([string]$manifest.rootNotice.path) }
    catch { Write-ResultAndExit $false 'distribution_notice_bundle_invalid' @('invalid_root_notice_path') $payloadCount 1 }
    if (Test-PathChainHasReparsePoint $rootNoticePath) {
        Write-ResultAndExit $false 'distribution_notice_reparse_point' @('reparse_point_rejected') $payloadCount 1
    }
    if (-not (Test-Path -LiteralPath $rootNoticePath -PathType Leaf)) {
        $blockers.Add('root_notice_missing')
    }
    else {
        try { $rootNoticeHash = Get-ProfiledSha256 $rootNoticePath ([string]$manifest.rootNotice.hashProfile) }
        catch { Write-ResultAndExit $false 'distribution_notice_bundle_invalid' @('invalid_root_notice_hash_profile') $payloadCount 1 }
        if (-not (Test-Sha256 ([string]$manifest.rootNotice.sha256)) -or
            -not (Test-Sha256 ([string]$manifest.rootNotice.rawSha256)) -or
            $rootNoticeHash -ne ([string]$manifest.rootNotice.sha256).ToUpperInvariant() -or
            (Get-FileSha256 $rootNoticePath) -ne ([string]$manifest.rootNotice.rawSha256).ToUpperInvariant()) {
            $blockers.Add('root_notice_hash_mismatch')
        }
    }

    try {
        $noticeIndexPath = Resolve-BundleFile $resolvedBundleRoot ([string]$manifest.noticeIndex.path)
    }
    catch {
        Write-ResultAndExit $false 'distribution_notice_bundle_invalid' @('invalid_notice_index_path') $payloadCount 1
    }
    if (Test-PathChainHasReparsePoint $noticeIndexPath) {
        Write-ResultAndExit $false 'distribution_notice_reparse_point' @('reparse_point_rejected') $payloadCount 1
    }
    if (-not (Test-Path -LiteralPath $noticeIndexPath -PathType Leaf)) {
        $blockers.Add('notice_index_missing')
        $noticeIndex = $null
    }
    else {
        try { $noticeIndexHash = Get-ProfiledSha256 $noticeIndexPath ([string]$manifest.noticeIndex.hashProfile) }
        catch { Write-ResultAndExit $false 'distribution_notice_bundle_invalid' @('invalid_notice_index_hash_profile') $payloadCount 1 }
        if (-not (Test-Sha256 ([string]$manifest.noticeIndex.sha256)) -or
            $noticeIndexHash -ne ([string]$manifest.noticeIndex.sha256).ToUpperInvariant()) {
            $blockers.Add('notice_index_hash_mismatch')
        }
        try {
            $noticeIndex = Get-Content -Raw -LiteralPath $noticeIndexPath | ConvertFrom-Json
        }
        catch {
            Write-ResultAndExit $false 'distribution_notice_bundle_invalid' @('invalid_notice_index_json') $payloadCount 1
        }
    }

    $componentById = @{}
    $licensePaths = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $licensePathsByComponent = @{}
    foreach ($component in @($manifest.components)) {
        $componentId = [string]$component.componentId
        if ([string]::IsNullOrWhiteSpace($componentId) -or $componentById.ContainsKey($componentId)) {
            Write-ResultAndExit $false 'distribution_notice_bundle_invalid' @('duplicate_or_unknown_component') $payloadCount 1
        }
        $componentById[$componentId] = $component
        $componentLicensePaths = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        $licensePathsByComponent[$componentId] = $componentLicensePaths

        if ([string]$component.artifactScope -notin @('installedPayload', 'installerContainer')) {
            Write-ResultAndExit $false 'distribution_notice_bundle_invalid' @('invalid_artifact_scope') $payloadCount 1
        }

        foreach ($axis in @('source', 'bundling', 'attribution', 'notice')) {
            if ([string]$component.status.$axis -ne 'VERIFIED') {
                $blockers.Add(('component_status_not_verified:' + $componentId + ':' + $axis))
            }
        }

        if ($null -eq $component.sourceBinding -or
            [string]::IsNullOrWhiteSpace([string]$component.sourceBinding.kind) -or
            [string]::IsNullOrWhiteSpace([string]$component.sourceBinding.reference) -or
            [string]::IsNullOrWhiteSpace([string]$component.sourceBinding.path) -or
            -not (Test-Sha256 ([string]$component.sourceBinding.sha256)) -or
            -not (Test-Sha256 ([string]$component.sourceBinding.rawSha256))) {
            $blockers.Add(('source_binding_incomplete:' + $componentId))
        }
        elseif (-not (Test-SourceUrls $component.sourceBinding.sourceUrls)) {
            Write-ResultAndExit $false 'distribution_notice_bundle_invalid' @('source_reference_unpinned') $payloadCount 1
        }
        else {
            $sourceBindingPath = ([string]$component.sourceBinding.path).Replace('\', '/')
            if (-not $sourceBindingPath.StartsWith('files/', [StringComparison]::Ordinal)) {
                Write-ResultAndExit $false 'distribution_notice_bundle_invalid' @('invalid_source_binding_layout') $payloadCount 1
            }
            try { $resolvedSourceBinding = Resolve-BundleFile $resolvedBundleRoot $sourceBindingPath }
            catch { Write-ResultAndExit $false 'distribution_notice_bundle_invalid' @('invalid_source_binding_path') $payloadCount 1 }
            if (Test-PathChainHasReparsePoint $resolvedSourceBinding) {
                Write-ResultAndExit $false 'distribution_notice_reparse_point' @('reparse_point_rejected') $payloadCount 1
            }
            if (-not (Test-Path -LiteralPath $resolvedSourceBinding -PathType Leaf)) {
                $blockers.Add(('source_binding_file_missing:' + $componentId))
            }
            else {
                try { $sourceBindingHash = Get-ProfiledSha256 $resolvedSourceBinding ([string]$component.sourceBinding.hashProfile) }
                catch { Write-ResultAndExit $false 'distribution_notice_bundle_invalid' @('invalid_source_binding_hash_profile') $payloadCount 1 }
                if ($sourceBindingHash -ne ([string]$component.sourceBinding.sha256).ToUpperInvariant() -or
                    (Get-FileSha256 $resolvedSourceBinding) -ne ([string]$component.sourceBinding.rawSha256).ToUpperInvariant()) {
                    $blockers.Add(('source_binding_hash_mismatch:' + $componentId))
                }
            }
        }

        $componentFiles = @($component.licenseNoticeFiles)
        if ($componentFiles.Count -eq 0) {
            $blockers.Add(('license_notice_file_missing:' + $componentId))
        }
        foreach ($file in $componentFiles) {
            $relativeLicensePath = ([string]$file.path).Replace('\', '/')
            if ([string]$file.kind -notin @('LICENSE', 'NOTICE', 'TERMS_REFERENCE') -or
                -not (Test-SourceUrls $file.sourceUrls)) {
                Write-ResultAndExit $false 'distribution_notice_bundle_invalid' @('source_reference_unpinned') $payloadCount 1
            }
            if (-not $relativeLicensePath.StartsWith('files/', [StringComparison]::Ordinal) -or $relativeLicensePath.Length -le 6) {
                Write-ResultAndExit $false 'distribution_notice_bundle_invalid' @('invalid_license_notice_layout') $payloadCount 1
            }
            try {
                $resolvedFile = Resolve-BundleFile $resolvedBundleRoot $relativeLicensePath
            }
            catch {
                Write-ResultAndExit $false 'distribution_notice_bundle_invalid' @('invalid_license_notice_path') $payloadCount 1
            }
            if (Test-PathChainHasReparsePoint $resolvedFile) {
                Write-ResultAndExit $false 'distribution_notice_reparse_point' @('reparse_point_rejected') $payloadCount 1
            }
            if (-not (Test-Path -LiteralPath $resolvedFile -PathType Leaf)) {
                $blockers.Add(('license_notice_file_missing:' + $componentId))
                continue
            }
            try { $licenseNoticeHash = Get-ProfiledSha256 $resolvedFile ([string]$file.hashProfile) }
            catch { Write-ResultAndExit $false 'distribution_notice_bundle_invalid' @('invalid_license_notice_hash_profile') $payloadCount 1 }
            if (-not (Test-Sha256 ([string]$file.sha256)) -or
                -not (Test-Sha256 ([string]$file.rawSha256)) -or
                $licenseNoticeHash -ne ([string]$file.sha256).ToUpperInvariant() -or
                (Get-FileSha256 $resolvedFile) -ne ([string]$file.rawSha256).ToUpperInvariant()) {
                $blockers.Add(('license_notice_hash_mismatch:' + $componentId))
            }
            $normalizedLicensePath = $relativeLicensePath.Replace('/', '\')
            $licensePaths.Add($normalizedLicensePath) | Out-Null
            $componentLicensePaths.Add($normalizedLicensePath) | Out-Null
        }
    }

    if ([string]$manifest.bundleStatus -ne 'VERIFIED' -or @($manifest.remainingBlockers).Count -ne 0) {
        $blockers.Add('bundle_status_incomplete')
    }
    foreach ($declaredBlocker in @($manifest.remainingBlockers)) {
        $blocker = [string]$declaredBlocker
        if ($blocker -notmatch '^[a-z0-9._:-]+$') {
            Write-ResultAndExit $false 'distribution_notice_bundle_invalid' @('invalid_declared_blocker') $payloadCount 1
        }
        $blockers.Add(('declared_blocker:' + $blocker))
    }

    if ($null -eq $noticeIndex -or $noticeIndex.schemaVersion -ne 1 -or $null -eq $noticeIndex.records) {
        $blockers.Add('notice_index_schema_invalid')
    }
    else {
        $payloadByPath = @{}
        foreach ($payload in @($payloadManifest.records)) {
            $payloadPath = [string]$payload.frozenPath
            if (-not (Test-RelativeBundlePath $payloadPath) -or $payloadByPath.ContainsKey($payloadPath)) {
                Write-ResultAndExit $false 'distribution_notice_bundle_invalid' @('duplicate_or_invalid_payload') $payloadCount 1
            }
            $payloadByPath[$payloadPath] = $payload
        }

        $mappedPaths = @{}
        $mappedContainerEntries = @{}
        $componentMappingCounts = @{}
        foreach ($record in @($noticeIndex.records)) {
            $artifactScope = if ([string]::IsNullOrWhiteSpace([string]$record.artifactScope)) { 'installedPayload' } else { [string]$record.artifactScope }
            $componentId = [string]$record.componentId
            if (-not $componentById.ContainsKey($componentId)) {
                Write-ResultAndExit $false 'distribution_notice_bundle_invalid' @('duplicate_unknown_or_unmapped_component') $payloadCount 1
            }
            $component = $componentById[$componentId]
            if ([string]$record.version -ne [string]$component.version -or [string]$component.artifactScope -ne $artifactScope) {
                $blockers.Add(('payload_component_binding_mismatch:' + $componentId))
            }
            if ($artifactScope -eq 'installedPayload') {
                $payloadPath = [string]$record.payloadPath
                if (-not $payloadByPath.ContainsKey($payloadPath) -or $mappedPaths.ContainsKey($payloadPath)) {
                    Write-ResultAndExit $false 'distribution_notice_bundle_invalid' @('duplicate_unknown_or_unmapped_component') $payloadCount 1
                }
                $mappedPaths[$payloadPath] = $true
                $derivedSource = Get-PayloadSourceIdentity $payloadByPath[$payloadPath]
                if ($null -eq $derivedSource -or
                    [string]$component.payloadSourceKind -ne $derivedSource.Kind -or
                    [string]$component.payloadComponentId -ne $derivedSource.Id) {
                    $blockers.Add(('payload_component_binding_mismatch:' + $componentId))
                }
                $mappingTarget = $payloadPath
            }
            elseif ($artifactScope -eq 'installerContainer') {
                $containerEntry = [string]$record.containerEntry
                if (-not (Test-RelativeBundlePath $containerEntry) -or $mappedContainerEntries.ContainsKey($containerEntry)) {
                    Write-ResultAndExit $false 'distribution_notice_bundle_invalid' @('duplicate_or_invalid_container_entry') $payloadCount 1
                }
                $mappedContainerEntries[$containerEntry] = $true
                $mappingTarget = $containerEntry
            }
            else {
                Write-ResultAndExit $false 'distribution_notice_bundle_invalid' @('invalid_artifact_scope') $payloadCount 1
            }
            if (-not $componentMappingCounts.ContainsKey($componentId)) { $componentMappingCounts[$componentId] = 0 }
            $componentMappingCounts[$componentId] = [int]$componentMappingCounts[$componentId] + 1
            if (@($record.licenseNoticePaths).Count -eq 0) {
                $blockers.Add(('payload_notice_mapping_missing:' + $mappingTarget))
            }
            foreach ($mappedLicensePath in @($record.licenseNoticePaths)) {
                $normalizedMapped = ([string]$mappedLicensePath).Replace('/', '\')
                $componentLicensePaths = $licensePathsByComponent[$componentId]
                if (-not $componentLicensePaths.Contains($normalizedMapped) -and $licensePaths.Contains($normalizedMapped)) {
                    $blockers.Add(('cross_component_notice_mapping:' + $componentId))
                }
                elseif (-not $componentLicensePaths.Contains($normalizedMapped)) {
                    $blockers.Add(('payload_notice_mapping_unknown:' + $mappingTarget))
                }
            }
        }
        if ($mappedPaths.Count -ne $payloadByPath.Count) {
            $blockers.Add('payload_notice_mapping_incomplete')
        }
        foreach ($componentId in $componentById.Keys) {
            if (-not $componentMappingCounts.ContainsKey($componentId)) {
                $blockers.Add(('component_notice_mapping_missing:' + $componentId))
            }
        }
    }

    if ($blockers.Count -gt 0) {
        Write-ResultAndExit $false 'distribution_notice_bundle_incomplete' $blockers.ToArray() $payloadCount 1
    }

    $includeLines = [Collections.Generic.List[string]]::new()
    $includeLines.Add('; Generated only after the distribution notice bundle passes validation.')
    $includeLines.Add(('Source: "' + $resolvedManifestPath + '"; DestDir: "{app}\distribution"; Flags: ignoreversion'))
    $includeLines.Add(('Source: "' + $noticeIndexPath + '"; DestDir: "{app}\distribution"; Flags: ignoreversion'))
    $includeLines.Add(('Source: "' + $rootNoticePath + '"; DestDir: "{app}"; Flags: ignoreversion'))
    foreach ($licensePath in @($licensePaths | Sort-Object -Unique)) {
        $installedLicensePath = $licensePath.Substring('files\'.Length)
        $destination = [IO.Path]::GetDirectoryName($installedLicensePath)
        $destinationSuffix = if ([string]::IsNullOrWhiteSpace($destination)) { '' } else { '\' + $destination }
        $resolvedLicensePath = Resolve-BundleFile $resolvedBundleRoot $licensePath
        $includeLines.Add(('Source: "' + $resolvedLicensePath + '"; DestDir: "{app}\licenses' + $destinationSuffix + '"; Flags: ignoreversion'))
    }

    $includeDirectory = [IO.Path]::GetDirectoryName($resolvedIncludePath)
    if (Test-PathChainHasReparsePoint $resolvedIncludePath) {
        Write-ResultAndExit $false 'distribution_notice_reparse_point' @('reparse_point_rejected') $payloadCount 1
    }
    [IO.Directory]::CreateDirectory($includeDirectory) | Out-Null
    $temporaryInclude = $resolvedIncludePath + '.tmp'
    [IO.File]::WriteAllLines($temporaryInclude, $includeLines, [Text.UTF8Encoding]::new($true))
    Move-Item -LiteralPath $temporaryInclude -Destination $resolvedIncludePath -Force

    Write-ResultAndExit $true $null @() $payloadCount 0
}
catch {
    Write-ResultAndExit $false 'distribution_notice_bundle_invalid' @('validation_failed') 0 1
}
