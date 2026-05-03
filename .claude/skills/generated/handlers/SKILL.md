---
name: handlers
description: "Skill for the Handlers area of RemEx. 19 symbols across 9 files."
---

# Handlers

19 symbols | 9 files | Cohesion: 73%

## When to Use

- Working with code in `Remex.Host/`
- Understanding how GetScreenSize, GetCursorPosition, HandleAsync work
- Modifying handlers-related functionality

## Key Files

| File | Symbols |
|------|---------|
| `Remex.Host/Handlers/RemoteDesktopHandler.cs` | HandleAsync, StreamCursorPositionAsync, ReceiveInputLoopAsync, ApplyConfig, StreamFramesAsync (+1) |
| `Remex.Host.Tests/RemoteDesktopHandlerTests.cs` | GetScreenSize, GetCursorPosition, CaptureScreenAsync |
| `Remex.Core/Services/IScreenCaptureService.cs` | GetScreenSize, CaptureScreenAsync |
| `Remex.Host/Services/Input/WindowsInputSimulationService.cs` | GetCursorPos, GetCursorPosition |
| `Remex.Host/Services/Input/LinuxInputSimulationService.cs` | GetCursorPosition, RunToolWithOutput |
| `Remex.Core/Services/IInputSimulationService.cs` | GetCursorPosition |
| `Remex.Core/Messages/MessageSerializer.cs` | ReceiveAsync |
| `Remex.Host/Services/ScreenCapture/WindowsScreenCaptureService.cs` | GetScreenSize |
| `Remex.Host/Services/ScreenCapture/LinuxScreenCaptureService.cs` | GetScreenSize |

## Entry Points

Start here when exploring this area:

- **`GetScreenSize`** (Method) — `Remex.Host.Tests/RemoteDesktopHandlerTests.cs:49`
- **`GetCursorPosition`** (Method) — `Remex.Host.Tests/RemoteDesktopHandlerTests.cs:65`
- **`HandleAsync`** (Method) — `Remex.Host/Handlers/RemoteDesktopHandler.cs:70`
- **`ReceiveAsync`** (Method) — `Remex.Core/Messages/MessageSerializer.cs:54`
- **`GetScreenSize`** (Method) — `Remex.Host/Services/ScreenCapture/WindowsScreenCaptureService.cs:132`

## Key Symbols

| Symbol | Type | File | Line |
|--------|------|------|------|
| `GetScreenSize` | Method | `Remex.Host.Tests/RemoteDesktopHandlerTests.cs` | 49 |
| `GetCursorPosition` | Method | `Remex.Host.Tests/RemoteDesktopHandlerTests.cs` | 65 |
| `HandleAsync` | Method | `Remex.Host/Handlers/RemoteDesktopHandler.cs` | 70 |
| `ReceiveAsync` | Method | `Remex.Core/Messages/MessageSerializer.cs` | 54 |
| `GetScreenSize` | Method | `Remex.Host/Services/ScreenCapture/WindowsScreenCaptureService.cs` | 132 |
| `GetScreenSize` | Method | `Remex.Host/Services/ScreenCapture/LinuxScreenCaptureService.cs` | 105 |
| `GetCursorPosition` | Method | `Remex.Host/Services/Input/WindowsInputSimulationService.cs` | 293 |
| `GetCursorPosition` | Method | `Remex.Host/Services/Input/LinuxInputSimulationService.cs` | 48 |
| `CaptureScreenAsync` | Method | `Remex.Host.Tests/RemoteDesktopHandlerTests.cs` | 46 |
| `StreamCursorPositionAsync` | Method | `Remex.Host/Handlers/RemoteDesktopHandler.cs` | 376 |
| `ReceiveInputLoopAsync` | Method | `Remex.Host/Handlers/RemoteDesktopHandler.cs` | 427 |
| `ApplyConfig` | Method | `Remex.Host/Handlers/RemoteDesktopHandler.cs` | 518 |
| `GetScreenSize` | Method | `Remex.Core/Services/IScreenCaptureService.cs` | 17 |
| `GetCursorPosition` | Method | `Remex.Core/Services/IInputSimulationService.cs` | 18 |
| `GetCursorPos` | Method | `Remex.Host/Services/Input/WindowsInputSimulationService.cs` | 282 |
| `RunToolWithOutput` | Method | `Remex.Host/Services/Input/LinuxInputSimulationService.cs` | 236 |
| `StreamFramesAsync` | Method | `Remex.Host/Handlers/RemoteDesktopHandler.cs` | 185 |
| `SendDesktopError` | Method | `Remex.Host/Handlers/RemoteDesktopHandler.cs` | 355 |
| `CaptureScreenAsync` | Method | `Remex.Core/Services/IScreenCaptureService.cs` | 12 |

## Execution Flows

| Flow | Type | Steps |
|------|------|-------|
| `InstallServiceAsync → Deserialize` | cross_community | 8 |
| `Main → GetPlatform` | cross_community | 6 |
| `Main → GetIsInteractiveSession` | cross_community | 6 |
| `Main → GetRuntimeMode` | cross_community | 6 |
| `Main → SupportsRemoteDesktop` | cross_community | 6 |
| `Main → GetCurrent` | cross_community | 5 |
| `Main → GetCurrent` | cross_community | 5 |
| `ConnectAsync → Deserialize` | cross_community | 5 |
| `CreateApplication → Deserialize` | cross_community | 5 |
| `StreamFramesAsync → QueryInterface` | cross_community | 5 |

## Connected Areas

| Area | Connections |
|------|-------------|
| Remex.Host.Tests | 3 calls |
| ScreenCapture | 3 calls |
| Remex.Host | 2 calls |
| Services | 1 calls |
| Remex.Core.Tests | 1 calls |

## How to Explore

1. `gitnexus_context({name: "GetScreenSize"})` — see callers and callees
2. `gitnexus_query({query: "handlers"})` — find related execution flows
3. Read key files listed above for implementation details
