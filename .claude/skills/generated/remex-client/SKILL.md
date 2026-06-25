---
name: remex-client
description: "Skill for the remex.desktop area of RemEx. 18 symbols across 5 files."
---

# remex.desktop

18 symbols | 5 files | Cohesion: 94%

## When to Use

- Working with code in `remex.desktop/`
- Understanding how MainWindow, ShellView, TrayFlyoutWindow work
- Modifying remex.client-related functionality

## Key Files

| File | Symbols |
|------|---------|
| `remex.desktop/App.axaml.cs` | InitializeAppAsync, OnShowMainWindow, TriggerPlatformWidgetUpdate, UpdateTrayTooltip, OnToggleTheme (+7) |
| `remex.desktop/Views/TrayFlyoutWindow.axaml.cs` | TrayFlyoutWindow, ShowAtTray |
| `remex.desktop/Services/CommandModeContext.cs` | ConfigureServices, StartListener |
| `remex.desktop/MainWindow.axaml.cs` | MainWindow |
| `remex.desktop/Views/ShellView.axaml.cs` | ShellView |

## Entry Points

Start here when exploring this area:

- **`MainWindow`** (Class) — `remex.desktop/MainWindow.axaml.cs:10`
- **`ShellView`** (Class) — `remex.desktop/Views/ShellView.axaml.cs:9`
- **`TrayFlyoutWindow`** (Class) — `remex.desktop/Views/TrayFlyoutWindow.axaml.cs:8`
- **`ShowAtTray`** (Method) — `remex.desktop/Views/TrayFlyoutWindow.axaml.cs:30`
- **`OnFrameworkInitializationCompleted`** (Method) — `remex.desktop/App.axaml.cs:42`

## Key Symbols

| Symbol | Type | File | Line |
|--------|------|------|------|
| `MainWindow` | Class | `remex.desktop/MainWindow.axaml.cs` | 10 |
| `ShellView` | Class | `remex.desktop/Views/ShellView.axaml.cs` | 9 |
| `TrayFlyoutWindow` | Class | `remex.desktop/Views/TrayFlyoutWindow.axaml.cs` | 8 |
| `ShowAtTray` | Method | `remex.desktop/Views/TrayFlyoutWindow.axaml.cs` | 30 |
| `OnFrameworkInitializationCompleted` | Method | `remex.desktop/App.axaml.cs` | 42 |
| `ConfigureServices` | Method | `remex.desktop/Services/CommandModeContext.cs` | 20 |
| `StartListener` | Method | `remex.desktop/Services/CommandModeContext.cs` | 89 |
| `InitializeAppAsync` | Method | `remex.desktop/App.axaml.cs` | 109 |
| `OnShowMainWindow` | Method | `remex.desktop/App.axaml.cs` | 231 |
| `TriggerPlatformWidgetUpdate` | Method | `remex.desktop/App.axaml.cs` | 262 |
| `UpdateTrayTooltip` | Method | `remex.desktop/App.axaml.cs` | 266 |
| `OnToggleTheme` | Method | `remex.desktop/App.axaml.cs` | 275 |
| `UpdateThemeToggleLabel` | Method | `remex.desktop/App.axaml.cs` | 298 |
| `FindThemeToggleMenuItem` | Method | `remex.desktop/App.axaml.cs` | 307 |
| `OnTrayIconClicked` | Method | `remex.desktop/App.axaml.cs` | 201 |
| `OnToggleLiveGlance` | Method | `remex.desktop/App.axaml.cs` | 206 |
| `ToggleLiveGlance` | Method | `remex.desktop/App.axaml.cs` | 208 |
| `ApplyThemeBeforeWindowShown` | Method | `remex.desktop/App.axaml.cs` | 82 |

## Execution Flows

| Flow | Type | Steps |
|------|------|-------|
| `OnFrameworkInitializationCompleted → FindThemeToggleMenuItem` | cross_community | 4 |

## How to Explore

1. `gitnexus_context({name: "MainWindow"})` — see callers and callees
2. `gitnexus_query({query: "remex.client"})` — find related execution flows
3. Read key files listed above for implementation details
