# Changelog

All notable changes to Remex are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/), and this project adheres to [Semantic Versioning](https://semver.org/).

---

## [1.0.0] - 2026-06-12

### Added

- **Remote Desktop** — Live screen streaming over a dedicated WebSocket (`/ws/desktop`) with configurable JPEG quality, downscale factor, and target FPS. Supports mouse, keyboard, and stylus input forwarding. Includes fullscreen immersive mode, virtual cursor pad, and zoom/pan viewport.
- **App Launcher** — Define app shortcuts on the host, sync them to the client, and launch applications remotely. Supports Session 0 → interactive-session launching on Windows Services. Persistent local storage with host-sync fallback.
- **Task Manager** — Real-time process list with CPU and memory usage. Search, sort by column, and kill processes remotely. Cross-platform support via Windows `Process` API and Linux `/proc` filesystem.
- **Wake-on-LAN** — Send magic packets to wake machines on the network. Broadcasts across all active physical NICs. Accessible from the remote execution panel and TCP command ingress.
- **Sidebar Navigation** — Replaced per-view navigation buttons with a collapsible `SplitView` sidebar. Includes compact icon mode (64px), expanded label mode (220px), and connection status indicator.
- **Theme Customization** — Dynamic accent color, corner radius, glass opacity, glow strength, and canvas background selection. Multiple base themes: DeepSpace (default dark), Cyberpunk, NordAurora, SolarFlare (light).
- **Home Screen Redesign** — Quick-action pills for one-tap navigation to Remote, Desktop, Launcher, and Tasks. Live pinned sensor cards with sparkline graphs.
- **Settings Panel** — Overlay panel accessible from the sidebar with snap-to-grid toggle, grid size slider, host address input, sensor pin management, and Windows Service status.
- **GitHub Actions** — `build-android.yml` workflow for building and uploading signed Android APKs.

### Changed

- **Navigation architecture** — Moved from three-view `PageSlide` to six-view sidebar with randomized transitions (slide, crossfade, zoom-fade).
- **Version bump** — `0.2.0` → `1.0.0`.
- **Repository cleanup** — Removed AI agent artifacts, build logs, and internal planning documents. Cleaned `.gitignore`. Updated all documentation for public release.

### Removed

- **Authentication system** — Removed access-key authentication from all endpoints and client code. The TCP command ingress should be protected at the network/firewall level.
- **Multi-monitor support** — Simplified remote desktop to primary monitor only, resolving stability issues on Android.

---

## [0.2.0] - 2026-03-06

### Added

- **Dark Glass UI Overhaul** — Application uses modern glassmorphic aesthetics with layered translucent cards (`.glass-card`).
- **Mica / Acrylic Blur** — `MainWindow` drops native OS chrome for edge-to-edge transparent content.
- **Custom TitleBar** — Implemented an integrated drag-area header for window movement.
- **Sensor Staging Drawer Interaction** — Sensors can now be removed from the active canvas by dragging them back into the Staging Drawer.
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
- **Dashboard Persistence** — `DashboardProfile` saved to JSON with debounced writes (2s timer).
- **IP Address Persistence** — Host address now stored in profile, resolving Android connection memory loss.
- `DashboardProfile` and `CardState` data models in `Remex.Core`.
- `IDashboardLayoutService` interface + `DashboardLayoutService` implementation.
- Added ViewModels: `ShellViewModel`, `HomeViewModel`, `CanvasCardViewModel`, `CanvasDashboardViewModel`, `SettingsViewModel`.
- Added `StringMatchConverter` for CardType-based DataTemplate visibility switching.
- Introduced 8 new unit tests: `DashboardProfile` serialization round-trip, default values, and snap-to-grid math.

### Changed

- **Removed Solid Backgrounds** — Refactored `HomeView`, `CanvasView`, `SettingsView`, and others, dropping hardcoded `#12121E` colors for `Transparent` backgrounds to expose OS window blur.
- Button styles updated for interactive saturated pointer-over states.
- `MainWindow.axaml` — Now hosts `ShellView` instead of `DashboardView`.
- `App.axaml.cs` — Wires `ShellViewModel` + `DashboardLayoutService` as root DataContext.
- `README.md` — Full rewrite reflecting Command Center architecture and feature set.

### Fixed

- **Android App Crash** — Fixed `XamlLoadException: No precompiled XAML found for Remex.Client.App` on Android Release build caused by 5 hidden XAML compilation errors in `CanvasView.axaml` (e.g., misapplied `BoxShadow` and missing `x:DataType`).
- **Android View Resolution** — Fixed issue where Android displayed raw `ViewModel` class names instead of Views by adding explicit DataTemplates in `App.axaml`.
