param(
    [Parameter(Mandatory = $true)]
    [string]$BundleRoot,

    [Parameter(Mandatory = $true)]
    [string]$ResultPath
)

$ErrorActionPreference = 'Stop'
$manifestName = 'lifecycle-probe-bundle.json'

function Write-Json([string]$path, $value) {
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($path))) | Out-Null
    [IO.File]::WriteAllText(
        [IO.Path]::GetFullPath($path),
        (($value | ConvertTo-Json -Depth 20).Replace("`r`n", "`n") + "`n"),
        [Text.UTF8Encoding]::new($false))
}

function Complete([string]$status, [string]$errorCode, [int]$exitCode, $details) {
    $result = [ordered]@{
        contractVersion = 1
        status = $status
        errorCode = $errorCode
        finalized = $true
        networkRequests = 0
        remoteSources = 0
        fileCount = if ($null -eq $details) { 0 } else { [int]$details.fileCount }
        details = $details
    }
    Write-Json $ResultPath $result
    Write-Output ($result | ConvertTo-Json -Depth 10 -Compress)
    exit $exitCode
}

function Has-ReparsePoint([string]$path) {
    $current = [IO.Path]::GetFullPath($path)
    while (-not [string]::IsNullOrWhiteSpace($current)) {
        if (Test-Path -LiteralPath $current) {
            if (([IO.File]::GetAttributes($current) -band [IO.FileAttributes]::ReparsePoint) -ne 0) { return $true }
        }
        $parent = [IO.Directory]::GetParent($current)
        if ($null -eq $parent) { break }
        $current = $parent.FullName
    }
    return $false
}

function Is-SafeRelativePath([string]$path) {
    if ([string]::IsNullOrWhiteSpace($path) -or [IO.Path]::IsPathRooted($path) -or
        $path.Contains('\') -or $path.Contains(':')) { return $false }
    $segments = @($path.Split('/'))
    return $segments.Count -gt 0 -and @($segments | Where-Object { $_ -eq '' -or $_ -eq '.' -or $_ -eq '..' }).Count -eq 0
}

try {
    $root = [IO.Path]::GetFullPath($BundleRoot).TrimEnd('\')
    if (-not (Test-Path -LiteralPath $root -PathType Container) -or (Has-ReparsePoint $root)) {
        Complete 'BLOCKED' 's5_lifecycle_probe_bundle_invalid' 1 ([ordered]@{ phase = 'path' })
    }
    $manifestPath = Join-Path $root $manifestName
    if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf) -or (Has-ReparsePoint $manifestPath)) {
        Complete 'BLOCKED' 's5_lifecycle_probe_bundle_incomplete' 1 ([ordered]@{ phase = 'manifest' })
    }
    $manifest = Get-Content -Raw -LiteralPath $manifestPath | ConvertFrom-Json
    if ($manifest.contractVersion -ne 1 -or [string]$manifest.bundleKind -cne 'self-contained-probe' -or
        [string]$manifest.runtimeIdentifier -cne 'win-x64' -or -not [bool]$manifest.selfContained -or
        [string]$manifest.entryPoint -cne 'ScreenGuide.Stage5LifecycleProbe.exe' -or $null -eq $manifest.files) {
        Complete 'BLOCKED' 's5_lifecycle_probe_bundle_invalid' 1 ([ordered]@{ phase = 'manifest' })
    }

    $declared = @{}
    foreach ($entry in @($manifest.files)) {
        $relative = [string]$entry.relativePath
        if (-not (Is-SafeRelativePath $relative) -or $declared.ContainsKey($relative) -or
            [string]$entry.sha256 -cnotmatch '^[0-9A-F]{64}$' -or [long]$entry.size -lt 0) {
            Complete 'BLOCKED' 's5_lifecycle_probe_bundle_invalid' 1 ([ordered]@{ phase = 'manifest-entry' })
        }
        $declared[$relative] = $entry
    }
    foreach ($required in @(
        'ScreenGuide.Stage5LifecycleProbe.exe',
        'hostfxr.dll',
        'hostpolicy.dll',
        'coreclr.dll',
        'System.Private.CoreLib.dll',
        'e_sqlite3.dll')) {
        if (-not $declared.ContainsKey($required)) {
            Complete 'BLOCKED' 's5_lifecycle_probe_bundle_incomplete' 1 ([ordered]@{ phase = 'required-runtime' })
        }
    }

    $actualFiles = @(
        Get-ChildItem -LiteralPath $root -Recurse -File |
            Where-Object { $_.FullName -cne $manifestPath } |
            Sort-Object FullName)
    if ($actualFiles.Count -ne $declared.Count) {
        Complete 'BLOCKED' 's5_lifecycle_probe_bundle_incomplete' 1 ([ordered]@{ phase = 'file-count' })
    }
    foreach ($file in $actualFiles) {
        if (Has-ReparsePoint $file.FullName) {
            Complete 'BLOCKED' 's5_lifecycle_probe_bundle_invalid' 1 ([ordered]@{ phase = 'reparse' })
        }
        $relative = $file.FullName.Substring($root.Length + 1).Replace('\', '/')
        if (-not $declared.ContainsKey($relative)) {
            Complete 'BLOCKED' 's5_lifecycle_probe_bundle_incomplete' 1 ([ordered]@{ phase = 'unmapped-file' })
        }
        $entry = $declared[$relative]
        if ([long]$entry.size -ne [long]$file.Length -or
            [string]$entry.sha256 -cne (Get-FileHash -Algorithm SHA256 -LiteralPath $file.FullName).Hash) {
            Complete 'BLOCKED' 's5_lifecycle_probe_bundle_hash_mismatch' 1 ([ordered]@{ phase = 'file-hash' })
        }
    }

    Complete 'PASS' $null 0 ([ordered]@{
        runtimeIdentifier = 'win-x64'
        selfContained = $true
        fileCount = $declared.Count
        manifestSha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $manifestPath).Hash
    })
}
catch {
    Complete 'BLOCKED' 's5_lifecycle_probe_bundle_invalid' 1 ([ordered]@{ phase = 'validation' })
}
