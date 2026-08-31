$ErrorActionPreference = 'Stop'

function New-Stage5LifecycleDiagnosticState {
    return [pscustomobject]@{
        CurrentPhase = 'initializing'
        Processes = [Collections.Generic.List[object]]::new()
    }
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
