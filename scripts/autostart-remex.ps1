#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Installs, uninstalls, or checks the RemEx elevated logon auto-start task.

.DESCRIPTION
    RemEx no longer runs as a Windows Service. It is a single interactive
    user-session app that hosts the listener itself, always elevated (high
    integrity). This script registers a Task Scheduler "logon task" that starts
    RemEx automatically when you sign in, with the highest privileges and WITHOUT
    a UAC prompt. (A scheduled task set to "Run with highest privileges" is the
    supported way to auto-start an elevated app at logon with no consent dialog.)

    The filename is kept (install-service.ps1) so the installer and the in-app
    Settings screen continue to resolve it; the word "service" is historical.

.PARAMETER Action
    The action to perform: Install, Uninstall, Status, Start, or Stop.

.PARAMETER Username
    (Install only) Optional. The Windows account the logon task should run as and
    trigger for. Format: DOMAIN\User or .\User. Defaults to the current user
    (the person running this script / installing RemEx).

.PARAMETER Password
    Accepted for backward compatibility only and ignored. A logon task running at
    the interactive token needs no stored password.

.PARAMETER HostPath
    Path to Remex.Agent.exe (or its folder). Used by the installer to point the
    task at the installed binary.

.EXAMPLE
    .\install-service.ps1 -Action Install
    .\install-service.ps1 -Action Install -Username ".\Connor"
    .\install-service.ps1 -Action Status
    .\install-service.ps1 -Action Uninstall
#>
param(
    [Parameter(Mandatory)]
    [ValidateSet("Install", "Uninstall", "Status", "Start", "Stop")]
    [string]$Action,

    [Parameter()]
    [string]$Username,

    [Parameter()]
    [string]$Password,

    [Parameter()]
    [string]$HostPath = ""
)

# Scheduled-task identity. Keep "RemEx" stable — the in-app launch-at-login toggle
# (StartupRegistrationService) and the verification steps query this exact name.
$TaskName      = "RemEx"
$DisplayName   = "RemEx"
$Description   = "Starts RemEx at sign-in, elevated, so your PC is reachable from your phone."
$ProjectDir    = Join-Path $PSScriptRoot "..\remex.agent"
$EventLogName  = "Application"
$EventSource   = "Remex.Agent"
$LegacyService = "RemexHost"   # pre-2.0 Windows Service — removed on install/uninstall if present.

function Test-EventSourceRegistered {
    return [System.Diagnostics.EventLog]::SourceExists($EventSource)
}

function Ensure-EventSourceRegistered {
    if (Test-EventSourceRegistered) {
        Write-Host "Windows Event Log source '$EventSource' is already registered." -ForegroundColor Green
        return
    }

    Write-Host "Registering Windows Event Log source '$EventSource'..." -ForegroundColor Cyan
    try {
        [System.Diagnostics.EventLog]::CreateEventSource($EventSource, $EventLogName)
        Write-Host "Windows Event Log source '$EventSource' registered in '$EventLogName'." -ForegroundColor Green
    } catch {
        Write-Error "Failed to register Windows Event Log source '$EventSource': $($_.Exception.Message)"
        exit 1
    }
}

function Write-DiagnosticsGuidance {
    Write-Host ""
    if (Test-EventSourceRegistered) {
        Write-Host "Diagnostics: Event Viewer -> Windows Logs -> $EventLogName" -ForegroundColor Cyan
        Write-Host "Filter by source '$EventSource' to inspect RemEx logs." -ForegroundColor Cyan
    } else {
        Write-Warning "Windows Event Log source '$EventSource' is not registered yet."
        Write-Warning "Re-run .\install-service.ps1 -Action Install as Administrator to provision Event Viewer diagnostics."
    }
}

# Remove any pre-2.0 RemexHost Windows Service. RemEx is now a single user-session
# app; a leftover Session-0 service would compete for port 5005 and never be able to
# capture the interactive desktop. Safe no-op when the service is absent.
function Remove-LegacyService {
    $svc = Get-Service -Name $LegacyService -ErrorAction SilentlyContinue
    if (-not $svc) { return }

    Write-Host "Removing legacy '$LegacyService' Windows Service (no longer used)..." -ForegroundColor Yellow
    try {
        if ($svc.Status -eq "Running") {
            Stop-Service -Name $LegacyService -Force -ErrorAction SilentlyContinue
            Start-Sleep -Seconds 2
        }
        sc.exe delete $LegacyService | Out-Null
        Write-Host "Legacy service '$LegacyService' removed." -ForegroundColor Green
    } catch {
        Write-Warning "Could not remove legacy service '$LegacyService': $($_.Exception.Message)"
    }
}

