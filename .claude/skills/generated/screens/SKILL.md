---
name: screens
description: "Skill for the Screens area of RemEx. 171 symbols across 29 files."
---

# Screens

171 symbols | 29 files | Cohesion: 79%

## When to Use

- Working with code in `RemEx.Android/`
- Understanding how RemoteDesktopScreen, ConnectionScreen, AppNavigation work
- Modifying screens-related functionality

## Key Files

| File | Symbols |
|------|---------|
| `RemEx.Android/app/src/main/java/com/clindsay94/remex/ui/screens/RemoteDesktopViewModel.kt` | updateDirectTouch, sendMouseMove, sendMouseScroll, sendMouseClick, sendMouseDown (+23) |
| `RemEx.Android/app/src/main/java/com/clindsay94/remex/ui/screens/DashboardViewModel.kt` | setCardEnabled, moveCard, resizeCard, cycleTelemetryDisplayMode, saveCardLayout (+10) |
| `RemEx.Android/app/src/main/java/com/clindsay94/remex/ui/screens/RemoteDesktopScreen.kt` | RemoteDesktopUiState, RemoteDesktopScreen, TapContext, RemoteDesktopScreenContent, showControlsWithTimer (+6) |
| `RemEx.Android/app/src/main/java/com/clindsay94/remex/data/SettingsManager.kt` | saveRemoteDesktopDirectTouch, saveConnectionSettings, SettingsManager, resetOnboarding, saveRemoteDesktopPointerSpeed (+5) |
| `RemEx.Android/app/src/main/java/com/clindsay94/remex/ui/screens/RemoteControlViewModel.kt` | updateScrollSensitivity, sendMouseMove, sendMouseClick, sendScroll, sendText (+4) |
| `RemEx.Android/app/src/main/java/com/clindsay94/remex/ui/screens/DashboardScreen.kt` | DashboardScreen, AvailableCardItem, CardSizeDp, defaultCardSizeFor, DashboardScreenContent (+4) |
| `RemEx.Android/app/src/main/java/com/clindsay94/remex/ui/screens/TaskManagerViewModel.kt` | updateSearchQuery, updateSortField, clearKillError, refreshProcesses, killProcess (+4) |
| `RemEx.Android/app/src/main/java/com/clindsay94/remex/ui/screens/ConnectionScreen.kt` | ConnectionScreen, ConnectionScreenContent, hasNearbyWifiPermission, hasAllConnectPermissions, doConnect (+2) |
| `RemEx.Android/app/src/main/java/com/clindsay94/remex/ui/screens/SplashScreen.kt` | Particle, FloatingShape, StreamParticle, SplashScreen, skipSplash (+2) |
| `RemEx.Android/app/src/main/java/com/clindsay94/remex/RemexCoreClient.kt` | SendMessage, SendMessageNative, StopDesktopStream, StopDesktopStreamNative, StartDesktopStream (+1) |

## Entry Points

Start here when exploring this area:

- **`RemoteDesktopScreen`** (Function) — `RemEx.Android/app/src/main/java/com/clindsay94/remex/ui/screens/RemoteDesktopScreen.kt:124`
- **`ConnectionScreen`** (Function) — `RemEx.Android/app/src/main/java/com/clindsay94/remex/ui/screens/ConnectionScreen.kt:44`
- **`AppNavigation`** (Function) — `RemEx.Android/app/src/main/java/com/clindsay94/remex/ui/navigation/AppNavigation.kt:112`
- **`SettingsScreen`** (Function) — `RemEx.Android/app/src/main/java/com/clindsay94/remex/ui/screens/SettingsScreen.kt:38`
- **`DashboardScreen`** (Function) — `RemEx.Android/app/src/main/java/com/clindsay94/remex/ui/screens/DashboardScreen.kt:117`

## Key Symbols

