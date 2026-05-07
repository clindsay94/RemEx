---
name: network
description: "Skill for the Network area of RemEx. 39 symbols across 13 files."
---

# Network

39 symbols | 13 files | Cohesion: 95%

## When to Use

- Working with code in `Remex.Core/`
- Understanding how AndroidNativeExports, WakeOnLanService, IpcWakeOnLanService work
- Modifying network-related functionality

## Key Files

| File | Symbols |
|------|---------|
| `Remex.Client/Services/Network/RemoteDesktopService.cs` | ConnectAsync, StartStreamAsync, StopStreamAsync, SendInputAsync, Disconnect (+3) |
| `Remex.Core/Services/Network/RemexNetworkListener.cs` | StartListeningAsync, AcceptClientsAsync, HandleClientSafeAsync, HandleClientAsync, StopListening (+2) |
| `Remex.Client/ViewModels/RemoteDesktopViewModel.cs` | StartStreamAsync, StopStreamAsync, SendInputAsync, OnFrameReceived, OnMetaReceived (+1) |
| `Remex.Host/Services/Network/ExternalNetworkListenerService.cs` | ExecuteAsync, StopAsync, Dispose |
| `Remex.Core/Services/Network/INetworkListener.cs` | StartListeningAsync, StopListening, INetworkListener |
| `Remex.Core/Services/Network/MdnsDiscoveryService.cs` | DiscoverHostsAsync, TryResolveHost, MdnsDiscoveryService |
| `Remex.Core/Services/Network/IMdnsDiscoveryService.cs` | MdnsHostDiscoveredEventArgs, IMdnsDiscoveryService |
| `Remex.Host/Services/Network/MdnsAdvertisingService.cs` | ExecuteAsync, Dispose |
| `Remex.Client/Views/RemoteDesktopView.axaml.cs` | OnCursorPadInput |
| `Remex.Core/Native/AndroidNativeExports.cs` | AndroidNativeExports |

## Entry Points

Start here when exploring this area:

- **`AndroidNativeExports`** (Class) — `Remex.Core/Native/AndroidNativeExports.cs:18`
- **`WakeOnLanService`** (Class) — `Remex.Core/Services/Network/WakeOnLanService.cs:11`
- **`IpcWakeOnLanService`** (Class) — `Remex.Client/Services/Network/IpcWakeOnLanService.cs:13`
- **`MdnsHostDiscoveredEventArgs`** (Class) — `Remex.Core/Services/Network/IMdnsDiscoveryService.cs:14`
- **`RemexNetworkListener`** (Class) — `Remex.Core/Services/Network/RemexNetworkListener.cs:22`

## Key Symbols

| Symbol | Type | File | Line |
|--------|------|------|------|
| `AndroidNativeExports` | Class | `Remex.Core/Native/AndroidNativeExports.cs` | 18 |
| `WakeOnLanService` | Class | `Remex.Core/Services/Network/WakeOnLanService.cs` | 11 |
| `IpcWakeOnLanService` | Class | `Remex.Client/Services/Network/IpcWakeOnLanService.cs` | 13 |
| `MdnsHostDiscoveredEventArgs` | Class | `Remex.Core/Services/Network/IMdnsDiscoveryService.cs` | 14 |
| `RemexNetworkListener` | Class | `Remex.Core/Services/Network/RemexNetworkListener.cs` | 22 |
| `MdnsDiscoveryService` | Class | `Remex.Core/Services/Network/MdnsDiscoveryService.cs` | 10 |
| `IWakeOnLanService` | Interface | `Remex.Core/Services/Network/IWakeOnLanService.cs` | 4 |
| `INetworkListener` | Interface | `Remex.Core/Services/Network/INetworkListener.cs` | 5 |
| `IMdnsDiscoveryService` | Interface | `Remex.Core/Services/Network/IMdnsDiscoveryService.cs` | 7 |
| `SendInputAsync` | Method | `Remex.Client/ViewModels/RemoteDesktopViewModel.cs` | 400 |
| `Dispose` | Method | `Remex.Client/ViewModels/RemoteDesktopViewModel.cs` | 515 |
| `ConnectAsync` | Method | `Remex.Client/Services/Network/RemoteDesktopService.cs` | 34 |
| `StartStreamAsync` | Method | `Remex.Client/Services/Network/RemoteDesktopService.cs` | 55 |
| `StopStreamAsync` | Method | `Remex.Client/Services/Network/RemoteDesktopService.cs` | 65 |
| `SendInputAsync` | Method | `Remex.Client/Services/Network/RemoteDesktopService.cs` | 71 |
| `Disconnect` | Method | `Remex.Client/Services/Network/RemoteDesktopService.cs` | 91 |
| `Dispose` | Method | `Remex.Client/Services/Network/RemoteDesktopService.cs` | 245 |
| `StartListeningAsync` | Method | `Remex.Core/Services/Network/RemexNetworkListener.cs` | 50 |
| `StopAsync` | Method | `Remex.Host/Services/Network/ExternalNetworkListenerService.cs` | 21 |
| `Dispose` | Method | `Remex.Host/Services/Network/ExternalNetworkListenerService.cs` | 27 |

## Execution Flows

| Flow | Type | Steps |
|------|------|-------|
| `ExecuteAsync → ExecuteProcess` | cross_community | 8 |
| `ExecuteAsync → NormalizeDelay` | cross_community | 8 |
| `ExecuteAsync → ExecuteProcess` | cross_community | 8 |
| `ExecuteAsync → BuildShutdownArgs` | cross_community | 8 |
| `ExecuteAsync → SendCommandAsync` | cross_community | 8 |
| `ExecuteAsync → CommandRequest` | cross_community | 8 |
| `ExecuteAsync → CreateDelayParameters` | cross_community | 8 |
| `ExecuteAsync → Shutdown` | cross_community | 7 |
| `ExecuteAsync → Deserialize` | cross_community | 6 |
| `ExecuteAsync → Serialize` | cross_community | 6 |

## Connected Areas

| Area | Connections |
|------|-------------|
| Native | 2 calls |
| Command | 1 calls |

## How to Explore

1. `gitnexus_context({name: "AndroidNativeExports"})` — see callers and callees
2. `gitnexus_query({query: "network"})` — find related execution flows
3. Read key files listed above for implementation details
