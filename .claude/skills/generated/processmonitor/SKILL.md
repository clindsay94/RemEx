---
name: processmonitor
description: "Skill for the ProcessMonitor area of RemEx. 19 symbols across 11 files."
---

# ProcessMonitor

19 symbols | 11 files | Cohesion: 60%

## When to Use

- Working with code in `remex.agent/`
- Understanding how RemoteDesktopHandler, CertificateService, WindowsProcessMonitorService work
- Modifying processmonitor-related functionality

## Key Files

| File | Symbols |
|------|---------|
| `remex.agent/Services/ProcessMonitor/LinuxProcessMonitorService.cs` | GetProcessesAsync, GetTotalCpuTime, ProcessCpuTracker, LinuxProcessMonitorService |
| `remex.agent/Services/ProcessMonitor/WindowsProcessMonitorService.cs` | GetProcessesAsync, ProcessCpuTracker, WindowsProcessMonitorService |
| `remex.core/Services/IProcessMonitorService.cs` | GetProcessesAsync, IProcessMonitorService |
| `remex.core/Services/DashboardProfileStorageService.cs` | LoadProfileAsync, SaveProfileAsync |
| `remex.core/Services/Security/ICertificateService.cs` | ICertificateService, GetOrCreateCertificateAsync |
| `remex.agent.tests/RemoteDesktopHandlerTests.cs` | GetCurrent |
| `remex.agent/HostBootstrapper.cs` | CreateApplication |
| `remex.agent/Services/HostCapabilitiesProvider.cs` | GetCurrent |
| `remex.agent/Handlers/RemoteDesktopHandler.cs` | RemoteDesktopHandler |
| `remex.agent/Handlers/PingPongHandler.cs` | HandleAsync |

## Entry Points

Start here when exploring this area:

- **`RemoteDesktopHandler`** (Class) — `remex.agent/Handlers/RemoteDesktopHandler.cs:17`
- **`CertificateService`** (Class) — `remex.agent/Services/Security/CertificateService.cs:16`
- **`WindowsProcessMonitorService`** (Class) — `remex.agent/Services/ProcessMonitor/WindowsProcessMonitorService.cs:12`
- **`LinuxProcessMonitorService`** (Class) — `remex.agent/Services/ProcessMonitor/LinuxProcessMonitorService.cs:12`
- **`GetCurrent`** (Method) — `remex.agent.tests/RemoteDesktopHandlerTests.cs:70`

## Key Symbols

| Symbol | Type | File | Line |
|--------|------|------|------|
| `RemoteDesktopHandler` | Class | `remex.agent/Handlers/RemoteDesktopHandler.cs` | 17 |
| `CertificateService` | Class | `remex.agent/Services/Security/CertificateService.cs` | 16 |
| `WindowsProcessMonitorService` | Class | `remex.agent/Services/ProcessMonitor/WindowsProcessMonitorService.cs` | 12 |
| `LinuxProcessMonitorService` | Class | `remex.agent/Services/ProcessMonitor/LinuxProcessMonitorService.cs` | 12 |
| `ICertificateService` | Interface | `remex.core/Services/Security/ICertificateService.cs` | 6 |
| `IProcessMonitorService` | Interface | `remex.core/Services/IProcessMonitorService.cs` | 6 |
| `GetCurrent` | Method | `remex.agent.tests/RemoteDesktopHandlerTests.cs` | 70 |
| `CreateApplication` | Method | `remex.agent/HostBootstrapper.cs` | 41 |
| `HandleAsync` | Method | `remex.agent/Handlers/PingPongHandler.cs` | 28 |
| `GetProcessesAsync` | Method | `remex.agent/Services/ProcessMonitor/WindowsProcessMonitorService.cs` | 24 |
| `GetProcessesAsync` | Method | `remex.agent/Services/ProcessMonitor/LinuxProcessMonitorService.cs` | 24 |
| `ProcessCpuTracker` | Class | `remex.agent/Services/ProcessMonitor/WindowsProcessMonitorService.cs` | 136 |
| `ProcessCpuTracker` | Class | `remex.agent/Services/ProcessMonitor/LinuxProcessMonitorService.cs` | 171 |
| `GetCurrent` | Method | `remex.agent/Services/HostCapabilitiesProvider.cs` | 7 |
| `GetProcessesAsync` | Method | `remex.core/Services/IProcessMonitorService.cs` | 8 |
| `LoadProfileAsync` | Method | `remex.core/Services/DashboardProfileStorageService.cs` | 10 |
| `SaveProfileAsync` | Method | `remex.core/Services/DashboardProfileStorageService.cs` | 11 |
| `GetTotalCpuTime` | Method | `remex.agent/Services/ProcessMonitor/LinuxProcessMonitorService.cs` | 151 |
| `GetOrCreateCertificateAsync` | Method | `remex.core/Services/Security/ICertificateService.cs` | 8 |

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
| remex.agent.tests | 2 calls |
| Native | 2 calls |
| FileTransfer | 2 calls |
| Command | 1 calls |
| Input | 1 calls |

## How to Explore

1. `gitnexus_context({name: "RemoteDesktopHandler"})` — see callers and callees
2. `gitnexus_query({query: "processmonitor"})` — find related execution flows
3. Read key files listed above for implementation details
