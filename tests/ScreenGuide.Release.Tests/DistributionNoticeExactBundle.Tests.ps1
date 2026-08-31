$ErrorActionPreference = 'Stop'

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$bundleRoot = Join-Path $repoRoot 'distribution\licenses'
$manifestPath = Join-Path $bundleRoot 'bundle-manifest.json'
$indexPath = Join-Path $bundleRoot 'notice-index.json'
$payloadPath = Join-Path $repoRoot 'docs\baselines\V0.6.0_STAGE4_C0_PAYLOAD_ATTRIBUTION.json'
$validator = Join-Path $repoRoot 'scripts\Test-DistributionNoticeBundle.ps1'
$installerTranslation = Join-Path $repoRoot 'installer\ChineseSimplified.isl'
$testRoot = Join-Path ([IO.Path]::GetTempPath()) ('screen-guide-exact-notice-tests-' + [Guid]::NewGuid().ToString('N'))

$expectedRawSources = @(
    'https://raw.githubusercontent.com/dotnet/runtime/v10.0.11/LICENSE.TXT',
    'https://raw.githubusercontent.com/dotnet/runtime/v10.0.11/THIRD-PARTY-NOTICES.TXT',
    'https://raw.githubusercontent.com/dotnet/windowsdesktop/v10.0.11/LICENSE',
    'https://raw.githubusercontent.com/dotnet/wpf/v10.0.11/THIRD-PARTY-NOTICES.TXT',
    'https://raw.githubusercontent.com/dotnet/winforms/v10.0.11/THIRD-PARTY-NOTICES.TXT',
    'https://raw.githubusercontent.com/dotnet/dotnet/e2f47b0110ed922f21a1522da67279133ce28f32/LICENSE.TXT',
    'https://raw.githubusercontent.com/dotnet/dotnet/e2f47b0110ed922f21a1522da67279133ce28f32/THIRD-PARTY-NOTICES.txt',
    'https://raw.githubusercontent.com/dotnet/dotnet/f7d90799ce4ef09a0bb257852a57248d2a8fb8dd/LICENSE.TXT',
    'https://raw.githubusercontent.com/dotnet/dotnet/f7d90799ce4ef09a0bb257852a57248d2a8fb8dd/THIRD-PARTY-NOTICES.txt',
    'https://raw.githubusercontent.com/naudio/NAudio/v2.2.1/license.txt',
    'https://raw.githubusercontent.com/ericsink/SQLitePCL.raw/v2.1.12/LICENSE.TXT',
    'https://raw.githubusercontent.com/ericsink/SQLitePCL.raw/v2.1.12/NOTICE.TXT',
    'https://raw.githubusercontent.com/k2-fsa/sherpa-onnx/142807252687d81b40d6315f23470a1512a00de3/LICENSE',
    'https://raw.githubusercontent.com/microsoft/onnxruntime/v1.27.0/LICENSE',
    'https://raw.githubusercontent.com/microsoft/onnxruntime/v1.27.0/ThirdPartyNotices.txt',
    'https://raw.githubusercontent.com/jrsoftware/issrc/is-6_7_3/license.txt',
    'https://raw.githubusercontent.com/kira-96/Inno-Setup-Chinese-Simplified-Translation/6da09d23e14443d4cf8f07b1c5fd821bfe459788/ChineseSimplified.isl',
    'https://raw.githubusercontent.com/kira-96/Inno-Setup-Chinese-Simplified-Translation/6da09d23e14443d4cf8f07b1c5fd821bfe459788/LICENSE'
)
$expectedReferenceSources = @(
    'https://learn.microsoft.com/en-us/legal/windows-sdk/redist',
    'https://www.nuget.org/packages/Microsoft.Windows.SDK.NET.Ref/10.0.19041.57',
    'https://dotnet.microsoft.com/en-us/dotnet_library_license.htm',
    'https://sqlite.org/copyright.html'
)

function Assert-True([bool]$condition, [string]$message) {
    if (-not $condition) { throw $message }
}

function Assert-Equal($expected, $actual, [string]$message) {
    if ($expected -ne $actual) { throw "$message Expected=[$expected] Actual=[$actual]" }
}

function Get-CanonicalSha256([string]$path) {
    $bytes = [IO.File]::ReadAllBytes($path)
    $offset = if ($bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF) { 3 } else { 0 }
    $text = [Text.UTF8Encoding]::new($false, $true).GetString($bytes, $offset, $bytes.Length - $offset)
    $normalizedBytes = [Text.UTF8Encoding]::new($false).GetBytes($text.Replace("`r`n", "`n").Replace("`r", "`n"))
    $sha = [Security.Cryptography.SHA256]::Create()
    try { return ([BitConverter]::ToString($sha.ComputeHash($normalizedBytes))).Replace('-', '') }
    finally { $sha.Dispose() }
}