# Resolve host binary location with multiple strategies (unchanged from the service era).
$PublishDir = if ($HostPath -and (Test-Path $HostPath)) {
    if (Test-Path $HostPath -PathType Leaf) { Split-Path $HostPath -Parent } else { $HostPath }
} elseif ($env:REMEX_HOST_PATH -and (Test-Path $env:REMEX_HOST_PATH)) {
    if (Test-Path $env:REMEX_HOST_PATH -PathType Leaf) { Split-Path $env:REMEX_HOST_PATH -Parent } else { $env:REMEX_HOST_PATH }
} elseif (Test-Path (Join-Path $PSScriptRoot "Remex.Agent.exe")) {
    $PSScriptRoot
} elseif (Test-Path (Join-Path $PSScriptRoot "..\remex.agent\Remex.Agent.exe")) {
    Join-Path $PSScriptRoot "..\remex.agent"
} else {
    # Dev-time fallback: matches the artifacts publish layout used by the installer and
    # update-local-install.ps1 (self-contained win-x64 -> artifacts/publish/<Project>/<pivot>).
    Join-Path $PSScriptRoot "..\artifacts\publish\remex.agent\release_win-x64"
}

function Publish-Host {
    Write-Host "Publishing Remex.Agent..." -ForegroundColor Cyan
    dotnet publish $ProjectDir -c Release -r win-x64 -o $PublishDir --self-contained
    if ($LASTEXITCODE -ne 0) {
        Write-Error "Publish failed."
        exit 1
    }
    Write-Host "Published to: $PublishDir" -ForegroundColor Green
}

function Resolve-Account {
    if ($Username) { return $Username }
    if ($env:USERDOMAIN -and $env:USERDOMAIN -ne $env:COMPUTERNAME) {
        return "$env:USERDOMAIN\$env:USERNAME"
    }
    return "$env:COMPUTERNAME\$env:USERNAME"
}

