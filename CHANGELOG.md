# Changelog

All notable changes to Remex are documented in this file.

Format: `[Agent:<name>] <type>: <description>` per the commit protocol in `AGENT_COMMS.md`.

---

## [0.2.0] - 2026-03-06

### Added

- **Dark Glass UI Overhaul** — Application uses modern glassmorphic aesthetics with layered translucent cards (`.glass-card`).
- **Mica / Acrylic Blur** — `MainWindow` drops native OS chrome for edge-to-edge transparent content.
- **Custom TitleBar** — Implemented an integrated drag-area header for window movement.
- **Sensor Staging Drawer Interaction** — Sensors can now be removed from the active canvas by dragging them back into the Staging Drawer.
- **Fluid Animations** — Switched to `PageSlide` for sleek horizontal navigation transitions.
- **Global `Directory.Build.props`** — Centralized versioning (`0.2.0`).
- **Command Center Dashboard** — replaced the WrapPanel dashboard with a three-view "on-glass" navigation system:
  - `HomeView` — NOC-style landing page with connection status hero card and pinned sensor UniformGrid
  - `CanvasView` — free-form 4000×4000 Canvas workspace with draggable, resizable sensor cards
  - `SettingsView` — snap-to-grid toggle, grid size slider, and persisted host address
- `ShellView` with `TransitioningContentControl` + `CrossFade(250ms)` for smooth view transitions
- `DraggableCard` control — pointer-capture drag with opacity/scale feedback + Thumb-based corner resize
- **Staging drawer** — new sensors discovered via telemetry appear in a collapsible sidebar before being placed on the canvas
- **Pin to Home** — right-click context menu (long-press on mobile) to pin/unpin sensor cards to the Home overview
- **Dashboard persistence** — `DashboardProfile` saved to JSON with debounced writes (2s timer)
- **IP address persistence** — host address now stored in profile, fixes Android connection memory loss
- `DashboardProfile` and `CardState` data models in `Remex.Core`
- `IDashboardLayoutService` interface + `DashboardLayoutService` implementation
- `ShellViewModel`, `HomeViewModel`, `CanvasCardViewModel`, `CanvasDashboardViewModel`, `SettingsViewModel`
- `StringMatchConverter` for CardType-based DataTemplate visibility switching
- 8 new unit tests: `DashboardProfile` serialization round-trip, default values, snap-to-grid math

### Changed

- **Removed Solid Backgrounds** — Refactored `HomeView`, `CanvasView`, `SettingsView`, and others, dropping hardcoded `#12121E` colors for `Transparent` backgrounds to expose OS window blur.
- Button styles updated for interactive saturated pointer-over states.
- `MainWindow.axaml` — now hosts `ShellView` instead of `DashboardView`
- `App.axaml.cs` — wires `ShellViewModel` + `DashboardLayoutService` as root DataContext
- `AGENTS.md` — updated roadmap (Phase 4 complete), added test projects to structure
- `system_architect.md` — updated agent persona table, updated roadmap
- `README.md` — full rewrite reflecting Command Center architecture and feature set
