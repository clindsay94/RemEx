---
name: widget
description: "Skill for the Widget area of RemEx. 32 symbols across 8 files."
---

# Widget

32 symbols | 8 files | Cohesion: 80%

## When to Use

- Working with code in `RemEx.Android/`
- Understanding how WidgetSensorData, HardwareInfoWidget, ConfigSensor work
- Modifying widget-related functionality

## Key Files

| File | Symbols |
|------|---------|
| `RemEx.Android/app/src/main/java/com/clindsay94/remex/widget/HardwareInfoWidget.kt` | WidgetSensorData, provideGlance, parseTelemetry, HardwareInfoContent, formatSensorValue (+1) |
| `RemEx.Android/app/src/main/java/com/clindsay94/remex/widget/HardwareInfoConfigActivity.kt` | ConfigSensor, onCreate, loadAvailableSensors, saveAndUpdate, SensorCheckRow |
| `RemEx.Android/app/src/main/java/com/clindsay94/remex/widget/AppLauncherConfigActivity.kt` | ConfigAppEntry, onCreate, loadAvailableApps, AppCheckRow, saveAndUpdate |
| `RemEx.Android/app/src/main/java/com/clindsay94/remex/widget/AppLauncherWidget.kt` | AppLauncherWidget, WidgetAppEntry, provideGlance, parseLauncherEntries, AppLauncherContent |
| `RemEx.Android/app/src/main/java/com/clindsay94/remex/widget/WidgetDataCache.kt` | getTelemetryJson, prefs, getLauncherJson, startCaching |
| `RemEx.Android/app/src/main/java/com/clindsay94/remex/widget/RemoteControlWidget.kt` | RemoteControlWidget, provideGlance, RemoteControlContent |
| `RemEx.Android/app/src/main/java/com/clindsay94/remex/widget/RemoteControlConfigActivity.kt` | onCreate, saveAndUpdate, CommandCheckRow |
| `RemEx.Android/app/src/main/java/com/clindsay94/remex/MainActivity.kt` | onCreate |

## Entry Points

Start here when exploring this area:

- **`WidgetSensorData`** (Class) — `RemEx.Android/app/src/main/java/com/clindsay94/remex/widget/HardwareInfoWidget.kt:37`
- **`HardwareInfoWidget`** (Class) — `RemEx.Android/app/src/main/java/com/clindsay94/remex/widget/HardwareInfoWidget.kt:45`
- **`ConfigSensor`** (Class) — `RemEx.Android/app/src/main/java/com/clindsay94/remex/widget/HardwareInfoConfigActivity.kt:46`
- **`ConfigAppEntry`** (Class) — `RemEx.Android/app/src/main/java/com/clindsay94/remex/widget/AppLauncherConfigActivity.kt:51`
- **`AppLauncherWidget`** (Class) — `RemEx.Android/app/src/main/java/com/clindsay94/remex/widget/AppLauncherWidget.kt:58`

## Key Symbols

| Symbol | Type | File | Line |
|--------|------|------|------|
| `WidgetSensorData` | Class | `RemEx.Android/app/src/main/java/com/clindsay94/remex/widget/HardwareInfoWidget.kt` | 37 |
| `HardwareInfoWidget` | Class | `RemEx.Android/app/src/main/java/com/clindsay94/remex/widget/HardwareInfoWidget.kt` | 45 |
| `ConfigSensor` | Class | `RemEx.Android/app/src/main/java/com/clindsay94/remex/widget/HardwareInfoConfigActivity.kt` | 46 |
| `ConfigAppEntry` | Class | `RemEx.Android/app/src/main/java/com/clindsay94/remex/widget/AppLauncherConfigActivity.kt` | 51 |
| `AppLauncherWidget` | Class | `RemEx.Android/app/src/main/java/com/clindsay94/remex/widget/AppLauncherWidget.kt` | 58 |
| `RemoteControlWidget` | Class | `RemEx.Android/app/src/main/java/com/clindsay94/remex/widget/RemoteControlWidget.kt` | 61 |
| `WidgetAppEntry` | Class | `RemEx.Android/app/src/main/java/com/clindsay94/remex/widget/AppLauncherWidget.kt` | 51 |
| `getTelemetryJson` | Method | `RemEx.Android/app/src/main/java/com/clindsay94/remex/widget/WidgetDataCache.kt` | 56 |
| `provideGlance` | Method | `RemEx.Android/app/src/main/java/com/clindsay94/remex/widget/HardwareInfoWidget.kt` | 49 |
| `onCreate` | Method | `RemEx.Android/app/src/main/java/com/clindsay94/remex/widget/HardwareInfoConfigActivity.kt` | 60 |
| `getLauncherJson` | Method | `RemEx.Android/app/src/main/java/com/clindsay94/remex/widget/WidgetDataCache.kt` | 59 |
| `onCreate` | Method | `RemEx.Android/app/src/main/java/com/clindsay94/remex/widget/AppLauncherConfigActivity.kt` | 61 |
| `onCreate` | Method | `RemEx.Android/app/src/main/java/com/clindsay94/remex/MainActivity.kt` | 18 |
| `startCaching` | Method | `RemEx.Android/app/src/main/java/com/clindsay94/remex/widget/WidgetDataCache.kt` | 20 |
| `onCreate` | Method | `RemEx.Android/app/src/main/java/com/clindsay94/remex/widget/RemoteControlConfigActivity.kt` | 47 |
| `provideGlance` | Method | `RemEx.Android/app/src/main/java/com/clindsay94/remex/widget/AppLauncherWidget.kt` | 62 |
| `provideGlance` | Method | `RemEx.Android/app/src/main/java/com/clindsay94/remex/widget/RemoteControlWidget.kt` | 65 |
| `HardwareInfoContent` | Function | `RemEx.Android/app/src/main/java/com/clindsay94/remex/widget/HardwareInfoWidget.kt` | 82 |
| `formatSensorValue` | Function | `RemEx.Android/app/src/main/java/com/clindsay94/remex/widget/HardwareInfoWidget.kt` | 228 |
| `SensorCheckRow` | Function | `RemEx.Android/app/src/main/java/com/clindsay94/remex/widget/HardwareInfoConfigActivity.kt` | 205 |

## Execution Flows

| Flow | Type | Steps |
|------|------|-------|
| `OnCreate → InitRemexNative` | cross_community | 5 |
| `OnCreate → PrimaryDestinationsPager` | cross_community | 5 |
| `OnCreate → QrScannerScreen` | cross_community | 5 |
| `OnCreate → NavigateToPrimary` | cross_community | 5 |
| `OnCreate → NavigateToConnection` | cross_community | 4 |
| `OnCreate → ConnectionStatusChip` | cross_community | 4 |
| `OnCreate → ClearError` | cross_community | 4 |
| `OnCreate → Prefs` | cross_community | 4 |
| `OnCreate → Prefs` | cross_community | 4 |
| `OnCreate → SettingsManager` | cross_community | 3 |

## Connected Areas

| Area | Connections |
|------|-------------|
| Remex | 1 calls |
| Screens | 1 calls |
| Theme | 1 calls |

## How to Explore

1. `gitnexus_context({name: "WidgetSensorData"})` — see callers and callees
2. `gitnexus_query({query: "widget"})` — find related execution flows
3. Read key files listed above for implementation details
