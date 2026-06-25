---
name: remex-core-tests
description: "Skill for the remex.core.tests area of RemEx. 23 symbols across 6 files."
---

# remex.core.tests

23 symbols | 6 files | Cohesion: 57%

## When to Use

- Working with code in `remex.core.tests/`
- Understanding how RoundTrip_DesktopMetaMessage_PreservesAllFields, RoundTrip_DesktopInputKeyDown_PreservesKeyCode, RoundTrip_DesktopStopMessage work
- Modifying remex.core.tests-related functionality

## Key Files

| File | Symbols |
|------|---------|
| `remex.core.tests/RemoteDesktopMessageTests.cs` | RoundTrip_DesktopMetaMessage_PreservesAllFields, RoundTrip_DesktopInputKeyDown_PreservesKeyCode, RoundTrip_DesktopStopMessage, RoundTrip_DesktopStartWithConfig_PreservesAllFields, RoundTrip_DesktopInputMouseMove_PreservesAllFields (+2) |
| `remex.core.tests/MessageSerializerTests.cs` | RoundTrip_PingMessage_PreservesAllFields, RoundTrip_NullTimestamp_IsPreserved, Deserialize_MalformedJson_ReturnsNull, Deserialize_EmptyBytes_ReturnsNull, Serialize_PingMessage_ProducesValidJson (+1) |
| `remex.core.tests/WakeOnLanServiceTests.cs` | WakeAsync_Accepts_Valid_MAC_Formats, WakeAsync_Rejects_Invalid_MAC, WakeAsync_Rejects_Invalid_BroadcastIp, WakeAsync_Accepts_Valid_BroadcastIp |
| `remex.core.tests/RemexMessageTests.cs` | RemexMessage_CommandType_SerializesCorrectly, HostInfoMessage_RoundTripsCapabilities |
| `remex.core/Messages/MessageSerializer.cs` | Deserialize, Serialize |
| `remex.core/Services/Network/WakeOnLanService.cs` | WakeAsync, SendToEndpointAsync |

## Entry Points

Start here when exploring this area:

- **`RoundTrip_DesktopMetaMessage_PreservesAllFields`** (Method) — `remex.core.tests/RemoteDesktopMessageTests.cs:27`
- **`RoundTrip_DesktopInputKeyDown_PreservesKeyCode`** (Method) — `remex.core.tests/RemoteDesktopMessageTests.cs:71`
- **`RoundTrip_DesktopStopMessage`** (Method) — `remex.core.tests/RemoteDesktopMessageTests.cs:117`
- **`RemexMessage_CommandType_SerializesCorrectly`** (Method) — `remex.core.tests/RemexMessageTests.cs:7`
- **`RoundTrip_PingMessage_PreservesAllFields`** (Method) — `remex.core.tests/MessageSerializerTests.cs:18`

## Key Symbols

| Symbol | Type | File | Line |
|--------|------|------|------|
| `RoundTrip_DesktopMetaMessage_PreservesAllFields` | Method | `remex.core.tests/RemoteDesktopMessageTests.cs` | 27 |
| `RoundTrip_DesktopInputKeyDown_PreservesKeyCode` | Method | `remex.core.tests/RemoteDesktopMessageTests.cs` | 71 |
| `RoundTrip_DesktopStopMessage` | Method | `remex.core.tests/RemoteDesktopMessageTests.cs` | 117 |
| `RemexMessage_CommandType_SerializesCorrectly` | Method | `remex.core.tests/RemexMessageTests.cs` | 7 |
| `RoundTrip_PingMessage_PreservesAllFields` | Method | `remex.core.tests/MessageSerializerTests.cs` | 18 |
| `RoundTrip_NullTimestamp_IsPreserved` | Method | `remex.core.tests/MessageSerializerTests.cs` | 42 |
| `Deserialize_MalformedJson_ReturnsNull` | Method | `remex.core.tests/MessageSerializerTests.cs` | 53 |
| `Deserialize_EmptyBytes_ReturnsNull` | Method | `remex.core.tests/MessageSerializerTests.cs` | 61 |
| `Deserialize` | Method | `remex.core/Messages/MessageSerializer.cs` | 20 |
| `RoundTrip_DesktopStartWithConfig_PreservesAllFields` | Method | `remex.core.tests/RemoteDesktopMessageTests.cs` | 7 |
| `RoundTrip_DesktopInputMouseMove_PreservesAllFields` | Method | `remex.core.tests/RemoteDesktopMessageTests.cs` | 46 |
| `RoundTrip_DesktopInputMouseScroll_PreservesDelta` | Method | `remex.core.tests/RemoteDesktopMessageTests.cs` | 93 |
| `RoundTrip_DesktopConfigMessage` | Method | `remex.core.tests/RemoteDesktopMessageTests.cs` | 128 |
| `HostInfoMessage_RoundTripsCapabilities` | Method | `remex.core.tests/RemexMessageTests.cs` | 58 |
| `Serialize_PingMessage_ProducesValidJson` | Method | `remex.core.tests/MessageSerializerTests.cs` | 6 |
| `RoundTrip_PongMessage_PreservesAllFields` | Method | `remex.core.tests/MessageSerializerTests.cs` | 30 |
| `Serialize` | Method | `remex.core/Messages/MessageSerializer.cs` | 13 |
| `WakeAsync_Accepts_Valid_MAC_Formats` | Method | `remex.core.tests/WakeOnLanServiceTests.cs` | 8 |
| `WakeAsync_Rejects_Invalid_MAC` | Method | `remex.core.tests/WakeOnLanServiceTests.cs` | 20 |
| `WakeAsync_Rejects_Invalid_BroadcastIp` | Method | `remex.core.tests/WakeOnLanServiceTests.cs` | 30 |

## Execution Flows

| Flow | Type | Steps |
|------|------|-------|
| `UploadAsync → SerializeToUtf8Bytes` | cross_community | 8 |
| `InstallWindowsServiceAsync → Deserialize` | cross_community | 7 |
| `StartPairingNative → Deserialize` | cross_community | 6 |
| `SendWolAsync → SerializeToUtf8Bytes` | cross_community | 6 |
| `DownloadAsync → SerializeToUtf8Bytes` | cross_community | 6 |
| `LaunchAppAsync → SerializeToUtf8Bytes` | cross_community | 6 |
| `ApplySensorAlert → SerializeToUtf8Bytes` | cross_community | 6 |
| `StartPairingNative → SerializeToUtf8Bytes` | cross_community | 5 |
| `CompletePairingAsync → Deserialize` | cross_community | 5 |
| `HandleAsync → SerializeToUtf8Bytes` | cross_community | 4 |

## Connected Areas

| Area | Connections |
|------|-------------|
| Native | 2 calls |

## How to Explore

1. `gitnexus_context({name: "RoundTrip_DesktopMetaMessage_PreservesAllFields"})` — see callers and callees
2. `gitnexus_query({query: "remex.core.tests"})` — find related execution flows
3. Read key files listed above for implementation details
