---
name: processmonitor
description: "Skill for the ProcessMonitor area of RemEx. 15 symbols across 6 files."
---

# ProcessMonitor

15 symbols | 6 files | Cohesion: 69%

## When to Use

- Working with code in `Remex.Host/`
- Understanding how WindowsProcessMonitorService, LinuxProcessMonitorService, HandleAsync work
- Modifying processmonitor-related functionality

## Key Files

| File | Symbols |
|------|---------|
| `Remex.Host/Services/ProcessMonitor/LinuxProcessMonitorService.cs` | GetProcessesAsync, GetTotalCpuTime, ProcessCpuTracker, LinuxProcessMonitorService |
| `Remex.Core/Services/DashboardProfileStorageService.cs` | LoadProfileAsync, SaveProfileAsync, SaveProfileAsync |
| `Remex.Host/Services/ProcessMonitor/WindowsProcessMonitorService.cs` | GetProcessesAsync, ProcessCpuTracker, WindowsProcessMonitorService |
| `Remex.Host/Handlers/PingPongHandler.cs` | HandleAsync, StreamTelemetryAsync |
| `Remex.Core/Services/IProcessMonitorService.cs` | GetProcessesAsync, IProcessMonitorService |
| `Remex.Core/Serialization/RemexJsonSerializerContext.cs` | SerializeIndented |

## Entry Points

Start here when exploring this area:

- **`WindowsProcessMonitorService`** (Class) — `Remex.Host/Services/ProcessMonitor/WindowsProcessMonitorService.cs:12`
- **`LinuxProcessMonitorService`** (Class) — `Remex.Host/Services/ProcessMonitor/LinuxProcessMonitorService.cs:12`
- **`HandleAsync`** (Method) — `Remex.Host/Handlers/PingPongHandler.cs:26`
- **`SerializeIndented`** (Method) — `Remex.Core/Serialization/RemexJsonSerializerContext.cs:55`
- **`SaveProfileAsync`** (Method) — `Remex.Core/Services/DashboardProfileStorageService.cs:44`

## Key Symbols

| Symbol | Type | File | Line |
|--------|------|------|------|
| `WindowsProcessMonitorService` | Class | `Remex.Host/Services/ProcessMonitor/WindowsProcessMonitorService.cs` | 12 |
| `LinuxProcessMonitorService` | Class | `Remex.Host/Services/ProcessMonitor/LinuxProcessMonitorService.cs` | 12 |
| `IProcessMonitorService` | Interface | `Remex.Core/Services/IProcessMonitorService.cs` | 6 |
| `HandleAsync` | Method | `Remex.Host/Handlers/PingPongHandler.cs` | 26 |
| `SerializeIndented` | Method | `Remex.Core/Serialization/RemexJsonSerializerContext.cs` | 55 |
| `SaveProfileAsync` | Method | `Remex.Core/Services/DashboardProfileStorageService.cs` | 44 |
| `GetProcessesAsync` | Method | `Remex.Host/Services/ProcessMonitor/WindowsProcessMonitorService.cs` | 24 |
| `GetProcessesAsync` | Method | `Remex.Host/Services/ProcessMonitor/LinuxProcessMonitorService.cs` | 24 |
| `ProcessCpuTracker` | Class | `Remex.Host/Services/ProcessMonitor/WindowsProcessMonitorService.cs` | 136 |
| `ProcessCpuTracker` | Class | `Remex.Host/Services/ProcessMonitor/LinuxProcessMonitorService.cs` | 171 |
| `StreamTelemetryAsync` | Method | `Remex.Host/Handlers/PingPongHandler.cs` | 298 |
| `GetProcessesAsync` | Method | `Remex.Core/Services/IProcessMonitorService.cs` | 8 |
| `LoadProfileAsync` | Method | `Remex.Core/Services/DashboardProfileStorageService.cs` | 10 |
| `SaveProfileAsync` | Method | `Remex.Core/Services/DashboardProfileStorageService.cs` | 11 |
| `GetTotalCpuTime` | Method | `Remex.Host/Services/ProcessMonitor/LinuxProcessMonitorService.cs` | 151 |

## Execution Flows

| Flow | Type | Steps |
|------|------|-------|
| `HandleAsync → SerializeToUtf8Bytes` | cross_community | 4 |
| `HandleAsync → GetPlatform` | cross_community | 3 |
| `HandleAsync → GetIsInteractiveSession` | cross_community | 3 |
| `HandleAsync → GetRuntimeMode` | cross_community | 3 |
| `HandleAsync → SupportsRemoteDesktop` | cross_community | 3 |

## Connected Areas

| Area | Connections |
|------|-------------|
| Services | 5 calls |
| Remex.Host | 2 calls |
| Remex.Host.Tests | 2 calls |
| Native | 2 calls |
| Handlers | 1 calls |
| Command | 1 calls |
| Input | 1 calls |

## How to Explore

1. `gitnexus_context({name: "WindowsProcessMonitorService"})` — see callers and callees
2. `gitnexus_query({query: "processmonitor"})` — find related execution flows
3. Read key files listed above for implementation details
