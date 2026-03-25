# Implementation Plan: RemEx Customization Engine

This plan breaks down the development of the Customization Engine into actionable tasks, following the design specification in `docs/superpowers/specs/2026-03-19-remex-customization-design.md`.

---

## 🏗️ Phase 1: Theme Infrastructure & Resource Management
**Goal:** Create the underlying system to swap themes and override resources at runtime.

- [ ] **1.1 Resource Separation:** 
  - Create `Remex.Client/Themes/` directory.
  - Extract current "Dark Glass" resources from `App.axaml` into `Remex.Client/Themes/BaseDarkGlass.axaml`.
  - Create `Remex.Client/Themes/CyberNOC.axaml`.
  - Create `Remex.Client/Themes/SolarFlare.axaml`.
  - Create `Remex.Client/Themes/Monolith.axaml`.
- [ ] **1.2 Dynamic Theme Service:**
  - Create `Remex.Client/Services/ThemeService.cs`.
  - Implement `SetBaseTheme(ThemeType theme)` which replaces the `MergedDictionaries` in `Application.Current.Resources`.
  - Implement `SetResourceOverride(string key, object value)` to support atomic fine-tuning.
- [ ] **1.3 Data Persistence:**
  - Update `Remex.Core/Models/DashboardProfile.cs` to include a `CustomizationSettings` object.
  - Ensure the `DashboardLayoutService` correctly saves and loads these new settings.

---

## 🎨 Phase 2: Customization Hub UI
**Goal:** Implement the two-tiered user interface for theme management.

- [ ] **2.1 Preset Gallery:**
  - Update `CustomizationView.axaml` to show three large interactive cards for the base themes.
  - Bind card selection to `CustomizationViewModel` and trigger the `ThemeService`.
- [ ] **2.2 Advanced Overrides Panel:**
  - Implement a collapsible `Expander` or `Drawer` for "Advanced Fine-Tuning".
  - Add `Slider` controls for:
    - Corner Radius (0px to 32px)
    - Glass Opacity (5% to 95%)
    - Glow Strength (0 to 20px spread)
  - Add a `ColorPicker` or Palette for Accent Color.
- [ ] **2.3 Component Customizer:**
  - Add dropdowns for Font Sets (Inter, JetBrains Mono, etc.).
  - Add toggles for hardware card sparkline types (Line, Area, Bar, Gauge).

---

## 📡 Phase 3: Canvas & Background Effects
**Goal:** Implement platform-specific background effects and Android fallbacks.

- [ ] **3.1 Background Manager:**
  - Create a `DashboardBackgroundControl.axaml` that wraps the `ShellView` content.
  - Implement logic to toggle between:
    - Native Mica/Acrylic (Desktop).
    - Custom Linear Gradient (Android/Fallback).
    - Local Wallpaper with Blur.
- [ ] **3.2 Composition Animations:**
  - Implement subtle "Atmospheric Motion" using Avalonia's `CompositionAPI` (e.g., animating the gradient offset or glow blur intensity).
  - Add "Reactive State" triggers: Update theme resource colors (e.g., `AccentPrimary`) when CPU/GPU load exceeds defined thresholds.

---

## 🧪 Phase 4: Validation & Polish
**Goal:** Ensure performance and visual fidelity across all platforms.

- [ ] **4.1 Performance Audit:**
  - Profile the application while dynamic effects are active to ensure no impact on telemetry polling.
- [ ] **4.2 Cross-Platform Verification:**
  - Test theme switching on Windows (Mica) and verify the Gradient fallback on Android.
- [ ] **4.3 UI Polish:**
  - Ensure all `DynamicResource` usages are correctly updated without flickering or layout jumps.

---

**Next Steps:**
1. Start Phase 1 Task 1.1: Resource Separation.
2. Integrate with `Subagent-Driven Development` for execution.
