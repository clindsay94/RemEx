---
name: remex
description: "Skill for the Remex area of RemEx. 41 symbols across 15 files."
---

# Remex

41 symbols | 15 files | Cohesion: 73%

## When to Use

- Working with code in `RemEx.Android/`
- Understanding how QrScannerScreen, PairingScreen, AppNavigation work
- Modifying remex-related functionality

## Key Files

| File | Symbols |
|------|---------|
| `RemEx.Android/app/src/main/java/com/clindsay94/remex/RemexCoreClient.kt` | InitRemex, InitRemexNative, GetPinnedHostHashNative, GetPinnedHostHash, SetPinnedHostHashNative (+13) |
| `RemEx.Android/app/src/main/java/com/clindsay94/remex/security/PinnedHostStore.kt` | aead, prefKey, setPin, getPin, removePin (+1) |
| `RemEx.Android/app/src/main/java/com/clindsay94/remex/ui/screens/PairingScreen.kt` | PairingUiState, submitPin, startPairing, PairingScreen |
| `RemEx.Android/app/src/main/java/com/clindsay94/remex/RemexClientManager.kt` | connect, initialize |
| `RemEx.Android/app/src/main/java/com/clindsay94/remex/ui/screens/QrScannerScreen.kt` | QrScannerScreen |
| `RemEx.Android/app/src/main/java/com/clindsay94/remex/MainActivity.kt` | onCreate |
| `RemEx.Android/app/src/main/java/com/clindsay94/remex/widget/AppLauncherWidget.kt` | onAction |
| `RemEx.Android/app/src/main/java/com/clindsay94/remex/data/SettingsManager.kt` | SettingsManager |
| `RemEx.Android/app/src/main/java/com/clindsay94/remex/ui/screens/ConnectionViewModel.kt` | applyQrResultAndConnect |
| `RemEx.Android/app/src/main/java/com/clindsay94/remex/ui/navigation/AppNavigation.kt` | AppNavigation |

## Entry Points

Start here when exploring this area:

- **`QrScannerScreen`** (Function) — `RemEx.Android/app/src/main/java/com/clindsay94/remex/ui/screens/QrScannerScreen.kt:39`
- **`PairingScreen`** (Function) — `RemEx.Android/app/src/main/java/com/clindsay94/remex/ui/screens/PairingScreen.kt:122`
- **`AppNavigation`** (Function) — `RemEx.Android/app/src/main/java/com/clindsay94/remex/ui/navigation/AppNavigation.kt:113`
- **`hapticClickable`** (Function) — `RemEx.Android/app/src/main/java/com/clindsay94/remex/ui/components/HapticModifier.kt:14`
- **`PairingUiState`** (Class) — `RemEx.Android/app/src/main/java/com/clindsay94/remex/ui/screens/PairingScreen.kt:27`

## Key Symbols

| Symbol | Type | File | Line |
|--------|------|------|------|
| `PairingUiState` | Class | `RemEx.Android/app/src/main/java/com/clindsay94/remex/ui/screens/PairingScreen.kt` | 27 |
| `SettingsManager` | Class | `RemEx.Android/app/src/main/java/com/clindsay94/remex/data/SettingsManager.kt` | 16 |
| `QrScannerScreen` | Function | `RemEx.Android/app/src/main/java/com/clindsay94/remex/ui/screens/QrScannerScreen.kt` | 39 |
| `PairingScreen` | Function | `RemEx.Android/app/src/main/java/com/clindsay94/remex/ui/screens/PairingScreen.kt` | 122 |
| `AppNavigation` | Function | `RemEx.Android/app/src/main/java/com/clindsay94/remex/ui/navigation/AppNavigation.kt` | 113 |
| `hapticClickable` | Function | `RemEx.Android/app/src/main/java/com/clindsay94/remex/ui/components/HapticModifier.kt` | 14 |
| `InitRemex` | Method | `RemEx.Android/app/src/main/java/com/clindsay94/remex/RemexCoreClient.kt` | 58 |
| `GetPinnedHostHash` | Method | `RemEx.Android/app/src/main/java/com/clindsay94/remex/RemexCoreClient.kt` | 251 |
| `SetPinnedHostHash` | Method | `RemEx.Android/app/src/main/java/com/clindsay94/remex/RemexCoreClient.kt` | 272 |
| `setPin` | Method | `RemEx.Android/app/src/main/java/com/clindsay94/remex/security/PinnedHostStore.kt` | 58 |
| `getPin` | Method | `RemEx.Android/app/src/main/java/com/clindsay94/remex/security/PinnedHostStore.kt` | 71 |
| `removePin` | Method | `RemEx.Android/app/src/main/java/com/clindsay94/remex/security/PinnedHostStore.kt` | 84 |
| `listPaired` | Method | `RemEx.Android/app/src/main/java/com/clindsay94/remex/security/PinnedHostStore.kt` | 90 |
| `StartPairing` | Method | `RemEx.Android/app/src/main/java/com/clindsay94/remex/RemexCoreClient.kt` | 201 |
| `SubmitPairingPin` | Method | `RemEx.Android/app/src/main/java/com/clindsay94/remex/RemexCoreClient.kt` | 225 |
| `submitPin` | Method | `RemEx.Android/app/src/main/java/com/clindsay94/remex/ui/screens/PairingScreen.kt` | 39 |
| `startPairing` | Method | `RemEx.Android/app/src/main/java/com/clindsay94/remex/ui/screens/PairingScreen.kt` | 83 |
| `initialize` | Method | `RemEx.Android/app/src/main/java/com/clindsay94/remex/RemexClientManager.kt` | 59 |
| `onCreate` | Method | `RemEx.Android/app/src/main/java/com/clindsay94/remex/MainActivity.kt` | 18 |
| `onAction` | Method | `RemEx.Android/app/src/main/java/com/clindsay94/remex/widget/AppLauncherWidget.kt` | 211 |

## Execution Flows

| Flow | Type | Steps |
|------|------|-------|
| `OnCreate → PrefKey` | cross_community | 5 |
| `OnCreate → Aead` | cross_community | 5 |
| `OnCreate → GetPinnedHostHashNative` | cross_community | 5 |
| `AppNavigationContent → SetPinnedHostHashNative` | cross_community | 5 |
| `AppNavigationContent → Aead` | cross_community | 5 |
| `RemoteControlScreen → WakePcNative` | cross_community | 4 |
| `RemoteControlScreen → SendCommandNative` | cross_community | 4 |
| `PairingScreen → StartPairingNative` | intra_community | 4 |
| `PairingScreen → SubmitPairingPinNative` | intra_community | 4 |
| `OnCreate → NavigateToConnection` | cross_community | 4 |

## Connected Areas

| Area | Connections |
|------|-------------|
| Widget | 1 calls |
| Theme | 1 calls |
| Navigation | 1 calls |
| Screens | 1 calls |

## How to Explore

1. `gitnexus_context({name: "QrScannerScreen"})` — see callers and callees
2. `gitnexus_query({query: "remex"})` — find related execution flows
3. Read key files listed above for implementation details
