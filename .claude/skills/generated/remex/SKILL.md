---
name: remex
description: "Skill for the Remex area of RemEx. 17 symbols across 5 files."
---

# Remex

17 symbols | 5 files | Cohesion: 75%

## When to Use

- Working with code in `RemEx.Android/`
- Understanding how WakePc, SendCommand, onAction work
- Modifying remex-related functionality

## Key Files

| File | Symbols |
|------|---------|
| `RemEx.Android/app/src/main/java/com/clindsay94/remex/RemexCoreClient.kt` | WakePc, WakePcNative, SendCommand, SendCommandNative, InitRemex (+5) |
| `RemEx.Android/app/src/main/java/com/clindsay94/remex/RemexClientManager.kt` | initialize, toggleConnection, connect |
| `RemEx.Android/app/src/main/java/com/clindsay94/remex/ui/screens/DashboardViewModel.kt` | wakePc, toggleConnection |
| `RemEx.Android/app/src/main/java/com/clindsay94/remex/widget/RemoteControlWidget.kt` | onAction |
| `RemEx.Android/app/src/main/java/com/clindsay94/remex/widget/AppLauncherWidget.kt` | onAction |

## Entry Points

Start here when exploring this area:

- **`WakePc`** (Method) — `RemEx.Android/app/src/main/java/com/clindsay94/remex/RemexCoreClient.kt:74`
- **`SendCommand`** (Method) — `RemEx.Android/app/src/main/java/com/clindsay94/remex/RemexCoreClient.kt:128`
- **`onAction`** (Method) — `RemEx.Android/app/src/main/java/com/clindsay94/remex/widget/RemoteControlWidget.kt:180`
- **`onAction`** (Method) — `RemEx.Android/app/src/main/java/com/clindsay94/remex/widget/AppLauncherWidget.kt:211`
- **`wakePc`** (Method) — `RemEx.Android/app/src/main/java/com/clindsay94/remex/ui/screens/DashboardViewModel.kt:127`

## Key Symbols

| Symbol | Type | File | Line |
|--------|------|------|------|
| `WakePc` | Method | `RemEx.Android/app/src/main/java/com/clindsay94/remex/RemexCoreClient.kt` | 74 |
| `SendCommand` | Method | `RemEx.Android/app/src/main/java/com/clindsay94/remex/RemexCoreClient.kt` | 128 |
| `onAction` | Method | `RemEx.Android/app/src/main/java/com/clindsay94/remex/widget/RemoteControlWidget.kt` | 180 |
| `onAction` | Method | `RemEx.Android/app/src/main/java/com/clindsay94/remex/widget/AppLauncherWidget.kt` | 211 |
| `wakePc` | Method | `RemEx.Android/app/src/main/java/com/clindsay94/remex/ui/screens/DashboardViewModel.kt` | 127 |
| `InitRemex` | Method | `RemEx.Android/app/src/main/java/com/clindsay94/remex/RemexCoreClient.kt` | 56 |
| `initialize` | Method | `RemEx.Android/app/src/main/java/com/clindsay94/remex/RemexClientManager.kt` | 58 |
| `toggleConnection` | Method | `RemEx.Android/app/src/main/java/com/clindsay94/remex/RemexClientManager.kt` | 103 |
| `toggleConnection` | Method | `RemEx.Android/app/src/main/java/com/clindsay94/remex/ui/screens/DashboardViewModel.kt` | 148 |
| `setCallback` | Method | `RemEx.Android/app/src/main/java/com/clindsay94/remex/RemexCoreClient.kt` | 41 |
| `GetTelemetry` | Method | `RemEx.Android/app/src/main/java/com/clindsay94/remex/RemexCoreClient.kt` | 92 |
| `WakePcNative` | Method | `RemEx.Android/app/src/main/java/com/clindsay94/remex/RemexCoreClient.kt` | 88 |
| `SendCommandNative` | Method | `RemEx.Android/app/src/main/java/com/clindsay94/remex/RemexCoreClient.kt` | 142 |
| `InitRemexNative` | Method | `RemEx.Android/app/src/main/java/com/clindsay94/remex/RemexCoreClient.kt` | 70 |
| `connect` | Method | `RemEx.Android/app/src/main/java/com/clindsay94/remex/RemexClientManager.kt` | 110 |
| `RegisterCallbackNative` | Method | `RemEx.Android/app/src/main/java/com/clindsay94/remex/RemexCoreClient.kt` | 53 |
| `GetTelemetryNative` | Method | `RemEx.Android/app/src/main/java/com/clindsay94/remex/RemexCoreClient.kt` | 106 |

## Execution Flows

| Flow | Type | Steps |
|------|------|-------|
| `OnCreate → InitRemexNative` | cross_community | 5 |
| `RemoteControlScreen → WakePcNative` | cross_community | 4 |
| `RemoteControlScreen → SendCommandNative` | cross_community | 4 |
| `ConnectionScreen → InitRemexNative` | cross_community | 4 |
| `OnCreate → SettingsManager` | cross_community | 3 |
| `OnAction → WakePcNative` | intra_community | 3 |
| `OnAction → SendCommandNative` | intra_community | 3 |

## Connected Areas

| Area | Connections |
|------|-------------|
| Screens | 3 calls |

## How to Explore

1. `gitnexus_context({name: "WakePc"})` — see callers and callees
2. `gitnexus_query({query: "remex"})` — find related execution flows
3. Read key files listed above for implementation details
