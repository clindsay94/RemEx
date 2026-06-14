#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Installs, uninstalls, or checks the status of the Remex Host Windows Service.

.PARAMETER Action
    The action to perform: Install, Uninstall, or Status.

.PARAMETER Username
    (Install only) Optional. The Windows user account the service should run as.
    Format: DOMAIN\User or .\User for local accounts.
    When omitted the service runs as LocalSystem (recommended — no credential
    required, service starts before any user logs in).

.PARAMETER Password
    (Install only) The password for -Username. If -Username is supplied but
    -Password is omitted you will be prompted interactively via Get-Credential.

.EXAMPLE
    .\install-service.ps1 -Action Install
    .\install-service.ps1 -Action Install -Username ".\Connor"
    .\install-service.ps1 -Action Install -Username ".\Connor" -Password "secret"
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

$ServiceName   = "RemexHost"
$DisplayName   = "Remex Host"
$Description   = "Remex remote execution and telemetry host service."
$ProjectDir    = Join-Path $PSScriptRoot "..\RemEx.Host"
$EventLogName  = "Application"
$EventSource   = "RemEx.Host"

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
        Write-Host "Filter by source '$EventSource' to inspect Remex Host service logs." -ForegroundColor Cyan
    } else {
        Write-Warning "Windows Event Log source '$EventSource' is not registered yet."
        Write-Warning "Re-run .\install-service.ps1 -Action Install as Administrator to provision Event Viewer diagnostics."
    }
}

# Resolve host binary location with multiple strategies
$PublishDir = if ($HostPath -and (Test-Path $HostPath)) {
    if (Test-Path $HostPath -PathType Leaf) { Split-Path $HostPath -Parent } else { $HostPath }
} elseif ($env:REMEX_HOST_PATH -and (Test-Path $env:REMEX_HOST_PATH)) {
    if (Test-Path $env:REMEX_HOST_PATH -PathType Leaf) { Split-Path $env:REMEX_HOST_PATH -Parent } else { $env:REMEX_HOST_PATH }
} elseif (Test-Path (Join-Path $PSScriptRoot "RemEx.Host.exe")) {
    $PSScriptRoot
} elseif (Test-Path (Join-Path $PSScriptRoot "..\RemEx.Host\RemEx.Host.exe")) {
    Join-Path $PSScriptRoot "..\RemEx.Host"
} else {
    # Original dev-time fallback
    Join-Path $PSScriptRoot "..\publish\RemEx.Host"
}

function Publish-Host {
    Write-Host "Publishing RemEx.Host..." -ForegroundColor Cyan
    dotnet publish $ProjectDir -c Release -o $PublishDir --self-contained false
    if ($LASTEXITCODE -ne 0) {
        Write-Error "Publish failed."
        exit 1
    }
    Write-Host "Published to: $PublishDir" -ForegroundColor Green
}

