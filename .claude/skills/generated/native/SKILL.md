---
name: native
description: "Skill for the Native area of RemEx. 72 symbols across 6 files."
---

# Native

72 symbols | 6 files | Cohesion: 82%

## When to Use

- Working with code in `Remex.Core/`
- Understanding how SerializeToUtf8Bytes, Deserialize, Deserialize work
- Modifying native-related functionality

## Key Files

| File | Symbols |
|------|---------|
| `Remex.Core/Native/AndroidNativeExports.cs` | InitRemex, WakePc, GetTelemetry, SendMessage, SendCommand (+28) |
| `Remex.Core/Native/JniHelper.cs` | ReadJString, CreateJString, __android_log_print, AndroidLogE, GetJavaVM (+10) |
| `Remex.Core/Native/RemexDesktopClient.cs` | ConnectAsync, EnsureConnectedAsync, StartStreamAsync, StopStreamAsync, SendInputAsync (+6) |
| `Remex.Core/Native/RemexNativeClient.cs` | SendCommandAsync, SendMessageAsync, ConnectAsync, DisconnectAsync, ReceiveLoopAsync (+3) |
| `Remex.Core/Serialization/RemexJsonSerializerContext.cs` | SerializeToUtf8Bytes, Deserialize, Deserialize, Serialize |
| `Remex.Core/Services/DashboardProfileStorageService.cs` | LoadProfileAsync |

## Entry Points

Start here when exploring this area:

- **`SerializeToUtf8Bytes`** (Method) — `Remex.Core/Serialization/RemexJsonSerializerContext.cs:64`
- **`Deserialize`** (Method) — `Remex.Core/Serialization/RemexJsonSerializerContext.cs:74`
- **`Deserialize`** (Method) — `Remex.Core/Serialization/RemexJsonSerializerContext.cs:77`
- **`LoadProfileAsync`** (Method) — `Remex.Core/Services/DashboardProfileStorageService.cs:28`
- **`SendCommandAsync`** (Method) — `Remex.Core/Native/RemexNativeClient.cs:87`

## Key Symbols

| Symbol | Type | File | Line |
|--------|------|------|------|
| `SerializeToUtf8Bytes` | Method | `Remex.Core/Serialization/RemexJsonSerializerContext.cs` | 64 |
| `Deserialize` | Method | `Remex.Core/Serialization/RemexJsonSerializerContext.cs` | 74 |
| `Deserialize` | Method | `Remex.Core/Serialization/RemexJsonSerializerContext.cs` | 77 |
| `LoadProfileAsync` | Method | `Remex.Core/Services/DashboardProfileStorageService.cs` | 28 |
| `SendCommandAsync` | Method | `Remex.Core/Native/RemexNativeClient.cs` | 87 |
| `SendMessageAsync` | Method | `Remex.Core/Native/RemexNativeClient.cs` | 125 |
| `ConnectAsync` | Method | `Remex.Core/Native/RemexDesktopClient.cs` | 32 |
| `EnsureConnectedAsync` | Method | `Remex.Core/Native/RemexDesktopClient.cs` | 44 |
| `StartStreamAsync` | Method | `Remex.Core/Native/RemexDesktopClient.cs` | 54 |
| `StopStreamAsync` | Method | `Remex.Core/Native/RemexDesktopClient.cs` | 75 |
| `SendInputAsync` | Method | `Remex.Core/Native/RemexDesktopClient.cs` | 90 |
| `SendConfigAsync` | Method | `Remex.Core/Native/RemexDesktopClient.cs` | 106 |
| `DisconnectAsync` | Method | `Remex.Core/Native/RemexDesktopClient.cs` | 131 |
| `Dispose` | Method | `Remex.Core/Native/RemexDesktopClient.cs` | 205 |
| `ReadJString` | Method | `Remex.Core/Native/JniHelper.cs` | 22 |
| `CreateJString` | Method | `Remex.Core/Native/JniHelper.cs` | 45 |
| `AndroidLogE` | Method | `Remex.Core/Native/JniHelper.cs` | 172 |
| `InitRemex` | Method | `Remex.Core/Native/AndroidNativeExports.cs` | 97 |
| `WakePc` | Method | `Remex.Core/Native/AndroidNativeExports.cs` | 101 |
| `GetTelemetry` | Method | `Remex.Core/Native/AndroidNativeExports.cs` | 105 |

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
| `StartDesktopStream → Dispose` | intra_community | 6 |
| `StartDesktopStream → Deserialize` | intra_community | 6 |

## Connected Areas

| Area | Connections |
|------|-------------|
| Command | 2 calls |
| Remex.Core.Tests | 1 calls |

## How to Explore

1. `gitnexus_context({name: "SerializeToUtf8Bytes"})` — see callers and callees
2. `gitnexus_query({query: "native"})` — find related execution flows
3. Read key files listed above for implementation details
