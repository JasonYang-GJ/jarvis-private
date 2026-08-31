param(
    [Parameter(Mandatory = $true)]
    [string]$ProjectPath,

    [Parameter(Mandatory = $true)]
    [string]$IntermediateRoot,

    [Parameter(Mandatory = $true)]
    [string]$OutputRoot,

    [Parameter(Mandatory = $true)]
    [string]$ResultPath,

    [string]$OfflinePackageCache
)

$ErrorActionPreference = 'Stop'
$manifestName = 'lifecycle-probe-bundle.json'
$requiredPackages = @(
    'microsoft.data.sqlite\10.0.11',
    'microsoft.data.sqlite.core\10.0.11',
    'sqlitepclraw.bundle_e_sqlite3\2.1.12',
    'sqlitepclraw.core\2.1.12',
    'sqlitepclraw.lib.e_sqlite3\2.1.12',
    'sqlitepclraw.provider.e_sqlite3\2.1.12',
    'microsoft.netcore.app.runtime.win-x64\10.0.11')

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
        runtimeIdentifier = 'win-x64'
        selfContained = $status -eq 'PASS'
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

function Test-CompleteCache([string]$path) {
    if ([string]::IsNullOrWhiteSpace($path) -or -not (Test-Path -LiteralPath $path -PathType Container) -or
        (Has-ReparsePoint $path)) { return $false }
    foreach ($relative in $requiredPackages) {
        if (-not (Test-Path -LiteralPath (Join-Path $path $relative) -PathType Container)) { return $false }
    }
    return $true
}

function Resolve-OfflineCache {
    if (-not [string]::IsNullOrWhiteSpace($OfflinePackageCache)) {
        $explicit = [IO.Path]::GetFullPath($OfflinePackageCache)
        if (-not (Test-CompleteCache $explicit)) { return $null }
        return $explicit
    }
    $candidates = [Collections.Generic.List[string]]::new()
    if (-not [string]::IsNullOrWhiteSpace($env:NUGET_PACKAGES)) { $candidates.Add($env:NUGET_PACKAGES) }
    $locals = @(& dotnet nuget locals global-packages --list 2>$null)
    if ($LASTEXITCODE -eq 0) {
        foreach ($line in $locals) {
            if ([string]$line -match '^global-packages:\s*(.+)$') { $candidates.Add($Matches[1].Trim()) }
        }
    }
    if (-not [string]::IsNullOrWhiteSpace($env:USERPROFILE)) {
        $candidates.Add((Join-Path $env:USERPROFILE '.nuget\packages'))
    }
    if (-not [string]::IsNullOrWhiteSpace($env:NUGET_FALLBACK_PACKAGES)) {
        foreach ($candidate in $env:NUGET_FALLBACK_PACKAGES.Split(';')) {
            if (-not [string]::IsNullOrWhiteSpace($candidate)) { $candidates.Add($candidate) }
        }
    }
    foreach ($candidate in @($candidates | Select-Object -Unique)) {
        $resolved = [IO.Path]::GetFullPath($candidate)
        if (Test-CompleteCache $resolved) { return $resolved }
    }
    return $null
}