function Grant-LogOnAsService($accountName) {
    Write-Host "Granting 'Log on as a service' right to $accountName..." -ForegroundColor Cyan
    
    $source = @"
using System;
using System.Runtime.InteropServices;
using System.Text;

public class LsaWrapper {
    [StructLayout(LayoutKind.Sequential)]
    struct LSA_UNICODE_STRING {
        public ushort Length;
        public ushort MaximumLength;
        public IntPtr Buffer;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct LSA_OBJECT_ATTRIBUTES {
        public int Length;
        public IntPtr RootDirectory;
        public LSA_UNICODE_STRING ObjectName;
        public uint Attributes;
        public IntPtr SecurityDescriptor;
        public IntPtr SecurityQualityOfService;
    }

    [DllImport("advapi32.dll", PreserveSig = true)]
    static extern uint LsaOpenPolicy(ref LSA_UNICODE_STRING SystemName, ref LSA_OBJECT_ATTRIBUTES ObjectAttributes, uint DesiredAccess, out IntPtr PolicyHandle);

    [DllImport("advapi32.dll", PreserveSig = true)]
    static extern uint LsaAddAccountRights(IntPtr PolicyHandle, IntPtr AccountSid, LSA_UNICODE_STRING[] UserRights, uint CountOfRights);

    [DllImport("advapi32.dll", PreserveSig = true)]
    static extern uint LsaClose(IntPtr ObjectHandle);

    [DllImport("advapi32.dll", PreserveSig = true)]
    static extern uint LsaNtStatusToWinError(uint Status);

    public static void GrantRight(string accountName, string privilege) {
        var sid = new System.Security.Principal.NTAccount(accountName).Translate(typeof(System.Security.Principal.SecurityIdentifier)) as System.Security.Principal.SecurityIdentifier;
        byte[] sidBytes = new byte[sid.BinaryLength];
        sid.GetBinaryForm(sidBytes, 0);
        IntPtr sidPtr = Marshal.AllocHGlobal(sidBytes.Length);
        Marshal.Copy(sidBytes, 0, sidPtr, sidBytes.Length);

        LSA_OBJECT_ATTRIBUTES objAttr = new LSA_OBJECT_ATTRIBUTES();
        objAttr.Length = Marshal.SizeOf(typeof(LSA_OBJECT_ATTRIBUTES));

        LSA_UNICODE_STRING systemName = new LSA_UNICODE_STRING();
        IntPtr policyHandle;
        uint status = LsaOpenPolicy(ref systemName, ref objAttr, 0x0010 | 0x0800, out policyHandle);
        if (status != 0) throw new Exception("LsaOpenPolicy failed with status " + status);

        LSA_UNICODE_STRING right = new LSA_UNICODE_STRING();
        right.Buffer = Marshal.StringToHGlobalUni(privilege);
        right.Length = (ushort)(privilege.Length * 2);
        right.MaximumLength = (ushort)((privilege.Length + 1) * 2);

        status = LsaAddAccountRights(policyHandle, sidPtr, new LSA_UNICODE_STRING[] { right }, 1);
        LsaClose(policyHandle);
        Marshal.FreeHGlobal(sidPtr);
        Marshal.FreeHGlobal(right.Buffer);

        if (status != 0) throw new Exception("LsaAddAccountRights failed with status " + status);
    }
}
"@
    Add-Type -TypeDefinition $source -ErrorAction SilentlyContinue
    try {
        [LsaWrapper]::GrantRight($accountName, "SeServiceLogonRight")
        Write-Host "Successfully granted 'Log on as a service' right." -ForegroundColor Green
    } catch {
        Write-Warning "Failed to grant 'Log on as a service' right automatically: $($_.Exception.Message)"
        Write-Warning "You may need to grant this manually in Local Security Policy (secpol.msc)."
    }
}

switch ($Action) {
    "Install" {
        # Check if already installed
        $existing = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
        if ($existing) {
            Write-Warning "Service '$ServiceName' already exists (Status: $($existing.Status)). Uninstall first or check status."
            exit 1
        }

        $exePath = Join-Path $PublishDir "RemEx.Host.exe"
        if (-not (Test-Path $exePath)) {
            if (-not (Test-Path $ProjectDir)) {
                Write-Error "Executable not found at: $exePath"
                Write-Error "RemEx.Host project directory was not found at: $ProjectDir"
                Write-Error "Pass -HostPath to an existing RemEx.Host.exe when installing from a packaged build."
                exit 1
            }

            Publish-Host

            if (-not (Test-Path $exePath)) {
                Write-Error "Executable not found at: $exePath"
                exit 1
            }
        }

        Ensure-EventSourceRegistered

        if ($Username) {
            # Named account: collect credential and grant logon-as-service right.
            Write-Host "Registering Windows Service '$ServiceName' as '$Username'..." -ForegroundColor Cyan
            if ($Password) {
                $securePass = ConvertTo-SecureString $Password -AsPlainText -Force
                $cred = New-Object System.Management.Automation.PSCredential($Username, $securePass)
            } else {
                Write-Host "You will be prompted for the password of '$Username'." -ForegroundColor Yellow
                $cred = Get-Credential -UserName $Username -Message "Enter password for the Remex service account"
            }
            Grant-LogOnAsService $Username
            New-Service `
                -Name $ServiceName `
                -BinaryPathName "`"$exePath`" --agent" `
                -DisplayName $DisplayName `
                -Description $Description `
                -StartupType Automatic `
                -Credential $cred
        } else {
            # Default: LocalSystem — no credential needed.
            Write-Host "Registering Windows Service '$ServiceName' as LocalSystem..." -ForegroundColor Cyan
            New-Service `
                -Name $ServiceName `
                -BinaryPathName "`"$exePath`" --agent" `
                -DisplayName $DisplayName `
                -Description $Description `
                -StartupType Automatic
        }

        # Configure Windows Defender Firewall rules to allow inbound TCP/UDP connections for both the host and client
        Write-Host "Configuring Windows Defender Firewall rules..." -ForegroundColor Cyan
        try {
            $hostRuleName = "RemexHostInbound"
            $clientRuleName = "RemexClientInbound"

            # 1. Firewall rule for RemEx.Host.exe (background service / embedded host)
            if (Test-Path $exePath) {
                $existingHostRule = Get-NetFirewallRule -Name $hostRuleName -ErrorAction SilentlyContinue
                if ($existingHostRule) {
                    Remove-NetFirewallRule -Name $hostRuleName -ErrorAction SilentlyContinue
                }
                New-NetFirewallRule `
                    -Name $hostRuleName `
                    -DisplayName "Remex Host Service" `
                    -Description "Allows incoming TCP/UDP connections for the Remex Host Service" `
                    -Direction Inbound `
                    -Program "$exePath" `
                    -Action Allow `
                    -Enabled True `
                    -Profile Any `
                    -ErrorAction Stop | Out-Null
                Write-Host "Firewall rule 'Remex Host Service' configured successfully." -ForegroundColor Green
            }

            # 2. Firewall rule for RemEx.Host.exe (desktop UI / in-process host)
            $clientExePath = Join-Path $PublishDir "RemEx.Host.exe"
            if (Test-Path $clientExePath) {
                $existingClientRule = Get-NetFirewallRule -Name $clientRuleName -ErrorAction SilentlyContinue
                if ($existingClientRule) {
                    Remove-NetFirewallRule -Name $clientRuleName -ErrorAction SilentlyContinue
                }
                New-NetFirewallRule `
                    -Name $clientRuleName `
                    -DisplayName "Remex Desktop Client" `
                    -Description "Allows incoming TCP/UDP connections for the Remex Desktop Client" `
                    -Direction Inbound `
                    -Program "$clientExePath" `
                    -Action Allow `
                    -Enabled True `
                    -Profile Any `
                    -ErrorAction Stop | Out-Null
                Write-Host "Firewall rule 'Remex Desktop Client' configured successfully." -ForegroundColor Green
            }
        } catch {
            Write-Warning "Failed to configure Windows Firewall rules automatically: $($_.Exception.Message)"
            Write-Warning "You may need to allow the executables through the firewall manually."
        }

        Write-Host "Starting service..." -ForegroundColor Cyan
        Start-Service -Name $ServiceName

        Write-Host ""
        $runAs = if ($Username) { $Username } else { "LocalSystem" }
        Write-Host "Service '$DisplayName' installed and started as '$runAs'." -ForegroundColor Green
        Write-Host "It will auto-start on boot. View in services.msc or use: .\install-service.ps1 -Action Status"
        Write-Warning "Interactive desktop features still require the Remex Desktop app to be running in the signed-in Windows session."
        Write-DiagnosticsGuidance
    }

    "Uninstall" {
        $existing = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
        if ($existing) {
            if ($existing.Status -eq "Running") {
                Write-Host "Stopping service..." -ForegroundColor Yellow
                Stop-Service -Name $ServiceName -Force
                Start-Sleep -Seconds 2
            }

            Write-Host "Removing service '$ServiceName'..." -ForegroundColor Cyan
            sc.exe delete $ServiceName | Out-Null
            Write-Host "Service '$DisplayName' removed successfully." -ForegroundColor Green
        } else {
            Write-Warning "Service '$ServiceName' is not installed."
        }

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
        $existing = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
        if (-not $existing) {
            Write-Host "Service '$ServiceName' is not installed." -ForegroundColor Yellow
        } else {
            Write-Host ""
            Write-Host "  Service : $($existing.DisplayName)" -ForegroundColor Cyan
            Write-Host "  Status  : $($existing.Status)"
            Write-Host "  Startup : $($existing.StartType)"
            Write-DiagnosticsGuidance
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
