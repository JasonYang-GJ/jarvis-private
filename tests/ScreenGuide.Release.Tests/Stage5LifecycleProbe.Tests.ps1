$ErrorActionPreference = 'Stop'

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$projectRoot = Join-Path $repoRoot 'tests\ScreenGuide.Stage5LifecycleProbe'
$prepareScript = Join-Path $repoRoot 'scripts\New-Stage5LifecycleProbeBundle.ps1'
$validateScript = Join-Path $repoRoot 'scripts\Test-Stage5LifecycleProbeBundle.ps1'
$transportScript = Join-Path $repoRoot 'scripts\Stage5LifecycleProbeTransport.ps1'
$testRoot = Join-Path ([IO.Path]::GetTempPath()) ('YuanshuStage5ProbeTests-' + [Guid]::NewGuid().ToString('N'))

function Assert-True([bool]$condition, [string]$message) {
    if (-not $condition) { throw $message }
}

function Get-TreeFingerprint([string]$path) {
    if (-not (Test-Path -LiteralPath $path)) { return 'ABSENT' }
    $root = [IO.Path]::GetFullPath($path).TrimEnd('\')
    $rows = @(
        Get-ChildItem -LiteralPath $root -Recurse -Force |
            Sort-Object FullName |
            ForEach-Object {
                $relative = $_.FullName.Substring($root.Length).TrimStart('\').Replace('\', '/')
                if ($_.PSIsContainer) { "D|$relative|$($_.LastWriteTimeUtc.Ticks)" }
                else { "F|$relative|$($_.Length)|$($_.LastWriteTimeUtc.Ticks)|$((Get-FileHash -Algorithm SHA256 -LiteralPath $_.FullName).Hash)" }
            })
    $bytes = [Text.Encoding]::UTF8.GetBytes(($rows -join "`n"))
    $sha = [Security.Cryptography.SHA256]::Create()
    try { return ([BitConverter]::ToString($sha.ComputeHash($bytes))).Replace('-', '') }
    finally { $sha.Dispose() }
}

function Invoke-Validator([string]$bundleRoot, [string]$resultPath) {
    $output = @(& powershell.exe -NoProfile -ExecutionPolicy Bypass -File $validateScript `
        -BundleRoot $bundleRoot -ResultPath $resultPath 2>&1)
    return [pscustomobject]@{
        ExitCode = $LASTEXITCODE
        Result = if (Test-Path -LiteralPath $resultPath) { Get-Content -Raw -LiteralPath $resultPath | ConvertFrom-Json } else { $null }
        Output = $output -join "`n"
    }
}

function Remove-ZipEntry([string]$source, [string]$destination, [string]$entryName) {
    Copy-Item -LiteralPath $source -Destination $destination
    $stream = [IO.File]::Open($destination, [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
    try {
        $zip = [IO.Compression.ZipArchive]::new($stream, [IO.Compression.ZipArchiveMode]::Update, $false)
        try {
            $entry = $zip.GetEntry($entryName)
            if ($null -eq $entry) { throw 'fixture entry missing' }
            $entry.Delete()
        }
        finally { $zip.Dispose() }
    }
    finally { $stream.Dispose() }
}

function Add-ZipEntry([string]$source, [string]$destination, [string]$entryName, [string]$content) {
    Copy-Item -LiteralPath $source -Destination $destination
    $stream = [IO.File]::Open($destination, [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
    try {
        $zip = [IO.Compression.ZipArchive]::new($stream, [IO.Compression.ZipArchiveMode]::Update, $false)
        try {
            $entry = $zip.CreateEntry($entryName, [IO.Compression.CompressionLevel]::NoCompression)
            $entry.LastWriteTime = [DateTimeOffset]::new(1980, 1, 1, 0, 0, 0, [TimeSpan]::Zero)
            $writer = [IO.StreamWriter]::new($entry.Open(), [Text.UTF8Encoding]::new($false))
            try { $writer.Write($content) }
            finally { $writer.Dispose() }
        }
        finally { $zip.Dispose() }
    }
    finally { $stream.Dispose() }
}

function Replace-ZipEntry([string]$source, [string]$destination, [string]$entryName, [string]$content) {
    Remove-ZipEntry $source $destination $entryName
    $temporary = $destination + '.replacement'
    Add-ZipEntry $destination $temporary $entryName $content
    Move-Item -LiteralPath $temporary -Destination $destination -Force
}

try {
    [IO.Directory]::CreateDirectory($testRoot) | Out-Null
    Assert-True (Test-Path -LiteralPath $prepareScript -PathType Leaf) 'Offline probe bundle preparation script must exist.'
    Assert-True (Test-Path -LiteralPath $validateScript -PathType Leaf) 'Probe bundle validator must exist.'
    Assert-True (Test-Path -LiteralPath $transportScript -PathType Leaf) 'Sealed probe transport implementation must exist.'

    $repoObjBefore = Get-TreeFingerprint (Join-Path $projectRoot 'obj')
    $repoBinBefore = Get-TreeFingerprint (Join-Path $projectRoot 'bin')
    $readOnlySourceRoot = Join-Path $testRoot 'read-only-source'
    [IO.Directory]::CreateDirectory($readOnlySourceRoot) | Out-Null
    foreach ($name in @('ScreenGuide.Stage5LifecycleProbe.csproj', 'Program.cs', 'packages.lock.json')) {
        Copy-Item -LiteralPath (Join-Path $projectRoot $name) -Destination (Join-Path $readOnlySourceRoot $name)
    }
    $readOnlyProject = Join-Path $readOnlySourceRoot 'ScreenGuide.Stage5LifecycleProbe.csproj'
    $sourceAcl = Get-Acl -LiteralPath $readOnlySourceRoot
    $denyWrite = [Security.AccessControl.FileSystemAccessRule]::new(
        [Security.Principal.WindowsIdentity]::GetCurrent().User,
        [Security.AccessControl.FileSystemRights]::Write,
        [Security.AccessControl.InheritanceFlags]'ContainerInherit, ObjectInherit',
        [Security.AccessControl.PropagationFlags]::None,
        [Security.AccessControl.AccessControlType]::Deny)
    $readOnlyAcl = Get-Acl -LiteralPath $readOnlySourceRoot
    [void]$readOnlyAcl.AddAccessRule($denyWrite)
    Set-Acl -LiteralPath $readOnlySourceRoot -AclObject $readOnlyAcl
    $sourceWriteDenied = $false
    try { [IO.File]::WriteAllText((Join-Path $readOnlySourceRoot 'write-probe.tmp'), 'must fail') }
    catch [UnauthorizedAccessException] { $sourceWriteDenied = $true }
    Assert-True $sourceWriteDenied 'Test source checkout must actually reject writes before preparation starts.'

    $intermediateRoot = Join-Path $testRoot 'fresh-intermediate'
    $bundleRoot = Join-Path $testRoot 'self-contained-bundle'
    $prepareResultPath = Join-Path $testRoot 'prepare-result.json'
    try {
        $prepareOutput = @(& powershell.exe -NoProfile -ExecutionPolicy Bypass -File $prepareScript `
            -ProjectPath $readOnlyProject -IntermediateRoot $intermediateRoot -OutputRoot $bundleRoot `
            -ResultPath $prepareResultPath 2>&1)
    }
    finally {
        Set-Acl -LiteralPath $readOnlySourceRoot -AclObject $sourceAcl
    }
    Assert-True ($LASTEXITCODE -eq 0) ('Fresh offline self-contained preparation must pass. ' + ($prepareOutput -join "`n"))
    $prepare = Get-Content -Raw -LiteralPath $prepareResultPath | ConvertFrom-Json
    Assert-True ($prepare.status -eq 'PASS' -and $prepare.selfContained -and $prepare.runtimeIdentifier -eq 'win-x64') 'Preparation evidence must prove a self-contained win-x64 bundle.'
    Assert-True ($prepare.networkRequests -eq 0 -and $prepare.remoteSources -eq 0) 'Preparation must prove no network or remote NuGet source.'
    Assert-True ($prepare.fileCount -gt 6) 'Self-contained bundle must contain the runtime, not only one executable.'
    Assert-True (Test-Path -LiteralPath (Join-Path $intermediateRoot 'project.assets.json') -PathType Leaf) 'Fresh locked restore must create project.assets.json in the owned intermediate root.'
    Assert-True (-not (Test-Path -LiteralPath (Join-Path $readOnlySourceRoot 'obj'))) 'Preparation must not create obj under the read-only source checkout.'
    Assert-True (-not (Test-Path -LiteralPath (Join-Path $readOnlySourceRoot 'bin'))) 'Preparation must not create bin under the read-only source checkout.'
    Assert-True ((Get-TreeFingerprint (Join-Path $projectRoot 'obj')) -ceq $repoObjBefore) 'Preparation must not change the repository obj fingerprint.'
    Assert-True ((Get-TreeFingerprint (Join-Path $projectRoot 'bin')) -ceq $repoBinBefore) 'Preparation must not change the repository bin fingerprint.'
    foreach ($required in @('ScreenGuide.Stage5LifecycleProbe.exe', 'hostfxr.dll', 'hostpolicy.dll', 'coreclr.dll', 'System.Private.CoreLib.dll', 'e_sqlite3.dll', 'lifecycle-probe-bundle.json')) {
        Assert-True (Test-Path -LiteralPath (Join-Path $bundleRoot $required) -PathType Leaf) "Self-contained bundle is missing $required."
    }

    $validation = Invoke-Validator $bundleRoot (Join-Path $testRoot 'validate-pass.json')
    Assert-True ($validation.ExitCode -eq 0 -and $validation.Result.status -eq 'PASS') 'Fresh bundle validation must pass.'
    Assert-True ($validation.Result.fileCount -eq $prepare.fileCount) 'Preparation and validation file counts must match.'

    . $transportScript
    $transportOne = Join-Path $testRoot 'probe-transport-one.zip'
    $transportTwo = Join-Path $testRoot 'probe-transport-two.zip'
    $createdOne = New-Stage5LifecycleProbeTransport -BundleRoot $bundleRoot -ArchivePath $transportOne
    $createdTwo = New-Stage5LifecycleProbeTransport -BundleRoot $bundleRoot -ArchivePath $transportTwo
    Assert-True ($createdOne.status -eq 'PASS' -and $createdTwo.status -eq 'PASS') 'Complete bundle must seal into a transport archive.'
    Assert-True ($createdOne.archiveSha256 -ceq $createdTwo.archiveSha256) 'Sealed transport must be deterministic for identical bundle bytes.'
    $transportValidation = Test-Stage5LifecycleProbeTransport -ArchivePath $transportOne `
        -ExpectedArchiveSha256 $createdOne.archiveSha256 -ExpectedManifestSha256 $createdOne.manifestSha256 `
        -ExpectedFileCount $createdOne.declaredCount
    Assert-True ($transportValidation.status -eq 'PASS') 'Sealed transport validation must pass before expansion.'
    $expandedRoot = Join-Path $testRoot 'expanded-probe'
    $expanded = Expand-Stage5LifecycleProbeTransport -ArchivePath $transportOne -DestinationRoot $expandedRoot `
        -ApprovedRoot $testRoot -ExpectedArchiveSha256 $createdOne.archiveSha256 `
        -ExpectedManifestSha256 $createdOne.manifestSha256 -ExpectedFileCount $createdOne.declaredCount
    Assert-True ($expanded.status -eq 'PASS') 'Sealed transport must expand and validate inside an approved local root.'
    Assert-True (Test-Path -LiteralPath (Join-Path $expandedRoot 'ScreenGuide.Stage5LifecycleProbe.exe') -PathType Leaf) 'Expanded transport must contain the exact probe entry point.'

    $volatileRoot = Join-Path $testRoot 'volatile-source-bundle'
    Copy-Item -LiteralPath $bundleRoot -Destination $volatileRoot -Recurse
    $sealedFromVolatile = Join-Path $testRoot 'sealed-before-source-loss.zip'
    $volatileTransport = New-Stage5LifecycleProbeTransport -BundleRoot $volatileRoot -ArchivePath $sealedFromVolatile
    Remove-Item -LiteralPath (Join-Path $volatileRoot 'hostfxr.dll') -Force
    $volatileSourceAfter = Test-Stage5LifecycleProbeBundleCore $volatileRoot
    $sealedAfterSourceLoss = Test-Stage5LifecycleProbeTransport -ArchivePath $sealedFromVolatile `
        -ExpectedArchiveSha256 $volatileTransport.archiveSha256 -ExpectedManifestSha256 $volatileTransport.manifestSha256 `
        -ExpectedFileCount $volatileTransport.declaredCount
    Assert-True ($volatileSourceAfter.status -eq 'BLOCKED' -and $volatileSourceAfter.missingCount -eq 1) 'Loose source loss must remain detectable.'
    Assert-True ($sealedAfterSourceLoss.status -eq 'PASS') 'Sealed transport must remain complete after the loose source later loses a file.'

    $missingArchive = Join-Path $testRoot 'transport-missing.zip'
    Remove-ZipEntry $transportOne $missingArchive 'hostfxr.dll'
    $missingArchiveResult = Test-Stage5LifecycleProbeTransport -ArchivePath $missingArchive `
        -ExpectedManifestSha256 $createdOne.manifestSha256 -ExpectedFileCount $createdOne.declaredCount
    Assert-True ($missingArchiveResult.errorCode -eq 's5_lifecycle_probe_transport_incomplete' -and
        $missingArchiveResult.missingCount -eq 1 -and $missingArchiveResult.extraCount -eq 0) 'Missing archive entry must fail closed with numeric counts.'

    $extraArchive = Join-Path $testRoot 'transport-extra.zip'
    Add-ZipEntry $transportOne $extraArchive 'unexpected-runtime.dll' 'extra'
    $extraArchiveResult = Test-Stage5LifecycleProbeTransport -ArchivePath $extraArchive `
        -ExpectedManifestSha256 $createdOne.manifestSha256 -ExpectedFileCount $createdOne.declaredCount
    Assert-True ($extraArchiveResult.errorCode -eq 's5_lifecycle_probe_transport_incomplete' -and
        $extraArchiveResult.missingCount -eq 0 -and $extraArchiveResult.extraCount -eq 1) 'Extra archive entry must fail closed with numeric counts.'

    $tamperedArchive = Join-Path $testRoot 'transport-tampered.zip'
    Replace-ZipEntry $transportOne $tamperedArchive 'hostfxr.dll' 'tampered-runtime'
    $tamperedArchiveResult = Test-Stage5LifecycleProbeTransport -ArchivePath $tamperedArchive `
        -ExpectedManifestSha256 $createdOne.manifestSha256 -ExpectedFileCount $createdOne.declaredCount
    Assert-True ($tamperedArchiveResult.errorCode -eq 's5_lifecycle_probe_transport_hash_mismatch') 'Tampered archive entry must fail closed.'

    $corruptArchive = Join-Path $testRoot 'transport-corrupt.zip'
    [IO.File]::WriteAllText($corruptArchive, 'not-a-zip', [Text.UTF8Encoding]::new($false))
    $corruptArchiveResult = Test-Stage5LifecycleProbeTransport -ArchivePath $corruptArchive
    Assert-True ($corruptArchiveResult.errorCode -eq 's5_lifecycle_probe_transport_invalid') 'Corrupt archive must fail closed.'

    $zipSlipArchive = Join-Path $testRoot 'transport-zip-slip.zip'
    Add-ZipEntry $transportOne $zipSlipArchive '../escape.dll' 'escape'
    $zipSlipResult = Test-Stage5LifecycleProbeTransport -ArchivePath $zipSlipArchive
    Assert-True ($zipSlipResult.errorCode -eq 's5_lifecycle_probe_transport_invalid' -and $zipSlipResult.phase -eq 'entry-path') 'Zip-slip entry must fail closed before expansion.'

    $manifestMismatch = Test-Stage5LifecycleProbeTransport -ArchivePath $transportOne `
        -ExpectedArchiveSha256 $createdOne.archiveSha256 -ExpectedManifestSha256 ('0' * 64) `
        -ExpectedFileCount $createdOne.declaredCount
    Assert-True ($manifestMismatch.errorCode -eq 's5_lifecycle_probe_manifest_mismatch') 'Manifest identity mismatch must fail closed.'

    $reparseTarget = Join-Path $testRoot 'transport-reparse-target'
    $reparsePath = Join-Path $testRoot 'transport-reparse-link'
    [IO.Directory]::CreateDirectory($reparseTarget) | Out-Null
    Copy-Item -LiteralPath $transportOne -Destination (Join-Path $reparseTarget 'transport.zip')
    try {
        [void](New-Item -ItemType Junction -Path $reparsePath -Target $reparseTarget)
        $reparseResult = Test-Stage5LifecycleProbeTransport -ArchivePath (Join-Path $reparsePath 'transport.zip')
        Assert-True ($reparseResult.errorCode -eq 's5_lifecycle_probe_transport_invalid' -and $reparseResult.phase -eq 'transport-path') 'Reparse-point archive path must fail closed.'
    }
    finally {
        if (Test-Path -LiteralPath $reparsePath) { [IO.Directory]::Delete($reparsePath) }
    }

    Remove-Item -LiteralPath (Join-Path $expandedRoot 'hostfxr.dll') -Force
    $expandedAfterLoss = Test-Stage5LifecycleProbeBundleCore $expandedRoot
    Assert-True ($expandedAfterLoss.errorCode -eq 's5_lifecycle_probe_bundle_incomplete' -and $expandedAfterLoss.missingCount -eq 1) 'Expanded bundle deletion must fail closed before entrypoint execution.'

    $probe = Join-Path $bundleRoot 'ScreenGuide.Stage5LifecycleProbe.exe'
    $database = Join-Path $testRoot ('YuanshuStage5ProbeFixture-' + [Guid]::NewGuid().ToString('N') + '.db')
    $passOutput = Join-Path $testRoot 'pass.json'
    $previous = $env:SCREEN_GUIDE_STAGE5_PROBE_TEST_MODE
    try {
        $env:SCREEN_GUIDE_STAGE5_PROBE_TEST_MODE = '1'
        & $probe --database $database --expected-schema 10 --output $passOutput --fixture-schema 10 | Out-Null
    }
    finally { $env:SCREEN_GUIDE_STAGE5_PROBE_TEST_MODE = $previous }
    Assert-True ($LASTEXITCODE -eq 0) 'Exact schema fixture must pass from the self-contained bundle.'
    $pass = Get-Content -Raw -LiteralPath $passOutput | ConvertFrom-Json
    Assert-True ($pass.passed -and $pass.schemaVersion -eq 10 -and $pass.integrityOk) 'Probe PASS evidence must be bounded and exact.'

    $failOutput = Join-Path $testRoot 'mismatch.json'
    & $probe --database $database --expected-schema 11 --output $failOutput | Out-Null
    Assert-True ($LASTEXITCODE -ne 0) 'Mismatched schema must fail closed.'
    $failure = Get-Content -Raw -LiteralPath $failOutput | ConvertFrom-Json
    Assert-True ($failure.errorCode -eq 's5_lifecycle_probe_schema_mismatch') 'Schema mismatch must use a stable code.'
    Assert-True (-not (($failure | ConvertTo-Json -Depth 10).Contains($testRoot))) 'Probe evidence must not contain local paths.'

    $tamperedRoot = Join-Path $testRoot 'tampered-bundle'
    Copy-Item -LiteralPath $bundleRoot -Destination $tamperedRoot -Recurse
    [IO.File]::AppendAllText((Join-Path $tamperedRoot 'hostfxr.dll'), 'tamper', [Text.UTF8Encoding]::new($false))
    $tampered = Invoke-Validator $tamperedRoot (Join-Path $testRoot 'validate-tampered.json')
    Assert-True ($tampered.ExitCode -ne 0 -and $tampered.Result.errorCode -eq 's5_lifecycle_probe_bundle_hash_mismatch') 'Tampered runtime file must fail closed.'

    $missingRoot = Join-Path $testRoot 'missing-bundle'
    Copy-Item -LiteralPath $bundleRoot -Destination $missingRoot -Recurse
    Remove-Item -LiteralPath (Join-Path $missingRoot 'hostfxr.dll') -Force
    $missingBundle = Invoke-Validator $missingRoot (Join-Path $testRoot 'validate-missing.json')
    Assert-True ($missingBundle.ExitCode -ne 0 -and $missingBundle.Result.errorCode -eq 's5_lifecycle_probe_bundle_incomplete' -and
        $missingBundle.Result.details.declaredCount -eq $prepare.fileCount -and
        $missingBundle.Result.details.actualCount -eq ($prepare.fileCount - 1) -and
        $missingBundle.Result.details.missingCount -eq 1 -and $missingBundle.Result.details.extraCount -eq 0) 'Loose bundle failure evidence must contain safe numeric counts.'

    $emptyCache = Join-Path $testRoot 'empty-offline-cache'
    [IO.Directory]::CreateDirectory($emptyCache) | Out-Null
    $missingCacheResult = Join-Path $testRoot 'missing-cache.json'
    $missingCacheOutput = @(& powershell.exe -NoProfile -ExecutionPolicy Bypass -File $prepareScript `
        -ProjectPath $readOnlyProject -IntermediateRoot (Join-Path $testRoot 'missing-cache-intermediate') `
        -OutputRoot (Join-Path $testRoot 'missing-cache-output') -ResultPath $missingCacheResult `
        -OfflinePackageCache $emptyCache 2>&1)
    $missingCache = Get-Content -Raw -LiteralPath $missingCacheResult | ConvertFrom-Json
    Assert-True ($LASTEXITCODE -ne 0 -and $missingCache.errorCode -eq 's5_lifecycle_probe_offline_cache_incomplete') ('Empty cache must fail closed without fallback. ' + ($missingCacheOutput -join "`n"))
    Assert-True (-not (Test-Path -LiteralPath (Join-Path $testRoot 'missing-cache-output\lifecycle-probe-bundle.json'))) 'Failed preparation must not emit a valid-looking bundle manifest.'

    foreach ($script in @($prepareScript, $validateScript, $transportScript)) {
        $text = Get-Content -Raw -LiteralPath $script
        Assert-True (-not $text.Contains('http://') -and -not $text.Contains('https://')) 'Probe bundle scripts must not contain remote sources.'
    }
    Write-Host 'Stage5 lifecycle probe: fresh offline publish + deterministic sealed transport + 10 transport/bundle failures + 2 probe cases passed'
}
finally {
    $resolved = [IO.Path]::GetFullPath($testRoot)
    $prefix = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
    if ($resolved.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase) -and
        [IO.Path]::GetFileName($resolved).StartsWith('YuanshuStage5ProbeTests-', [StringComparison]::Ordinal)) {
        if (Test-Path -LiteralPath $resolved) { Remove-Item -LiteralPath $resolved -Recurse -Force }
    }
}
