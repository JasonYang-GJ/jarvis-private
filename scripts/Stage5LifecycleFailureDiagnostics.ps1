$ErrorActionPreference = 'Stop'

function New-Stage5LifecycleDiagnosticState {
    return [pscustomobject]@{
        CurrentPhase = 'initializing'
        Processes = [Collections.Generic.List[object]]::new()
        PairIdentities = [Collections.Generic.List[object]]::new()
        ProbeValidations = [Collections.Generic.List[object]]::new()
    }
}

function Add-Stage5LifecycleProbeValidation(
    $State,
    [ValidateSet('transport-mapped', 'transport-local', 'expanded-bundle', 'entrypoint')]
    [string]$Layer,
    $Validation) {
    if ($null -eq $State -or $null -eq $State.ProbeValidations -or $null -eq $Validation -or
        [string]$Validation.status -cnotin @('PASS', 'BLOCKED') -or
        [string]$Validation.phase -cnotmatch '^[a-z0-9]+(?:-[a-z0-9]+)*$') {
        throw [InvalidOperationException]::new('s5_lifecycle_probe_evidence_invalid')
    }
    $errorCode = [string]$Validation.errorCode
    if (-not [string]::IsNullOrWhiteSpace($errorCode) -and $errorCode -cnotmatch '^s5_lifecycle_[a-z0-9_]+$') {
        throw [InvalidOperationException]::new('s5_lifecycle_probe_evidence_invalid')
    }
    $archiveSha = if ([string]$Validation.archiveSha256 -cmatch '^[0-9A-F]{64}$') { [string]$Validation.archiveSha256 } else { $null }
    $manifestSha = if ([string]$Validation.manifestSha256 -cmatch '^[0-9A-F]{64}$') { [string]$Validation.manifestSha256 } else { $null }
    $counts = @(
        [int]$Validation.declaredCount,
        [int]$Validation.actualCount,
        [int]$Validation.missingCount,
        [int]$Validation.extraCount)
    if (@($counts | Where-Object { $_ -lt 0 -or $_ -gt 512 }).Count -gt 0) {
        throw [InvalidOperationException]::new('s5_lifecycle_probe_evidence_invalid')
    }
    $State.ProbeValidations.Add([pscustomobject][ordered]@{
        layer = $Layer
        status = [string]$Validation.status
        errorCode = if ([string]::IsNullOrWhiteSpace($errorCode)) { $null } else { $errorCode }
        phase = [string]$Validation.phase
        declaredCount = $counts[0]
        actualCount = $counts[1]
        missingCount = $counts[2]
        extraCount = $counts[3]
        archiveSha256 = $archiveSha
        manifestSha256 = $manifestSha
        entryPointPresent = [bool]$Validation.entryPointPresent
    })
}

function Set-Stage5LifecyclePhase($State, [string]$Phase) {
    if ($null -eq $State -or [string]$Phase -cnotmatch '^[a-z0-9]+(?:-[a-z0-9]+)*$') {
        throw [InvalidOperationException]::new('s5_lifecycle_phase_invalid')
    }
    $State.CurrentPhase = $Phase
}

function Get-Stage5LifecyclePhaseFailureCode([string]$Phase) {
    if ([string]$Phase -cnotmatch '^[a-z0-9]+(?:-[a-z0-9]+)*$') {
        return 's5_lifecycle_phase_failed'
    }
    return 's5_lifecycle_' + $Phase.Replace('-', '_') + '_failed'
}

function ConvertTo-Stage5LifecycleSafeProductVersion($Value) {
    $text = [string]$Value
    if ([string]::IsNullOrWhiteSpace($text)) { return $null }
    $text = $text.Trim()
    if ($text -cnotmatch '^[0-9]+\.[0-9]+\.[0-9]+(?:\.[0-9]+)?(?:\+[0-9a-f]{40})?$') { return $null }
    return $text
}

function ConvertTo-Stage5LifecycleSafeFileVersion($Value) {
    $text = [string]$Value
    if ([string]::IsNullOrWhiteSpace($text)) { return $null }
    $text = $text.Trim()
    if ($text -cnotmatch '^[0-9]+\.[0-9]+\.[0-9]+\.[0-9]+$') { return $null }
    return $text
}

