# Changelog

All notable changes to Remex are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/), and this project adheres to [Semantic Versioning](https://semver.org/).

---

## [1.7.0] - 2026-04-08

> **"Polish Overhaul"** — 4-phase quality pass across the desktop client, host, and tooling.

### Added

**Phase 1 — Service Installation Robustness + Session 0 Handling**

- **Multi-strategy host binary resolution** — `FindHostExePath()` tries 5 strategies in order: user-configured path, adjacent directory, sibling subfolder, parent-level sibling, dev-time publish output. Replaces the broken `GetPublishDir()` which only worked during development.
- **Host version verification** — `VerifyHostVersion()` compares `FileVersionInfo` of the found host binary against the running client assembly version and warns on mismatch.
- **Host Binary Path UI** — new text input + Browse button in the Windows Service section of Settings. Persisted to `DashboardProfile.HostPath`.
- **Session 0 detection** — warns when the desktop client is running in a non-interactive Windows Service session (Session 0), where screen capture and app launching may not work as expected.
- **`install-service.ps1` improvements** — multi-strategy path resolution matching the client, plus `-HostPath` parameter for explicit override.

**Phase 2 — Sensor UX Improvements + Remote Desktop Bug Fix**

- **`SensorReading.Source` property** — new `Source` field on `SensorReading` in `TelemetryPayload.cs`, tagged as `"HWInfo"`, `"WindowsPerf"`, or `"Linux"` at the telemetry service level.
- **Sensor source badges** — `SensorPinItem` carries `Source` info; Settings panel sensor list shows source indicators.
- **HWInfo info tooltip** — hover info near the PINNED SENSORS header explaining HWInfo setup, sensor counts, and the 12-hour shared memory timeout.
- **Scrollable sensor list** — `ItemsControl` for pinned sensors wrapped in a `ScrollViewer` with `MaxHeight="350"` for large sensor collections.
- **DXGI Desktop Duplication** (`DxgiDesktopCapture.cs`) — new GPU-accelerated screen capture implementation using the Windows Desktop Duplication API for lower latency and CPU usage.
- **`DashboardLayoutService` exposed** — `ShellViewModel.LayoutService` public property allows child VMs to read persisted stream settings.

**Phase 3 — Desktop Tutorial Overhaul**

- **9-page interactive tutorial** — replaced the generic 5-page walkthrough with a comprehensive host-centric guide:
  - Page 0: Welcome to RemEx
  - Page 1: How It Works (architecture: PC = host, Android = client)
  - Page 2: Windows Service Setup (install, configure login, auto-start)
  - Page 3: Linux Service Setup (systemd commands, link to full guide)
  - Page 4: HWInfo Integration (download, shared memory setup, 12-hour warning)
  - Page 5: Sensor Monitoring (HWInfo 300+ sensors vs built-in basics)
  - Page 6: App Launcher Setup (configure on desktop, use from mobile)
  - Page 7: Network & Discovery (mDNS, manual IP lookup, connection URI)
  - Page 8: You're All Set (feature overview, replay from Settings)
- **Tutorial localization keys** — 40+ new `.resx` keys for all tutorial content across all 8 locale files.
- **Skip tooltip** — tutorial skip button now shows "Skip the tutorial — you can replay it from Settings".

**Phase 4 — Live Localization**

- **`LocalizationService`** (`Remex.Client/Services/LocalizationService.cs`) — `INotifyPropertyChanged` singleton wrapping `Strings.ResourceManager` with a string indexer. On culture change, raises `PropertyChanged` for `""` and `"Item[]"` to refresh all XAML bindings live.
- **`LocalizeExtension`** (`Remex.Client/Converters/LocalizeExtension.cs`) — XAML markup extension enabling `{local:Localize KEY}` syntax. Creates one-way bindings to the `LocalizationService` singleton.
- **110 new resource keys** added to `Strings.resx` and all 7 locale `.resx` files covering tooltips, watermarks, section headers, and hint text across all views.
- **`Strings.Designer.cs`** — 110 new strongly-typed static properties.

### Changed

**Phase 1**

- `SettingsViewModel.cs` — rewrote `InstallServiceAsync()` to use `FindHostExePath()` instead of attempting `dotnet publish` from source.
- `DashboardProfile.cs` — added `HostPath` property for persisted custom host binary path.
- `scripts/install-service.ps1` — replaced hardcoded `$PublishDir` with multi-strategy resolution and `-HostPath` parameter.

**Phase 2**

- `RemoteDesktopViewModel.cs` — stream properties (`Quality`, `TargetFps`) now sync from `DashboardProfile` via `ShellViewModel.LayoutService` on construction. Settings panel sliders actually control the stream.
- `ShellViewModel.cs` — exposed `LayoutService` as a public property for child VM access.
- `WindowsTelemetryService.cs` — all `SensorReading` constructors include `Source = "WindowsPerf"` or `Source = "HWInfo"`; category-level deduplication removes Windows fallback sensors when HWInfo covers the same category.
- `LinuxTelemetryService.cs` — all `SensorReading` constructors include `Source = "Linux"`.
- `WindowsScreenCaptureService.cs` — updated to support DXGI desktop duplication alongside existing GDI capture.
- `RemoteDesktopHandler.cs` — updated to handle new capture service capabilities.
- `HostBootstrapper.cs` — registered new DXGI capture service.
- `ShellView.axaml` — FPS slider `Maximum` raised from 60 to 120.
- `SettingsView.axaml` — added `ScrollViewer` around sensor list, source badges, and HWInfo tooltip.

**Phase 3**

- `ShellView.axaml` — tutorial section (lines 784–918) completely rewritten with 9 pages of structured content, 9 page indicator dots, and updated navigation button conditions.
- `ShellViewModel.cs` — `TutorialPageCount` changed from 5 to 9.

**Phase 4**

- **13 AXAML views fully localized** — replaced 316+ hardcoded English strings with `{local:Localize KEY}` bindings:
  `ShellView` (112), `SettingsView` (45), `RemoteDesktopView` (23), `RemoteView` (20), `CanvasView` (20), `HomeView` (19), `DashboardView` (17), `CustomizationView` (14), `AddProgramWindow` (13), `AppLauncherView` (12), `TaskManagerView` (9), `MainView` (8), `TrayFlyoutWindow` (4)
- `SettingsViewModel.cs` — `OnLanguageChanged()` calls `LocalizationService.Instance.SetCulture()` for instant live switching; `InitializeAsync()` sets initial culture from saved preference.
- Removed "Takes effect on next launch" restart note from `SettingsView.axaml` and `ShellView.axaml` — language changes are now live.

### Fixed

- **Stream settings bug** (Phase 2) — Settings panel Quality/FPS/Scale sliders were completely disconnected from the actual remote desktop stream. `RemoteDesktopViewModel` had independent properties that ignored `DashboardProfile` values. Now synced on construction.

---

## [1.1.1] - 2026-04-02

### Added

- **TCP Command Authentication** — The TCP external network listener on port 8338 now validates an optional `AccessKey` parameter in command requests (matching the WebSocket access key). All Android app TCP commands automatically inject the stored access key.
- **GitHub Actions CI Workflow** — New `build-native-android.yml` workflow for building the native Kotlin/Compose Android app on every push and PR affecting `RemEx.Android/`.

### Fixed

**Android Native App (`RemEx.Android`)**
- **Task Manager Card Shape Persistence** — `PersonalizationScreen` was silently resetting `taskManagerCardShapePreset` to zero on every save. Now preserves the stored value across preference changes.
- **Backup Security** — `AndroidManifest.xml` allowed all app data (including access keys) to be backed up to Google Cloud. Updated `backup_rules.xml` and `data_extraction_rules.xml` to exclude `datastore/settings.preferences_pb`.
- **Thread Safety in `Theme.kt`** — `morphCache` LinkedHashMap was not synchronized; concurrent Compose recomposition could cause crashes. Wrapped with `Collections.synchronizedMap()` and added synchronized block for compound operations.
- **Missing `first()` Import** — `TaskManagerViewModel.kt` was missing `import kotlinx.coroutines.flow.first`.
- **Build Tool Release Candidate** — Updated `buildToolsVersion` from `"37.0.0 rc2"` (pre-release) to `"36.0.0"` (stable).
- **Stale Build Comment** — Removed misleading "Temporarily swapped to arm64-v8a" emulator screenshot comment.

**Host Service (`Remex.Host`)**
- **RemoteDesktopHandler Thread Leak** — Background task that consumed the input queue never received a completion signal, leaking a long-running thread on each remote desktop session. Implemented `IDisposable` to call `_inputQueue.CompleteAdding()` on session end.
- **Non-Volatile `_streaming` Bool** — `RemoteDesktopHandler._streaming` flag was not marked volatile and shared between concurrent tasks. Removed the flag entirely in favor of the existing `CancellationTokenSource`.
- **Dispose Race Condition** — `ExternalNetworkListenerService.StopListening()` returned without waiting for the background listen task to finish, causing port availability races. Added proper `Task.Wait()` with timeout after cancellation.

**Desktop Client (`Remex.Client`)**
- **ThemeService Code Duplication** — `SetBaseTheme()` and `ApplyBaseThemeInternal()` had identical theme-switching logic. Extracted to a shared `ApplyBaseThemeCore()` method.

**Android CLI Tooling**
- **Version Code Mismatch** — Android `versionCode = 1` and `versionName = "1.0"` while the release was v1.1. Updated to `versionCode = 2` and `versionName = "1.1"`. APK built from v1.0 config would block user upgrades.

### Changed

- **Documentation Updates** — Updated README.md and CONTRIBUTING.md to clarify access key injection into TCP commands and Android version code syncing requirements.
- **Dependency Updates** — Upgraded `material` to stable `1.13.0` (no longer alpha); `androidx-compose-material3` bumped to `1.5.0-alpha16`. Added explanatory comments in `libs.versions.toml` documenting the Expressive APIs used in `Theme.kt`.

---

## [1.1.1] - 2026-03-25

### Fixed

**Avalonia Client (Critical Bugs)**
- **Base64ToImageConverter memory leak** — Removed `using` statement that disposed MemoryStream before Bitmap finished reading. Bitmap now properly takes ownership of the stream lifecycle.
- **SparklineControl collection event unsubscription bug** — Fixed copy-paste error that was unsubscribing from the *new* collection change event instead of just the old one, causing duplicate event handler accumulation on each property change.
- **MainWindow ThemeService event leak** — Added `OnClosed` override to properly unsubscribe from `ThemeService.CustomizationApplied` event. Stored handler reference for cleanup.
- **SensorColorConverter brush allocation** — Cached brush instances (`NeutralBrush`, `RedBrush`, `YellowBrush`) as `static readonly` fields to eliminate per-frame allocations on every Convert() call.

**ViewModel Event Handler Leaks**
- **ShellViewModel** — Implemented `IDisposable` with proper cleanup of `ThemeService.CustomizationApplied` handler.
- **SettingsViewModel** — Implemented `IDisposable` with cleanup of `ConnectionViewModel.PropertyChanged`; `RefreshSensors()` now unsubscribes old `SensorPinItem.PinChanged` handlers before clearing the collection to prevent accumulation across refresh cycles.
- **RemoteDesktopView** — Moved ComboBox `SelectionChanged` subscriptions from constructor to `OnAttachedToVisualTree`/`OnDetachedFromVisualTree` lifecycle methods for proper subscription/unsubscription balance.

**Android Native Client**
- **Missing imports** — Added `androidx.compose.foundation.layout.padding` extension import to AppNavigation.kt and `androidx.compose.ui.input.pointer.isSecondaryPressed` extension import to RemoteDesktopScreen.kt for S-Pen button press detection.

### Changed

- **Build quality** — All Kotlin and .NET code compiles with zero errors and zero warnings.
- **Test coverage** — All 94 unit tests pass successfully.

---

## [1.1.0] - 2026-03-25

### Added

- **Native Android App (`RemEx.Android`)** — A full Kotlin + Jetpack Compose Android client powered by a .NET NativeAOT JNI core (`libRemexCore.so`). Implements all RemEx features natively with smooth Material 3 motion and animations.
  - `RemexCoreClient` Kotlin JNI bridge exposes `InitRemex`, `WakePc`, `GetTelemetry`, `SendCommand`, `StartMdnsDiscovery`, and a callback interface for receiving telemetry, process list, frame data, launcher state, and host info updates.
  - All screens implemented in Compose: Dashboard, Task Manager, App Launcher, Remote Desktop, Remote Control, Remote Mouse, Personalization, Connection, and Splash.
  - `RemexClientManager` singleton manages connection state and routes JNI callbacks to screen ViewModels.
- **Personalization System (native Android)** — Font family selection (System, Roboto, Montserrat, Nunito), Material You dynamic color seed, and card-shape presets (Rounded, Cut, Mixed). Persisted via `SettingsManager` (Kotlin `DataStore`).
- **Bottom NavigationBar + FAB** — Replaced the modal navigation drawer with a persistent bottom bar and a floating action button for settings. Navigation hidden during splash/connection flow.
- **Dashboard Drag-and-Drop** — Animated drop-target previews with spring animations for card placement. Per-type shape support respects personalization presets.
- **Android Home-Screen Widgets** — Four widget providers: `SensorWidgetProvider`, `ResourceWidgetProvider`, `RemoteControlWidgetProvider`, and `RemexWidgetProvider`. Configurable from `WidgetConfigActivity` using `WidgetSettingsManager`.
- **Material 3 Android Overhaul (Avalonia Android client)** — `Material3Android.axaml` theme override file with M3 design tokens (corner radii, elevation shadows), card, button, input, nav-pill, and typography tweaks. `ShellViewModel` uses `CrossFade` consistently on Android. `TaskManagerView` fully redesigned with M3 surfaces, search/sort controls, and improved touch targets.
- **Adaptive Launcher Icons** — `mipmap` foreground/background drawables and a Material 3 dynamic-color adaptive icon for the native Android app.
- **Gradle Typed Tasks** — `SyncRemexCoreSoTask` and `VerifyRemexCoreInApkTask` replace ad-hoc scripts. SHA-256 hash matching enforced between the published `.so` and the APK-embedded copy; stale artifacts fail the build immediately.
- **JNI Callbacks for Desktop Streaming** — `onFrameReceived(ByteArray)` and `onDesktopError(String)` callbacks deliver remote desktop frames and errors directly to the native renderer from `.NET` via JNI, avoiding intermediate copies.
- **Task Manager Search & Sort (native Android)** — Filter processes by name; sort ascending/descending by name, CPU, or memory.
- **Spring Animations** — Dashboard card placement and list transitions use Compose `spring()` specs for fluid, physics-based motion.

### Changed

- **Version bump** — `1.0.0` → `1.1.0` in `Directory.Build.props`.
- **Navigation (native Android)** — Bottom `NavigationBar` replaces the modal drawer; settings open via FAB instead of a dedicated nav item.
- **Personalization screen** — Expanded to cover font, color seed, and card-shape presets in addition to theme style.
- **Gradle build script** — Removed the destructive `purge` task; complex logic extracted to typed tasks with proper input/output declarations.

---

## [1.0.0] - 2026-03-21

### Added

- **Remote Desktop** — Live screen streaming over a dedicated WebSocket (`/ws/desktop`) with configurable JPEG quality, downscale factor, and target FPS. Supports mouse, keyboard, and stylus input forwarding. Includes fullscreen immersive mode, virtual cursor pad, and zoom/pan viewport.
- **App Launcher** — Define app shortcuts on the host, sync them to the client, and launch applications remotely. Supports Session 0 → interactive-session launching on Windows Services. Persistent local storage with host-sync fallback.
- **Task Manager** — Real-time process list with CPU and memory usage. Searchable and sortable. Kill processes remotely with elevation fallback. Cross-platform via Windows `Process` API and Linux `/proc`.
- **Wake-on-LAN** — Send magic packets to wake machines on the network. Broadcasts across all active physical NICs.
- **Access Key Security** — Shared access-key authentication on all WebSocket endpoints. Configured in host `appsettings.json` and entered once from the Settings panel.
- **mDNS Service Discovery** — Host advertises itself via mDNS (`MdnsAdvertisingService`); clients auto-discover the host on the LAN via `MdnsDiscoveryService`, eliminating manual IP entry.
- **Theme Engine** — Runtime theme switching with four built-in themes: BaseDarkGlass, CyberNOC, Monolith, and SolarFlare. `ThemeService` applies accent color, corner radius, glass opacity, and glow overrides.
- **DashboardBackground Control** — Configurable canvas backgrounds (solid, gradient, animated) via `DashboardBackgroundControl`.
- **Sidebar Navigation** — Replaced per-view navigation buttons with a collapsible `SplitView` sidebar. Compact icon mode (64 px), expanded label mode (220 px), and a connection status indicator.
- **Home Screen Redesign** — Quick-action pills for Remote, Desktop, Launcher, and Tasks. Live pinned sensor cards with sparkline graphs.
- **Settings Panel** — Overlay panel with snap-to-grid toggle, grid size, host address, access key, sensor pin management, and Windows Service status.
- **Windows Service Manager UI** — Service install/start/stop/uninstall from the Settings panel without leaving the app.
- **Input Simulation Improvements** — Extended `WindowsInputSimulationService` with improved stylus, touch, and keyboard handling for remote desktop.
- **Avalonia Android Entry Point (`Remex.Client.Android`)** — Early Android home-screen tile (`RemexTileService`) and basic widget (`RemexWidgetProvider`).
- **SparklineControl Performance** — Brushes and pens cached as `ImmutableSolidColorBrush` / `ImmutablePen` to eliminate per-frame allocations.
- **GitHub Actions** — `build-android.yml` workflow for building and uploading signed Android APKs.
- **API Contracts** — [`docs/API_CONTRACTS.md`](docs/API_CONTRACTS.md) with full endpoint, WebSocket message, and TCP command specifications.
- **CONTRIBUTING.md** — Development setup, build commands, architecture notes, and versioning conventions.

### Changed

- **Navigation architecture** — Moved from three-view `PageSlide` to six-view sidebar with platform-adaptive transitions (randomized slide/crossfade/zoom-fade on desktop; `CrossFade` on Android).
- **Version bump** — `0.2.0` → `1.0.0`.
- **Repository cleanup** — Removed AI agent artifacts, build logs, and internal planning documents. Cleaned `.gitignore`. Updated all documentation for public release.

---

## [0.2.0] - 2026-03-06

### Added

- **Dark Glass UI Overhaul** — Application uses modern glassmorphic aesthetics with layered translucent cards (`.glass-card`).
- **Mica / Acrylic Blur** — `MainWindow` drops native OS chrome for edge-to-edge transparent content.
- **Custom TitleBar** — Integrated drag-area header for window movement.
- **Sensor Staging Drawer Interaction** — Sensors can be removed from the active canvas by dragging them back into the Staging Drawer.
- **Fluid Animations** — Switched to `PageSlide` for sleek horizontal navigation transitions.
- **Global `Directory.Build.props`** — Centralized versioning (`0.2.0`).
- **Command Center Dashboard** — Replaced the WrapPanel dashboard with a three-view "on-glass" navigation system:
  - `HomeView` — NOC-style landing page with connection status hero card and pinned sensor UniformGrid.
  - `CanvasView` — Free-form 4000×4000 Canvas workspace with draggable, resizable sensor cards.
  - `SettingsView` — Snap-to-grid toggle, grid size slider, and persisted host address.
- `ShellView` with `TransitioningContentControl` + `CrossFade(250ms)` for smooth view transitions.
- `DraggableCard` control — Pointer-capture drag with opacity/scale feedback + Thumb-based corner resize.
- **Staging Drawer** — New sensors discovered via telemetry appear in a collapsible sidebar before being placed on the canvas.
- **Pin to Home** — Right-click context menu (long-press on mobile) to pin/unpin sensor cards to the Home overview.
- **Dashboard Persistence** — `DashboardProfile` saved to JSON with debounced writes (2 s timer).
- **IP Address Persistence** — Host address stored in profile, resolving Android connection memory loss.
- `DashboardProfile` and `CardState` data models in `Remex.Core`.
- `IDashboardLayoutService` interface + `DashboardLayoutService` implementation.
- Added ViewModels: `ShellViewModel`, `HomeViewModel`, `CanvasCardViewModel`, `CanvasDashboardViewModel`, `SettingsViewModel`.
- Added `StringMatchConverter` for CardType-based DataTemplate visibility switching.
- 8 new unit tests: `DashboardProfile` serialization round-trip, default values, and snap-to-grid math.

### Changed

- **Removed Solid Backgrounds** — Refactored views to use `Transparent` backgrounds, exposing OS window blur.
- Button styles updated for interactive saturated pointer-over states.
- `MainWindow.axaml` — Now hosts `ShellView` instead of `DashboardView`.
- `App.axaml.cs` — Wires `ShellViewModel` + `DashboardLayoutService` as root DataContext.
- `README.md` — Full rewrite reflecting Command Center architecture and feature set.

### Fixed

- **Android App Crash** — Fixed `XamlLoadException: No precompiled XAML found for Remex.Client.App` on Android Release build caused by 5 hidden XAML compilation errors in `CanvasView.axaml`.
- **Android View Resolution** — Fixed issue where Android displayed raw ViewModel class names instead of Views by adding explicit DataTemplates in `App.axaml`.
