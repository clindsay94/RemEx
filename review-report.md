# RemEx `feature/desktop-ui-improvements` — Microscopic Code Review

**Date:** 2026-04-26
**Branch:** `feature/desktop-ui-improvements`
**Reviewer:** microscopic-code-reviewer agent
**Status:** Diagnostic pass complete — fixes NOT YET APPLIED

---

## PART 1: SETTINGS-VIEW FREEZE — ROOT CAUSE ANALYSIS (CRITICAL)

**Confirmed Root Cause: Threading Violation in `RefreshServiceStatusAsync`, Linux Path**

`SettingsViewModel.cs:129` fires `_ = RefreshServiceStatusAsync()` as fire-and-forget from inside `InitializeAsync()`. On Linux, the method continues executing on a ThreadPool thread (since `InitializeAsync` is already running on one after the `await _layoutService.LoadAsync()` on line 107). The Linux code path (lines 860–880) then directly sets `[ObservableProperty]`-backed properties without marshalling to the UI thread:

- Line 865: `IsServiceInstalled = File.Exists(...)` — set from ThreadPool thread
- Line 869: `IsServiceRunning = processes.Length > 0` — set from ThreadPool thread
- Line 870–872, 876, 879: `ServiceStatusText = ...` — set from ThreadPool thread

Each property is decorated with `[ObservableProperty]`, so setting it raises `PropertyChanged` from a background thread. In Avalonia's Skia backend on Linux, attempting to mutate bound visual state from a non-UI thread can stall the dispatcher rather than throw — manifesting as a freeze.

**Fix:** Wrap the Linux branch of `RefreshServiceStatusAsync` (lines 860–881) in `await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(...)`. The same pattern exists correctly in `InitializeAsync()` line 109 — apply consistently.

**Contributing factor:** `EnsureSettingsVm()` in `ShellViewModel.cs:579–587` calls `RefreshSensors()` unconditionally every time the panel toggles, clearing/rebuilding `AvailableSensors` from canvas cards. On a 50-card canvas, this fires 51 `CollectionChanged` events per open/close.

---

## PART 2: CRITICAL ISSUES

### C-1: `SavedStatus` setter from ThreadPool thread
- **File:** `Remex.Client/ViewModels/SettingsViewModel.cs:260`
- `_ = Task.Delay(3000).ContinueWith(_ => SavedStatus = string.Empty);` — continuation runs on ThreadPool, raising `PropertyChanged` off-thread.
- **Fix:** Wrap in `Dispatcher.UIThread.Post(...)`. `CanvasDashboardViewModel.cs:732–733` already has the correct pattern.

### C-2: `DiscoverHostsAsync` sets `HostAddress` from background thread
- **File:** `Remex.Client/ViewModels/ConnectionViewModel.cs:141`
- After `await _discoveryService.DiscoverHostsAsync(...)`, `HostAddress = firstHost` runs on a ThreadPool continuation. The earlier `Dispatcher.UIThread.Post()` block at 127–132 covers `DiscoveredHosts` but not this assignment.

### C-3: `async void OnShowSetAlertRequested` lacks try/catch
- **File:** `Remex.Client/Views/CanvasView.axaml.cs:94`
- An exception from `dialog.ShowDialog(ownerWindow)` would propagate to the UI thread `SynchronizationContext` and crash the process.

### C-4: Sensor `AlertTriggered` event subscription leak on reconnect
- **File:** `Remex.Client/ViewModels/CanvasDashboardViewModel.cs:770`
- `sensor.AlertTriggered += OnSensorAlertTriggered` re-subscribes on each reconnect when `sensorVms.Count == 0`, with no corresponding `-=`. Old `SensorViewModel`s remain rooted by the delegate; `OnSensorAlertTriggered` fires N times after N reconnects.

---

## PART 3: HIGH SEVERITY

- **H-1:** Duplicate `<Style Selector="ctrl|DraggableCard.alert-active">` blocks in `CanvasView.axaml:19,29` — second block partially overrides first, producing an unintended merged result.
- **H-2:** `OnCopySnapshot` (`CanvasView.axaml.cs:171–177`) puts the **file path string** on the clipboard, not the image bitmap. Status message says "Copied to clipboard" — misleading. Avalonia 11.x has no cross-platform clipboard bitmap API; needs platform-conditional code or feature redesign.
- **H-3:** `WireMinimapControl` is called from both `OnDataContextChanged` and `OnAttachedToVisualTree` — currently safe by accident (`FindControl` returns null pre-attach) but fragile.
- **H-4:** `RefreshSensors()` runs on every Settings open/close, not just on actual show.
- **H-5:** `targetSdk = 36` (Android 16 preview as of 2026-04-26). Risk of API behavior changes before Play Store submission. Recommend `35`.
- **H-6:** `androidx.compose.material3:material3:1.5.0-alpha17` — alpha dependency in production submission. Lock the version explicitly.
- **H-7:** `android:enableOnBackInvokedCallback="true"` missing from `<application>` in `AndroidManifest.xml`. **Required** for predictive back on API 33+.

