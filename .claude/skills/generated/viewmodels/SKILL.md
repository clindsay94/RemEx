---
name: viewmodels
description: "Skill for the ViewModels area of RemEx. 239 symbols across 22 files."
---

# ViewModels

239 symbols | 22 files | Cohesion: 87%

## When to Use

- Working with code in `Remex.Client/`
- Understanding how TaskManagerViewModel, RemoteViewModel, RemoteDesktopViewModel work
- Modifying viewmodels-related functionality

## Key Files

| File | Symbols |
|------|---------|
| `Remex.Client/ViewModels/SettingsViewModel.cs` | RefreshServiceAsync, BrowseHostPathAsync, InstallServiceAsync, InstallWindowsServiceAsync, InstallLinuxServiceAsync (+34) |
| `Remex.Client/ViewModels/CanvasDashboardViewModel.cs` | OnConnectionPropertyChanged, OnLayoutProfileReceived, OnLayoutProfileReceivedAsync, InitializeAsync, EnsureDefaultCards (+25) |
| `Remex.Client/ViewModels/ConnectionViewModel.cs` | LockAsync, SleepAsync, HibernateAsync, SignOutAsync, ShutdownAsync (+25) |
| `Remex.Client/ViewModels/ShellViewModel.cs` | NotifyIfDisconnected, SetTransitionAndNavigate, NavigateToCanvas, NavigateToRemote, NavigateToAppLauncher (+21) |
| `Remex.Client/ViewModels/RemoteViewModel.cs` | RemoteViewModel, LoadWolConfigAsync, ConfirmAsync, LockPcAsync, ShutdownPcAsync (+13) |
| `Remex.Client/ViewModels/CustomizationViewModel.cs` | FindSnap, VibrateCardCornerRadius, VibrateRemoteCornerRadius, OnSelectedThemeChanged, OnCornerRadiusChanged (+13) |
| `Remex.Client/ViewModels/TaskManagerViewModel.cs` | TaskManagerViewModel, RefreshProcessesAsync, KillProcessAsync, StartPolling, PollAsync (+6) |
| `Remex.Client/ViewModels/AppLauncherViewModel.cs` | LaunchAppAsync, NavigateBack, NormalizeEntry, NormalizeString, RemoveAppAsync (+5) |
| `Remex.Client.Tests/ViewModels/ConnectionViewModelTests.cs` | RequestProcessListAsync_WhenNotConnected_ShouldNotThrow, Dispose, Dispose, Constructor_WithAllNullDependencies_ShouldNotThrow, Dispose_ShouldNotThrowOnDoubleDispose (+5) |
| `Remex.Client/ViewModels/HomeViewModel.cs` | NavigateToCanvas, NavigateToRemote, NavigateToAppLauncher, NavigateToRemoteDesktop, NavigateToTaskManager (+4) |

## Entry Points

Start here when exploring this area:

- **`TaskManagerViewModel`** (Class) — `Remex.Client/ViewModels/TaskManagerViewModel.cs:14`
- **`RemoteViewModel`** (Class) — `Remex.Client/ViewModels/RemoteViewModel.cs:14`
- **`RemoteDesktopViewModel`** (Class) — `Remex.Client/ViewModels/RemoteDesktopViewModel.cs:20`
- **`AboutViewModel`** (Class) — `Remex.Client/ViewModels/AboutViewModel.cs:13`
- **`SensorViewModel`** (Class) — `Remex.Client/ViewModels/SensorViewModel.cs:11`

## Key Symbols

| Symbol | Type | File | Line |
|--------|------|------|------|
| `TaskManagerViewModel` | Class | `Remex.Client/ViewModels/TaskManagerViewModel.cs` | 14 |
| `RemoteViewModel` | Class | `Remex.Client/ViewModels/RemoteViewModel.cs` | 14 |
| `RemoteDesktopViewModel` | Class | `Remex.Client/ViewModels/RemoteDesktopViewModel.cs` | 20 |
| `AboutViewModel` | Class | `Remex.Client/ViewModels/AboutViewModel.cs` | 13 |
| `SensorViewModel` | Class | `Remex.Client/ViewModels/SensorViewModel.cs` | 11 |
| `CanvasCardViewModel` | Class | `Remex.Client/ViewModels/CanvasCardViewModel.cs` | 13 |
| `RemoveCardOperation` | Class | `Remex.Client/Services/ICanvasOperation.cs` | 68 |
| `ShellViewModel` | Class | `Remex.Client/ViewModels/ShellViewModel.cs` | 22 |
| `SettingsViewModel` | Class | `Remex.Client/ViewModels/SettingsViewModel.cs` | 24 |
| `CustomizationViewModel` | Class | `Remex.Client/ViewModels/CustomizationViewModel.cs` | 17 |
| `SensorActivationItem` | Class | `Remex.Client/ViewModels/CanvasDashboardViewModel.cs` | 1049 |
| `ConnectionViewModel` | Class | `Remex.Client/ViewModels/ConnectionViewModel.cs` | 29 |
| `SensorPinItem` | Class | `Remex.Client/ViewModels/SettingsViewModel.cs` | 1197 |
| `CommandPaletteViewModel` | Class | `Remex.Client/ViewModels/CommandPaletteViewModel.cs` | 12 |
| `NavigateToCanvas` | Method | `Remex.Client/ViewModels/ShellViewModel.cs` | 457 |
| `NavigateToRemote` | Method | `Remex.Client/ViewModels/ShellViewModel.cs` | 464 |
| `NavigateToAppLauncher` | Method | `Remex.Client/ViewModels/ShellViewModel.cs` | 475 |
| `NavigateToTaskManager` | Method | `Remex.Client/ViewModels/ShellViewModel.cs` | 486 |
| `NavigateToRemoteDesktop` | Method | `Remex.Client/ViewModels/ShellViewModel.cs` | 494 |
| `NavigateToAbout` | Method | `Remex.Client/ViewModels/ShellViewModel.cs` | 501 |

## Execution Flows

| Flow | Type | Steps |
|------|------|-------|
| `InstallServiceAsync → Deserialize` | cross_community | 8 |
| `OnPointerReleased → SaveInternalAsync` | cross_community | 7 |
| `ToggleSettingsPanel → M3Palette` | cross_community | 7 |
| `ToggleSettingsPanel → ToColor` | cross_community | 7 |
| `InstallServiceAsync → Dispose` | cross_community | 7 |
| `ShutdownPcAsync → SerializeToUtf8Bytes` | cross_community | 7 |
| `ForceShutdownPcAsync → SerializeToUtf8Bytes` | cross_community | 7 |
| `RestartPcAsync → SerializeToUtf8Bytes` | cross_community | 7 |
| `ForceRestartAsync → SerializeToUtf8Bytes` | cross_community | 7 |
| `RestartToUefiAsync → SerializeToUtf8Bytes` | cross_community | 7 |

## Connected Areas

| Area | Connections |
|------|-------------|
| Services | 6 calls |
| Remex.Host.Tests | 4 calls |
| Native | 2 calls |
| Network | 2 calls |
| Handlers | 1 calls |

## How to Explore

1. `gitnexus_context({name: "TaskManagerViewModel"})` — see callers and callees
2. `gitnexus_query({query: "viewmodels"})` — find related execution flows
3. Read key files listed above for implementation details
