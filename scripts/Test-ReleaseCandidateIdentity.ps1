param(
    [Parameter(Mandatory = $true)]
    [string]$RepositoryRoot,

    [Parameter(Mandatory = $true)]
    [string]$ExpectedSourceSha,

    [Parameter(Mandatory = $true)]
    [string]$ExpectedParentSha,

    [Parameter(Mandatory = $true)]
    [string]$ReleaseVersion,

    [Parameter(Mandatory = $true)]
    [string]$AttributionContractPath,

    [switch]$ConfigurationOnly
)

$ErrorActionPreference = 'Stop'
$productName = -join @([char]0x5143, [char]0x67A2)
$candidateLabel = -join @([char]0x5019, [char]0x9009, [char]0x5B89, [char]0x88C5, [char]0x5305)
$candidateOutputBase = $productName + '-V0.7.0-' + $candidateLabel
$legacyOutputName = $productName + '-V0.6.0-' + (-join @([char]0x5B89, [char]0x88C5, [char]0x5305)) + '.exe'

function Complete([bool]$passed, [string]$errorCode, [int]$exitCode) {
    [ordered]@{
        passed = $passed
        errorCode = $errorCode
        releaseVersion = $ReleaseVersion
        expectedSourceSha = $ExpectedSourceSha
        expectedParentSha = $ExpectedParentSha
        configurationOnly = [bool]$ConfigurationOnly
    } | ConvertTo-Json -Compress | Write-Output
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
    try { return ([BitConverter]::ToString($sha.ComputeHash([Text.UTF8Encoding]::new($false).GetBytes($normalized)))).Replace('-', '') }
    finally { $sha.Dispose() }
}

