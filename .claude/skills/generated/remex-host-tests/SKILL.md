---
name: remex-host-tests
description: "Skill for the Remex.Host.Tests area of RemEx. 15 symbols across 5 files."
---

# Remex.Host.Tests

15 symbols | 5 files | Cohesion: 80%

## When to Use

- Working with code in `Remex.Host.Tests/`
- Understanding how DesktopStart_ReceivesMetaAndBinaryFrame, DesktopStop_StopsStreaming, Command_Lock_ReturnsSuccess work
- Modifying remex.host.tests-related functionality

## Key Files

| File | Symbols |
|------|---------|
| `Remex.Host.Tests/PingPongTests.cs` | Command_Lock_ReturnsSuccess, GetFactory, PingPong_SendPing_ReceivesPong, PingPong_EchoesTimestamp, PingPong_MultiplePings_AllGetPongs (+1) |
| `Remex.Host.Tests/IpcHostServerTests.cs` | SendCommand_UnknownAction_ReturnsFailure, SendCommand_LaunchApp_MissingPath_ReturnsFailure, SendCommand_LaunchApp_ValidPath_ReturnsSuccess, SendIpcCommand |
| `Remex.Host.Tests/RemoteDesktopHandlerTests.cs` | GetFactory, DesktopStart_ReceivesMetaAndBinaryFrame, DesktopStop_StopsStreaming |
| `Remex.Core/Messages/MessageSerializer.cs` | SendAsync |
| `Remex.Client/ViewModels/ConnectionViewModel.cs` | SendPingAsync |

## Entry Points

Start here when exploring this area:

- **`DesktopStart_ReceivesMetaAndBinaryFrame`** (Method) — `Remex.Host.Tests/RemoteDesktopHandlerTests.cs:96`
- **`DesktopStop_StopsStreaming`** (Method) — `Remex.Host.Tests/RemoteDesktopHandlerTests.cs:164`
- **`Command_Lock_ReturnsSuccess`** (Method) — `Remex.Host.Tests/PingPongTests.cs:30`
- **`PingPong_SendPing_ReceivesPong`** (Method) — `Remex.Host.Tests/PingPongTests.cs:77`
- **`PingPong_EchoesTimestamp`** (Method) — `Remex.Host.Tests/PingPongTests.cs:95`

## Key Symbols

| Symbol | Type | File | Line |
|--------|------|------|------|
| `DesktopStart_ReceivesMetaAndBinaryFrame` | Method | `Remex.Host.Tests/RemoteDesktopHandlerTests.cs` | 96 |
| `DesktopStop_StopsStreaming` | Method | `Remex.Host.Tests/RemoteDesktopHandlerTests.cs` | 164 |
| `Command_Lock_ReturnsSuccess` | Method | `Remex.Host.Tests/PingPongTests.cs` | 30 |
| `PingPong_SendPing_ReceivesPong` | Method | `Remex.Host.Tests/PingPongTests.cs` | 77 |
| `PingPong_EchoesTimestamp` | Method | `Remex.Host.Tests/PingPongTests.cs` | 95 |
| `PingPong_MultiplePings_AllGetPongs` | Method | `Remex.Host.Tests/PingPongTests.cs` | 112 |
| `SendAsync` | Method | `Remex.Core/Messages/MessageSerializer.cs` | 35 |
| `SendCommand_UnknownAction_ReturnsFailure` | Method | `Remex.Host.Tests/IpcHostServerTests.cs` | 38 |
| `SendCommand_LaunchApp_MissingPath_ReturnsFailure` | Method | `Remex.Host.Tests/IpcHostServerTests.cs` | 47 |
| `SendCommand_LaunchApp_ValidPath_ReturnsSuccess` | Method | `Remex.Host.Tests/IpcHostServerTests.cs` | 56 |
| `GetFactory` | Method | `Remex.Host.Tests/RemoteDesktopHandlerTests.cs` | 77 |
| `GetFactory` | Method | `Remex.Host.Tests/PingPongTests.cs` | 60 |
| `ReceivePongAsync` | Method | `Remex.Host.Tests/PingPongTests.cs` | 136 |
| `SendPingAsync` | Method | `Remex.Client/ViewModels/ConnectionViewModel.cs` | 492 |
| `SendIpcCommand` | Method | `Remex.Host.Tests/IpcHostServerTests.cs` | 69 |

## Execution Flows

| Flow | Type | Steps |
|------|------|-------|
| `ShutdownPcAsync → SerializeToUtf8Bytes` | cross_community | 7 |
| `ForceShutdownPcAsync → SerializeToUtf8Bytes` | cross_community | 7 |
| `RestartPcAsync → SerializeToUtf8Bytes` | cross_community | 7 |
| `ForceRestartAsync → SerializeToUtf8Bytes` | cross_community | 7 |
| `RestartToUefiAsync → SerializeToUtf8Bytes` | cross_community | 7 |
| `OnStagedCardsCollectionChanged → SerializeToUtf8Bytes` | cross_community | 7 |
| `OnCardDropped → SerializeToUtf8Bytes` | cross_community | 7 |
| `SendWolAsync → SerializeToUtf8Bytes` | cross_community | 6 |
| `LaunchAppAsync → SerializeToUtf8Bytes` | cross_community | 6 |
| `ApplySensorAlert → SerializeToUtf8Bytes` | cross_community | 6 |

## Connected Areas

| Area | Connections |
|------|-------------|
| Handlers | 2 calls |
| Remex.Core.Tests | 1 calls |

## How to Explore

1. `gitnexus_context({name: "DesktopStart_ReceivesMetaAndBinaryFrame"})` — see callers and callees
2. `gitnexus_query({query: "remex.host.tests"})` — find related execution flows
3. Read key files listed above for implementation details