| Symbol | Type | File | Line |
|--------|------|------|------|
| `RemoteDesktopUiState` | Class | `RemEx.Android/app/src/main/java/com/clindsay94/remex/ui/screens/RemoteDesktopScreen.kt` | 90 |
| `SettingsManager` | Class | `RemEx.Android/app/src/main/java/com/clindsay94/remex/data/SettingsManager.kt` | 16 |
| `MorphPolygonShape` | Class | `RemEx.Android/app/src/main/java/com/clindsay94/remex/ui/theme/Theme.kt` | 122 |
| `AppLauncherViewModel` | Class | `RemEx.Android/app/src/main/java/com/clindsay94/remex/ui/screens/AppLauncherViewModel.kt` | 30 |
| `AppLauncherUiState` | Class | `RemEx.Android/app/src/main/java/com/clindsay94/remex/ui/screens/AppLauncherScreen.kt` | 50 |
| `DesktopFrame` | Class | `RemEx.Android/app/src/main/java/com/clindsay94/remex/ui/screens/RemoteDesktopViewModel.kt` | 26 |
| `RemoteControlUiState` | Class | `RemEx.Android/app/src/main/java/com/clindsay94/remex/ui/screens/RemoteControlScreen.kt` | 133 |
| `HomeCardState` | Class | `RemEx.Android/app/src/main/java/com/clindsay94/remex/ui/screens/DashboardViewModel.kt` | 38 |
| `TelemetrySensor` | Class | `RemEx.Android/app/src/main/java/com/clindsay94/remex/ui/screens/DashboardViewModel.kt` | 30 |
| `AppEntry` | Class | `RemEx.Android/app/src/main/java/com/clindsay94/remex/ui/screens/AppLauncherViewModel.kt` | 24 |
| `RemoteDesktopScreen` | Function | `RemEx.Android/app/src/main/java/com/clindsay94/remex/ui/screens/RemoteDesktopScreen.kt` | 124 |
| `ConnectionScreen` | Function | `RemEx.Android/app/src/main/java/com/clindsay94/remex/ui/screens/ConnectionScreen.kt` | 44 |
| `AppNavigation` | Function | `RemEx.Android/app/src/main/java/com/clindsay94/remex/ui/navigation/AppNavigation.kt` | 112 |
| `SettingsScreen` | Function | `RemEx.Android/app/src/main/java/com/clindsay94/remex/ui/screens/SettingsScreen.kt` | 38 |
| `DashboardScreen` | Function | `RemEx.Android/app/src/main/java/com/clindsay94/remex/ui/screens/DashboardScreen.kt` | 117 |
| `cardShape` | Function | `RemEx.Android/app/src/main/java/com/clindsay94/remex/ui/theme/Theme.kt` | 171 |
| `DashboardScreenContent` | Function | `RemEx.Android/app/src/main/java/com/clindsay94/remex/ui/screens/DashboardScreen.kt` | 159 |
| `RemoteDesktopScreenContent` | Function | `RemEx.Android/app/src/main/java/com/clindsay94/remex/ui/screens/RemoteDesktopScreen.kt` | 185 |
| `showControlsWithTimer` | Function | `RemEx.Android/app/src/main/java/com/clindsay94/remex/ui/screens/RemoteDesktopScreen.kt` | 236 |
| `mapLocalToHost` | Function | `RemEx.Android/app/src/main/java/com/clindsay94/remex/ui/screens/RemoteDesktopScreen.kt` | 295 |

## Execution Flows

| Flow | Type | Steps |
|------|------|-------|
| `TaskManagerScreen → SendMessageNative` | cross_community | 6 |
| `RemoteMouseScreen → SendMessageNative` | cross_community | 5 |
| `FloatingMouseIsland → SendMessageNative` | cross_community | 5 |
| `OnCreate → PrimaryDestinationsPager` | cross_community | 5 |
| `OnCreate → QrScannerScreen` | cross_community | 5 |
| `OnCreate → NavigateToPrimary` | cross_community | 5 |
| `AppNavigationDisconnectedPreview → Particle` | cross_community | 5 |
| `AppNavigationDisconnectedPreview → FloatingShape` | cross_community | 5 |
| `AppNavigationDisconnectedPreview → StreamParticle` | cross_community | 5 |
| `AppNavigationContent → MarkOnboardingCompleted` | cross_community | 5 |

## Connected Areas

| Area | Connections |
|------|-------------|
| Remex | 7 calls |
| Navigation | 1 calls |

## How to Explore

1. `gitnexus_context({name: "RemoteDesktopScreen"})` — see callers and callees
2. `gitnexus_query({query: "screens"})` — find related execution flows
3. Read key files listed above for implementation details
