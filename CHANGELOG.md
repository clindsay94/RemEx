# Changelog

All notable changes to Remex are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/), and this project adheres to [Semantic Versioning](https://semver.org/).

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
