---
name: command
description: "Skill for the Command area of RemEx. 67 symbols across 13 files."
---

# Command

67 symbols | 13 files | Cohesion: 96%

## When to Use

- Working with code in `remex.core/`
- Understanding how WindowsSystemCommandService, LinuxSystemCommandService, IpcClientCommandService work
- Modifying command-related functionality

## Key Files

| File | Symbols |
|------|---------|
| `remex.core/Services/Command/WindowsSystemCommandService.cs` | LockWorkStation, SendMessage, Shutdown, ForceShutdown, Restart (+10) |
| `remex.core/Services/Command/LinuxSystemCommandService.cs` | Shutdown, ForceShutdown, Restart, ForceRestart, RestartToUefi (+8) |
| `remex.desktop/Services/Command/IpcClientCommandService.cs` | Shutdown, ForceShutdown, Restart, ForceRestart, RestartToUefi (+8) |
| `remex.core/Services/Command/ISystemCommandService.cs` | Shutdown, ForceShutdown, Restart, ForceRestart, RestartToUefi (+6) |
| `remex.agent/Services/IPC/LocalIpcServerService.cs` | ExecuteAsync, HandleClientAsync, ExecuteCommandAsync, ParseDelaySeconds |
| `remex.agent/Handlers/PingPongHandler.cs` | ExecuteCommandAsync, MakeCommandResponse, ParseDelaySeconds |
| `remex.core/Services/Network/RemexNetworkListener.cs` | ExecuteCommandAsync, ParseDelaySeconds |
| `remex.core/Services/IProcessMonitorService.cs` | KillProcess |
| `remex.desktop/ViewModels/RemoteViewModel.cs` | SendWolAsync |
| `remex.agent/Services/ProcessMonitor/WindowsProcessMonitorService.cs` | KillProcess |

## Entry Points

Start here when exploring this area:

- **`WindowsSystemCommandService`** (Class) — `remex.core/Services/Command/WindowsSystemCommandService.cs:7`
- **`LinuxSystemCommandService`** (Class) — `remex.core/Services/Command/LinuxSystemCommandService.cs:5`
- **`IpcClientCommandService`** (Class) — `remex.desktop/Services/Command/IpcClientCommandService.cs:13`
- **`KillProcess`** (Method) — `remex.agent/Services/ProcessMonitor/WindowsProcessMonitorService.cs:121`
- **`KillProcess`** (Method) — `remex.agent/Services/ProcessMonitor/LinuxProcessMonitorService.cs:136`

## Key Symbols

| Symbol | Type | File | Line |
|--------|------|------|------|
| `WindowsSystemCommandService` | Class | `remex.core/Services/Command/WindowsSystemCommandService.cs` | 7 |
| `LinuxSystemCommandService` | Class | `remex.core/Services/Command/LinuxSystemCommandService.cs` | 5 |
| `IpcClientCommandService` | Class | `remex.desktop/Services/Command/IpcClientCommandService.cs` | 13 |
| `ISystemCommandService` | Interface | `remex.core/Services/Command/ISystemCommandService.cs` | 5 |
| `KillProcess` | Method | `remex.agent/Services/ProcessMonitor/WindowsProcessMonitorService.cs` | 121 |
| `KillProcess` | Method | `remex.agent/Services/ProcessMonitor/LinuxProcessMonitorService.cs` | 136 |
| `Shutdown` | Method | `remex.core/Services/Command/WindowsSystemCommandService.cs` | 19 |
| `ForceShutdown` | Method | `remex.core/Services/Command/WindowsSystemCommandService.cs` | 24 |
| `Restart` | Method | `remex.core/Services/Command/WindowsSystemCommandService.cs` | 29 |
| `ForceRestart` | Method | `remex.core/Services/Command/WindowsSystemCommandService.cs` | 34 |
| `RestartToUefi` | Method | `remex.core/Services/Command/WindowsSystemCommandService.cs` | 39 |
| `Sleep` | Method | `remex.core/Services/Command/WindowsSystemCommandService.cs` | 44 |
| `Hibernate` | Method | `remex.core/Services/Command/WindowsSystemCommandService.cs` | 49 |
| `SignOut` | Method | `remex.core/Services/Command/WindowsSystemCommandService.cs` | 54 |
| `Lock` | Method | `remex.core/Services/Command/WindowsSystemCommandService.cs` | 59 |
| `MonitorOff` | Method | `remex.core/Services/Command/WindowsSystemCommandService.cs` | 68 |
| `Shutdown` | Method | `remex.core/Services/Command/LinuxSystemCommandService.cs` | 7 |
| `ForceShutdown` | Method | `remex.core/Services/Command/LinuxSystemCommandService.cs` | 12 |
| `Restart` | Method | `remex.core/Services/Command/LinuxSystemCommandService.cs` | 23 |
| `ForceRestart` | Method | `remex.core/Services/Command/LinuxSystemCommandService.cs` | 28 |

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
| remex.core.tests | 3 calls |
| ViewModels | 1 calls |

## How to Explore

1. `gitnexus_context({name: "WindowsSystemCommandService"})` — see callers and callees
2. `gitnexus_query({query: "command"})` — find related execution flows
3. Read key files listed above for implementation details