function New-Stage5LifecyclePairComponentEvidence {
    return [pscustomobject][ordered]@{
        filePresent = $false
        metadataRead = $false
        productVersionPresent = $false
        productVersionValid = $false
        productVersion = $null
        fileVersionPresent = $false
        fileVersionValid = $false
        fileVersion = $null
    }
}

function Get-Stage5LifecyclePairIdentity(
    $State,
    [string]$InstallRoot,
    [scriptblock]$VersionReader) {
    if ($null -eq $State -or $null -eq $State.PairIdentities) {
        throw [InvalidOperationException]::new('s5_lifecycle_diagnostics_invalid')
    }
    $record = [pscustomobject][ordered]@{
        phase = [string]$State.CurrentPhase
        client = New-Stage5LifecyclePairComponentEvidence
        host = New-Stage5LifecyclePairComponentEvidence
    }
    $State.PairIdentities.Add($record)

    $components = @(
        [pscustomobject]@{ evidence = $record.client; path = Join-Path $InstallRoot 'ScreenGuide.DesktopClient.exe' },
        [pscustomobject]@{ evidence = $record.host; path = Join-Path $InstallRoot 'ScreenGuide.DesktopHost.exe' })
    foreach ($component in $components) {
        try { $component.evidence.filePresent = Test-Path -LiteralPath $component.path -PathType Leaf -ErrorAction Stop }
        catch { $component.evidence.filePresent = $false }
        if (-not $component.evidence.filePresent) { continue }

        try {
            $info = if ($null -eq $VersionReader) {
                [Diagnostics.FileVersionInfo]::GetVersionInfo($component.path)
            } else {
                & $VersionReader $component.path
            }
            if ($null -eq $info) { throw [InvalidOperationException]::new('metadata unavailable') }
            $component.evidence.metadataRead = $true
            $productText = [string]$info.ProductVersion
            $fileText = [string]$info.FileVersion
            $component.evidence.productVersionPresent = -not [string]::IsNullOrWhiteSpace($productText)
            $component.evidence.fileVersionPresent = -not [string]::IsNullOrWhiteSpace($fileText)
            $component.evidence.productVersion = ConvertTo-Stage5LifecycleSafeProductVersion $productText
            $component.evidence.fileVersion = ConvertTo-Stage5LifecycleSafeFileVersion $fileText
            $component.evidence.productVersionValid = $null -ne $component.evidence.productVersion
            $component.evidence.fileVersionValid = $null -ne $component.evidence.fileVersion
        }
        catch {
            $component.evidence.metadataRead = $false
        }
    }

    $all = @($record.client, $record.host)
    if (@($all | Where-Object { -not $_.filePresent }).Count -gt 0) {
        throw [InvalidOperationException]::new('s5_lifecycle_mixed_version_pair')
    }
    if (@($all | Where-Object { -not $_.metadataRead }).Count -gt 0) {
        throw [InvalidOperationException]::new('s5_lifecycle_pair_metadata_unreadable')
    }
    if (@($all | Where-Object { -not $_.productVersionPresent }).Count -gt 0) {
        throw [InvalidOperationException]::new('s5_lifecycle_pair_product_version_missing')
    }
    if (@($all | Where-Object { -not $_.fileVersionPresent }).Count -gt 0) {
        throw [InvalidOperationException]::new('s5_lifecycle_pair_file_version_missing')
    }
    if (@($all | Where-Object { -not $_.productVersionValid -or -not $_.fileVersionValid }).Count -gt 0) {
        throw [InvalidOperationException]::new('s5_lifecycle_pair_identity_invalid')
    }
    return [ordered]@{
        clientProductVersion = [string]$record.client.productVersion
        hostProductVersion = [string]$record.host.productVersion
        clientFileVersion = [string]$record.client.fileVersion
        hostFileVersion = [string]$record.host.fileVersion
    }
}

