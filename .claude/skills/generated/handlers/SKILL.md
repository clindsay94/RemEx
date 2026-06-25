---
name: handlers
description: "Skill for the Handlers area of RemEx. 33 symbols across 13 files."
---

# Handlers

33 symbols | 13 files | Cohesion: 67%

## When to Use

- Working with code in `remex.agent/`
- Understanding how GetScreenSize, GetCursorPosition, HandleAsync work
- Modifying handlers-related functionality

## Key Files

| File | Symbols |
|------|---------|
| `remex.agent/Handlers/FileTransferHandler.cs` | FileTransferState, HandleFileTransferStartAsync, StreamDownloadAsync, HandleFileTransferChunkAsync, HandleFileTransferEndAsync (+3) |
| `remex.agent/Handlers/RemoteDesktopHandler.cs` | HandleAsync, StreamCursorPositionAsync, ReceiveInputLoopAsync, ApplyConfig, StreamFramesAsync (+1) |
| `remex.agent.tests/RemoteDesktopHandlerTests.cs` | GetScreenSize, GetCursorPosition, CaptureScreenAsync |
| `remex.core/Services/IScreenCaptureService.cs` | GetScreenSize, CaptureScreenAsync |
| `remex.agent/Services/Input/WindowsInputSimulationService.cs` | GetCursorPos, GetCursorPosition |
| `remex.agent/Services/Input/LinuxInputSimulationService.cs` | GetCursorPosition, RunToolWithOutput |
| `remex.core/Services/FileTransfer/IFileTransferService.cs` | OpenForReadAsync, OpenForWriteAsync |
| `remex.agent/Handlers/PairingHandler.cs` | HandlePairingCompleteAsync, MakeError |
| `remex.agent/Services/Security/PairingService.cs` | VerifyClientHmacAsync, CancelPairing |
| `remex.core/Services/IInputSimulationService.cs` | GetCursorPosition |

## Entry Points

Start here when exploring this area:

- **`GetScreenSize`** (Method) — `remex.agent.tests/RemoteDesktopHandlerTests.cs:49`
- **`GetCursorPosition`** (Method) — `remex.agent.tests/RemoteDesktopHandlerTests.cs:65`
- **`HandleAsync`** (Method) — `remex.agent/Handlers/RemoteDesktopHandler.cs:70`
- **`ReceiveAsync`** (Method) — `remex.core/Messages/MessageSerializer.cs:54`
- **`GetScreenSize`** (Method) — `remex.agent/Services/ScreenCapture/WindowsScreenCaptureService.cs:132`

## Key Symbols

| Symbol | Type | File | Line |
|--------|------|------|------|
| `GetScreenSize` | Method | `remex.agent.tests/RemoteDesktopHandlerTests.cs` | 49 |
| `GetCursorPosition` | Method | `remex.agent.tests/RemoteDesktopHandlerTests.cs` | 65 |
| `HandleAsync` | Method | `remex.agent/Handlers/RemoteDesktopHandler.cs` | 70 |
| `ReceiveAsync` | Method | `remex.core/Messages/MessageSerializer.cs` | 54 |
| `GetScreenSize` | Method | `remex.agent/Services/ScreenCapture/WindowsScreenCaptureService.cs` | 132 |
| `GetScreenSize` | Method | `remex.agent/Services/ScreenCapture/LinuxScreenCaptureService.cs` | 105 |
| `GetCursorPosition` | Method | `remex.agent/Services/Input/WindowsInputSimulationService.cs` | 293 |
| `GetCursorPosition` | Method | `remex.agent/Services/Input/LinuxInputSimulationService.cs` | 48 |
| `HandleFileTransferStartAsync` | Method | `remex.agent/Handlers/FileTransferHandler.cs` | 106 |
| `HandleFileTransferChunkAsync` | Method | `remex.agent/Handlers/FileTransferHandler.cs` | 156 |
| `HandleFileTransferEndAsync` | Method | `remex.agent/Handlers/FileTransferHandler.cs` | 190 |
| `HandleFileTransferCancelAsync` | Method | `remex.agent/Handlers/FileTransferHandler.cs` | 224 |
| `CleanupAllTransfersAsync` | Method | `remex.agent/Handlers/FileTransferHandler.cs` | 232 |
| `CaptureScreenAsync` | Method | `remex.agent.tests/RemoteDesktopHandlerTests.cs` | 46 |
| `HandlePairingCompleteAsync` | Method | `remex.agent/Handlers/PairingHandler.cs` | 82 |
| `VerifyClientHmacAsync` | Method | `remex.agent/Services/Security/PairingService.cs` | 93 |
| `CancelPairing` | Method | `remex.agent/Services/Security/PairingService.cs` | 137 |
| `FileTransferState` | Class | `remex.agent/Handlers/FileTransferHandler.cs` | 16 |
| `StreamCursorPositionAsync` | Method | `remex.agent/Handlers/RemoteDesktopHandler.cs` | 376 |
| `ReceiveInputLoopAsync` | Method | `remex.agent/Handlers/RemoteDesktopHandler.cs` | 427 |

## Execution Flows

| Flow | Type | Steps |
|------|------|-------|
| `InstallWindowsServiceAsync → Deserialize` | cross_community | 7 |
| `HandleFileTransferStartAsync → CreateDefaultRoots` | cross_community | 6 |
| `HandleFileTransferStartAsync → SaveConfiguredRoots` | cross_community | 6 |
| `StartPairingNative → Deserialize` | cross_community | 6 |
| `StreamFramesAsync → QueryInterface` | cross_community | 5 |
| `StreamFramesAsync → Release` | cross_community | 5 |
| `CompletePairingAsync → Deserialize` | cross_community | 5 |
| `HandleAsync → Deserialize` | cross_community | 4 |
| `StreamFramesAsync → CURSORINFO` | cross_community | 4 |
| `StreamFramesAsync → GetCursorInfo` | cross_community | 4 |

## Connected Areas

| Area | Connections |
|------|-------------|
| remex.agent.tests | 7 calls |
| ScreenCapture | 3 calls |
| ProcessMonitor | 2 calls |
| FileTransfer | 2 calls |
| Services | 1 calls |
| remex.core.tests | 1 calls |

## How to Explore

1. `gitnexus_context({name: "GetScreenSize"})` — see callers and callees
2. `gitnexus_query({query: "handlers"})` — find related execution flows
3. Read key files listed above for implementation details
