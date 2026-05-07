---
name: widget
description: "Skill for the Widget area of RemEx. 31 symbols across 7 files."
---

# Widget

31 symbols | 7 files | Cohesion: 84%

## When to Use

- Working with code in `RemEx.Android/`
- Understanding how WidgetSensorData, WidgetAppEntry, ConfigAppEntry work
- Modifying widget-related functionality

## Key Files

| File | Symbols |
|------|---------|
| `RemEx.Android/app/src/main/java/com/clindsay94/remex/widget/HardwareInfoWidget.kt` | WidgetSensorData, provideGlance, parseTelemetry, HardwareInfoContent, formatSensorValue (+1) |
| `RemEx.Android/app/src/main/java/com/clindsay94/remex/widget/AppLauncherWidget.kt` | WidgetAppEntry, provideGlance, parseLauncherEntries, AppLauncherContent, AppLauncherWidget |
| `RemEx.Android/app/src/main/java/com/clindsay94/remex/widget/AppLauncherConfigActivity.kt` | ConfigAppEntry, loadAvailableApps, onCreate, saveAndUpdate, AppCheckRow |
| `RemEx.Android/app/src/main/java/com/clindsay94/remex/widget/HardwareInfoConfigActivity.kt` | ConfigSensor, onCreate, loadAvailableSensors, saveAndUpdate, SensorCheckRow |
| `RemEx.Android/app/src/main/java/com/clindsay94/remex/widget/WidgetDataCache.kt` | getTelemetryJson, prefs, getLauncherJson, startCaching |
| `RemEx.Android/app/src/main/java/com/clindsay94/remex/widget/RemoteControlWidget.kt` | RemoteControlWidget, provideGlance, RemoteControlContent |
| `RemEx.Android/app/src/main/java/com/clindsay94/remex/widget/RemoteControlConfigActivity.kt` | onCreate, saveAndUpdate, CommandCheckRow |

## Entry Points

Start here when exploring this area:

- **`WidgetSensorData`** (Class) — `RemEx.Android/app/src/main/java/com/clindsay94/remex/widget/HardwareInfoWidget.kt:37`
- **`WidgetAppEntry`** (Class) — `RemEx.Android/app/src/main/java/com/clindsay94/remex/widget/AppLauncherWidget.kt:51`
- **`ConfigAppEntry`** (Class) — `RemEx.Android/app/src/main/java/com/clindsay94/remex/widget/AppLauncherConfigActivity.kt:51`
- **`HardwareInfoWidget`** (Class) — `RemEx.Android/app/src/main/java/com/clindsay94/remex/widget/HardwareInfoWidget.kt:45`
- **`ConfigSensor`** (Class) — `RemEx.Android/app/src/main/java/com/clindsay94/remex/widget/HardwareInfoConfigActivity.kt:46`

## Key Symbols

| Symbol | Type | File | Line |
|--------|------|------|------|
| `WidgetSensorData` | Class | `RemEx.Android/app/src/main/java/com/clindsay94/remex/widget/HardwareInfoWidget.kt` | 37 |
| `WidgetAppEntry` | Class | `RemEx.Android/app/src/main/java/com/clindsay94/remex/widget/AppLauncherWidget.kt` | 51 |
| `ConfigAppEntry` | Class | `RemEx.Android/app/src/main/java/com/clindsay94/remex/widget/AppLauncherConfigActivity.kt` | 51 |
| `HardwareInfoWidget` | Class | `RemEx.Android/app/src/main/java/com/clindsay94/remex/widget/HardwareInfoWidget.kt` | 45 |
| `ConfigSensor` | Class | `RemEx.Android/app/src/main/java/com/clindsay94/remex/widget/HardwareInfoConfigActivity.kt` | 46 |
| `AppLauncherWidget` | Class | `RemEx.Android/app/src/main/java/com/clindsay94/remex/widget/AppLauncherWidget.kt` | 58 |
| `RemoteControlWidget` | Class | `RemEx.Android/app/src/main/java/com/clindsay94/remex/widget/RemoteControlWidget.kt` | 61 |
| `getTelemetryJson` | Method | `RemEx.Android/app/src/main/java/com/clindsay94/remex/widget/WidgetDataCache.kt` | 56 |
| `provideGlance` | Method | `RemEx.Android/app/src/main/java/com/clindsay94/remex/widget/HardwareInfoWidget.kt` | 49 |
| `getLauncherJson` | Method | `RemEx.Android/app/src/main/java/com/clindsay94/remex/widget/WidgetDataCache.kt` | 59 |
| `provideGlance` | Method | `RemEx.Android/app/src/main/java/com/clindsay94/remex/widget/AppLauncherWidget.kt` | 62 |
| `onCreate` | Method | `RemEx.Android/app/src/main/java/com/clindsay94/remex/widget/HardwareInfoConfigActivity.kt` | 60 |
| `startCaching` | Method | `RemEx.Android/app/src/main/java/com/clindsay94/remex/widget/WidgetDataCache.kt` | 20 |
| `onCreate` | Method | `RemEx.Android/app/src/main/java/com/clindsay94/remex/widget/AppLauncherConfigActivity.kt` | 61 |
| `onCreate` | Method | `RemEx.Android/app/src/main/java/com/clindsay94/remex/widget/RemoteControlConfigActivity.kt` | 47 |
| `provideGlance` | Method | `RemEx.Android/app/src/main/java/com/clindsay94/remex/widget/RemoteControlWidget.kt` | 65 |
| `HardwareInfoContent` | Function | `RemEx.Android/app/src/main/java/com/clindsay94/remex/widget/HardwareInfoWidget.kt` | 82 |
| `formatSensorValue` | Function | `RemEx.Android/app/src/main/java/com/clindsay94/remex/widget/HardwareInfoWidget.kt` | 228 |
| `AppLauncherContent` | Function | `RemEx.Android/app/src/main/java/com/clindsay94/remex/widget/AppLauncherWidget.kt` | 102 |
| `SensorCheckRow` | Function | `RemEx.Android/app/src/main/java/com/clindsay94/remex/widget/HardwareInfoConfigActivity.kt` | 205 |

## Execution Flows

| Flow | Type | Steps |
|------|------|-------|
| `OnCreate → Prefs` | cross_community | 4 |
| `OnCreate → Prefs` | cross_community | 4 |
| `OnCreate → Prefs` | cross_community | 3 |
| `OnCreate → HardwareInfoWidget` | cross_community | 3 |
| `OnCreate → AppLauncherWidget` | cross_community | 3 |
| `OnCreate → ConfigSensor` | intra_community | 3 |
| `OnCreate → HardwareInfoWidget` | intra_community | 3 |
| `OnCreate → ConfigAppEntry` | cross_community | 3 |
| `OnCreate → AppLauncherWidget` | intra_community | 3 |
| `OnCreate → RemoteControlWidget` | intra_community | 3 |

## How to Explore

1. `gitnexus_context({name: "WidgetSensorData"})` — see callers and callees
2. `gitnexus_query({query: "widget"})` — find related execution flows
3. Read key files listed above for implementation details