function Assert-Stage5LifecyclePairIdentity($Actual, $Expected) {
    $expectedProductVersion = ConvertTo-Stage5LifecycleSafeProductVersion $Expected.productVersion
    $expectedFileVersion = ConvertTo-Stage5LifecycleSafeFileVersion $Expected.fileVersion
    if ($null -eq $expectedProductVersion -or $null -eq $expectedFileVersion) {
        throw [InvalidOperationException]::new('s5_lifecycle_pair_expected_identity_invalid')
    }
    if ([string]$Actual.clientProductVersion -cne $expectedProductVersion -or
        [string]$Actual.hostProductVersion -cne $expectedProductVersion -or
        [string]$Actual.clientFileVersion -cne $expectedFileVersion -or
        [string]$Actual.hostFileVersion -cne $expectedFileVersion) {
        throw [InvalidOperationException]::new('s5_lifecycle_mixed_version_pair')
    }
}

function Invoke-Stage5LifecycleObservedProcess(
    $State,
    $Budget,
    [string]$FilePath,
    [string[]]$Arguments,
    [ValidateSet('installer', 'host', 'probe')] [string]$Kind,
    [string]$InstallerKind,
    [scriptblock]$StartProcessCommand) {
    if ($null -eq $State) { throw [InvalidOperationException]::new('s5_lifecycle_diagnostics_invalid') }
    if ($Kind -eq 'installer') {
        if ($null -eq $Budget -or @('InstallOrUpgrade', 'Uninstall') -cnotcontains $InstallerKind) {
            throw [InvalidOperationException]::new('s5_lifecycle_installer_execution_count_mismatch')
        }
        Enter-Stage5LifecycleInstallerExecution -Budget $Budget -Kind $InstallerKind
    }

    $record = [pscustomobject][ordered]@{
        phase = [string]$State.CurrentPhase
        processKind = $Kind
        installerKind = if ($Kind -eq 'installer') { $InstallerKind } else { $null }
        processStarted = $false
        processExited = $false
        exitCode = $null
    }
    $State.Processes.Add($record)

    try {
        $process = if ($null -eq $StartProcessCommand) {
            Start-Process -FilePath $FilePath -ArgumentList $Arguments -Wait -PassThru -WindowStyle Hidden
        } else {
            & $StartProcessCommand $FilePath $Arguments
        }
    }
    catch {
        throw [InvalidOperationException]::new('s5_lifecycle_' + $Kind + '_launch_failed')
    }
    if ($null -eq $process -or @($process.PSObject.Properties.Name) -cnotcontains 'ExitCode') {
        throw [InvalidOperationException]::new('s5_lifecycle_' + $Kind + '_launch_failed')
    }

    $record.processStarted = $true
    $record.processExited = $true
    $record.exitCode = [int]$process.ExitCode
    if ([int]$process.ExitCode -ne 0) {
        throw [InvalidOperationException]::new('s5_lifecycle_' + $Kind + '_failed')
    }
    return [int]$process.ExitCode
}

function Get-Stage5LifecycleSafePresence(
    [string]$InstallRoot,
    [string]$UninstallKey) {
    function Test-Presence([string]$Path, [switch]$Leaf) {
        try {
            if ($Leaf) { return Test-Path -LiteralPath $Path -PathType Leaf -ErrorAction Stop }
            return Test-Path -LiteralPath $Path -ErrorAction Stop
        }
        catch { return $false }
    }
    return [ordered]@{
        installRootPresent = Test-Presence $InstallRoot
        clientPresent = Test-Presence (Join-Path $InstallRoot 'ScreenGuide.DesktopClient.exe') -Leaf
        hostPresent = Test-Presence (Join-Path $InstallRoot 'ScreenGuide.DesktopHost.exe') -Leaf
        uninstallRegistrationPresent = Test-Presence $UninstallKey
    }
}

