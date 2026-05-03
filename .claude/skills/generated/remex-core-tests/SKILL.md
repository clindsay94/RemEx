---
name: remex-core-tests
description: "Skill for the Remex.Core.Tests area of RemEx. 23 symbols across 6 files."
---

# Remex.Core.Tests

23 symbols | 6 files | Cohesion: 57%

## When to Use

- Working with code in `Remex.Core.Tests/`
- Understanding how RoundTrip_DesktopMetaMessage_PreservesAllFields, RoundTrip_DesktopInputKeyDown_PreservesKeyCode, RoundTrip_DesktopStopMessage work
- Modifying remex.core.tests-related functionality

## Key Files

| File | Symbols |
|------|---------|
| `Remex.Core.Tests/RemoteDesktopMessageTests.cs` | RoundTrip_DesktopMetaMessage_PreservesAllFields, RoundTrip_DesktopInputKeyDown_PreservesKeyCode, RoundTrip_DesktopStopMessage, RoundTrip_DesktopStartWithConfig_PreservesAllFields, RoundTrip_DesktopInputMouseMove_PreservesAllFields (+2) |
| `Remex.Core.Tests/MessageSerializerTests.cs` | RoundTrip_PingMessage_PreservesAllFields, RoundTrip_PongMessage_PreservesAllFields, Deserialize_MalformedJson_ReturnsNull, Deserialize_EmptyBytes_ReturnsNull, Serialize_PingMessage_ProducesValidJson (+1) |
| `Remex.Core.Tests/WakeOnLanServiceTests.cs` | WakeAsync_Accepts_Valid_MAC_Formats, WakeAsync_Rejects_Invalid_MAC, WakeAsync_Rejects_Invalid_BroadcastIp, WakeAsync_Accepts_Valid_BroadcastIp |
| `Remex.Core.Tests/RemexMessageTests.cs` | RemexMessage_CommandType_SerializesCorrectly, HostInfoMessage_RoundTripsCapabilities |
| `Remex.Core/Messages/MessageSerializer.cs` | Deserialize, Serialize |
| `Remex.Core/Services/Network/WakeOnLanService.cs` | WakeAsync, SendToEndpointAsync |

## Entry Points

Start here when exploring this area:

- **`RoundTrip_DesktopMetaMessage_PreservesAllFields`** (Method) — `Remex.Core.Tests/RemoteDesktopMessageTests.cs:27`
- **`RoundTrip_DesktopInputKeyDown_PreservesKeyCode`** (Method) — `Remex.Core.Tests/RemoteDesktopMessageTests.cs:71`
- **`RoundTrip_DesktopStopMessage`** (Method) — `Remex.Core.Tests/RemoteDesktopMessageTests.cs:117`
- **`RemexMessage_CommandType_SerializesCorrectly`** (Method) — `Remex.Core.Tests/RemexMessageTests.cs:7`
- **`RoundTrip_PingMessage_PreservesAllFields`** (Method) — `Remex.Core.Tests/MessageSerializerTests.cs:18`

## Key Symbols

| Symbol | Type | File | Line |
|--------|------|------|------|
| `RoundTrip_DesktopMetaMessage_PreservesAllFields` | Method | `Remex.Core.Tests/RemoteDesktopMessageTests.cs` | 27 |
| `RoundTrip_DesktopInputKeyDown_PreservesKeyCode` | Method | `Remex.Core.Tests/RemoteDesktopMessageTests.cs` | 71 |
| `RoundTrip_DesktopStopMessage` | Method | `Remex.Core.Tests/RemoteDesktopMessageTests.cs` | 117 |
| `RemexMessage_CommandType_SerializesCorrectly` | Method | `Remex.Core.Tests/RemexMessageTests.cs` | 7 |
| `RoundTrip_PingMessage_PreservesAllFields` | Method | `Remex.Core.Tests/MessageSerializerTests.cs` | 18 |
| `RoundTrip_PongMessage_PreservesAllFields` | Method | `Remex.Core.Tests/MessageSerializerTests.cs` | 30 |
| `Deserialize_MalformedJson_ReturnsNull` | Method | `Remex.Core.Tests/MessageSerializerTests.cs` | 53 |
| `Deserialize_EmptyBytes_ReturnsNull` | Method | `Remex.Core.Tests/MessageSerializerTests.cs` | 61 |
| `Deserialize` | Method | `Remex.Core/Messages/MessageSerializer.cs` | 20 |
| `RoundTrip_DesktopStartWithConfig_PreservesAllFields` | Method | `Remex.Core.Tests/RemoteDesktopMessageTests.cs` | 7 |
| `RoundTrip_DesktopInputMouseMove_PreservesAllFields` | Method | `Remex.Core.Tests/RemoteDesktopMessageTests.cs` | 46 |
| `RoundTrip_DesktopInputMouseScroll_PreservesDelta` | Method | `Remex.Core.Tests/RemoteDesktopMessageTests.cs` | 93 |
| `RoundTrip_DesktopConfigMessage` | Method | `Remex.Core.Tests/RemoteDesktopMessageTests.cs` | 128 |
| `HostInfoMessage_RoundTripsCapabilities` | Method | `Remex.Core.Tests/RemexMessageTests.cs` | 58 |
| `Serialize_PingMessage_ProducesValidJson` | Method | `Remex.Core.Tests/MessageSerializerTests.cs` | 6 |
| `RoundTrip_NullTimestamp_IsPreserved` | Method | `Remex.Core.Tests/MessageSerializerTests.cs` | 42 |
| `Serialize` | Method | `Remex.Core/Messages/MessageSerializer.cs` | 13 |
| `WakeAsync_Accepts_Valid_MAC_Formats` | Method | `Remex.Core.Tests/WakeOnLanServiceTests.cs` | 8 |
| `WakeAsync_Rejects_Invalid_MAC` | Method | `Remex.Core.Tests/WakeOnLanServiceTests.cs` | 20 |
| `WakeAsync_Rejects_Invalid_BroadcastIp` | Method | `Remex.Core.Tests/WakeOnLanServiceTests.cs` | 30 |

## Execution Flows

| Flow | Type | Steps |
|------|------|-------|
| `InstallServiceAsync → Deserialize` | cross_community | 8 |
| `ShutdownPcAsync → SerializeToUtf8Bytes` | cross_community | 7 |
| `ForceShutdownPcAsync → SerializeToUtf8Bytes` | cross_community | 7 |
| `RestartPcAsync → SerializeToUtf8Bytes` | cross_community | 7 |
| `ForceRestartAsync → SerializeToUtf8Bytes` | cross_community | 7 |
| `RestartToUefiAsync → SerializeToUtf8Bytes` | cross_community | 7 |
| `OnStagedCardsCollectionChanged → SerializeToUtf8Bytes` | cross_community | 7 |
| `OnCardDropped → SerializeToUtf8Bytes` | cross_community | 7 |
| `SendWolAsync → SerializeToUtf8Bytes` | cross_community | 6 |
| `LaunchAppAsync → SerializeToUtf8Bytes` | cross_community | 6 |

## Connected Areas

| Area | Connections |
|------|-------------|
| Native | 2 calls |

## How to Explore

1. `gitnexus_context({name: "RoundTrip_DesktopMetaMessage_PreservesAllFields"})` — see callers and callees
2. `gitnexus_query({query: "remex.core.tests"})` — find related execution flows
3. Read key files listed above for implementation details
