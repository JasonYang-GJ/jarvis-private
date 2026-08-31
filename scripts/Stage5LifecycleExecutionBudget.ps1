function New-Stage5LifecycleExecutionBudget {
    param(
        [Parameter(Mandatory = $true)]
        [int]$InstallerExecutions,

        [Parameter(Mandatory = $true)]
        [int]$InstallOrUpgradeExecutions,

        [Parameter(Mandatory = $true)]
        [int]$UninstallExecutions
    )

    if ($InstallerExecutions -ne 5 -or
        $InstallOrUpgradeExecutions -ne 3 -or
        $UninstallExecutions -ne 2 -or
        $InstallerExecutions -ne ($InstallOrUpgradeExecutions + $UninstallExecutions)) {
        throw 's5_lifecycle_installer_execution_count_mismatch'
    }

    return [pscustomobject]@{
        PlannedInstallerExecutions = $InstallerExecutions
        PlannedInstallOrUpgradeExecutions = $InstallOrUpgradeExecutions
        PlannedUninstallExecutions = $UninstallExecutions
        InstallerExecutions = 0
        InstallOrUpgradeExecutions = 0
        UninstallExecutions = 0
    }
}

function Enter-Stage5LifecycleInstallerExecution {
    param(
        [Parameter(Mandatory = $true)]
        $Budget,

        [Parameter(Mandatory = $true)]
        [ValidateSet('InstallOrUpgrade', 'Uninstall')]
        [string]$Kind
    )

    $kindCount = if ($Kind -eq 'InstallOrUpgrade') {
        [int]$Budget.InstallOrUpgradeExecutions
    } else {
        [int]$Budget.UninstallExecutions
    }
    $kindLimit = if ($Kind -eq 'InstallOrUpgrade') {
        [int]$Budget.PlannedInstallOrUpgradeExecutions
    } else {
        [int]$Budget.PlannedUninstallExecutions
    }

    if ([int]$Budget.InstallerExecutions -ge [int]$Budget.PlannedInstallerExecutions -or
        $kindCount -ge $kindLimit) {
        throw 's5_lifecycle_installer_execution_budget_exceeded'
    }

    $Budget.InstallerExecutions = [int]$Budget.InstallerExecutions + 1
    if ($Kind -eq 'InstallOrUpgrade') {
        $Budget.InstallOrUpgradeExecutions = [int]$Budget.InstallOrUpgradeExecutions + 1
    } else {
        $Budget.UninstallExecutions = [int]$Budget.UninstallExecutions + 1
    }
}

function Get-Stage5LifecycleExecutionBudgetSnapshot {
    param(
        [Parameter(Mandatory = $true)]
        $Budget
    )

    return [ordered]@{
        installerExecutions = [int]$Budget.InstallerExecutions
        installOrUpgradeExecutions = [int]$Budget.InstallOrUpgradeExecutions
        uninstallExecutions = [int]$Budget.UninstallExecutions
        plannedInstallerExecutions = [int]$Budget.PlannedInstallerExecutions
        plannedInstallOrUpgradeExecutions = [int]$Budget.PlannedInstallOrUpgradeExecutions
        plannedUninstallExecutions = [int]$Budget.PlannedUninstallExecutions
    }
}
