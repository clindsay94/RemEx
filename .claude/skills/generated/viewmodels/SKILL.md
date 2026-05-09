---
name: viewmodels
description: "Skill for the ViewModels area of RemEx. 280 symbols across 26 files."
---

# ViewModels

280 symbols | 26 files | Cohesion: 86%

## When to Use

- Working with code in `Remex.Client/`
- Understanding how TaskManagerViewModel, RemoteViewModel, RemoteDesktopViewModel work
- Modifying viewmodels-related functionality

## Key Files

| File | Symbols |
|------|---------|
| `Remex.Client/ViewModels/SettingsViewModel.cs` | RefreshServiceAsync, BrowseHostPathAsync, InstallServiceAsync, InstallWindowsServiceAsync, InstallLinuxServiceAsync (+47) |
| `Remex.Client/ViewModels/ConnectionViewModel.cs` | SendAsync, LockAsync, SleepAsync, HibernateAsync, SignOutAsync (+29) |
| `Remex.Client/ViewModels/CanvasDashboardViewModel.cs` | OnConnectionPropertyChanged, OnLayoutProfileReceived, OnLayoutProfileReceivedAsync, InitializeAsync, EnsureDefaultCards (+27) |
| `Remex.Client/ViewModels/ShellViewModel.cs` | NotifyIfDisconnected, SetTransitionAndNavigate, NavigateToCanvas, NavigateToRemote, NavigateToAppLauncher (+22) |
| `Remex.Client/ViewModels/RemoteViewModel.cs` | RemoteViewModel, LoadWolConfigAsync, ConfirmAsync, LockPcAsync, ShutdownPcAsync (+13) |
| `Remex.Client/ViewModels/CustomizationViewModel.cs` | CustomizationViewModel, FindSnap, VibrateCardCornerRadius, VibrateRemoteCornerRadius, OnSelectedThemeChanged (+13) |
| `Remex.Client/ViewModels/FileTransferViewModel.cs` | FileTransferViewModel, OnSelectedRemoteRootChanged, InitializeAsync, LoadRemoteRootsAsync, BrowseRemoteAsync (+10) |
| `Remex.Client.Tests/ViewModels/ConnectionViewModelTests.cs` | Dispose, Constructor_WithAllNullDependencies_ShouldNotThrow, Dispose_ShouldNotThrowOnDoubleDispose, DiscoverHostsCommand_WhenNoDiscoveryService_ShouldNotCrash, RequestProcessListAsync_WhenNotConnected_ShouldNotThrow (+10) |
| `Remex.Client/ViewModels/TaskManagerViewModel.cs` | TaskManagerViewModel, RefreshProcessesAsync, KillProcessAsync, StartPolling, PollAsync (+6) |
| `Remex.Client/ViewModels/AppLauncherViewModel.cs` | LaunchAppAsync, NavigateBack, NormalizeEntry, NormalizeString, RemoveAppAsync (+5) |

## Entry Points

Start here when exploring this area:

- **`TaskManagerViewModel`** (Class) — `Remex.Client/ViewModels/TaskManagerViewModel.cs:14`
- **`RemoteViewModel`** (Class) — `Remex.Client/ViewModels/RemoteViewModel.cs:14`
- **`RemoteDesktopViewModel`** (Class) — `Remex.Client/ViewModels/RemoteDesktopViewModel.cs:20`
- **`FileTransferViewModel`** (Class) — `Remex.Client/ViewModels/FileTransferViewModel.cs:17`
- **`AboutViewModel`** (Class) — `Remex.Client/ViewModels/AboutViewModel.cs:13`

## Key Symbols

