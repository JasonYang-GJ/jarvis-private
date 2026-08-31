$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

$script:Stage5ProbeManifestName = 'lifecycle-probe-bundle.json'
$script:Stage5ProbeEntryPoint = 'ScreenGuide.Stage5LifecycleProbe.exe'
$script:Stage5ProbeRequiredFiles = @(
    'ScreenGuide.Stage5LifecycleProbe.exe',
    'hostfxr.dll',
    'hostpolicy.dll',
    'coreclr.dll',
    'System.Private.CoreLib.dll',
    'e_sqlite3.dll')
$script:Stage5ProbeMaxFiles = 512
$script:Stage5ProbeMaxManifestBytes = 4MB
$script:Stage5ProbeMaxEntryBytes = 256MB
$script:Stage5ProbeMaxExpandedBytes = 512MB

function Test-Stage5ProbeReparsePoint([string]$Path) {
    try { $current = [IO.Path]::GetFullPath($Path) }
    catch { return $true }
    while (-not [string]::IsNullOrWhiteSpace($current)) {
        if (Test-Path -LiteralPath $current) {
            try {
                if (([IO.File]::GetAttributes($current) -band [IO.FileAttributes]::ReparsePoint) -ne 0) { return $true }
            }
            catch { return $true }
        }
        $parent = [IO.Directory]::GetParent($current)
        if ($null -eq $parent) { break }
        $current = $parent.FullName
    }
    return $false
}

