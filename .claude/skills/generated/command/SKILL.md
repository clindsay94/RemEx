---
name: command
description: "Skill for the Command area of RemEx. 67 symbols across 13 files."
---

# Command

67 symbols | 13 files | Cohesion: 96%

## When to Use

- Working with code in `Remex.Core/`
- Understanding how WindowsSystemCommandService, LinuxSystemCommandService, IpcClientCommandService work
- Modifying command-related functionality

## Key Files

| File | Symbols |
|------|---------|
| `Remex.Core/Services/Command/WindowsSystemCommandService.cs` | LockWorkStation, SendMessage, Shutdown, ForceShutdown, Restart (+10) |
| `Remex.Core/Services/Command/LinuxSystemCommandService.cs` | Shutdown, ForceShutdown, Restart, ForceRestart, RestartToUefi (+8) |
| `Remex.Client/Services/Command/IpcClientCommandService.cs` | Shutdown, ForceShutdown, Restart, ForceRestart, RestartToUefi (+8) |
| `Remex.Core/Services/Command/ISystemCommandService.cs` | Shutdown, ForceShutdown, Restart, ForceRestart, RestartToUefi (+6) |
| `Remex.Host/Services/IPC/LocalIpcServerService.cs` | ExecuteAsync, HandleClientAsync, ExecuteCommandAsync, ParseDelaySeconds |
| `Remex.Host/Handlers/PingPongHandler.cs` | ExecuteCommandAsync, MakeCommandResponse, ParseDelaySeconds |
| `Remex.Core/Services/Network/RemexNetworkListener.cs` | ExecuteCommandAsync, ParseDelaySeconds |
| `Remex.Core/Services/IProcessMonitorService.cs` | KillProcess |
| `Remex.Client/ViewModels/RemoteViewModel.cs` | SendWolAsync |
| `Remex.Host/Services/ProcessMonitor/WindowsProcessMonitorService.cs` | KillProcess |

## Entry Points

Start here when exploring this area:

- **`WindowsSystemCommandService`** (Class) — `Remex.Core/Services/Command/WindowsSystemCommandService.cs:7`
- **`LinuxSystemCommandService`** (Class) — `Remex.Core/Services/Command/LinuxSystemCommandService.cs:5`
- **`IpcClientCommandService`** (Class) — `Remex.Client/Services/Command/IpcClientCommandService.cs:13`
- **`KillProcess`** (Method) — `Remex.Host/Services/ProcessMonitor/WindowsProcessMonitorService.cs:121`
- **`KillProcess`** (Method) — `Remex.Host/Services/ProcessMonitor/LinuxProcessMonitorService.cs:136`

## Key Symbols

| Symbol | Type | File | Line |
|--------|------|------|------|
| `WindowsSystemCommandService` | Class | `Remex.Core/Services/Command/WindowsSystemCommandService.cs` | 7 |
| `LinuxSystemCommandService` | Class | `Remex.Core/Services/Command/LinuxSystemCommandService.cs` | 5 |
| `IpcClientCommandService` | Class | `Remex.Client/Services/Command/IpcClientCommandService.cs` | 13 |
| `ISystemCommandService` | Interface | `Remex.Core/Services/Command/ISystemCommandService.cs` | 5 |
| `KillProcess` | Method | `Remex.Host/Services/ProcessMonitor/WindowsProcessMonitorService.cs` | 121 |
| `KillProcess` | Method | `Remex.Host/Services/ProcessMonitor/LinuxProcessMonitorService.cs` | 136 |
| `Shutdown` | Method | `Remex.Core/Services/Command/WindowsSystemCommandService.cs` | 19 |
| `ForceShutdown` | Method | `Remex.Core/Services/Command/WindowsSystemCommandService.cs` | 24 |
| `Restart` | Method | `Remex.Core/Services/Command/WindowsSystemCommandService.cs` | 29 |
| `ForceRestart` | Method | `Remex.Core/Services/Command/WindowsSystemCommandService.cs` | 34 |
| `RestartToUefi` | Method | `Remex.Core/Services/Command/WindowsSystemCommandService.cs` | 39 |
| `Sleep` | Method | `Remex.Core/Services/Command/WindowsSystemCommandService.cs` | 44 |
| `Hibernate` | Method | `Remex.Core/Services/Command/WindowsSystemCommandService.cs` | 49 |
| `SignOut` | Method | `Remex.Core/Services/Command/WindowsSystemCommandService.cs` | 54 |
| `Lock` | Method | `Remex.Core/Services/Command/WindowsSystemCommandService.cs` | 59 |
| `MonitorOff` | Method | `Remex.Core/Services/Command/WindowsSystemCommandService.cs` | 68 |
| `Shutdown` | Method | `Remex.Core/Services/Command/LinuxSystemCommandService.cs` | 7 |
| `ForceShutdown` | Method | `Remex.Core/Services/Command/LinuxSystemCommandService.cs` | 12 |
| `Restart` | Method | `Remex.Core/Services/Command/LinuxSystemCommandService.cs` | 23 |
| `ForceRestart` | Method | `Remex.Core/Services/Command/LinuxSystemCommandService.cs` | 28 |

## Execution Flows

| Flow | Type | Steps |
|------|------|-------|
| `ExecuteAsync → ExecuteProcess` | cross_community | 8 |
| `ExecuteAsync → NormalizeDelay` | cross_community | 8 |
| `ExecuteAsync → ExecuteProcess` | cross_community | 8 |
| `ExecuteAsync → BuildShutdownArgs` | cross_community | 8 |
| `ExecuteAsync → SendCommandAsync` | cross_community | 8 |
| `ExecuteAsync → CommandRequest` | cross_community | 8 |
| `ExecuteAsync → CreateDelayParameters` | cross_community | 8 |
| `ExecuteAsync → Shutdown` | cross_community | 7 |
| `SendWolAsync → SerializeToUtf8Bytes` | cross_community | 6 |
| `WakePc → CommandRequest` | cross_community | 4 |

## Connected Areas

| Area | Connections |
|------|-------------|
| Services | 3 calls |
| Remex.Core.Tests | 3 calls |
| ViewModels | 1 calls |

## How to Explore

1. `gitnexus_context({name: "WindowsSystemCommandService"})` — see callers and callees
2. `gitnexus_query({query: "command"})` — find related execution flows
3. Read key files listed above for implementation details