try {
    $project = [IO.Path]::GetFullPath($ProjectPath)
    $intermediate = [IO.Path]::GetFullPath($IntermediateRoot).TrimEnd('\')
    $output = [IO.Path]::GetFullPath($OutputRoot).TrimEnd('\')
    $result = [IO.Path]::GetFullPath($ResultPath)
    $tempPrefix = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
    $ownedRoot = [IO.Directory]::GetParent($intermediate).FullName.TrimEnd('\')
    foreach ($path in @($ownedRoot, $intermediate, $output, $result)) {
        if (-not $path.StartsWith($tempPrefix, [StringComparison]::OrdinalIgnoreCase) -or (Has-ReparsePoint $path)) {
            Complete 'BLOCKED' 's5_lifecycle_probe_output_root_invalid' 1 ([ordered]@{ phase = 'path' })
        }
    }
    if ([IO.Directory]::GetParent($output).FullName -cne $ownedRoot -or
        [IO.Path]::GetDirectoryName($result) -cne $ownedRoot -or
        $intermediate -ceq $output) {
        Complete 'BLOCKED' 's5_lifecycle_probe_output_root_invalid' 1 ([ordered]@{ phase = 'owned-root' })
    }
    if (-not (Test-Path -LiteralPath $project -PathType Leaf) -or
        -not (Test-Path -LiteralPath (Join-Path (Split-Path -Parent $project) 'packages.lock.json') -PathType Leaf)) {
        Complete 'BLOCKED' 's5_lifecycle_probe_lock_missing' 1 ([ordered]@{ phase = 'project' })
    }
    if ((Test-Path -LiteralPath $intermediate) -or (Test-Path -LiteralPath $output)) {
        Complete 'BLOCKED' 's5_lifecycle_probe_output_not_empty' 1 ([ordered]@{ phase = 'output' })
    }
    $cache = Resolve-OfflineCache
    if ([string]::IsNullOrWhiteSpace($cache)) {
        Complete 'BLOCKED' 's5_lifecycle_probe_offline_cache_incomplete' 1 ([ordered]@{ phase = 'offline-cache' })
    }
    $dotnetRoot = Split-Path -Parent (Get-Command dotnet).Source
    foreach ($pack in @(
        'packs\Microsoft.NETCore.App.Host.win-x64\10.0.11',
        'packs\Microsoft.NETCore.App.Ref\10.0.11')) {
        if (-not (Test-Path -LiteralPath (Join-Path $dotnetRoot $pack) -PathType Container)) {
            Complete 'BLOCKED' 's5_lifecycle_probe_offline_cache_incomplete' 1 ([ordered]@{ phase = 'sdk-pack' })
        }
    }

    [IO.Directory]::CreateDirectory($intermediate) | Out-Null
    [IO.Directory]::CreateDirectory($output) | Out-Null
    $intermediateProperty = $intermediate.TrimEnd('\') + '\'
    $compileOutput = Join-Path $intermediate 'bin'
    [IO.Directory]::CreateDirectory($compileOutput) | Out-Null
    $compileOutputProperty = $compileOutput.TrimEnd('\') + '\'
    $publishProperty = $output.TrimEnd('\') + '\'
    $redirectedBuildProperties = @(
        "-p:BaseIntermediateOutputPath=$intermediateProperty",
        "-p:MSBuildProjectExtensionsPath=$intermediateProperty",
        "-p:BaseOutputPath=$compileOutputProperty",
        "-p:OutputPath=$compileOutputProperty",
        "-p:PublishDir=$publishProperty")
    $restoreArguments = @(
        'restore', $project,
        '--locked-mode', '--runtime', 'win-x64', '--packages', $cache, '--source', $cache,
        '--disable-parallel', '--nologo',
        '-p:NuGetAudit=false', '-p:RestoreIgnoreFailedSources=false', '-p:RestorePackagesWithLockFile=true') +
        $redirectedBuildProperties
    $restoreOutput = @(& dotnet @restoreArguments 2>&1)
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath (Join-Path $intermediate 'project.assets.json') -PathType Leaf)) {
        Complete 'BLOCKED' 's5_lifecycle_probe_locked_restore_failed' 1 ([ordered]@{ phase = 'restore' })
    }
    $publishArguments = @(
        'publish', $project,
        '--configuration', 'Release', '--runtime', 'win-x64', '--self-contained', 'true', '--no-restore',
        '--nologo') + $redirectedBuildProperties
    $publishOutput = @(& dotnet @publishArguments 2>&1)
    if ($LASTEXITCODE -ne 0) {
        Complete 'BLOCKED' 's5_lifecycle_probe_publish_failed' 1 ([ordered]@{ phase = 'publish' })
    }

    $files = @(
        Get-ChildItem -LiteralPath $output -Recurse -File |
            Sort-Object FullName |
            ForEach-Object {
                [ordered]@{
                    relativePath = $_.FullName.Substring($output.Length + 1).Replace('\', '/')
                    size = [long]$_.Length
                    sha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $_.FullName).Hash
                }
            })
    $manifest = [ordered]@{
        contractVersion = 1
        bundleKind = 'self-contained-probe'
        targetFramework = 'net10.0'
        runtimeIdentifier = 'win-x64'
        selfContained = $true
        entryPoint = 'ScreenGuide.Stage5LifecycleProbe.exe'
        files = $files
    }
    $manifestPath = Join-Path $output $manifestName
    Write-Json $manifestPath $manifest
    $validationPath = Join-Path $intermediate 'bundle-validation.json'
    $validator = Join-Path $PSScriptRoot 'Test-Stage5LifecycleProbeBundle.ps1'
    $validationOutput = @(& powershell.exe -NoProfile -ExecutionPolicy Bypass -File $validator `
        -BundleRoot $output -ResultPath $validationPath 2>&1)
    if ($LASTEXITCODE -ne 0) {
        Complete 'BLOCKED' 's5_lifecycle_probe_bundle_invalid' 1 ([ordered]@{ phase = 'validation' })
    }
    $validation = Get-Content -Raw -LiteralPath $validationPath | ConvertFrom-Json
    Complete 'PASS' $null 0 ([ordered]@{
        fileCount = [int]$validation.details.fileCount
        manifestSha256 = [string]$validation.details.manifestSha256
        lockedRestore = $true
        offlineCache = 'explicit-local'
    })
}
catch {
    Complete 'BLOCKED' 's5_lifecycle_probe_preparation_failed' 1 ([ordered]@{ phase = 'preparation' })
}
