---
name: input
description: "Skill for the Input area of RemEx. 49 symbols across 6 files."
---

# Input

49 symbols | 6 files | Cohesion: 100%

## When to Use

- Working with code in `remex.agent/`
- Understanding how WindowsInputSimulationService, LinuxInputSimulationService, MoveMouse work
- Modifying input-related functionality

## Key Files

| File | Symbols |
|------|---------|
| `remex.agent/Services/Input/WindowsInputSimulationService.cs` | MoveMouse, MouseMoveRelative, MouseDown, MouseUp, MouseClick (+8) |
| `remex.agent/Services/Input/LinuxInputSimulationService.cs` | MoveMouse, MouseMoveRelative, MouseDown, MouseUp, MouseClick (+8) |
| `remex.agent.tests/RemoteDesktopHandlerTests.cs` | MoveMouse, MouseMoveRelative, MouseDown, MouseUp, MouseClick (+5) |
| `remex.core/Services/IInputSimulationService.cs` | MoveMouse, MouseMoveRelative, MouseDown, MouseUp, MouseClick (+5) |
| `remex.agent/Handlers/RemoteDesktopHandler.cs` | ProcessInputQueue, DispatchInput |
| `remex.agent/Handlers/PingPongHandler.cs` | DispatchInput |

## Entry Points

Start here when exploring this area:

- **`WindowsInputSimulationService`** (Class) — `remex.agent/Services/Input/WindowsInputSimulationService.cs:8`
- **`LinuxInputSimulationService`** (Class) — `remex.agent/Services/Input/LinuxInputSimulationService.cs:9`
- **`MoveMouse`** (Method) — `remex.agent.tests/RemoteDesktopHandlerTests.cs:56`
- **`MouseMoveRelative`** (Method) — `remex.agent.tests/RemoteDesktopHandlerTests.cs:57`
- **`MouseDown`** (Method) — `remex.agent.tests/RemoteDesktopHandlerTests.cs:58`

## Key Symbols

| Symbol | Type | File | Line |
|--------|------|------|------|
| `WindowsInputSimulationService` | Class | `remex.agent/Services/Input/WindowsInputSimulationService.cs` | 8 |
| `LinuxInputSimulationService` | Class | `remex.agent/Services/Input/LinuxInputSimulationService.cs` | 9 |
| `IInputSimulationService` | Interface | `remex.core/Services/IInputSimulationService.cs` | 2 |
| `MoveMouse` | Method | `remex.agent.tests/RemoteDesktopHandlerTests.cs` | 56 |
| `MouseMoveRelative` | Method | `remex.agent.tests/RemoteDesktopHandlerTests.cs` | 57 |
| `MouseDown` | Method | `remex.agent.tests/RemoteDesktopHandlerTests.cs` | 58 |
| `MouseUp` | Method | `remex.agent.tests/RemoteDesktopHandlerTests.cs` | 59 |
| `MouseClick` | Method | `remex.agent.tests/RemoteDesktopHandlerTests.cs` | 60 |
| `MouseScroll` | Method | `remex.agent.tests/RemoteDesktopHandlerTests.cs` | 61 |
| `KeyDown` | Method | `remex.agent.tests/RemoteDesktopHandlerTests.cs` | 62 |
| `KeyUp` | Method | `remex.agent.tests/RemoteDesktopHandlerTests.cs` | 63 |
| `TypeText` | Method | `remex.agent.tests/RemoteDesktopHandlerTests.cs` | 64 |
| `MoveMouse` | Method | `remex.agent/Services/Input/WindowsInputSimulationService.cs` | 18 |
| `MouseMoveRelative` | Method | `remex.agent/Services/Input/WindowsInputSimulationService.cs` | 48 |
| `MouseDown` | Method | `remex.agent/Services/Input/WindowsInputSimulationService.cs` | 67 |
| `MouseUp` | Method | `remex.agent/Services/Input/WindowsInputSimulationService.cs` | 85 |
| `MouseClick` | Method | `remex.agent/Services/Input/WindowsInputSimulationService.cs` | 103 |
| `MouseScroll` | Method | `remex.agent/Services/Input/WindowsInputSimulationService.cs` | 109 |
| `KeyDown` | Method | `remex.agent/Services/Input/WindowsInputSimulationService.cs` | 132 |
| `KeyUp` | Method | `remex.agent/Services/Input/WindowsInputSimulationService.cs` | 156 |

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
