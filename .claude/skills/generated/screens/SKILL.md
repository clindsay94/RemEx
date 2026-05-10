---
name: screens
description: "Skill for the Screens area of RemEx. 200 symbols across 33 files."
---

# Screens

200 symbols | 33 files | Cohesion: 76%

## When to Use

- Working with code in `RemEx.Android/`
- Understanding how RemoteDesktopScreen, DashboardScreen, cardShape work
- Modifying screens-related functionality

## Key Files

| File | Symbols |
|------|---------|
| `RemEx.Android/app/src/main/java/com/clindsay94/remex/ui/screens/RemoteDesktopViewModel.kt` | updateDirectTouch, sendMouseMove, sendMouseScroll, sendMouseClick, sendMouseDown (+23) |
| `RemEx.Android/app/src/main/java/com/clindsay94/remex/ui/screens/FileTransferViewModel.kt` | ActiveDownload, loadRemoteRoots, uploadFromUri, downloadToUri, queryMetadata (+16) |
| `RemEx.Android/app/src/main/java/com/clindsay94/remex/ui/screens/DashboardViewModel.kt` | setCardEnabled, moveCard, resizeCard, cycleTelemetryDisplayMode, saveCardLayout (+11) |
| `RemEx.Android/app/src/main/java/com/clindsay94/remex/ui/screens/RemoteDesktopScreen.kt` | RemoteDesktopUiState, RemoteDesktopScreen, TapContext, RemoteDesktopScreenContent, showControlsWithTimer (+5) |
| `RemEx.Android/app/src/main/java/com/clindsay94/remex/data/SettingsManager.kt` | saveRemoteDesktopDirectTouch, saveHomeLayout, saveHomeEnabledCards, saveRemoteDesktopDefaults, saveRemoteDesktopPointerSpeed (+4) |
| `RemEx.Android/app/src/main/java/com/clindsay94/remex/ui/screens/DashboardScreen.kt` | DashboardScreen, AvailableCardItem, CardSizeDp, defaultCardSizeFor, DashboardScreenContent (+4) |
| `RemEx.Android/app/src/main/java/com/clindsay94/remex/ui/screens/RemoteControlViewModel.kt` | sendMouseMove, sendMouseClick, sendScroll, sendText, sendInput (+4) |
| `RemEx.Android/app/src/main/java/com/clindsay94/remex/ui/screens/TaskManagerViewModel.kt` | updateSearchQuery, updateSortField, clearKillError, refreshProcesses, killProcess (+4) |
| `RemEx.Android/app/src/main/java/com/clindsay94/remex/ui/screens/SplashScreen.kt` | Particle, FloatingShape, StreamParticle, SplashScreen, skipSplash (+2) |
| `RemEx.Android/app/src/main/java/com/clindsay94/remex/ui/screens/ConnectionScreen.kt` | ConnectionScreenContent, hasNearbyWifiPermission, hasAllConnectPermissions, doConnect, HelpStep (+2) |

## Entry Points

Start here when exploring this area:

- **`RemoteDesktopScreen`** (Function) — `RemEx.Android/app/src/main/java/com/clindsay94/remex/ui/screens/RemoteDesktopScreen.kt:124`
- **`DashboardScreen`** (Function) — `RemEx.Android/app/src/main/java/com/clindsay94/remex/ui/screens/DashboardScreen.kt:117`
- **`cardShape`** (Function) — `RemEx.Android/app/src/main/java/com/clindsay94/remex/ui/theme/Theme.kt:169`
- **`DashboardScreenContent`** (Function) — `RemEx.Android/app/src/main/java/com/clindsay94/remex/ui/screens/DashboardScreen.kt:159`
- **`AppLauncherScreen`** (Function) — `RemEx.Android/app/src/main/java/com/clindsay94/remex/ui/screens/AppLauncherScreen.kt:58`

## Key Symbols

