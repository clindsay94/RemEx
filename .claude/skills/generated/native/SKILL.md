---
name: native
description: "Skill for the Native area of RemEx. 82 symbols across 7 files."
---

# Native

82 symbols | 7 files | Cohesion: 75%

## When to Use

- Working with code in `remex.core/`
- Understanding how PairingClient, LoadProfileAsync, SerializeToUtf8Bytes work
- Modifying native-related functionality

## Key Files

| File | Symbols |
|------|---------|
| `remex.core/Native/AndroidNativeExports.cs` | SendCommand, StartDesktopStream, StopDesktopStream, ProcessOutboundMessagesAsync, HandleDesktopMessage (+36) |
| `remex.core/Native/JniHelper.cs` | __android_log_print, AndroidLogE, ReadJString, CreateJString, GetJavaVM (+10) |
| `remex.core/Native/RemexDesktopClient.cs` | ConnectAsync, EnsureConnectedAsync, StartStreamAsync, StopStreamAsync, SendInputAsync (+5) |
| `remex.core/Native/RemexNativeClient.cs` | SendCommandAsync, SendMessageAsync, ConnectAsync, DisconnectAsync, ReceiveLoopAsync (+2) |
| `remex.core/Serialization/RemexJsonSerializerContext.cs` | SerializeToUtf8Bytes, Deserialize, Deserialize, Serialize, SerializeIndented |
| `remex.core/Services/DashboardProfileStorageService.cs` | LoadProfileAsync, SaveProfileAsync |
| `remex.core/Native/PairingClient.cs` | PairingClient, StartPairingAsync |

## Entry Points

Start here when exploring this area:

- **`PairingClient`** (Class) — `remex.core/Native/PairingClient.cs:12`
- **`LoadProfileAsync`** (Method) — `remex.core/Services/DashboardProfileStorageService.cs:28`
- **`SerializeToUtf8Bytes`** (Method) — `remex.core/Serialization/RemexJsonSerializerContext.cs:80`
- **`Deserialize`** (Method) — `remex.core/Serialization/RemexJsonSerializerContext.cs:90`
- **`Deserialize`** (Method) — `remex.core/Serialization/RemexJsonSerializerContext.cs:93`

## Key Symbols

| Symbol | Type | File | Line |
|--------|------|------|------|
| `PairingClient` | Class | `remex.core/Native/PairingClient.cs` | 12 |
| `LoadProfileAsync` | Method | `remex.core/Services/DashboardProfileStorageService.cs` | 28 |
| `SerializeToUtf8Bytes` | Method | `remex.core/Serialization/RemexJsonSerializerContext.cs` | 80 |
| `Deserialize` | Method | `remex.core/Serialization/RemexJsonSerializerContext.cs` | 90 |
| `Deserialize` | Method | `remex.core/Serialization/RemexJsonSerializerContext.cs` | 93 |
| `SendCommandAsync` | Method | `remex.core/Native/RemexNativeClient.cs` | 157 |
| `SendMessageAsync` | Method | `remex.core/Native/RemexNativeClient.cs` | 195 |
| `ConnectAsync` | Method | `remex.core/Native/RemexDesktopClient.cs` | 33 |
| `EnsureConnectedAsync` | Method | `remex.core/Native/RemexDesktopClient.cs` | 58 |
| `StartStreamAsync` | Method | `remex.core/Native/RemexDesktopClient.cs` | 68 |
| `StopStreamAsync` | Method | `remex.core/Native/RemexDesktopClient.cs` | 89 |
| `SendInputAsync` | Method | `remex.core/Native/RemexDesktopClient.cs` | 104 |
| `SendConfigAsync` | Method | `remex.core/Native/RemexDesktopClient.cs` | 120 |
| `DisconnectAsync` | Method | `remex.core/Native/RemexDesktopClient.cs` | 145 |
| `Dispose` | Method | `remex.core/Native/RemexDesktopClient.cs` | 219 |
| `AndroidLogE` | Method | `remex.core/Native/JniHelper.cs` | 172 |
| `SendCommand` | Method | `remex.core/Native/AndroidNativeExports.cs` | 154 |
| `StartDesktopStream` | Method | `remex.core/Native/AndroidNativeExports.cs` | 158 |
| `StopDesktopStream` | Method | `remex.core/Native/AndroidNativeExports.cs` | 177 |
| `SaveProfileAsync` | Method | `remex.core/Services/DashboardProfileStorageService.cs` | 44 |

## Execution Flows

| Flow | Type | Steps |
|------|------|-------|
| `SendMessage → Dispose` | cross_community | 8 |
| `UploadAsync → SerializeToUtf8Bytes` | cross_community | 8 |
| `SendMessage → SerializeToUtf8Bytes` | cross_community | 7 |
| `InstallWindowsServiceAsync → Deserialize` | cross_community | 7 |
| `StartDesktopStream → Dispose` | intra_community | 6 |
| `StartDesktopStream → Deserialize` | intra_community | 6 |
| `StartPairingNative → Deserialize` | cross_community | 6 |
| `InitRemex → Dispose` | cross_community | 6 |
| `InitRemex → CommandResponse` | cross_community | 6 |
| `SendWolAsync → SerializeToUtf8Bytes` | cross_community | 6 |

## Connected Areas

| Area | Connections |
|------|-------------|
| Command | 2 calls |
| remex.agent.tests | 1 calls |
| remex.core.tests | 1 calls |
| ViewModels | 1 calls |

## How to Explore

1. `gitnexus_context({name: "PairingClient"})` — see callers and callees
2. `gitnexus_query({query: "native"})` — find related execution flows
3. Read key files listed above for implementation details
