---
name: processmonitor
description: "Skill for the ProcessMonitor area of RemEx. 19 symbols across 11 files."
---

# ProcessMonitor

19 symbols | 11 files | Cohesion: 60%

## When to Use

- Working with code in `Remex.Host/`
- Understanding how RemoteDesktopHandler, CertificateService, WindowsProcessMonitorService work
- Modifying processmonitor-related functionality

## Key Files

| File | Symbols |
|------|---------|
| `Remex.Host/Services/ProcessMonitor/LinuxProcessMonitorService.cs` | GetProcessesAsync, GetTotalCpuTime, ProcessCpuTracker, LinuxProcessMonitorService |
| `Remex.Host/Services/ProcessMonitor/WindowsProcessMonitorService.cs` | GetProcessesAsync, ProcessCpuTracker, WindowsProcessMonitorService |
| `Remex.Core/Services/IProcessMonitorService.cs` | GetProcessesAsync, IProcessMonitorService |
| `Remex.Core/Services/DashboardProfileStorageService.cs` | LoadProfileAsync, SaveProfileAsync |
| `Remex.Core/Services/Security/ICertificateService.cs` | ICertificateService, GetOrCreateCertificateAsync |
| `Remex.Host.Tests/RemoteDesktopHandlerTests.cs` | GetCurrent |
| `Remex.Host/HostBootstrapper.cs` | CreateApplication |
| `Remex.Host/Services/HostCapabilitiesProvider.cs` | GetCurrent |
| `Remex.Host/Handlers/RemoteDesktopHandler.cs` | RemoteDesktopHandler |
| `Remex.Host/Handlers/PingPongHandler.cs` | HandleAsync |

## Entry Points

Start here when exploring this area:

- **`RemoteDesktopHandler`** (Class) — `Remex.Host/Handlers/RemoteDesktopHandler.cs:17`
- **`CertificateService`** (Class) — `Remex.Host/Services/Security/CertificateService.cs:16`
- **`WindowsProcessMonitorService`** (Class) — `Remex.Host/Services/ProcessMonitor/WindowsProcessMonitorService.cs:12`
- **`LinuxProcessMonitorService`** (Class) — `Remex.Host/Services/ProcessMonitor/LinuxProcessMonitorService.cs:12`
- **`GetCurrent`** (Method) — `Remex.Host.Tests/RemoteDesktopHandlerTests.cs:70`

## Key Symbols

| Symbol | Type | File | Line |
|--------|------|------|------|
| `RemoteDesktopHandler` | Class | `Remex.Host/Handlers/RemoteDesktopHandler.cs` | 17 |
| `CertificateService` | Class | `Remex.Host/Services/Security/CertificateService.cs` | 16 |
| `WindowsProcessMonitorService` | Class | `Remex.Host/Services/ProcessMonitor/WindowsProcessMonitorService.cs` | 12 |
| `LinuxProcessMonitorService` | Class | `Remex.Host/Services/ProcessMonitor/LinuxProcessMonitorService.cs` | 12 |
| `ICertificateService` | Interface | `Remex.Core/Services/Security/ICertificateService.cs` | 6 |
| `IProcessMonitorService` | Interface | `Remex.Core/Services/IProcessMonitorService.cs` | 6 |
| `GetCurrent` | Method | `Remex.Host.Tests/RemoteDesktopHandlerTests.cs` | 70 |
| `CreateApplication` | Method | `Remex.Host/HostBootstrapper.cs` | 41 |
| `HandleAsync` | Method | `Remex.Host/Handlers/PingPongHandler.cs` | 28 |
| `GetProcessesAsync` | Method | `Remex.Host/Services/ProcessMonitor/WindowsProcessMonitorService.cs` | 24 |
| `GetProcessesAsync` | Method | `Remex.Host/Services/ProcessMonitor/LinuxProcessMonitorService.cs` | 24 |
| `ProcessCpuTracker` | Class | `Remex.Host/Services/ProcessMonitor/WindowsProcessMonitorService.cs` | 136 |
| `ProcessCpuTracker` | Class | `Remex.Host/Services/ProcessMonitor/LinuxProcessMonitorService.cs` | 171 |
| `GetCurrent` | Method | `Remex.Host/Services/HostCapabilitiesProvider.cs` | 7 |
| `GetProcessesAsync` | Method | `Remex.Core/Services/IProcessMonitorService.cs` | 8 |
| `LoadProfileAsync` | Method | `Remex.Core/Services/DashboardProfileStorageService.cs` | 10 |
| `SaveProfileAsync` | Method | `Remex.Core/Services/DashboardProfileStorageService.cs` | 11 |
| `GetTotalCpuTime` | Method | `Remex.Host/Services/ProcessMonitor/LinuxProcessMonitorService.cs` | 151 |
| `GetOrCreateCertificateAsync` | Method | `Remex.Core/Services/Security/ICertificateService.cs` | 8 |

## Execution Flows

| Flow | Type | Steps |
|------|------|-------|
| `Main → TryCreateDirectory` | cross_community | 6 |
| `Main → GenerateAndSaveCertificate` | cross_community | 5 |
| `Main → ComputeSpkiHash` | cross_community | 5 |
| `HandleAsync → SerializeToUtf8Bytes` | cross_community | 4 |
| `Main → GetOrCreateCertificateAsync` | cross_community | 4 |
| `Main → GetCurrent` | cross_community | 4 |
| `Main → GetCurrent` | cross_community | 4 |
| `HandleAsync → GetPlatform` | cross_community | 3 |
| `HandleAsync → GetIsInteractiveSession` | cross_community | 3 |
| `HandleAsync → GetRuntimeMode` | cross_community | 3 |

## Connected Areas

| Area | Connections |
|------|-------------|
| Handlers | 8 calls |
| Services | 6 calls |
| Security | 4 calls |
| Remex.Host.Tests | 2 calls |
| Native | 2 calls |
| FileTransfer | 2 calls |
| Command | 1 calls |
| Input | 1 calls |

## How to Explore

1. `gitnexus_context({name: "RemoteDesktopHandler"})` — see callers and callees
2. `gitnexus_query({query: "processmonitor"})` — find related execution flows
3. Read key files listed above for implementation details
