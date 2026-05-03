---
name: services
description: "Skill for the Services area of RemEx. 68 symbols across 21 files."
---

# Services

68 symbols | 21 files | Cohesion: 85%

## When to Use

- Working with code in `Remex.Client/`
- Understanding how AddProgramWindow, MoveCardOperation, GroupMoveOperation work
- Modifying services-related functionality

## Key Files

| File | Symbols |
|------|---------|
| `Remex.Host/Services/AppLauncherService.cs` | LaunchAppAsync, LaunchStandard, LaunchInInteractiveSession, WTSGetActiveConsoleSessionId, WTSQueryUserToken (+3) |
| `Remex.Client/Services/ICanvasOperation.cs` | ICanvasOperation, MoveCardOperation, GroupMoveOperation, AddCardOperation, Execute (+3) |
| `Remex.Client.Desktop/Services/DesktopIconExtractionService.cs` | ExtractIconAsBase64, ExtractWindowsIcon, ExtractLinuxIcon, ParseDesktopFileForIcon, FindIconNameFromBinary (+2) |
| `Remex.Host/Services/HostCapabilitiesProvider.cs` | GetCurrent, GetPlatform, GetIsInteractiveSession, GetRuntimeMode, SupportsRemoteDesktop (+2) |
| `Remex.Core/Services/LauncherStorageService.cs` | SaveEntriesAsync, SaveEntriesAsync, LoadEntriesAsync, LoadEntriesAsync, ILauncherStorageService (+1) |
| `Remex.Client/Services/ThemeService.cs` | SetBaseTheme, ApplyThemeSync, ApplyCustomization, ApplyBaseThemeInternal, SetResourceOverrideInternal |
| `Remex.Core/Services/IconExtractionService.cs` | ExtractIconAsBase64, ExtractIconAsBase64, IIconExtractionService, IconExtractionService |
| `Remex.Client/ViewModels/AppLauncherViewModel.cs` | SaveLaunchersAsync, LoadLaunchersAsync, NormalizeEntries |
| `Remex.Client/Services/DynamicColorGenerator.cs` | Generate, ToColor |
| `Remex.Host.Tests/IpcHostServerTests.cs` | LaunchAppAsync, FakeAppLauncherService |

## Entry Points

Start here when exploring this area:

- **`AddProgramWindow`** (Class) — `Remex.Client/Views/AddProgramWindow.axaml.cs:8`
- **`MoveCardOperation`** (Class) — `Remex.Client/Services/ICanvasOperation.cs:14`
- **`GroupMoveOperation`** (Class) — `Remex.Client/Services/ICanvasOperation.cs:31`
- **`AddCardOperation`** (Class) — `Remex.Client/Services/ICanvasOperation.cs:52`
- **`HostCapabilitiesProvider`** (Class) — `Remex.Host/Services/HostCapabilitiesProvider.cs:10`

## Key Symbols

| Symbol | Type | File | Line |
|--------|------|------|------|
| `AddProgramWindow` | Class | `Remex.Client/Views/AddProgramWindow.axaml.cs` | 8 |
| `MoveCardOperation` | Class | `Remex.Client/Services/ICanvasOperation.cs` | 14 |
| `GroupMoveOperation` | Class | `Remex.Client/Services/ICanvasOperation.cs` | 31 |
| `AddCardOperation` | Class | `Remex.Client/Services/ICanvasOperation.cs` | 52 |
| `HostCapabilitiesProvider` | Class | `Remex.Host/Services/HostCapabilitiesProvider.cs` | 10 |
| `AppLauncherService` | Class | `Remex.Host/Services/AppLauncherService.cs` | 10 |
| `IconExtractionService` | Class | `Remex.Core/Services/IconExtractionService.cs` | 13 |
| `DesktopIconExtractionService` | Class | `Remex.Client.Desktop/Services/DesktopIconExtractionService.cs` | 10 |
| `LauncherStorageService` | Class | `Remex.Core/Services/LauncherStorageService.cs` | 18 |
| `DashboardLayoutService` | Class | `Remex.Client/Services/DashboardLayoutService.cs` | 15 |
| `DashboardProfileStorageService` | Class | `Remex.Core/Services/DashboardProfileStorageService.cs` | 17 |
| `ICanvasOperation` | Interface | `Remex.Client/Services/ICanvasOperation.cs` | 7 |
| `IHostCapabilitiesProvider` | Interface | `Remex.Host/Services/HostCapabilitiesProvider.cs` | 5 |
| `IAppLauncherService` | Interface | `Remex.Core/Services/IAppLauncherService.cs` | 7 |
| `IIconExtractionService` | Interface | `Remex.Core/Services/IconExtractionService.cs` | 8 |
| `ILauncherStorageService` | Interface | `Remex.Core/Services/LauncherStorageService.cs` | 9 |
| `IDashboardLayoutService` | Interface | `Remex.Core/Services/IDashboardLayoutService.cs` | 8 |
| `IDashboardProfileStorageService` | Interface | `Remex.Core/Services/DashboardProfileStorageService.cs` | 8 |
| `SetBaseTheme` | Method | `Remex.Client/Services/ThemeService.cs` | 34 |
| `ApplyThemeSync` | Method | `Remex.Client/Services/ThemeService.cs` | 44 |

## Execution Flows

| Flow | Type | Steps |
|------|------|-------|
| `ToggleSettingsPanel → M3Palette` | cross_community | 7 |
| `ToggleSettingsPanel → ToColor` | cross_community | 7 |
| `Main → GetPlatform` | cross_community | 6 |
| `Main → GetIsInteractiveSession` | cross_community | 6 |
| `Main → GetRuntimeMode` | cross_community | 6 |
| `Main → SupportsRemoteDesktop` | cross_community | 6 |
| `ToggleSettingsPanel → ApplyBaseThemeInternal` | cross_community | 6 |
| `ToggleSettingsPanel → SetResourceOverrideInternal` | cross_community | 6 |
| `InitializeAsync → M3Palette` | cross_community | 5 |
| `InitializeAsync → ToColor` | cross_community | 5 |

## Connected Areas

| Area | Connections |
|------|-------------|
| ViewModels | 1 calls |
| Native | 1 calls |

## How to Explore

1. `gitnexus_context({name: "AddProgramWindow"})` — see callers and callees
2. `gitnexus_query({query: "services"})` — find related execution flows
3. Read key files listed above for implementation details
