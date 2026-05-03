---
name: network
description: "Skill for the Network area of RemEx. 33 symbols across 13 files."
---

# Network

33 symbols | 13 files | Cohesion: 91%

## When to Use

- Working with code in `Remex.Core/`
- Understanding how AndroidNativeExports, WakeOnLanService, IpcWakeOnLanService work
- Modifying network-related functionality

## Key Files

| File | Symbols |
|------|---------|
| `Remex.Core/Services/Network/RemexNetworkListener.cs` | StartListeningAsync, AcceptClientsAsync, HandleClientSafeAsync, HandleClientAsync, ValidateAccessKey (+3) |
| `Remex.Client/Services/Network/RemoteDesktopService.cs` | StartStreamAsync, SendInputAsync, SendConfigAsync, SendJsonAsync |
| `Remex.Client/ViewModels/RemoteDesktopViewModel.cs` | ApplySettingsAsync, PersistStreamSettings, SendInputAsync |
| `Remex.Host/Services/Network/ExternalNetworkListenerService.cs` | ExecuteAsync, StopAsync, Dispose |
| `Remex.Core/Services/Network/INetworkListener.cs` | StartListeningAsync, StopListening, INetworkListener |
| `Remex.Core/Services/Network/MdnsDiscoveryService.cs` | DiscoverHostsAsync, TryResolveHost, MdnsDiscoveryService |
| `Remex.Core/Services/Network/IMdnsDiscoveryService.cs` | MdnsHostDiscoveredEventArgs, IMdnsDiscoveryService |
| `Remex.Host/Services/Network/MdnsAdvertisingService.cs` | ExecuteAsync, Dispose |
| `Remex.Client/Views/RemoteDesktopView.axaml.cs` | OnCursorPadInput |
| `Remex.Core/Native/AndroidNativeExports.cs` | AndroidNativeExports |

## Entry Points

Start here when exploring this area:

- **`AndroidNativeExports`** (Class) — `Remex.Core/Native/AndroidNativeExports.cs:15`
- **`WakeOnLanService`** (Class) — `Remex.Core/Services/Network/WakeOnLanService.cs:11`
- **`IpcWakeOnLanService`** (Class) — `Remex.Client/Services/Network/IpcWakeOnLanService.cs:13`
- **`MdnsHostDiscoveredEventArgs`** (Class) — `Remex.Core/Services/Network/IMdnsDiscoveryService.cs:14`
- **`RemexNetworkListener`** (Class) — `Remex.Core/Services/Network/RemexNetworkListener.cs:19`

## Key Symbols

| Symbol | Type | File | Line |
|--------|------|------|------|
| `AndroidNativeExports` | Class | `Remex.Core/Native/AndroidNativeExports.cs` | 15 |
| `WakeOnLanService` | Class | `Remex.Core/Services/Network/WakeOnLanService.cs` | 11 |
| `IpcWakeOnLanService` | Class | `Remex.Client/Services/Network/IpcWakeOnLanService.cs` | 13 |
| `MdnsHostDiscoveredEventArgs` | Class | `Remex.Core/Services/Network/IMdnsDiscoveryService.cs` | 14 |
| `RemexNetworkListener` | Class | `Remex.Core/Services/Network/RemexNetworkListener.cs` | 19 |
| `MdnsDiscoveryService` | Class | `Remex.Core/Services/Network/MdnsDiscoveryService.cs` | 10 |
| `IWakeOnLanService` | Interface | `Remex.Core/Services/Network/IWakeOnLanService.cs` | 4 |
| `INetworkListener` | Interface | `Remex.Core/Services/Network/INetworkListener.cs` | 5 |
| `IMdnsDiscoveryService` | Interface | `Remex.Core/Services/Network/IMdnsDiscoveryService.cs` | 7 |
| `SendInputAsync` | Method | `Remex.Client/ViewModels/RemoteDesktopViewModel.cs` | 400 |
| `StartStreamAsync` | Method | `Remex.Client/Services/Network/RemoteDesktopService.cs` | 50 |
| `SendInputAsync` | Method | `Remex.Client/Services/Network/RemoteDesktopService.cs` | 66 |
| `SendConfigAsync` | Method | `Remex.Client/Services/Network/RemoteDesktopService.cs` | 76 |
| `StartListeningAsync` | Method | `Remex.Core/Services/Network/RemexNetworkListener.cs` | 45 |
| `StopAsync` | Method | `Remex.Host/Services/Network/ExternalNetworkListenerService.cs` | 21 |
| `Dispose` | Method | `Remex.Host/Services/Network/ExternalNetworkListenerService.cs` | 27 |
| `StopListening` | Method | `Remex.Core/Services/Network/RemexNetworkListener.cs` | 110 |
| `Dispose` | Method | `Remex.Core/Services/Network/RemexNetworkListener.cs` | 359 |
| `DiscoverHostsAsync` | Method | `Remex.Core/Services/Network/MdnsDiscoveryService.cs` | 21 |
| `Dispose` | Method | `Remex.Host/Services/Network/MdnsAdvertisingService.cs` | 57 |

## Execution Flows

| Flow | Type | Steps |
|------|------|-------|
| `ExecuteAsync → Deserialize` | cross_community | 6 |
| `ExecuteAsync → Serialize` | cross_community | 6 |
| `ExecuteAsync → CommandResponse` | intra_community | 6 |
| `ExecuteAsync → ValidateAccessKey` | intra_community | 6 |
| `ApplySettingsAsync → SaveInternalAsync` | cross_community | 5 |
| `ApplySettingsAsync → Dispose` | cross_community | 5 |

## Connected Areas

| Area | Connections |
|------|-------------|
| Native | 2 calls |
| ViewModels | 1 calls |
| Command | 1 calls |

## How to Explore

1. `gitnexus_context({name: "AndroidNativeExports"})` — see callers and callees
2. `gitnexus_query({query: "network"})` — find related execution flows
3. Read key files listed above for implementation details
