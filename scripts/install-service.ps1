#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Installs, uninstalls, or checks the status of the Remex Host Windows Service.

.PARAMETER Action
    The action to perform: Install, Uninstall, or Status.

.PARAMETER Username
    (Install only) The Windows user account the service should run as.
    Format: DOMAIN\User or .\User for local accounts.
    When omitted the service runs as the current user (.\<current-username>).
    Running as a real user account is REQUIRED so the service can interact
    with the desktop session (lock workstation, launch apps, read HWiNFO).

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
    [string]$Username
)

$ServiceName   = "RemexHost"
$DisplayName   = "Remex Host"
$Description   = "Remex remote execution and telemetry host service."
$ProjectDir    = Join-Path $PSScriptRoot "..\Remex.Host"
$PublishDir    = Join-Path $PSScriptRoot "..\publish\Remex.Host"

function Publish-Host {
    Write-Host "Publishing Remex.Host..." -ForegroundColor Cyan
    dotnet publish $ProjectDir -c Release -o $PublishDir --self-contained false
    if ($LASTEXITCODE -ne 0) {
        Write-Error "Publish failed."
        exit 1
    }
    Write-Host "Published to: $PublishDir" -ForegroundColor Green
}

switch ($Action) {
    "Install" {
        # Check if already installed
        $existing = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
        if ($existing) {
            Write-Warning "Service '$ServiceName' already exists (Status: $($existing.Status)). Uninstall first or check status."
            exit 1
        }

        Publish-Host

        $exePath = Join-Path $PublishDir "Remex.Host.exe"
        if (-not (Test-Path $exePath)) {
            Write-Error "Executable not found at: $exePath"
            exit 1
        }

        # Determine the user account for the service.
        # Running as a real user (not LocalSystem) is required so the service
        # can lock the workstation, launch apps, and read the user's HWiNFO registry hive.
        if (-not $Username) {
            $Username = ".\$env:USERNAME"
        }

        Write-Host "Registering Windows Service '$ServiceName' as '$Username'..." -ForegroundColor Cyan
        Write-Host "You will be prompted for the password of '$Username'." -ForegroundColor Yellow
        $cred = Get-Credential -UserName $Username -Message "Enter password for the Remex service account"

        New-Service `
            -Name $ServiceName `
            -BinaryPathName "`"$exePath`"" `
            -DisplayName $DisplayName `
            -Description $Description `
            -StartupType Automatic `
            -Credential $cred

        # Allow the service to interact with the user's desktop session.
        # This grants the "Log on as a service" right implicitly via New-Service -Credential.

        Write-Host "Starting service..." -ForegroundColor Cyan
        Start-Service -Name $ServiceName

        Write-Host ""
        Write-Host "Service '$DisplayName' installed and started as '$Username'." -ForegroundColor Green
        Write-Host "It will auto-start on boot. View in services.msc or use: .\install-service.ps1 -Action Status"
    }

    "Uninstall" {
        $existing = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
        if (-not $existing) {
            Write-Warning "Service '$ServiceName' is not installed."
            exit 0
        }

        if ($existing.Status -eq "Running") {
            Write-Host "Stopping service..." -ForegroundColor Yellow
            Stop-Service -Name $ServiceName -Force
            Start-Sleep -Seconds 2
        }

        Write-Host "Removing service '$ServiceName'..." -ForegroundColor Cyan
        Remove-Service -Name $ServiceName

        Write-Host "Service '$DisplayName' removed successfully." -ForegroundColor Green
    }

    "Status" {
        $existing = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
        if (-not $existing) {
            Write-Host "Service '$ServiceName' is not installed." -ForegroundColor Yellow
        } else {
            Write-Host ""
            Write-Host "  Service : $($existing.DisplayName)" -ForegroundColor Cyan
            Write-Host "  Status  : $($existing.Status)"
            Write-Host "  Startup : $($existing.StartType)"
            Write-Host ""
        }
    }

    "Start" {
        $existing = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
        if (-not $existing) {
            Write-Error "Service '$ServiceName' is not installed."
            exit 1
        }
        Write-Host "Starting service '$ServiceName'..." -ForegroundColor Cyan
        Start-Service -Name $ServiceName
    }

    "Stop" {
        $existing = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
        if (-not $existing) {
            Write-Error "Service '$ServiceName' is not installed."
            exit 1
        }
        Write-Host "Stopping service '$ServiceName'..." -ForegroundColor Yellow
        Stop-Service -Name $ServiceName -Force
    }
}