function Write-Stage5LifecycleFailureEvidence(
    [string]$EvidencePath,
    [string]$ErrorCode,
    $State,
    $InstallerExecutionBudget,
    $Presence) {
    $processes = @(
        $State.Processes | ForEach-Object {
            [ordered]@{
                phase = [string]$_.phase
                processKind = [string]$_.processKind
                installerKind = if ($null -eq $_.installerKind) { $null } else { [string]$_.installerKind }
                processStarted = [bool]$_.processStarted
                processExited = [bool]$_.processExited
                exitCode = if ($null -eq $_.exitCode) { $null } else { [int]$_.exitCode }
            }
        })
    $pairIdentities = @(
        $State.PairIdentities | ForEach-Object {
            $clientProduct = ConvertTo-Stage5LifecycleSafeProductVersion $_.client.productVersion
            $clientFile = ConvertTo-Stage5LifecycleSafeFileVersion $_.client.fileVersion
            $hostProduct = ConvertTo-Stage5LifecycleSafeProductVersion $_.host.productVersion
            $hostFile = ConvertTo-Stage5LifecycleSafeFileVersion $_.host.fileVersion
            [ordered]@{
                phase = [string]$_.phase
                client = [ordered]@{
                    filePresent = [bool]$_.client.filePresent
                    metadataRead = [bool]$_.client.metadataRead
                    productVersionPresent = [bool]$_.client.productVersionPresent
                    productVersionValid = $null -ne $clientProduct
                    productVersion = $clientProduct
                    fileVersionPresent = [bool]$_.client.fileVersionPresent
                    fileVersionValid = $null -ne $clientFile
                    fileVersion = $clientFile
                }
                host = [ordered]@{
                    filePresent = [bool]$_.host.filePresent
                    metadataRead = [bool]$_.host.metadataRead
                    productVersionPresent = [bool]$_.host.productVersionPresent
                    productVersionValid = $null -ne $hostProduct
                    productVersion = $hostProduct
                    fileVersionPresent = [bool]$_.host.fileVersionPresent
                    fileVersionValid = $null -ne $hostFile
                    fileVersion = $hostFile
                }
            }
        })
    $probeValidations = @(
        $State.ProbeValidations | ForEach-Object {
            [ordered]@{
                layer = [string]$_.layer
                status = [string]$_.status
                errorCode = if ($null -eq $_.errorCode) { $null } else { [string]$_.errorCode }
                phase = [string]$_.phase
                declaredCount = [int]$_.declaredCount
                actualCount = [int]$_.actualCount
                missingCount = [int]$_.missingCount
                extraCount = [int]$_.extraCount
                archiveSha256 = if ($null -eq $_.archiveSha256) { $null } else { [string]$_.archiveSha256 }
                manifestSha256 = if ($null -eq $_.manifestSha256) { $null } else { [string]$_.manifestSha256 }
                entryPointPresent = [bool]$_.entryPointPresent
            }
        })
    $result = [ordered]@{
        contractVersion = 1
        status = 'BLOCKED'
        errorCode = $ErrorCode
        phase = [string]$State.CurrentPhase
        finalized = $true
        networkRequests = 0
        providerRequests = 0
        credentialReads = 0
        installerExecutionBudget = $InstallerExecutionBudget
        hostExecutions = @($processes | Where-Object { $_.processKind -ceq 'host' -and $_.processStarted }).Count
        processes = $processes
        pairIdentities = $pairIdentities
        probeValidations = $probeValidations
        presence = [ordered]@{
            installRootPresent = [bool]$Presence.installRootPresent
            clientPresent = [bool]$Presence.clientPresent
            hostPresent = [bool]$Presence.hostPresent
            uninstallRegistrationPresent = [bool]$Presence.uninstallRegistrationPresent
        }
    }
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($EvidencePath))) | Out-Null
    [IO.File]::WriteAllText(
        [IO.Path]::GetFullPath($EvidencePath),
        (($result | ConvertTo-Json -Depth 20).Replace("`r`n", "`n") + "`n"),
        [Text.UTF8Encoding]::new($false))
    return $result
}

function Remove-Stage5LifecycleOwnedRuntime([string]$RuntimeRoot) {
    $resolved = [IO.Path]::GetFullPath($RuntimeRoot)
    $tempPrefix = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
    if (-not $resolved.StartsWith($tempPrefix, [StringComparison]::OrdinalIgnoreCase) -or
        -not [IO.Path]::GetFileName($resolved).StartsWith('YuanshuStage5Lifecycle-', [StringComparison]::Ordinal)) {
        throw [InvalidOperationException]::new('s5_lifecycle_cleanup_root_invalid')
    }
    if (Test-Path -LiteralPath $resolved) { Remove-Item -LiteralPath $resolved -Recurse -Force }
}
