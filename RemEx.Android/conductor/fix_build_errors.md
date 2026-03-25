# Build & Code Quality Fix Plan

## Background & Motivation
The previous extensive changes introduced several build errors and code quality issues:
1.  **Syntax Errors**: Invalid property declarations and missing braces in `Theme.kt`.
2.  **Duplicate/Incorrect Code**: Duplicate methods in `SettingsManager.kt` and missing parameters in `PersonalizationViewModel.kt`.
3.  **Code Style**: Imports added in the middle of files and unused color schemes.
4.  **Missing Definitions**: The `Shapes` definition was lost or not correctly applied.

## Key Files & Context
- `app/src/main/java/com/clindsay94/remex/ui/theme/Theme.kt`
- `app/src/main/java/com/clindsay94/remex/data/SettingsManager.kt`
- `app/src/main/java/com/clindsay94/remex/ui/screens/PersonalizationViewModel.kt`
- `app/src/main/java/com/clindsay94/remex/ui/screens/DashboardScreen.kt`
- `app/src/main/java/com/clindsay94/remex/ui/screens/TaskManagerScreen.kt`

## Implementation Steps

### 1. Fix `Theme.kt`
- Consolidate all imports at the top.
- Fix `private val MaterialDynamicColorsInstance = MaterialDynamicColors()`.
- Add the missing closing brace for `colorSchemeFromSeed`.
- Add the `Shapes` definition.
- Remove unused `MonolithDarkScheme`.
- Ensure `RemExTheme` uses the correct parameters.

### 2. Fix `SettingsManager.kt`
- Remove the duplicate non-suspend `savePersonalization` method.

### 3. Fix `PersonalizationViewModel.kt`
- Update the `save` method to correctly pass `themeSeedColor` to `settingsManager.savePersonalization`.
- Align parameter order with `SettingsManager.kt` for consistency.

### 4. Cleanup UI Screens
- Review and cleanup any mid-code imports or duplicate imports in `DashboardScreen.kt`, `TaskManagerScreen.kt`, and `ConnectionScreen.kt`.

## Verification & Testing
- Attempt to build the project using `./gradlew assembleDebug`.
- Verify that the app launches and the Splash Screen animation plays.
- Verify that Personalization settings (theme, seed color, fonts) apply correctly and persist after restart.
- Verify that the Connection Screen layout is correct and sliders don't overlap.
