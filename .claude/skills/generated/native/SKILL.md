---
name: native
description: "Skill for the Native area of RemEx. 82 symbols across 7 files."
---

# Native

82 symbols | 7 files | Cohesion: 75%

## When to Use

- Working with code in `Remex.Core/`
- Understanding how PairingClient, LoadProfileAsync, SerializeToUtf8Bytes work
- Modifying native-related functionality

## Key Files

| File | Symbols |
|------|---------|
| `Remex.Core/Native/AndroidNativeExports.cs` | SendCommand, StartDesktopStream, StopDesktopStream, ProcessOutboundMessagesAsync, HandleDesktopMessage (+36) |
| `Remex.Core/Native/JniHelper.cs` | __android_log_print, AndroidLogE, ReadJString, CreateJString, GetJavaVM (+10) |
| `Remex.Core/Native/RemexDesktopClient.cs` | ConnectAsync, EnsureConnectedAsync, StartStreamAsync, StopStreamAsync, SendInputAsync (+5) |
| `Remex.Core/Native/RemexNativeClient.cs` | SendCommandAsync, SendMessageAsync, ConnectAsync, DisconnectAsync, ReceiveLoopAsync (+2) |
| `Remex.Core/Serialization/RemexJsonSerializerContext.cs` | SerializeToUtf8Bytes, Deserialize, Deserialize, Serialize, SerializeIndented |
| `Remex.Core/Services/DashboardProfileStorageService.cs` | LoadProfileAsync, SaveProfileAsync |
| `Remex.Core/Native/PairingClient.cs` | PairingClient, StartPairingAsync |

## Entry Points

Start here when exploring this area:

- **`PairingClient`** (Class) — `Remex.Core/Native/PairingClient.cs:12`
- **`LoadProfileAsync`** (Method) — `Remex.Core/Services/DashboardProfileStorageService.cs:28`
- **`SerializeToUtf8Bytes`** (Method) — `Remex.Core/Serialization/RemexJsonSerializerContext.cs:80`
- **`Deserialize`** (Method) — `Remex.Core/Serialization/RemexJsonSerializerContext.cs:90`
- **`Deserialize`** (Method) — `Remex.Core/Serialization/RemexJsonSerializerContext.cs:93`

## Key Symbols

| Symbol | Type | File | Line |
|--------|------|------|------|
| `PairingClient` | Class | `Remex.Core/Native/PairingClient.cs` | 12 |
| `LoadProfileAsync` | Method | `Remex.Core/Services/DashboardProfileStorageService.cs` | 28 |
| `SerializeToUtf8Bytes` | Method | `Remex.Core/Serialization/RemexJsonSerializerContext.cs` | 80 |
| `Deserialize` | Method | `Remex.Core/Serialization/RemexJsonSerializerContext.cs` | 90 |
| `Deserialize` | Method | `Remex.Core/Serialization/RemexJsonSerializerContext.cs` | 93 |
| `SendCommandAsync` | Method | `Remex.Core/Native/RemexNativeClient.cs` | 157 |
| `SendMessageAsync` | Method | `Remex.Core/Native/RemexNativeClient.cs` | 195 |
| `ConnectAsync` | Method | `Remex.Core/Native/RemexDesktopClient.cs` | 33 |
| `EnsureConnectedAsync` | Method | `Remex.Core/Native/RemexDesktopClient.cs` | 58 |
| `StartStreamAsync` | Method | `Remex.Core/Native/RemexDesktopClient.cs` | 68 |
| `StopStreamAsync` | Method | `Remex.Core/Native/RemexDesktopClient.cs` | 89 |
| `SendInputAsync` | Method | `Remex.Core/Native/RemexDesktopClient.cs` | 104 |
| `SendConfigAsync` | Method | `Remex.Core/Native/RemexDesktopClient.cs` | 120 |
| `DisconnectAsync` | Method | `Remex.Core/Native/RemexDesktopClient.cs` | 145 |
| `Dispose` | Method | `Remex.Core/Native/RemexDesktopClient.cs` | 219 |
| `AndroidLogE` | Method | `Remex.Core/Native/JniHelper.cs` | 172 |
| `SendCommand` | Method | `Remex.Core/Native/AndroidNativeExports.cs` | 154 |
| `StartDesktopStream` | Method | `Remex.Core/Native/AndroidNativeExports.cs` | 158 |
| `StopDesktopStream` | Method | `Remex.Core/Native/AndroidNativeExports.cs` | 177 |
| `SaveProfileAsync` | Method | `Remex.Core/Services/DashboardProfileStorageService.cs` | 44 |

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
| Remex.Host.Tests | 1 calls |
| Remex.Core.Tests | 1 calls |
| ViewModels | 1 calls |

## How to Explore

1. `gitnexus_context({name: "PairingClient"})` — see callers and callees
2. `gitnexus_query({query: "native"})` — find related execution flows
3. Read key files listed above for implementation details
