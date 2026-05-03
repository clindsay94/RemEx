---
name: remex-client
description: "Skill for the Remex.Client area of RemEx. 18 symbols across 5 files."
---

# Remex.Client

18 symbols | 5 files | Cohesion: 94%

## When to Use

- Working with code in `Remex.Client/`
- Understanding how MainWindow, ShellView, TrayFlyoutWindow work
- Modifying remex.client-related functionality

## Key Files

| File | Symbols |
|------|---------|
| `Remex.Client/App.axaml.cs` | InitializeAppAsync, OnShowMainWindow, TriggerPlatformWidgetUpdate, UpdateTrayTooltip, OnToggleTheme (+7) |
| `Remex.Client/Views/TrayFlyoutWindow.axaml.cs` | TrayFlyoutWindow, ShowAtTray |
| `Remex.Client/Services/CommandModeContext.cs` | ConfigureServices, StartListener |
| `Remex.Client/MainWindow.axaml.cs` | MainWindow |
| `Remex.Client/Views/ShellView.axaml.cs` | ShellView |

## Entry Points

Start here when exploring this area:

- **`MainWindow`** (Class) — `Remex.Client/MainWindow.axaml.cs:10`
- **`ShellView`** (Class) — `Remex.Client/Views/ShellView.axaml.cs:9`
- **`TrayFlyoutWindow`** (Class) — `Remex.Client/Views/TrayFlyoutWindow.axaml.cs:8`
- **`ShowAtTray`** (Method) — `Remex.Client/Views/TrayFlyoutWindow.axaml.cs:30`
- **`OnFrameworkInitializationCompleted`** (Method) — `Remex.Client/App.axaml.cs:35`

## Key Symbols

| Symbol | Type | File | Line |
|--------|------|------|------|
| `MainWindow` | Class | `Remex.Client/MainWindow.axaml.cs` | 10 |
| `ShellView` | Class | `Remex.Client/Views/ShellView.axaml.cs` | 9 |
| `TrayFlyoutWindow` | Class | `Remex.Client/Views/TrayFlyoutWindow.axaml.cs` | 8 |
| `ShowAtTray` | Method | `Remex.Client/Views/TrayFlyoutWindow.axaml.cs` | 30 |
| `OnFrameworkInitializationCompleted` | Method | `Remex.Client/App.axaml.cs` | 35 |
| `ConfigureServices` | Method | `Remex.Client/Services/CommandModeContext.cs` | 20 |
| `StartListener` | Method | `Remex.Client/Services/CommandModeContext.cs` | 89 |
| `InitializeAppAsync` | Method | `Remex.Client/App.axaml.cs` | 101 |
| `OnShowMainWindow` | Method | `Remex.Client/App.axaml.cs` | 215 |
| `TriggerPlatformWidgetUpdate` | Method | `Remex.Client/App.axaml.cs` | 246 |
| `UpdateTrayTooltip` | Method | `Remex.Client/App.axaml.cs` | 250 |
| `OnToggleTheme` | Method | `Remex.Client/App.axaml.cs` | 259 |
| `UpdateThemeToggleLabel` | Method | `Remex.Client/App.axaml.cs` | 282 |
| `FindThemeToggleMenuItem` | Method | `Remex.Client/App.axaml.cs` | 291 |
| `OnTrayIconClicked` | Method | `Remex.Client/App.axaml.cs` | 185 |
| `OnToggleLiveGlance` | Method | `Remex.Client/App.axaml.cs` | 190 |
| `ToggleLiveGlance` | Method | `Remex.Client/App.axaml.cs` | 192 |
| `ApplyThemeBeforeWindowShown` | Method | `Remex.Client/App.axaml.cs` | 74 |

## Execution Flows

| Flow | Type | Steps |
|------|------|-------|
| `OnFrameworkInitializationCompleted → FindThemeToggleMenuItem` | cross_community | 4 |
| `OnFrameworkInitializationCompleted → TriggerPlatformWidgetUpdate` | cross_community | 3 |
| `OnFrameworkInitializationCompleted → UpdateTrayTooltip` | cross_community | 3 |
| `OnFrameworkInitializationCompleted → MainWindow` | cross_community | 3 |

## How to Explore

1. `gitnexus_context({name: "MainWindow"})` — see callers and callees
2. `gitnexus_query({query: "remex.client"})` — find related execution flows
3. Read key files listed above for implementation details
