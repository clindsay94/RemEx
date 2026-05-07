---
name: handlers
description: "Skill for the Handlers area of RemEx. 33 symbols across 13 files."
---

# Handlers

33 symbols | 13 files | Cohesion: 67%

## When to Use

- Working with code in `Remex.Host/`
- Understanding how GetScreenSize, GetCursorPosition, HandleAsync work
- Modifying handlers-related functionality

## Key Files

| File | Symbols |
|------|---------|
| `Remex.Host/Handlers/FileTransferHandler.cs` | FileTransferState, HandleFileTransferStartAsync, StreamDownloadAsync, HandleFileTransferChunkAsync, HandleFileTransferEndAsync (+3) |
| `Remex.Host/Handlers/RemoteDesktopHandler.cs` | HandleAsync, StreamCursorPositionAsync, ReceiveInputLoopAsync, ApplyConfig, StreamFramesAsync (+1) |
| `Remex.Host.Tests/RemoteDesktopHandlerTests.cs` | GetScreenSize, GetCursorPosition, CaptureScreenAsync |
| `Remex.Core/Services/IScreenCaptureService.cs` | GetScreenSize, CaptureScreenAsync |
| `Remex.Host/Services/Input/WindowsInputSimulationService.cs` | GetCursorPos, GetCursorPosition |
| `Remex.Host/Services/Input/LinuxInputSimulationService.cs` | GetCursorPosition, RunToolWithOutput |
| `Remex.Core/Services/FileTransfer/IFileTransferService.cs` | OpenForReadAsync, OpenForWriteAsync |
| `Remex.Host/Handlers/PairingHandler.cs` | HandlePairingCompleteAsync, MakeError |
| `Remex.Host/Services/Security/PairingService.cs` | VerifyClientHmacAsync, CancelPairing |
| `Remex.Core/Services/IInputSimulationService.cs` | GetCursorPosition |

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
| `HandleFileTransferStartAsync` | Method | `Remex.Host/Handlers/FileTransferHandler.cs` | 106 |
| `HandleFileTransferChunkAsync` | Method | `Remex.Host/Handlers/FileTransferHandler.cs` | 156 |
| `HandleFileTransferEndAsync` | Method | `Remex.Host/Handlers/FileTransferHandler.cs` | 190 |
| `HandleFileTransferCancelAsync` | Method | `Remex.Host/Handlers/FileTransferHandler.cs` | 224 |
| `CleanupAllTransfersAsync` | Method | `Remex.Host/Handlers/FileTransferHandler.cs` | 232 |
| `CaptureScreenAsync` | Method | `Remex.Host.Tests/RemoteDesktopHandlerTests.cs` | 46 |
| `HandlePairingCompleteAsync` | Method | `Remex.Host/Handlers/PairingHandler.cs` | 82 |
| `VerifyClientHmacAsync` | Method | `Remex.Host/Services/Security/PairingService.cs` | 93 |
| `CancelPairing` | Method | `Remex.Host/Services/Security/PairingService.cs` | 137 |
| `FileTransferState` | Class | `Remex.Host/Handlers/FileTransferHandler.cs` | 16 |
| `StreamCursorPositionAsync` | Method | `Remex.Host/Handlers/RemoteDesktopHandler.cs` | 376 |
| `ReceiveInputLoopAsync` | Method | `Remex.Host/Handlers/RemoteDesktopHandler.cs` | 427 |

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
| Remex.Host.Tests | 7 calls |
| ScreenCapture | 3 calls |
| ProcessMonitor | 2 calls |
| FileTransfer | 2 calls |
| Services | 1 calls |
| Remex.Core.Tests | 1 calls |

## How to Explore

1. `gitnexus_context({name: "GetScreenSize"})` — see callers and callees
2. `gitnexus_query({query: "handlers"})` — find related execution flows
3. Read key files listed above for implementation details