| Symbol | Type | File | Line |
|--------|------|------|------|
| `RemoteDesktopUiState` | Class | `RemEx.Android/app/src/main/java/com/clindsay94/remex/ui/screens/RemoteDesktopScreen.kt` | 90 |
| `MorphPolygonShape` | Class | `RemEx.Android/app/src/main/java/com/clindsay94/remex/ui/theme/Theme.kt` | 120 |
| `AppLauncherViewModel` | Class | `RemEx.Android/app/src/main/java/com/clindsay94/remex/ui/screens/AppLauncherViewModel.kt` | 30 |
| `AppLauncherUiState` | Class | `RemEx.Android/app/src/main/java/com/clindsay94/remex/ui/screens/AppLauncherScreen.kt` | 50 |
| `DesktopFrame` | Class | `RemEx.Android/app/src/main/java/com/clindsay94/remex/ui/screens/RemoteDesktopViewModel.kt` | 26 |
| `RemoteFileEntry` | Class | `RemEx.Android/app/src/main/java/com/clindsay94/remex/ui/screens/FileTransferViewModel.kt` | 35 |
| `RemoteSharedRoot` | Class | `RemEx.Android/app/src/main/java/com/clindsay94/remex/ui/screens/FileTransferViewModel.kt` | 41 |
| `RemoteControlUiState` | Class | `RemEx.Android/app/src/main/java/com/clindsay94/remex/ui/screens/RemoteControlScreen.kt` | 136 |
| `HomeCardState` | Class | `RemEx.Android/app/src/main/java/com/clindsay94/remex/ui/screens/DashboardViewModel.kt` | 38 |
| `TelemetrySensor` | Class | `RemEx.Android/app/src/main/java/com/clindsay94/remex/ui/screens/DashboardViewModel.kt` | 30 |
| `AppEntry` | Class | `RemEx.Android/app/src/main/java/com/clindsay94/remex/ui/screens/AppLauncherViewModel.kt` | 24 |
| `RemoteDesktopScreen` | Function | `RemEx.Android/app/src/main/java/com/clindsay94/remex/ui/screens/RemoteDesktopScreen.kt` | 124 |
| `DashboardScreen` | Function | `RemEx.Android/app/src/main/java/com/clindsay94/remex/ui/screens/DashboardScreen.kt` | 117 |
| `cardShape` | Function | `RemEx.Android/app/src/main/java/com/clindsay94/remex/ui/theme/Theme.kt` | 169 |
| `DashboardScreenContent` | Function | `RemEx.Android/app/src/main/java/com/clindsay94/remex/ui/screens/DashboardScreen.kt` | 159 |
| `AppLauncherScreen` | Function | `RemEx.Android/app/src/main/java/com/clindsay94/remex/ui/screens/AppLauncherScreen.kt` | 58 |
| `AppLauncherScreenPreview` | Function | `RemEx.Android/app/src/main/java/com/clindsay94/remex/ui/screens/AppLauncherScreen.kt` | 191 |
| `RemoteMouseScreen` | Function | `RemEx.Android/app/src/main/java/com/clindsay94/remex/ui/screens/RemoteMouseScreen.kt` | 62 |
| `RemoteMouseScreenContent` | Function | `RemEx.Android/app/src/main/java/com/clindsay94/remex/ui/screens/RemoteMouseScreen.kt` | 86 |
| `FloatingMouseIsland` | Function | `RemEx.Android/app/src/main/java/com/clindsay94/remex/ui/screens/RemoteMouseScreen.kt` | 269 |

## Execution Flows

| Flow | Type | Steps |
|------|------|-------|
| `HandleFileTransferMessage → SendMessageNative` | cross_community | 7 |
| `TaskManagerScreen → SendMessageNative` | cross_community | 6 |
| `FileTransferScreen → CanPostNotifications` | cross_community | 5 |
| `FileTransferScreen → EnsureChannel` | cross_community | 5 |
| `FileTransferScreen → SendMessageNative` | cross_community | 5 |
| `RemoteMouseScreen → SendMessageNative` | cross_community | 5 |
| `FloatingMouseIsland → SendMessageNative` | cross_community | 5 |
| `HandleTransferEnd → SendMessageNative` | cross_community | 5 |
| `HandleFileTransferMessage → CanPostNotifications` | cross_community | 5 |
| `HandleFileTransferMessage → EnsureChannel` | cross_community | 5 |

## Connected Areas

| Area | Connections |
|------|-------------|
| Remex | 13 calls |
| Service | 6 calls |

## Best Practices

<!-- Evolution: 2026-05-09 | source: ep-2026-05-09-001 | pattern: compose_pager_sync_settled_page -->

### Compose Pager Synchronization
When synchronizing a `HorizontalPager` with external state (like a selected tab index), use `pagerState.settledPage` for the back-sync (Pager -> State). Using `currentPage` causes a feedback loop during animations as `currentPage` changes multiple times before reaching the target.

**Correct Pattern:**
```kotlin
LaunchedEffect(pagerState.settledPage) {
    selectedTabIndex = pagerState.settledPage
}
```

<!-- Evolution: 2026-05-09 | source: ep-2026-05-09-002 | pattern: high_fidelity_preview_derivation -->

### High-Fidelity Previews
UI previews (especially for personalization features) must use the same underlying derivation logic as the runtime components. If the app uses `colorSchemeFromSeed`, the preview component should also derive a full `ColorScheme` from the seed color to ensure accuracy.

## How to Explore

1. `gitnexus_context({name: "RemoteDesktopScreen"})` — see callers and callees
2. `gitnexus_query({query: "screens"})` — find related execution flows
3. Read key files listed above for implementation details
