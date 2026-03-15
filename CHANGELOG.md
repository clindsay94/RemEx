# Changelog

All notable changes to Remex are documented in this file.

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