---

## PART 4: MEDIUM SEVERITY

- **M-1:** Private `Enumerable` class in `GroupMoveOperation` shadows `System.Linq.Enumerable`.
- **M-2:** `CustomizationSettings.CustomAccentColors` is mutable `List<string>` inside a `record` — breaks value equality and `with`-expression copies share the list reference.
- **M-3:** `PlaceFromStaging` undo asymmetry — sensor cards push nothing to undo stack; non-sensor cards do. Ctrl+Z behaves inconsistently.
- **M-4:** `killProcess` result captured but never checked in `TaskManagerViewModel.kt:194`. No user feedback on failure.
- **M-5:** Hardcoded `"Sort by:"` string in `TaskManagerScreen.kt:235` — all neighbors use `stringResource()`.
- **M-6:** `ConnectionOrbCard` creates `rememberInfiniteTransition` even when `!isConnecting`.
- **M-7:** `DashboardLayoutService.RequestSave` Android path has no debounce — slider tick storm causes lost intermediate writes.
- **M-8:** `LaunchedEffect(isConnected)` fires on initial composition — shows "Connected" snackbar on cold-launch with auto-connect.
- **M-9:** `allowBackup="true"` may include DataStore (containing `AccessKey`) in Google Drive backups. Verify exclusions in `data_extraction_rules.xml` (API 31+ format).

---

## PART 5: LOW SEVERITY / NITS

- **L-1:** `navigationCompose = "2.8.1"` in versions.toml is dead — library entry hardcodes `2.9.7`.
- **L-2:** `RemexClientManager.connect()` only clears `_isConnecting` on failure path; success relies on a callback that may never fire. Add a finally/timeout guard.
- **L-3:** `pointerInput(... uiState.cards)` key invalidates gesture detector during drags when telemetry updates cards.
- **L-4:** Tray menu strings hardcoded English in `App.axaml`.
- **L-5:** `draggingCardSize = remember(draggingCard?.id, uiState.cards)` recomputes during drags.
- **L-6:** Static `SolidColorBrush` instances in `CanvasMinimap.cs` are mutable — use `ImmutableSolidColorBrush`.
- **L-7:** `InitializeAppAsync` swallows top-level exceptions with only `Debug.WriteLine`.
- **L-8:** `isPublishBuild` detection by inspecting `gradle.startParameter.taskNames` is fragile — use a `-PisPublish=true` Gradle property.

---

## PART 6: GOOGLE PLAY READINESS

| # | Requirement | Status |
|---|-------------|--------|
| 1 | `targetSdk >= 35` | RISK (currently 36 preview) |
| 2 | `enableOnBackInvokedCallback="true"` | **MISSING** |
| 3 | No alpha dependencies in production | RISK (material3 alpha17) |
| 4 | Backup rules exclude AccessKey | UNVERIFIED |
| 5 | All user-visible strings localized | FAIL ("Sort by:") |
| 6 | Kill action provides failure feedback | FAIL |
| 7 | Foreground service `foregroundServiceType` | PASS |
| 8 | Predictive back gesture supported | **MISSING** (needs #2) |
| 9 | Network security config restricts cleartext | UNVERIFIED |
| 10 | `versionCode` incremented for submission | UNVERIFIED |

---

## PART 7: MICROSOFT STORE / DESKTOP DISTRIBUTION

| # | Requirement | Status |
|---|-------------|--------|
| 1 | MSIX packaging configured | UNVERIFIED |
| 2 | Tray app `ShutdownMode` correct | PASS |
| 3 | No UI-thread blocking | FAIL (Settings freeze) |
| 4 | Snapshot save uses sandbox-accessible folder | RISK (needs `picturesLibrary` capability) |
| 5 | Layout save uses `LocalApplicationData` | PASS |

**On NPM distribution:** NPM is a JavaScript/Node package registry — it is **not** a valid distribution channel for an Avalonia .NET application. Strike this from the release plan. If the intent was **winget** (Windows Package Manager), that is a separate valid channel requiring its own manifest submission.

---

## PART 8: UNFIXED — REQUIRES DECISIONS

1. **Snapshot clipboard** — bitmap-on-clipboard requires platform-conditional code; redesign as "Copy Path" if simpler.
2. **`material3` alpha downgrade** — affects `MaterialShapes` / `MotionScheme.expressive()` call sites.
3. **`targetSdk` downgrade** — needs API-35 testing of foreground service + widget.
4. **Undo asymmetry for sensor cards** — product decision (intentional?).
5. **`pointerInput` refactor** — non-trivial; needs `rememberUpdatedState` captures.

---

## SEVERITY SUMMARY

| Severity | Count |
|----------|-------|
| Critical | 4 |
| High | 7 |
| Medium | 9 |
| Low / Nit | 8 |
| **Total** | **28** |
