---
name: input
description: "Skill for the Input area of RemEx. 49 symbols across 6 files."
---

# Input

49 symbols | 6 files | Cohesion: 100%

## When to Use

- Working with code in `Remex.Host/`
- Understanding how WindowsInputSimulationService, LinuxInputSimulationService, MoveMouse work
- Modifying input-related functionality

## Key Files

| File | Symbols |
|------|---------|
| `Remex.Host/Services/Input/WindowsInputSimulationService.cs` | MoveMouse, MouseMoveRelative, MouseDown, MouseUp, MouseClick (+8) |
| `Remex.Host/Services/Input/LinuxInputSimulationService.cs` | MoveMouse, MouseMoveRelative, MouseDown, MouseUp, MouseClick (+8) |
| `Remex.Host.Tests/RemoteDesktopHandlerTests.cs` | MoveMouse, MouseMoveRelative, MouseDown, MouseUp, MouseClick (+5) |
| `Remex.Core/Services/IInputSimulationService.cs` | MoveMouse, MouseMoveRelative, MouseDown, MouseUp, MouseClick (+5) |
| `Remex.Host/Handlers/RemoteDesktopHandler.cs` | ProcessInputQueue, DispatchInput |
| `Remex.Host/Handlers/PingPongHandler.cs` | DispatchInput |

## Entry Points

Start here when exploring this area:

- **`WindowsInputSimulationService`** (Class) — `Remex.Host/Services/Input/WindowsInputSimulationService.cs:8`
- **`LinuxInputSimulationService`** (Class) — `Remex.Host/Services/Input/LinuxInputSimulationService.cs:9`
- **`MoveMouse`** (Method) — `Remex.Host.Tests/RemoteDesktopHandlerTests.cs:56`
- **`MouseMoveRelative`** (Method) — `Remex.Host.Tests/RemoteDesktopHandlerTests.cs:57`
- **`MouseDown`** (Method) — `Remex.Host.Tests/RemoteDesktopHandlerTests.cs:58`

## Key Symbols

| Symbol | Type | File | Line |
|--------|------|------|------|
| `WindowsInputSimulationService` | Class | `Remex.Host/Services/Input/WindowsInputSimulationService.cs` | 8 |
| `LinuxInputSimulationService` | Class | `Remex.Host/Services/Input/LinuxInputSimulationService.cs` | 9 |
| `IInputSimulationService` | Interface | `Remex.Core/Services/IInputSimulationService.cs` | 2 |
| `MoveMouse` | Method | `Remex.Host.Tests/RemoteDesktopHandlerTests.cs` | 56 |
| `MouseMoveRelative` | Method | `Remex.Host.Tests/RemoteDesktopHandlerTests.cs` | 57 |
| `MouseDown` | Method | `Remex.Host.Tests/RemoteDesktopHandlerTests.cs` | 58 |
| `MouseUp` | Method | `Remex.Host.Tests/RemoteDesktopHandlerTests.cs` | 59 |
| `MouseClick` | Method | `Remex.Host.Tests/RemoteDesktopHandlerTests.cs` | 60 |
| `MouseScroll` | Method | `Remex.Host.Tests/RemoteDesktopHandlerTests.cs` | 61 |
| `KeyDown` | Method | `Remex.Host.Tests/RemoteDesktopHandlerTests.cs` | 62 |
| `KeyUp` | Method | `Remex.Host.Tests/RemoteDesktopHandlerTests.cs` | 63 |
| `TypeText` | Method | `Remex.Host.Tests/RemoteDesktopHandlerTests.cs` | 64 |
| `MoveMouse` | Method | `Remex.Host/Services/Input/WindowsInputSimulationService.cs` | 18 |
| `MouseMoveRelative` | Method | `Remex.Host/Services/Input/WindowsInputSimulationService.cs` | 48 |
| `MouseDown` | Method | `Remex.Host/Services/Input/WindowsInputSimulationService.cs` | 67 |
| `MouseUp` | Method | `Remex.Host/Services/Input/WindowsInputSimulationService.cs` | 85 |
| `MouseClick` | Method | `Remex.Host/Services/Input/WindowsInputSimulationService.cs` | 103 |
| `MouseScroll` | Method | `Remex.Host/Services/Input/WindowsInputSimulationService.cs` | 109 |
| `KeyDown` | Method | `Remex.Host/Services/Input/WindowsInputSimulationService.cs` | 132 |
| `KeyUp` | Method | `Remex.Host/Services/Input/WindowsInputSimulationService.cs` | 156 |

## Execution Flows

| Flow | Type | Steps |
|------|------|-------|
| `DispatchInput → GetSystemMetrics` | intra_community | 3 |
| `DispatchInput → INPUT` | intra_community | 3 |
| `DispatchInput → InputUnion` | intra_community | 3 |
| `DispatchInput → MOUSEINPUT` | intra_community | 3 |
| `DispatchInput → RunTool` | intra_community | 3 |
| `DispatchInput → GetSystemMetrics` | intra_community | 3 |
| `DispatchInput → INPUT` | intra_community | 3 |
| `DispatchInput → InputUnion` | intra_community | 3 |
| `DispatchInput → MOUSEINPUT` | intra_community | 3 |
| `DispatchInput → RunTool` | intra_community | 3 |

## How to Explore

1. `gitnexus_context({name: "WindowsInputSimulationService"})` — see callers and callees
2. `gitnexus_query({query: "input"})` — find related execution flows
3. Read key files listed above for implementation details