function Configure-FirewallRules($exePath) {
    Write-Host "Configuring Windows Defender Firewall rules..." -ForegroundColor Cyan
    try {
        foreach ($ruleName in @("RemexHostInbound", "RemexClientInbound")) {
            if (Get-NetFirewallRule -Name $ruleName -ErrorAction SilentlyContinue) {
                Remove-NetFirewallRule -Name $ruleName -ErrorAction SilentlyContinue
            }
            New-NetFirewallRule `
                -Name $ruleName `
                -DisplayName "RemEx" `
                -Description "Allows incoming connections to RemEx from your phone." `
                -Direction Inbound `
                -Program "$exePath" `
                -Action Allow `
                -Enabled True `
                -Profile Any `
                -ErrorAction Stop | Out-Null
        }
        Write-Host "Firewall rules configured successfully." -ForegroundColor Green
    } catch {
        Write-Warning "Failed to configure Windows Firewall rules automatically: $($_.Exception.Message)"
        Write-Warning "You may need to allow Remex.Agent.exe through the firewall manually."
    }
}

switch ($Action) {
    "Install" {
        # Always retire any pre-2.0 service first so the two can never co-exist.
        Remove-LegacyService

        $exePath = Join-Path $PublishDir "Remex.Agent.exe"
        if (-not (Test-Path $exePath)) {
            if (-not (Test-Path $ProjectDir)) {
                Write-Error "Executable not found at: $exePath"
                Write-Error "Remex.Agent project directory was not found at: $ProjectDir"
                Write-Error "Pass -HostPath to an existing Remex.Agent.exe when installing from a packaged build."
                exit 1
            }

            Publish-Host

            if (-not (Test-Path $exePath)) {
                Write-Error "Executable not found at: $exePath"
                exit 1
            }
        }

        Ensure-EventSourceRegistered

        $account = Resolve-Account
        Write-Host "Registering RemEx logon task for '$account' (elevated, no UAC prompt)..." -ForegroundColor Cyan

        try {
            $action    = New-ScheduledTaskAction -Execute $exePath -Argument "--minimized"
            $trigger   = New-ScheduledTaskTrigger -AtLogOn -User $account
            $principal = New-ScheduledTaskPrincipal -UserId $account -LogonType Interactive -RunLevel Highest
            $settings  = New-ScheduledTaskSettingsSet `
                            -AllowStartIfOnBatteries `
                            -DontStopIfGoingOnBatteries `
                            -ExecutionTimeLimit ([TimeSpan]::Zero) `
                            -MultipleInstances IgnoreNew `
                            -RestartCount 0

            Register-ScheduledTask `
                -TaskName $TaskName `
                -Description $Description `
                -Action $action `
                -Trigger $trigger `
                -Principal $principal `
                -Settings $settings `
                -Force | Out-Null

            Write-Host "Logon task '$TaskName' registered." -ForegroundColor Green
        } catch {
            Write-Error "Failed to register the RemEx logon task: $($_.Exception.Message)"
            exit 1
        }

        Configure-FirewallRules $exePath

        Write-Host ""
        Write-Host "RemEx will now start automatically when '$account' signs in." -ForegroundColor Green
        Write-Host "Check it with: .\install-service.ps1 -Action Status"
        Write-Host "It starts elevated and minimized to the tray, ready for your phone to connect."
        Write-DiagnosticsGuidance
    }

    "Uninstall" {
        $task = Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
        if ($task) {
            Write-Host "Removing RemEx logon task '$TaskName'..." -ForegroundColor Cyan
            try {
                Stop-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
                Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false
                Write-Host "Logon task '$TaskName' removed." -ForegroundColor Green
            } catch {
                Write-Warning "Failed to remove logon task '$TaskName': $($_.Exception.Message)"
            }
        } else {
            Write-Warning "RemEx logon task '$TaskName' is not installed."
        }

        # Retire any pre-2.0 Windows Service as well (transition cleanup).
        Remove-LegacyService

        # Remove event log source
        if (Test-EventSourceRegistered) {
            Write-Host "Removing Windows Event Log source '$EventSource'..." -ForegroundColor Yellow
            try {
                [System.Diagnostics.EventLog]::DeleteEventSource($EventSource)
                Write-Host "Windows Event Log source '$EventSource' removed successfully." -ForegroundColor Green
            } catch {
                Write-Warning "Failed to remove Windows Event Log source '$EventSource': $($_.Exception.Message)"
            }
        }

        # Remove firewall rules
        Write-Host "Removing Windows Defender Firewall rules..." -ForegroundColor Yellow
        try {
            Remove-NetFirewallRule -Name "RemexHostInbound" -ErrorAction SilentlyContinue
            Remove-NetFirewallRule -Name "RemexClientInbound" -ErrorAction SilentlyContinue
            Write-Host "Firewall rules removed successfully." -ForegroundColor Green
        } catch {
            Write-Warning "Failed to remove Windows Firewall rules automatically: $($_.Exception.Message)"
        }
    }

    "Status" {
        $task = Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
        if (-not $task) {
            Write-Host "RemEx logon task '$TaskName' is not installed." -ForegroundColor Yellow
        } else {
            $info      = Get-ScheduledTaskInfo -TaskName $TaskName -ErrorAction SilentlyContinue
            $principal = $task.Principal
            Write-Host ""
            Write-Host "  Task      : $TaskName" -ForegroundColor Cyan
            Write-Host "  State     : $($task.State)"
            Write-Host "  Run as    : $($principal.UserId)"
            Write-Host "  Run level : $($principal.RunLevel)"
            Write-Host "  Logon type: $($principal.LogonType)"
            if ($info) {
                Write-Host "  Last run  : $($info.LastRunTime) (result 0x{0:X})" -ForegroundColor DarkGray -f $info.LastTaskResult
            }
            Write-DiagnosticsGuidance
        }
    }

    "Start" {
        $task = Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
        if (-not $task) {
            Write-Error "RemEx logon task '$TaskName' is not installed."
            exit 1
        }
        Write-Host "Starting RemEx now via task '$TaskName'..." -ForegroundColor Cyan
        Start-ScheduledTask -TaskName $TaskName
    }

    "Stop" {
        $task = Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
        if (-not $task) {
            Write-Error "RemEx logon task '$TaskName' is not installed."
            exit 1
        }
        Write-Host "Stopping the running RemEx task instance '$TaskName'..." -ForegroundColor Yellow
        Stop-ScheduledTask -TaskName $TaskName
    }
}