function Assert-RelativePath([string]$path, [string]$errorCode) {
    if ([string]::IsNullOrWhiteSpace($path) -or [IO.Path]::IsPathRooted($path) -or
        $path.Replace('\', '/').Split('/') -contains '..') {
        throw [InvalidOperationException]::new($errorCode)
    }
}

try {
    if ($ReleaseVersion -cne '0.7.0') {
        throw [InvalidOperationException]::new('release_candidate_version_mismatch')
    }
    if ($ExpectedSourceSha -notmatch '^[0-9a-f]{40}$' -or $ExpectedParentSha -notmatch '^[0-9a-f]{40}$') {
        throw [InvalidOperationException]::new('release_candidate_sha_invalid')
    }

    $repoRoot = [IO.Path]::GetFullPath($RepositoryRoot).TrimEnd('\')
    $contractPath = [IO.Path]::GetFullPath($AttributionContractPath)
    $candidateContractRoot = [IO.Path]::GetFullPath((Join-Path $repoRoot 'distribution\release-candidates\v0.7.0')).TrimEnd('\') + '\'
    if (-not $contractPath.StartsWith($candidateContractRoot, [StringComparison]::OrdinalIgnoreCase) -or
        -not (Test-Path -LiteralPath $contractPath -PathType Leaf)) {
        throw [InvalidOperationException]::new('release_candidate_contract_out_of_bounds')
    }

    $propsPath = Join-Path $repoRoot 'Directory.Build.props'
    $installerPath = Join-Path $repoRoot 'installer\ScreenGuideDesktop.iss'
    $releaseScriptPath = Join-Path $repoRoot 'scripts\build-desktop-release.ps1'
    foreach ($required in @($propsPath, $installerPath, $releaseScriptPath)) {
        if (-not (Test-Path -LiteralPath $required -PathType Leaf)) {
            throw [InvalidOperationException]::new('release_candidate_required_file_missing')
        }
    }

    [xml]$props = Get-Content -Raw -LiteralPath $propsPath
    $versionGroup = @($props.Project.PropertyGroup | Where-Object { $null -ne $_.Version })[0]
    if ([string]$versionGroup.Version -cne '0.7.0' -or
        [string]$versionGroup.AssemblyVersion -cne '0.7.0.0' -or
        [string]$versionGroup.FileVersion -cne '0.7.0.0' -or
        [string]$versionGroup.InformationalVersion -cne '0.7.0') {
        throw [InvalidOperationException]::new('release_candidate_managed_version_mismatch')
    }

    $installerText = [IO.File]::ReadAllText($installerPath, [Text.UTF8Encoding]::new($false, $true))
    if ($installerText -notmatch '(?m)^#define MyAppVersion "0\.7\.0"\s*$' -or
        $installerText -notmatch ('(?m)^OutputBaseFilename=' + [Regex]::Escape($candidateOutputBase) + '\s*$') -or
        $installerText -notmatch '(?m)^VersionInfoVersion=\{#MyAppVersion\}\.0\s*$' -or
        $installerText -notmatch '(?m)^VersionInfoProductVersion=\{#MyAppVersion\}\s*$' -or
        $installerText -notmatch '(?m)^AppId=\{\{E2B9C242-2965-48BC-B2C6-CF83A2B11953\}\s*$') {
        throw [InvalidOperationException]::new('release_candidate_installer_identity_mismatch')
    }

    $releaseScriptText = Get-Content -Raw -LiteralPath $releaseScriptPath
    if ($releaseScriptText.Contains('V0.6.0_STAGE4_C0_PAYLOAD_ATTRIBUTION.json') -or
        $releaseScriptText.Contains($legacyOutputName)) {
        throw [InvalidOperationException]::new('release_candidate_legacy_identity_fallback')
    }

    try { $contract = Get-Content -Raw -LiteralPath $contractPath | ConvertFrom-Json }
    catch { throw [InvalidOperationException]::new('release_candidate_contract_invalid') }
    if ($contract.schemaVersion -ne 1 -or
        [string]$contract.contractId -cne 'yuanshu-v0.7.0-candidate-attribution' -or
        [string]$contract.candidateStatus -cne 'INTERNAL_CANDIDATE_ONLY' -or
        [string]$contract.releaseVersion -cne $ReleaseVersion -or
        [string]$contract.baselineParentSha -cne $ExpectedParentSha -or
        [string]$contract.generationMode -cne 'PUBLISH_OUTPUT_EXACT_SOURCE_MAPPING_V1') {
        throw [InvalidOperationException]::new('release_candidate_contract_mismatch')
    }

    Assert-RelativePath ([string]$contract.baseBundleManifest.path) 'release_candidate_base_bundle_path_invalid'
    Assert-RelativePath ([string]$contract.candidateNotice.sourcePath) 'release_candidate_notice_path_invalid'
    Assert-RelativePath ([string]$contract.candidateNotice.bundlePath) 'release_candidate_notice_path_invalid'
    $baseBundlePath = [IO.Path]::GetFullPath((Join-Path $repoRoot ([string]$contract.baseBundleManifest.path)))
    $noticePath = [IO.Path]::GetFullPath((Join-Path $repoRoot ([string]$contract.candidateNotice.sourcePath)))
    if (-not (Test-Path -LiteralPath $baseBundlePath -PathType Leaf) -or
        (Get-RawSha256 $baseBundlePath) -cne ([string]$contract.baseBundleManifest.rawSha256).ToUpperInvariant()) {
        throw [InvalidOperationException]::new('release_candidate_base_bundle_hash_mismatch')
    }
    if ([string]$contract.candidateNotice.hashProfile -cne 'UTF8_NO_BOM_LF_V1' -or
        -not (Test-Path -LiteralPath $noticePath -PathType Leaf) -or
        (Get-CanonicalUtf8LfSha256 $noticePath) -cne ([string]$contract.candidateNotice.sha256).ToUpperInvariant()) {
        throw [InvalidOperationException]::new('release_candidate_notice_hash_mismatch')
    }
    if (@($contract.forbiddenPayloadManifestPaths).Count -eq 0 -or
        -not (@($contract.forbiddenPayloadManifestPaths) -contains 'docs/baselines/V0.6.0_STAGE4_C0_PAYLOAD_ATTRIBUTION.json')) {
        throw [InvalidOperationException]::new('release_candidate_legacy_manifest_guard_missing')
    }

    if (-not $ConfigurationOnly) {
        $actualRoot = (& git -C $repoRoot rev-parse --show-toplevel 2>$null).Trim()
        if ($LASTEXITCODE -ne 0 -or [IO.Path]::GetFullPath($actualRoot).TrimEnd('\') -cne $repoRoot) {
            throw [InvalidOperationException]::new('release_candidate_repository_identity_mismatch')
        }
        $actualSourceSha = (& git -C $repoRoot rev-parse HEAD 2>$null).Trim()
        $actualParentSha = (& git -C $repoRoot rev-parse HEAD^ 2>$null).Trim()
        if ($actualSourceSha -cne $ExpectedSourceSha) {
            throw [InvalidOperationException]::new('release_candidate_source_sha_mismatch')
        }
        if ($actualParentSha -cne $ExpectedParentSha) {
            throw [InvalidOperationException]::new('release_candidate_parent_sha_mismatch')
        }
        $status = @(& git -C $repoRoot status --porcelain=v1 --untracked-files=all 2>$null)
        if ($LASTEXITCODE -ne 0 -or $status.Count -ne 0) {
            throw [InvalidOperationException]::new('release_candidate_git_not_clean')
        }
    }

    Complete $true $null 0
}
catch {
    $code = [string]$_.Exception.Message
    if ($code -notmatch '^release_candidate_[a-z0-9_]+$') { $code = 'release_candidate_identity_invalid' }
    Complete $false $code 1
}