| Symbol | Type | File | Line |
|--------|------|------|------|
| `TaskManagerViewModel` | Class | `Remex.Client/ViewModels/TaskManagerViewModel.cs` | 14 |
| `RemoteViewModel` | Class | `Remex.Client/ViewModels/RemoteViewModel.cs` | 14 |
| `RemoteDesktopViewModel` | Class | `Remex.Client/ViewModels/RemoteDesktopViewModel.cs` | 20 |
| `FileTransferViewModel` | Class | `Remex.Client/ViewModels/FileTransferViewModel.cs` | 17 |
| `AboutViewModel` | Class | `Remex.Client/ViewModels/AboutViewModel.cs` | 13 |
| `SensorViewModel` | Class | `Remex.Client/ViewModels/SensorViewModel.cs` | 11 |
| `CanvasCardViewModel` | Class | `Remex.Client/ViewModels/CanvasCardViewModel.cs` | 13 |
| `ShellViewModel` | Class | `Remex.Client/ViewModels/ShellViewModel.cs` | 23 |
| `SettingsViewModel` | Class | `Remex.Client/ViewModels/SettingsViewModel.cs` | 25 |
| `SensorPinItem` | Class | `Remex.Client/ViewModels/SettingsViewModel.cs` | 1388 |
| `CustomizationViewModel` | Class | `Remex.Client/ViewModels/CustomizationViewModel.cs` | 17 |
| `FileTransferRootSettingsService` | Class | `Remex.Client/Services/FileTransfer/FileTransferRootSettingsService.cs` | 9 |
| `FileTransferSharedRootItem` | Class | `Remex.Client/ViewModels/SettingsViewModel.cs` | 1410 |
| `RemoveCardOperation` | Class | `Remex.Client/Services/ICanvasOperation.cs` | 68 |
| `ConnectionViewModel` | Class | `Remex.Client/ViewModels/ConnectionViewModel.cs` | 33 |
| `SensorActivationItem` | Class | `Remex.Client/ViewModels/CanvasDashboardViewModel.cs` | 1064 |
| `CommandPaletteViewModel` | Class | `Remex.Client/ViewModels/CommandPaletteViewModel.cs` | 12 |
| `NavigateToCanvas` | Method | `Remex.Client/ViewModels/ShellViewModel.cs` | 461 |
| `NavigateToRemote` | Method | `Remex.Client/ViewModels/ShellViewModel.cs` | 468 |
| `NavigateToAppLauncher` | Method | `Remex.Client/ViewModels/ShellViewModel.cs` | 479 |

## Execution Flows

| Flow | Type | Steps |
|------|------|-------|
| `InstallWindowsServiceAsync → DisposeWebSocket` | cross_community | 8 |
| `InstallWindowsServiceAsync → CancelAndDispose` | cross_community | 8 |
| `UploadAsync → SerializeToUtf8Bytes` | cross_community | 8 |
| `InstallWindowsServiceAsync → Deserialize` | cross_community | 7 |
| `OnPointerReleased → SaveInternalAsync` | cross_community | 7 |
| `InitializeAsync → SaveInternalAsync` | cross_community | 6 |
| `StartPairingNative → Deserialize` | cross_community | 6 |
| `InitializeAsync → GetDisplayName` | cross_community | 6 |
| `SendWolAsync → SerializeToUtf8Bytes` | cross_community | 6 |
| `DownloadAsync → SerializeToUtf8Bytes` | cross_community | 6 |

## Connected Areas

| Area | Connections |
|------|-------------|
| Remex.Host.Tests | 6 calls |
| Services | 6 calls |
| Native | 3 calls |
| FileTransfer | 3 calls |
| Handlers | 2 calls |
| Network | 1 calls |

## Best Practices

<!-- Evolution: 2026-05-08 | source: ep-2026-05-08-001 | pattern: dual_di_container_resolution -->

### Dual-DI Container Resolution
In dual-mode applications where the client hosts an embedded server, services may be registered in separate DI containers (the Client container and the Host container). When a ViewModel needs a service that might be owned by the host (e.g., `ICertificateService`), it should check both containers.

**Pattern:**
```csharp
var service = App.Services.GetService<IMyService>() 
           ?? App.EmbeddedHostServices?.GetService<IMyService>();
```

- Always check the client container first (`App.Services`).
- Use the null-coalescing operator to fallback to `App.EmbeddedHostServices`.
- Ensure proper null handling for `App.EmbeddedHostServices` as it will be null in standalone client mode.

## How to Explore

1. `gitnexus_context({name: "TaskManagerViewModel"})` — see callers and callees
2. `gitnexus_query({query: "viewmodels"})` — find related execution flows
3. Read key files listed above for implementation details