function Invoke-Validator([string]$root, [string]$manifest, [string]$payload, [string]$includePath) {
    $output = @(& powershell.exe -NoProfile -ExecutionPolicy Bypass -File $validator `
        -ManifestPath $manifest `
        -BundleRoot $root `
        -PayloadManifestPath $payload `
        -ApprovedStagingRoot ([IO.Path]::GetDirectoryName($includePath)) `
        -InnoIncludePath $includePath 2>&1)
    return [pscustomobject]@{ ExitCode = $LASTEXITCODE; Output = ($output -join "`n") }
}

try {
    [IO.Directory]::CreateDirectory($testRoot) | Out-Null
    $manifest = Get-Content -Raw -LiteralPath $manifestPath | ConvertFrom-Json
    $index = Get-Content -Raw -LiteralPath $indexPath | ConvertFrom-Json
    $payload = Get-Content -Raw -LiteralPath $payloadPath | ConvertFrom-Json

    Assert-Equal 'VERIFIED' $manifest.bundleStatus 'The exact material bundle must be verified.'
    Assert-Equal 0 @($manifest.remainingBlockers).Count 'A verified bundle cannot retain blockers.'
    Assert-Equal 66 @($manifest.components).Count 'The explicit catalog must retain 64 payload and two container components.'
    Assert-Equal 541 @($index.records).Count 'The index must contain 539 payload and two container mappings.'
    Assert-Equal 539 @($index.records | Where-Object artifactScope -eq 'installedPayload').Count 'Every payload row must remain mapped.'
    Assert-Equal 2 @($index.records | Where-Object artifactScope -eq 'installerContainer').Count 'Both installer entries must be mapped.'

    $observedSources = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $projectNoticePath = Join-Path $bundleRoot 'files\yuanshu-v0.6.0\PROPRIETARY-NOTICE.txt'
    $expectedProjectNoticeBase64 = '5YWD5p6iIFYwLjYuMCDku6XkuJPmnInnvJbor5Hkuqflk4HlvaLlvI/liIblj5HvvIzkuI3ljIXlkKvmiJbmjojmnYPlhazlvIDliIblj5Hpobnnm67mupDku6PnoIHjgILnrKzkuInmlrnnu4Tku7bliIbliKvlj5flhbbpmo/pmYQgTElDRU5TReOAgU5PVElDRSDlkozpgILnlKjmnaHmrL7nuqbmnZ/vvIzmnYPliKnlvZLlkITmnYPliKnkurrjgILkuqflk4Hlm77moIfnlLHkuqflk4HmiYDmnInogIXmjIflr7zlubbkvb/nlKggQ29kZXgg6L6F5Yqp55Sf5oiQ77yM57uP5Lq65bel6YCJ5oup77yb5pys5oqr6Zyy5LiN5Li75byg5Zu+5qCH5YW35pyJ54us5Y2g5p2D44CB5bey6I635ZWG5qCH5rOo5YaM44CB5b+F54S25YW35aSH54mI5p2D5L+d5oqk5oiW57ud5a+55LiN5L615p2D44CC5pys6K+05piO5LiN5p6E5oiQ5rOV5b6L5oSP6KeB77yM5Lmf5LiN6KGo56S65pWw5a2X562+5ZCN44CB5a6J6KOF5Y2H57qn55Sf5ZG95ZGo5pyf5oiW5aSW6YOo5YiG5Y+R5bey6I635pS+6KGM44CC'
    $expectedProjectNotice = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($expectedProjectNoticeBase64))
    $actualProjectNotice = [Text.Encoding]::UTF8.GetString([IO.File]::ReadAllBytes($projectNoticePath)).TrimEnd("`r", "`n")
    Assert-Equal $expectedProjectNotice $actualProjectNotice 'The project-authored distribution notice must remain exact.'
    foreach ($component in @($manifest.components)) {
        foreach ($axis in @('source', 'bundling', 'attribution', 'notice')) {
            Assert-Equal 'VERIFIED' ([string]$component.status.$axis) "Component $($component.componentId) axis $axis must be verified."
        }
        Assert-True (-not [string]::IsNullOrWhiteSpace([string]$component.sourceBinding.path)) "Component $($component.componentId) must bind a source evidence path."
        Assert-True ([string]$component.sourceBinding.sha256 -match '^[0-9A-F]{64}$') "Component $($component.componentId) must bind a canonical source hash."
        Assert-True ([string]$component.sourceBinding.rawSha256 -match '^[0-9A-F]{64}$') "Component $($component.componentId) must bind a raw source hash."
        $sourcePath = Join-Path $bundleRoot ([string]$component.sourceBinding.path).Replace('/', '\')
        Assert-True (Test-Path -LiteralPath $sourcePath -PathType Leaf) "Component $($component.componentId) source evidence file must exist."
        Assert-Equal ([string]$component.sourceBinding.sha256) (Get-CanonicalSha256 $sourcePath) "Component $($component.componentId) source hash must match."
        Assert-Equal ([string]$component.sourceBinding.rawSha256) (Get-FileHash -Algorithm SHA256 -LiteralPath $sourcePath).Hash "Component $($component.componentId) raw source hash must match."
        foreach ($url in @($component.sourceBinding.sourceUrls)) { $observedSources.Add([string]$url) | Out-Null }
        Assert-True (@($component.licenseNoticeFiles).Count -gt 0) "Component $($component.componentId) must have explicit material mappings."
        foreach ($file in @($component.licenseNoticeFiles)) {
            Assert-True ([string]$file.path -match '^files/.+') 'Every material must use the deterministic files/ layout.'
            $materialPath = Join-Path $bundleRoot ([string]$file.path).Replace('/', '\')
            Assert-True (Test-Path -LiteralPath $materialPath -PathType Leaf) "Material $($file.path) must exist."
            Assert-Equal ([string]$file.sha256) (Get-CanonicalSha256 $materialPath) "Material $($file.path) canonical hash must match."
            Assert-Equal ([string]$file.rawSha256) (Get-FileHash -Algorithm SHA256 -LiteralPath $materialPath).Hash "Material $($file.path) raw hash must match."
            foreach ($url in @($file.sourceUrls)) { $observedSources.Add([string]$url) | Out-Null }
        }
    }
    foreach ($url in @($expectedRawSources + $expectedReferenceSources)) {
        Assert-True ($observedSources.Contains($url)) "Exact approved source URL is missing: $url"
    }
    foreach ($url in $observedSources) {
        Assert-True ($url -eq 'PROJECT_AUTHORED' -or @($expectedRawSources + $expectedReferenceSources) -contains $url) "Unapproved or default-branch source URL found: $url"
    }

    $windowsSdk = $manifest.components | Where-Object componentId -eq 'payload-package-Microsoft.Windows.SDK.NET.Ref'
    Assert-Equal '10.0.19041.57' $windowsSdk.version 'WinSDK NET.Ref version must remain exact.'
    Assert-True (@($windowsSdk.licenseNoticeFiles.kind) -contains 'TERMS_REFERENCE') 'WinSDK must use a reference-only terms document.'
    Assert-Equal 1 @($payload.records | Where-Object frozenPath -eq 'Microsoft.Windows.SDK.NET.dll').Count 'The exact REDIST-listed DLL must exist in frozen payload.'
    Assert-Equal 0 @($payload.records | Where-Object { [string]$_.sourcePackage.packageId -eq 'Microsoft.Windows.SDK.BuildTools' }).Count 'WinSDK BuildTools must not be installed payload.'

    $translation = $manifest.components | Where-Object componentId -eq 'inno-chinese-simplified-translation'
    Assert-Equal '6da09d23e14443d4cf8f07b1c5fd821bfe459788' $translation.version 'Translation must be pinned to the exact Kira commit.'
    $translationSource = Join-Path $bundleRoot ([string]$translation.sourceBinding.path).Replace('/', '\')
    Assert-Equal (Get-CanonicalSha256 $translationSource) (Get-CanonicalSha256 $installerTranslation) 'Installer translation must equal the pinned source under UTF8_NO_BOM_LF_V1.'
    $inno = $manifest.components | Where-Object componentId -eq 'inno-installer-engine'
    Assert-Equal '6.7.3' $inno.version 'Inno container must be pinned to 6.7.3.'
    $releaseScript = Get-Content -Raw -LiteralPath (Join-Path $repoRoot 'scripts\build-desktop-release.ps1')
    Assert-True ($releaseScript.Contains("`$innoProductVersion -cne '6.7.3'")) 'Release compilation must fail closed unless the local Inno product version is exactly 6.7.3.'

    $rootNoticePath = Join-Path $bundleRoot ([string]$manifest.rootNotice.path).Replace('/', '\')
    Assert-True (Test-Path -LiteralPath $rootNoticePath -PathType Leaf) 'Root THIRD-PARTY-NOTICES index must exist.'
    Assert-Equal ([string]$manifest.rootNotice.sha256) (Get-CanonicalSha256 $rootNoticePath) 'Root notice canonical hash must match.'

    $includeOne = Join-Path $testRoot 'stage-one\distribution-notice-files.iss'
    $runOne = Invoke-Validator $bundleRoot $manifestPath $payloadPath $includeOne
    Assert-Equal 0 $runOne.ExitCode 'The real exact bundle must pass the validator.'
    Assert-True (($runOne.Output | ConvertFrom-Json).passed) 'The real validator result must report passed=true.'
    $includeText = Get-Content -Raw -LiteralPath $includeOne
    Assert-True (-not $includeText.Contains('*')) 'Generated include must not contain wildcards.'
    Assert-True (-not $includeText.Contains('skipifsourcedoesntexist')) 'Generated include must not make materials optional.'
    Assert-True ($includeText.Contains('THIRD-PARTY-NOTICES.txt')) 'Generated include must install the root notice.'
    Assert-True ($includeText.Contains('notice-index.json"; DestDir: "{app}\distribution"')) 'The machine-readable index must install under the distribution directory.'
    $includeTwo = Join-Path $testRoot 'stage-two\distribution-notice-files.iss'
    $runTwo = Invoke-Validator $bundleRoot $manifestPath $payloadPath $includeTwo
    Assert-Equal 0 $runTwo.ExitCode 'Repeated real validation must pass.'
    Assert-True ([Linq.Enumerable]::SequenceEqual([IO.File]::ReadAllBytes($includeOne), [IO.File]::ReadAllBytes($includeTwo))) 'Generated include must be byte-for-byte deterministic.'

    $tamperRoot = Join-Path $testRoot 'tampered-bundle'
    Copy-Item -LiteralPath $bundleRoot -Destination $tamperRoot -Recurse
    $tamperManifestPath = Join-Path $tamperRoot 'bundle-manifest.json'
    $tamperManifest = Get-Content -Raw -LiteralPath $tamperManifestPath | ConvertFrom-Json
    $tamperMaterial = Join-Path $tamperRoot ([string]$tamperManifest.components[0].licenseNoticeFiles[0].path).Replace('/', '\')
    [IO.File]::AppendAllText($tamperMaterial, "tampered`n", [Text.UTF8Encoding]::new($false))
    $tamperInclude = Join-Path $testRoot 'tamper-stage\distribution-notice-files.iss'
    $tamperRun = Invoke-Validator $tamperRoot $tamperManifestPath $payloadPath $tamperInclude
    Assert-True ($tamperRun.ExitCode -ne 0) 'Tampered material must fail closed.'
    Assert-True (@(($tamperRun.Output | ConvertFrom-Json).blockers | Where-Object { $_ -like 'license_notice_hash_mismatch:*' }).Count -gt 0) 'Tampered material must report a stable hash mismatch blocker.'
    Assert-True (-not (Test-Path -LiteralPath $tamperInclude)) 'Tampered material must not emit an include.'

    $defaultBranchRoot = Join-Path $testRoot 'default-branch-bundle'
    Copy-Item -LiteralPath $bundleRoot -Destination $defaultBranchRoot -Recurse
    $defaultManifestPath = Join-Path $defaultBranchRoot 'bundle-manifest.json'
    $defaultManifest = Get-Content -Raw -LiteralPath $defaultManifestPath | ConvertFrom-Json
    $defaultManifest.components[0].sourceBinding.sourceUrls = @('https://raw.githubusercontent.com/example/project/main/LICENSE')
    [IO.File]::WriteAllText($defaultManifestPath, (($defaultManifest | ConvertTo-Json -Depth 40).Replace("`r`n", "`n") + "`n"), [Text.UTF8Encoding]::new($false))
    $defaultInclude = Join-Path $testRoot 'default-stage\distribution-notice-files.iss'
    $defaultRun = Invoke-Validator $defaultBranchRoot $defaultManifestPath $payloadPath $defaultInclude
    Assert-True ($defaultRun.ExitCode -ne 0) 'Default-branch source substitution must fail closed.'
    Assert-True (@(($defaultRun.Output | ConvertFrom-Json).blockers) -contains 'source_reference_unpinned') 'Default-branch substitution must use the stable blocker.'

    Write-Host 'EXACT bundle positive and fail-closed cases: passed'
}
finally {
    $fullTestRoot = [IO.Path]::GetFullPath($testRoot)
    $tempPrefix = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
    if ($fullTestRoot.StartsWith($tempPrefix, [StringComparison]::OrdinalIgnoreCase) -and
        [IO.Path]::GetFileName($fullTestRoot).StartsWith('screen-guide-exact-notice-tests-', [StringComparison]::Ordinal)) {
        if (Test-Path -LiteralPath $fullTestRoot) { Remove-Item -LiteralPath $fullTestRoot -Recurse -Force }
    }
}
