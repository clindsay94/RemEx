# RemEx Customization Engine - Design Specification

**Date:** 2026-03-19  
**Status:** Draft / Pending Review  
**Topic:** UI Customization, Theming, and Visual Experience

---

## 1. Executive Summary
RemEx is expanding its visual identity from a fixed "Dark Glass" aesthetic to a modular, high-performance **Customization Engine**. This engine allows users to choose from high-fidelity visual presets, fine-tune them with atomic overrides, and experience a dynamic UI that reacts to system load and ambient movement.

## 2. Visual Themes (Base Presets)
Three distinct visual identities will be implemented as the foundation:

### 2.1 Cyber-NOC (Default)
- **Aesthetic:** High-contrast, futuristic hacker command center.
- **Colors:** Deep `#050505` background, Electric Cyan (`#00F3FF`) and Neon Magenta (`#FF00FF`) accents.
- **Geometry:** Sharp 90-degree corners or very slight (2px) radius.
- **Effects:** Heavy neon glow (`BoxShadow`), scan-line overlays (optional), and high-density information.

### 2.2 Solar-Flare (Light Mode)
- **Aesthetic:** Premium, frosted glass, airy and modern.
- **Colors:** Frosted white (`rgba(255,255,255,0.8)`) background, Gold (`#FFB800`) and Warm Orange accents.
- **Geometry:** Large, soft corner radius (16px - 24px).
- **Effects:** Heavy backdrop blur (20px+), soft shadows, and high transparency.

### 2.3 Monolith (Industrial)
- **Aesthetic:** Heavy hardware, brutalist, rack-mounted equipment.
- **Colors:** Slate Gray (`#2C2C2E`) background, Cobalt Blue (`#0A84FF`) accents.
- **Geometry:** Thick 3px borders, medium radius (8px).
- **Effects:** Solid surfaces, no transparency by default, monospaced typography (JetBrains Mono).

## 3. Dynamic Experience (Dynamism)
The UI will employ a **Hybrid Dynamic Model**:
- **Atmospheric Motion:** Subtle "breathing" gradients and glass highlights that shift ambiently.
- **Reactive State:** Visual elements (glow intensity, accent color hue, animation speed) scale based on hardware metrics (e.g., CPU load > 80% triggers a transition to a "Warning" color state).

## 4. Customization Hub (User Interface)
The `CustomizationView` will be refactored into a two-tiered management system:

### 4.1 Tier 1: Preset Selection
- Cards representing the three base themes.
- Selecting a preset updates all `DynamicResource` tokens in `App.axaml`.

### 4.2 Tier 2: Advanced Fine-Tuning (Atomic Overrides)
A collapsible "Advanced" panel allowing users to override the preset:
- **Card Aesthetics:** Slider for `CornerRadius`, slider for `GlassOpacity`, slider for `GlowStrength`.
- **Typography:** Dropdown for Font Sets (Modern Sans, Tech Mono, Retro Serif).
- **Hardware Cards:** Toggle for Sparkline types (Line, Area, Bar, Gauge).
- **Remote Desktop:** Quality vs. Latency slider, custom cursor toggle.

## 5. Canvas Background Section
A dedicated section to control the dashboard's "Atmosphere":
- **Mica (Windows 11):** Native hardware-accelerated transparency.
- **Acrylic Blur:** High-performance software blur fallback.
- **Custom Gradient:** Linear/Radial gradient selector (Deep Dark Violet to Black).
- **Wallpaper:** Ability to load a local image as the dashboard background with an adjustable blur overlay.

## 6. Technical Implementation
- **Resource Management:** Move theme variables into separate `ResourceDictionary` files (e.g., `Themes/CyberNOC.axaml`).
- **State Persistence:** Extend `DashboardProfile` to include `CustomizationSettings` (JSON serialized).
- **Performance:** Use `DrawingContext` and `CompositionAPI` for smooth dynamic glow and motion effects to ensure low CPU impact on the Host.

---

## 7. Success Criteria
- [ ] Users can switch between the 3 base themes without restarting the app.
- [ ] Custom overrides are persisted across sessions.
- [ ] Android client maintains a high-quality visual fallback for Mica effects.
- [ ] No significant telemetry latency increase due to dynamic UI effects.
