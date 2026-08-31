param(
    [Parameter(Mandatory = $true)]
    [string]$BundleRoot,

    [Parameter(Mandatory = $true)]
    [string]$ResultPath
)

$ErrorActionPreference = 'Stop'
$transportScript = Join-Path $PSScriptRoot 'Stage5LifecycleProbeTransport.ps1'

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

try {
    if (-not (Test-Path -LiteralPath $transportScript -PathType Leaf)) {
        Complete 'BLOCKED' 's5_lifecycle_probe_bundle_invalid' 1 ([ordered]@{ phase = 'validator' })
    }
    . $transportScript
    $validation = Test-Stage5LifecycleProbeBundleCore $BundleRoot
    $details = [ordered]@{
        phase = [string]$validation.phase
        runtimeIdentifier = 'win-x64'
        selfContained = $validation.status -ceq 'PASS'
        fileCount = [int]$validation.actualCount
        declaredCount = [int]$validation.declaredCount
        actualCount = [int]$validation.actualCount
        missingCount = [int]$validation.missingCount
        extraCount = [int]$validation.extraCount
        manifestSha256 = [string]$validation.manifestSha256
        entryPointPresent = [bool]$validation.entryPointPresent
    }
    if ($validation.status -cne 'PASS') {
        Complete 'BLOCKED' ([string]$validation.errorCode) 1 $details
    }
    Complete 'PASS' $null 0 $details
}
catch {
    Complete 'BLOCKED' 's5_lifecycle_probe_bundle_invalid' 1 ([ordered]@{ phase = 'validation' })
}