function Test-Stage5ProbeSafeRelativePath([string]$Path) {
    if ([string]::IsNullOrWhiteSpace($Path) -or [IO.Path]::IsPathRooted($Path) -or
        $Path.Contains('\') -or $Path.Contains(':') -or $Path.EndsWith('/')) { return $false }
    $segments = @($Path.Split('/'))
    return $segments.Count -gt 0 -and @($segments | Where-Object { $_ -eq '' -or $_ -eq '.' -or $_ -eq '..' }).Count -eq 0
}

function Get-Stage5ProbeStreamSha256($Stream) {
    $sha = [Security.Cryptography.SHA256]::Create()
    try { return ([BitConverter]::ToString($sha.ComputeHash($Stream))).Replace('-', '') }
    finally { $sha.Dispose() }
}

function Get-Stage5ProbeBytesSha256([byte[]]$Bytes) {
    $sha = [Security.Cryptography.SHA256]::Create()
    try { return ([BitConverter]::ToString($sha.ComputeHash($Bytes))).Replace('-', '') }
    finally { $sha.Dispose() }
}

function New-Stage5ProbeValidationResult(
    [string]$Status,
    [string]$ErrorCode,
    [string]$Phase,
    [int]$DeclaredCount,
    [int]$ActualCount,
    [int]$MissingCount,
    [int]$ExtraCount,
    [string]$ArchiveSha256,
    [string]$ManifestSha256,
    [bool]$EntryPointPresent) {
    return [pscustomobject][ordered]@{
        status = $Status
        errorCode = if ([string]::IsNullOrWhiteSpace($ErrorCode)) { $null } else { $ErrorCode }
        phase = $Phase
        declaredCount = [int]$DeclaredCount
        actualCount = [int]$ActualCount
        missingCount = [int]$MissingCount
        extraCount = [int]$ExtraCount
        archiveSha256 = if ([string]$ArchiveSha256 -cmatch '^[0-9A-F]{64}$') { $ArchiveSha256 } else { $null }
        manifestSha256 = if ([string]$ManifestSha256 -cmatch '^[0-9A-F]{64}$') { $ManifestSha256 } else { $null }
        entryPointPresent = [bool]$EntryPointPresent
    }
}

function Get-Stage5ProbeManifestDeclaration($Manifest) {
    if ($null -eq $Manifest -or $Manifest.contractVersion -ne 1 -or
        [string]$Manifest.bundleKind -cne 'self-contained-probe' -or
        [string]$Manifest.runtimeIdentifier -cne 'win-x64' -or
        -not [bool]$Manifest.selfContained -or
        [string]$Manifest.entryPoint -cne $script:Stage5ProbeEntryPoint -or
        $null -eq $Manifest.files) {
        throw [InvalidOperationException]::new('s5_lifecycle_probe_manifest_invalid')
    }
    $declared = [Collections.Generic.Dictionary[string,object]]::new([StringComparer]::Ordinal)
    [long]$totalBytes = 0
    foreach ($entry in @($Manifest.files)) {
        $relative = [string]$entry.relativePath
        [long]$size = 0
        try { $size = [long]$entry.size }
        catch { throw [InvalidOperationException]::new('s5_lifecycle_probe_manifest_invalid') }
        if (-not (Test-Stage5ProbeSafeRelativePath $relative) -or $declared.ContainsKey($relative) -or
            [string]$entry.sha256 -cnotmatch '^[0-9A-F]{64}$' -or $size -lt 0 -or $size -gt $script:Stage5ProbeMaxEntryBytes) {
            throw [InvalidOperationException]::new('s5_lifecycle_probe_manifest_invalid')
        }
        $totalBytes += $size
        if ($totalBytes -gt $script:Stage5ProbeMaxExpandedBytes) {
            throw [InvalidOperationException]::new('s5_lifecycle_probe_manifest_invalid')
        }
        $declared.Add($relative, [pscustomobject]@{ size = $size; sha256 = [string]$entry.sha256 })
    }
    if ($declared.Count -lt $script:Stage5ProbeRequiredFiles.Count -or $declared.Count -gt $script:Stage5ProbeMaxFiles) {
        throw [InvalidOperationException]::new('s5_lifecycle_probe_manifest_invalid')
    }
    foreach ($required in $script:Stage5ProbeRequiredFiles) {
        if (-not $declared.ContainsKey($required)) {
            throw [InvalidOperationException]::new('s5_lifecycle_probe_manifest_incomplete')
        }
    }
    return $declared
}

function Test-Stage5LifecycleProbeBundleCore([string]$BundleRoot) {
    $declaredCount = 0
    $actualCount = 0
    $manifestSha256 = $null
    try {
        $root = [IO.Path]::GetFullPath($BundleRoot).TrimEnd('\')
        if (-not (Test-Path -LiteralPath $root -PathType Container) -or (Test-Stage5ProbeReparsePoint $root)) {
            return New-Stage5ProbeValidationResult 'BLOCKED' 's5_lifecycle_probe_bundle_invalid' 'path' 0 0 0 0 $null $null $false
        }
        $manifestPath = Join-Path $root $script:Stage5ProbeManifestName
        if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf) -or
            (Test-Stage5ProbeReparsePoint $manifestPath) -or
            (Get-Item -LiteralPath $manifestPath).Length -gt $script:Stage5ProbeMaxManifestBytes) {
            return New-Stage5ProbeValidationResult 'BLOCKED' 's5_lifecycle_probe_bundle_incomplete' 'manifest' 0 0 1 0 $null $null $false
        }
        $manifestSha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $manifestPath).Hash
        $manifest = Get-Content -Raw -LiteralPath $manifestPath | ConvertFrom-Json
        try { $declared = Get-Stage5ProbeManifestDeclaration $manifest }
        catch {
            $code = if ($_.Exception.Message -ceq 's5_lifecycle_probe_manifest_incomplete') { 's5_lifecycle_probe_bundle_incomplete' } else { 's5_lifecycle_probe_bundle_invalid' }
            return New-Stage5ProbeValidationResult 'BLOCKED' $code 'manifest' 0 0 0 0 $null $manifestSha256 $false
        }
        $declaredCount = $declared.Count
        $actual = [Collections.Generic.Dictionary[string,object]]::new([StringComparer]::Ordinal)
        foreach ($file in @(Get-ChildItem -LiteralPath $root -Recurse -File | Sort-Object FullName)) {
            if ($file.FullName -ceq $manifestPath) { continue }
            if (Test-Stage5ProbeReparsePoint $file.FullName) {
                return New-Stage5ProbeValidationResult 'BLOCKED' 's5_lifecycle_probe_bundle_invalid' 'reparse' $declaredCount $actual.Count 0 0 $null $manifestSha256 $false
            }
            $relative = $file.FullName.Substring($root.Length + 1).Replace('\', '/')
            if (-not (Test-Stage5ProbeSafeRelativePath $relative) -or $actual.ContainsKey($relative)) {
                return New-Stage5ProbeValidationResult 'BLOCKED' 's5_lifecycle_probe_bundle_invalid' 'file-path' $declaredCount $actual.Count 0 0 $null $manifestSha256 $false
            }
            $actual.Add($relative, $file)
        }
        $actualCount = $actual.Count
        $missingCount = @($declared.Keys | Where-Object { -not $actual.ContainsKey($_) }).Count
        $extraCount = @($actual.Keys | Where-Object { -not $declared.ContainsKey($_) }).Count
        if ($missingCount -gt 0 -or $extraCount -gt 0) {
            return New-Stage5ProbeValidationResult 'BLOCKED' 's5_lifecycle_probe_bundle_incomplete' 'file-count' $declaredCount $actualCount $missingCount $extraCount $null $manifestSha256 $actual.ContainsKey($script:Stage5ProbeEntryPoint)
        }
        foreach ($relative in $declared.Keys) {
            $file = $actual[$relative]
            $expected = $declared[$relative]
            if ([long]$file.Length -ne [long]$expected.size -or
                (Get-FileHash -Algorithm SHA256 -LiteralPath $file.FullName).Hash -cne [string]$expected.sha256) {
                return New-Stage5ProbeValidationResult 'BLOCKED' 's5_lifecycle_probe_bundle_hash_mismatch' 'file-hash' $declaredCount $actualCount 0 0 $null $manifestSha256 $actual.ContainsKey($script:Stage5ProbeEntryPoint)
            }
        }
        return New-Stage5ProbeValidationResult 'PASS' $null 'complete' $declaredCount $actualCount 0 0 $null $manifestSha256 $true
    }
    catch {
        return New-Stage5ProbeValidationResult 'BLOCKED' 's5_lifecycle_probe_bundle_invalid' 'validation' $declaredCount $actualCount 0 0 $null $manifestSha256 $false
    }
}

function Test-Stage5LifecycleProbeTransport(
    [string]$ArchivePath,
    [string]$ExpectedArchiveSha256,
    [string]$ExpectedManifestSha256,
    [int]$ExpectedFileCount = -1) {
    $archiveSha256 = $null
    $manifestSha256 = $null
    $declaredCount = 0
    $actualCount = 0
    try {
        $archive = [IO.Path]::GetFullPath($ArchivePath)
        if (-not (Test-Path -LiteralPath $archive -PathType Leaf) -or (Test-Stage5ProbeReparsePoint $archive)) {
            return New-Stage5ProbeValidationResult 'BLOCKED' 's5_lifecycle_probe_transport_invalid' 'transport-path' 0 0 0 0 $null $null $false
        }
        $archiveItem = Get-Item -LiteralPath $archive
        if ($archiveItem.Length -le 0 -or $archiveItem.Length -gt $script:Stage5ProbeMaxExpandedBytes) {
            return New-Stage5ProbeValidationResult 'BLOCKED' 's5_lifecycle_probe_transport_invalid' 'transport-size' 0 0 0 0 $null $null $false
        }
        $archiveSha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $archive).Hash
        if (-not [string]::IsNullOrWhiteSpace($ExpectedArchiveSha256) -and
            ([string]$ExpectedArchiveSha256 -cnotmatch '^[0-9A-F]{64}$' -or $archiveSha256 -cne $ExpectedArchiveSha256)) {
            return New-Stage5ProbeValidationResult 'BLOCKED' 's5_lifecycle_probe_transport_hash_mismatch' 'transport-hash' 0 0 0 0 $archiveSha256 $null $false
        }
        $stream = [IO.File]::Open($archive, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
        try {
            $zip = [IO.Compression.ZipArchive]::new($stream, [IO.Compression.ZipArchiveMode]::Read, $false)
            try {
                if ($zip.Entries.Count -lt 2 -or $zip.Entries.Count -gt ($script:Stage5ProbeMaxFiles + 1)) {
                    return New-Stage5ProbeValidationResult 'BLOCKED' 's5_lifecycle_probe_transport_invalid' 'entry-count' 0 0 0 0 $archiveSha256 $null $false
                }
                $entries = [Collections.Generic.Dictionary[string,object]]::new([StringComparer]::Ordinal)
                foreach ($entry in $zip.Entries) {
                    $relative = [string]$entry.FullName
                    if (-not (Test-Stage5ProbeSafeRelativePath $relative) -or $entries.ContainsKey($relative) -or
                        [long]$entry.Length -lt 0 -or [long]$entry.Length -gt $script:Stage5ProbeMaxEntryBytes) {
                        return New-Stage5ProbeValidationResult 'BLOCKED' 's5_lifecycle_probe_transport_invalid' 'entry-path' 0 0 0 0 $archiveSha256 $null $false
                    }
                    $entries.Add($relative, $entry)
                }
                if (-not $entries.ContainsKey($script:Stage5ProbeManifestName)) {
                    return New-Stage5ProbeValidationResult 'BLOCKED' 's5_lifecycle_probe_transport_incomplete' 'manifest' 0 ($entries.Count - 1) 1 0 $archiveSha256 $null $false
                }
                $manifestEntry = $entries[$script:Stage5ProbeManifestName]
                if ([long]$manifestEntry.Length -gt $script:Stage5ProbeMaxManifestBytes) {
                    return New-Stage5ProbeValidationResult 'BLOCKED' 's5_lifecycle_probe_transport_invalid' 'manifest-size' 0 ($entries.Count - 1) 0 0 $archiveSha256 $null $false
                }
                $manifestStream = $manifestEntry.Open()
                try {
                    $memory = [IO.MemoryStream]::new()
                    try { $manifestStream.CopyTo($memory); $manifestBytes = $memory.ToArray() }
                    finally { $memory.Dispose() }
                }
                finally { $manifestStream.Dispose() }
                $manifestSha256 = Get-Stage5ProbeBytesSha256 $manifestBytes
                if (-not [string]::IsNullOrWhiteSpace($ExpectedManifestSha256) -and
                    ([string]$ExpectedManifestSha256 -cnotmatch '^[0-9A-F]{64}$' -or $manifestSha256 -cne $ExpectedManifestSha256)) {
                    return New-Stage5ProbeValidationResult 'BLOCKED' 's5_lifecycle_probe_manifest_mismatch' 'manifest-hash' 0 ($entries.Count - 1) 0 0 $archiveSha256 $manifestSha256 $false
                }
                $manifest = [Text.Encoding]::UTF8.GetString($manifestBytes) | ConvertFrom-Json
                try { $declared = Get-Stage5ProbeManifestDeclaration $manifest }
                catch {
                    $code = if ($_.Exception.Message -ceq 's5_lifecycle_probe_manifest_incomplete') { 's5_lifecycle_probe_transport_incomplete' } else { 's5_lifecycle_probe_transport_invalid' }
                    return New-Stage5ProbeValidationResult 'BLOCKED' $code 'manifest' 0 ($entries.Count - 1) 0 0 $archiveSha256 $manifestSha256 $false
                }
                $declaredCount = $declared.Count
                if ($ExpectedFileCount -ge 0 -and $declaredCount -ne $ExpectedFileCount) {
                    return New-Stage5ProbeValidationResult 'BLOCKED' 's5_lifecycle_probe_manifest_mismatch' 'declared-count' $declaredCount ($entries.Count - 1) 0 0 $archiveSha256 $manifestSha256 $false
                }
                $actual = [Collections.Generic.Dictionary[string,object]]::new([StringComparer]::Ordinal)
                foreach ($pair in $entries.GetEnumerator()) {
                    if ($pair.Key -cne $script:Stage5ProbeManifestName) { $actual.Add($pair.Key, $pair.Value) }
                }
                $actualCount = $actual.Count
                $missingCount = @($declared.Keys | Where-Object { -not $actual.ContainsKey($_) }).Count
                $extraCount = @($actual.Keys | Where-Object { -not $declared.ContainsKey($_) }).Count
                if ($missingCount -gt 0 -or $extraCount -gt 0) {
                    return New-Stage5ProbeValidationResult 'BLOCKED' 's5_lifecycle_probe_transport_incomplete' 'entry-count' $declaredCount $actualCount $missingCount $extraCount $archiveSha256 $manifestSha256 $actual.ContainsKey($script:Stage5ProbeEntryPoint)
                }
                foreach ($relative in $declared.Keys) {
                    $entry = $actual[$relative]
                    $expected = $declared[$relative]
                    if ([long]$entry.Length -ne [long]$expected.size) {
                        return New-Stage5ProbeValidationResult 'BLOCKED' 's5_lifecycle_probe_transport_hash_mismatch' 'entry-size' $declaredCount $actualCount 0 0 $archiveSha256 $manifestSha256 $actual.ContainsKey($script:Stage5ProbeEntryPoint)
                    }
                    $entryStream = $entry.Open()
                    try { $actualHash = Get-Stage5ProbeStreamSha256 $entryStream }
                    finally { $entryStream.Dispose() }
                    if ($actualHash -cne [string]$expected.sha256) {
                        return New-Stage5ProbeValidationResult 'BLOCKED' 's5_lifecycle_probe_transport_hash_mismatch' 'entry-hash' $declaredCount $actualCount 0 0 $archiveSha256 $manifestSha256 $actual.ContainsKey($script:Stage5ProbeEntryPoint)
                    }
                }
                return New-Stage5ProbeValidationResult 'PASS' $null 'complete' $declaredCount $actualCount 0 0 $archiveSha256 $manifestSha256 $true
            }
            finally { $zip.Dispose() }
        }
        finally { $stream.Dispose() }
    }
    catch {
        return New-Stage5ProbeValidationResult 'BLOCKED' 's5_lifecycle_probe_transport_invalid' 'transport-validation' $declaredCount $actualCount 0 0 $archiveSha256 $manifestSha256 $false
    }
}

function New-Stage5LifecycleProbeTransport([string]$BundleRoot, [string]$ArchivePath) {
    $source = Test-Stage5LifecycleProbeBundleCore $BundleRoot
    if ($source.status -cne 'PASS') { return $source }
    try {
        $root = [IO.Path]::GetFullPath($BundleRoot).TrimEnd('\')
        $archive = [IO.Path]::GetFullPath($ArchivePath)
        $parent = [IO.Path]::GetDirectoryName($archive)
        if ((Test-Stage5ProbeReparsePoint $parent) -or (Test-Path -LiteralPath $archive)) {
            return New-Stage5ProbeValidationResult 'BLOCKED' 's5_lifecycle_probe_transport_invalid' 'transport-output' $source.declaredCount 0 0 0 $null $source.manifestSha256 $false
        }
        [IO.Directory]::CreateDirectory($parent) | Out-Null
        $archiveStream = [IO.File]::Open($archive, [IO.FileMode]::CreateNew, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
        try {
            $zip = [IO.Compression.ZipArchive]::new($archiveStream, [IO.Compression.ZipArchiveMode]::Create, $true)
            try {
                $files = @(Get-ChildItem -LiteralPath $root -Recurse -File | Sort-Object { $_.FullName.Substring($root.Length + 1).Replace('\', '/') })
                foreach ($file in $files) {
                    if (Test-Stage5ProbeReparsePoint $file.FullName) { throw [InvalidOperationException]::new('reparse') }
                    $relative = $file.FullName.Substring($root.Length + 1).Replace('\', '/')
                    if (-not (Test-Stage5ProbeSafeRelativePath $relative)) { throw [InvalidOperationException]::new('path') }
                    $entry = $zip.CreateEntry($relative, [IO.Compression.CompressionLevel]::Optimal)
                    $entry.LastWriteTime = [DateTimeOffset]::new(1980, 1, 1, 0, 0, 0, [TimeSpan]::Zero)
                    $sourceStream = [IO.File]::OpenRead($file.FullName)
                    $entryStream = $entry.Open()
                    try { $sourceStream.CopyTo($entryStream) }
                    finally { $entryStream.Dispose(); $sourceStream.Dispose() }
                }
            }
            finally { $zip.Dispose() }
        }
        finally { $archiveStream.Dispose() }
        return Test-Stage5LifecycleProbeTransport -ArchivePath $archive `
            -ExpectedArchiveSha256 (Get-FileHash -Algorithm SHA256 -LiteralPath $archive).Hash `
            -ExpectedManifestSha256 $source.manifestSha256 -ExpectedFileCount $source.declaredCount
    }
    catch {
        return New-Stage5ProbeValidationResult 'BLOCKED' 's5_lifecycle_probe_transport_invalid' 'transport-create' $source.declaredCount 0 0 0 $null $source.manifestSha256 $false
    }
}

function Expand-Stage5LifecycleProbeTransport(
    [string]$ArchivePath,
    [string]$DestinationRoot,
    [string]$ApprovedRoot,
    [string]$ExpectedArchiveSha256,
    [string]$ExpectedManifestSha256,
    [int]$ExpectedFileCount) {
    $transport = Test-Stage5LifecycleProbeTransport -ArchivePath $ArchivePath `
        -ExpectedArchiveSha256 $ExpectedArchiveSha256 -ExpectedManifestSha256 $ExpectedManifestSha256 `
        -ExpectedFileCount $ExpectedFileCount
    if ($transport.status -cne 'PASS') { return $transport }
    try {
        $approved = [IO.Path]::GetFullPath($ApprovedRoot).TrimEnd('\')
        $destination = [IO.Path]::GetFullPath($DestinationRoot).TrimEnd('\')
        $tempPrefix = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
        if (-not $approved.StartsWith($tempPrefix, [StringComparison]::OrdinalIgnoreCase) -or
            -not [IO.Path]::GetFileName($approved).StartsWith('YuanshuStage5', [StringComparison]::Ordinal) -or
            -not $destination.StartsWith($approved + '\', [StringComparison]::OrdinalIgnoreCase) -or
            (Test-Stage5ProbeReparsePoint $approved) -or (Test-Stage5ProbeReparsePoint $destination) -or
            (Test-Path -LiteralPath $destination)) {
            return New-Stage5ProbeValidationResult 'BLOCKED' 's5_lifecycle_probe_transport_expand_failed' 'expanded-path' $transport.declaredCount 0 0 0 $transport.archiveSha256 $transport.manifestSha256 $false
        }
        [IO.Directory]::CreateDirectory($destination) | Out-Null
        $stream = [IO.File]::Open([IO.Path]::GetFullPath($ArchivePath), [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
        try {
            $zip = [IO.Compression.ZipArchive]::new($stream, [IO.Compression.ZipArchiveMode]::Read, $false)
            try {
                foreach ($entry in $zip.Entries) {
                    if (-not (Test-Stage5ProbeSafeRelativePath ([string]$entry.FullName))) { throw [InvalidOperationException]::new('entry') }
                    $target = [IO.Path]::GetFullPath((Join-Path $destination ([string]$entry.FullName).Replace('/', '\')))
                    if (-not $target.StartsWith($destination + '\', [StringComparison]::OrdinalIgnoreCase)) { throw [InvalidOperationException]::new('escape') }
                    $targetParent = [IO.Path]::GetDirectoryName($target)
                    [IO.Directory]::CreateDirectory($targetParent) | Out-Null
                    if (Test-Stage5ProbeReparsePoint $targetParent) { throw [InvalidOperationException]::new('reparse') }
                    $entryStream = $entry.Open()
                    $targetStream = [IO.File]::Open($target, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
                    try { $entryStream.CopyTo($targetStream) }
                    finally { $targetStream.Dispose(); $entryStream.Dispose() }
                }
            }
            finally { $zip.Dispose() }
        }
        finally { $stream.Dispose() }
        $expanded = Test-Stage5LifecycleProbeBundleCore $destination
        if ($expanded.status -cne 'PASS') { return $expanded }
        if ($expanded.declaredCount -ne $ExpectedFileCount -or $expanded.manifestSha256 -cne $ExpectedManifestSha256) {
            return New-Stage5ProbeValidationResult 'BLOCKED' 's5_lifecycle_probe_manifest_mismatch' 'expanded-manifest' $expanded.declaredCount $expanded.actualCount 0 0 $transport.archiveSha256 $expanded.manifestSha256 $expanded.entryPointPresent
        }
        return New-Stage5ProbeValidationResult 'PASS' $null 'expanded-complete' $expanded.declaredCount $expanded.actualCount 0 0 $transport.archiveSha256 $expanded.manifestSha256 $expanded.entryPointPresent
    }
    catch {
        return New-Stage5ProbeValidationResult 'BLOCKED' 's5_lifecycle_probe_transport_expand_failed' 'expanded-write' $transport.declaredCount 0 0 0 $transport.archiveSha256 $transport.manifestSha256 $false
    }
}
