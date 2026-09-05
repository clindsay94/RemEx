# SPEC: Personalization sheet redesign (PC)

**Status:** approved design, awaiting plan. **Date:** 2026-09-04. **Owner:** Connor.
**Source:** brainstorm of 2026-09-04 (scope map, mockup `sheet-flow.html`, seven design sections, all approved).
**Beads it serves:** RemEx-ddynd (Aurora and Wallpaper modes), RemEx-z94c7 (Mica), plus the sheet
reflow itself. RemEx-8y3qy (startup clobber) already landed and is a prerequisite that is met.
**Not this spec:** the theme-adaptive splash and icons (spec B, RemEx-alwfa.1), Android changes,
theme sync from the phone (RemEx-y06a0.1), repairing Mica.

## 1. Goal

The PC Personalization sheet mirrors the Android app's colour flow as closely as the platform
allows, so a person who has customised one device recognises the other: choose where the colour
comes from, shape it with vibrancy and contrast, pick a palette strategy, then the PC-only look,
then fine-tuning. One path to a colour: nothing on the sheet silently replaces work done higher up.

Principles that settled every decision below:

- **One seed, one path.** The accent swatches that today call the seed setter are gone as a
  concept. A colour is chosen by source, then shaped. Saved palettes are whole palettes.
- **Android's vocabulary.** Seven strategies in Android's order; the same slider names.
- **Describe what renders.** A mode that cannot render on this build (Mica) is not offered.
- **Remember the choice.** Every setting round-trips through the profile file; the startup path
  no longer discards it (RemEx-8y3qy).

## 2. Current state (verified 2026-09-04)

- View: `remex.desktop/Views/PersonalizationPanelView.axaml`, eight sections in this order: Base
  presets (swatch gallery, import, export), Palette Studio (wheel, hue, chroma, tone sliders, hex,
  recent seeds, system seed buttons), Base mode, Contrast, Advanced tuning (corners, glass, glow),
  Atmosphere (background mode, window opacity, scheme variant, tonal ramp), Advanced (splash,
  hardware sync, reduced motion), Typography (fonts, UI scale), Reset.
- View model: `remex.desktop/ViewModels/CustomizationViewModel.cs`. `SetAccent(hex)` assigns
  `AccentColor`, which is the seed, so a swatch regenerates the whole palette. Variant names at
  `:158-161`. Background types populated near `:641-650`: Mica, Acrylic, Glass, Gradient,
  Wallpaper, Solid. Splash options near `:713-715`: RemexCommand, CosmicZoom, Pong.
- Model: `remex.core/Models/DashboardProfile.cs` `CustomizationSettings`: `AccentColor` (seed,
  default `#6C4CFF`), `SchemeVariant` (default TonalSpot), `ThemeSeedChroma` (48), `ThemeContrast`
  (0), `ThemeMode` (Light, Dark, System), `BackgroundMaterial` (JSON `canvasBackgroundType`,
  default Mica), `SplashStyle` (default RemexCommand), `CustomAccentColors` (string array of hex),
  fonts, `UiScale`, `CornerRadius`, `GlassOpacity`, `GlowStrength`, `AppWindowOpacity`.
- Migration: `remex.desktop/Services/CustomizationMigration.cs`, `CurrentSchemaVersion = 2`,
  a `with`-based step per version.
- Engine: `remex.desktop/Services/DynamicColorGenerator.cs` over the MaterialColorUtilities
  package; `StyleFor(variant)` at `:198-206` maps Vibrant, Expressive, Rainbow, FruitSalad, Content,
  Spritz, default TonalSpot.
- Backgrounds: `remex.desktop/Controls/DashboardBackgroundControl.axaml`; the mode called
  Wallpaper is an animated aurora mesh (three radial blobs in accent colours, 8/12/15 s loops) that
  never reads the wallpaper; Mica and Acrylic set the window's `TransparencyLevelHint` in
  `remex.desktop/MainWindow.axaml.cs:99-115`. Mica never renders on this Avalonia build
  (RemEx-z94c7: pixels invariant to wallpaper changes).
- Seeds from the system: `remex.desktop/Services/SystemSeedSources.cs` reads the Windows accent
  from the DWM registry keys and extracts wallpaper seed candidates from the wallpaper file named
  in `HKCU\Control Panel\Desktop`. Windows-only.
- Light or dark resolution: `ThemeService.ResolveIsLight` (`ThemeMode` outranks the legacy flag;
  System follows the OS).
- Presets: `remex.desktop/Models/SeedPreset.cs`, eight entries in `SeedPresetCatalog`
  (BaseDarkGlass default, CyberNOC, SolarFlare, Monolith, Dynamic, Daybreak, Voltage, Sorbet).
  Palette export and import exist (RemEx-a7uzb) through the storage provider in
  `PersonalizationPanelView.axaml.cs`.

## 3. Target sheet

Six sections, top to bottom. Each section is a card with a header string; the order is pinned by a
source-text guard.

1. **Colour**
   - **Source**: segmented choice, Windows accent (default), Wallpaper, Custom.
     - Windows accent: shows the current accent as a swatch; follows Windows Settings live
       (within two seconds of a change, and again on resume from sleep). No wheel.
     - Wallpaper: shows the extracted candidate swatches the sheet already produces; the chosen
       candidate is remembered; a Refresh action re-extracts when the wallpaper changed.
     - Custom: the colour wheel and the hex box. Recent seeds row lives here.
   - **Vibrancy** (chroma) and **Contrast** sliders, visible for every source. They keep their
     current backing fields.
   - **Strategy** chips, Android's seven in Android's order: Tonal Spot, Expressive, Fruit Salad,
     Rainbow, Vibrant, Neutral, Monochrome.
   - **Preview**: the tonal ramp strip plus a sample card that takes the current corner radius and
     window opacity.
2. **Mode**: Light, Dark, System (unchanged control, moved).
3. **Look** (PC only)
   - **Background**: Aurora (default for new profiles), Wallpaper, Acrylic, Glass, Gradient,
     Solid. Mica is not offered.
   - When Wallpaper is selected: **Wallpaper source** (Desktop wallpaper, Pick an image) and a
     **Blur** slider.
   - **Window opacity** slider (unchanged).
4. **Fine-tuning** (expander): corners, glass opacity, glow, title font, body font, UI scale.
5. **Behaviour**: splash style with a Preview button (Cosmic Zoom default), hardware sync,
   reduced motion.
6. **Saved palettes**: built-in presets row, user palettes row, Save current, Import, Export.
   Reset to defaults sits below the last card, as today.

Removed from the sheet: the Tone slider (Android has no equivalent and the seed's tone barely
affects the derived palette), the accent swatch gallery as a seed shortcut, the Content and
Spritz strategies as user-facing names.

## 4. Data model and migration

`CustomizationSettings` changes (`remex.core`, NativeAOT-safe, `init` properties, JSON names in
camelCase like their neighbours):

| Property | JSON | Type | Default | Notes |
|---|---|---|---|---|
| `ColorSource` | `colorSource` | string | `WindowsAccent` | `WindowsAccent`, `Wallpaper`, `Custom`. Linux default `Custom`. |
| `WallpaperSeedIndex` | `wallpaperSeedIndex` | int | 0 | Which extracted candidate was chosen. |
| `WallpaperSource` | `wallpaperSource` | string | `Desktop` | `Desktop` or `Image`. |
| `WallpaperImagePath` | `wallpaperImagePath` | string? | null | Path of the app-owned copy, never the original. |
| `WallpaperBlur` | `wallpaperBlur` | double | 0.6 | 0 to 1, mapped to a blur radius. |
| `BackgroundMaterial` | `canvasBackgroundType` | string | `Aurora` | Adds `Aurora`, removes `Mica`. |
| `SchemeVariant` | unchanged | string | `TonalSpot` | Allowed: the seven Android names. |
| `SplashStyle` | unchanged | string | `CosmicZoom` | Default changes. |
| `SavedPalettes` | `savedPalettes` | array | empty | See section 7. |
| `CustomAccentColors` | unchanged | string[] | empty | Kept for reading old files; migrated, then left empty. |

`AccentColor` stays the seed for every source: the source only decides who writes it. With
Windows accent or Wallpaper selected the app writes `AccentColor` from that source; with Custom
the user writes it.

`CurrentSchemaVersion` becomes 3. The 2 to 3 step, in one `with` expression so no field is
dropped (the RemEx-8y3qy guard applies):

- `BackgroundMaterial` `Mica` becomes `Wallpaper` with `WallpaperSource` `Desktop` and
  `WallpaperBlur` 0.9.
- `SchemeVariant` `Spritz` becomes `Neutral`; `Content` becomes `TonalSpot`; anything outside the
  seven becomes `TonalSpot`.
- `SplashStyle` `RemexCommand` becomes `CosmicZoom` (the person who asked for the default is the
  person whose file holds the old default).
- Each entry of `CustomAccentColors` becomes a `SavedPalette` with that seed, source `Custom`,
  the file's current vibrancy, contrast and strategy, and the name `Palette 1`, `Palette 2`, and
  so on in file order (plain English, generated in `remex.core` where no localisation exists;
  the person renames it on the sheet); the array is then emptied.
- `ColorSource` is set to `Custom` for a migrated file, because the file's seed was chosen by hand
  or by a preset; new profiles start on Windows accent.

Every property is covered by the reflection-driven round-trip test from RemEx-8y3qy, which fails
automatically if a new field is dropped by the copy path.

## 5. Colour engine

- **Windows accent source.** Read through the existing registry path in `SystemSeedSources`. A
  watcher raises a change within two seconds (registry change notification, or a two-second
  poll while the app is visible; the plan decides, the spec requires the latency and that the
  poll stops while the window is hidden). On change, `AccentColor` is rewritten and the palette
  regenerates through the existing apply path; the profile is saved through the debounced path.
- **Wallpaper source.** Candidates from the existing extraction. The chosen index is persisted;
  if the wallpaper changed and the index is out of range, the first candidate is used and the
  index reset.
- **Custom.** Unchanged wheel and hex behaviour.
- **Vibrancy and contrast.** Bound to `ThemeSeedChroma` and `ThemeContrast` for every source.
- **Strategies.** `StyleFor` maps Neutral to the library's `Spritz` style (the low-chroma
  Material style, which is what Android's Neutral is) and Monochrome to the library's
  `Monochrome` style; if the package lacks `Monochrome`, a core palette with zero chroma on every
  tonal palette is built instead. Content is removed from the map. Any persisted strategy string
  outside the seven, wherever it is read (the profile's `SchemeVariant`, a saved palette, an
  imported file), normalises to Tonal Spot at load; `SavedPalette` is new in schema 3, so no
  existing file carries one. Strategy names on the sheet are localised; the persisted strings
  stay English.
- **Preview.** The existing tonal ramp plus a sample card control bound to the live palette, the
  corner radius and the window opacity. No new palette computation.

## 6. Background modes

- **Aurora.** The current mesh, kept as its own mode: blob radius up by half, peak opacity up so
  the colour reads on a dark surface at a glance, loops unchanged. Two colour sets from the tonal
  ramp: for dark mode the primary, secondary and tertiary containers at low tones; for light mode
  the same roles at high tones, over the surface colour. The set follows the same
  `ResolveIsLight` result the theme uses, so System mode flips with the OS. Reduced motion
  freezes the mesh at its first keyframe, as the existing mesh does today.
- **Wallpaper.** Draws the desktop wallpaper file (path from the existing registry read) or the
  app-owned copy behind the window content, stretched to fill, with a blur effect whose radius is
  `WallpaperBlur` mapped to 0 to 48 device pixels, and the palette's surface colour over it at
  the current window opacity so text keeps its contrast. Pick an image opens the storage
  provider for common raster types, copies the file under the per-user data directory (the same
  root as the profile file), downscaled so the longest edge is at most 2560 pixels, and stores the
  copy's path. A missing or unreadable file falls back to Solid for that session, raises the
  existing snackbar with a localised message, and leaves the setting unchanged so the person can
  pick again. A desktop wallpaper that changes while the app runs is picked up on the next mode
  change or launch; live tracking is not required.
- **Mica.** Removed from the list, the enum and the transparency plumbing. Acrylic keeps its
  hint. No repair attempted.
- **Acrylic, Glass, Gradient, Solid.** Unchanged.

## 7. Saved palettes

A `SavedPalette` record: `Name`, `ColorSource`, `Seed` (hex), `Vibrancy`, `Contrast`,
`Strategy`. Stored in `SavedPalettes` on the profile. The built-in `SeedPresetCatalog` entries
show first, unchanged in content, and applying one sets the same fields a saved palette sets.
Save current writes the current values with a name the person can edit. Applying a saved palette
sets `ColorSource` to `Custom` with that seed when the palette's source was Custom, or re-selects
the system source when it was Windows accent or Wallpaper. Export and import keep the RemEx-a7uzb
file format and gain the new fields; an older file imports with the migration rules of section 4.
Deleting a user palette is a per-item action; presets cannot be deleted.

## 8. Splash default

`SplashStyle` defaults to `CosmicZoom` in the model, in the preset that carries a splash style
(`SeedPreset.cs:88`, the BaseDarkGlass entry), and in the Skia control's fallback
(`SkiaSplashControl.cs`). The migration
in section 4 flips a stored `RemexCommand`. The Preview button plays the selected splash in the
existing preview surface.

## 9. Platform and errors

- **Linux.** The Windows accent source is hidden and `ColorSource` defaults to `Custom` with the
  brand seed. The seed extraction in `SystemSeedSources` is Windows-only today (every entry
  point returns nothing behind `OperatingSystem.IsWindows()`, `:54`, `:85`, `:152`), so the
  Wallpaper colour source is hidden on Linux and the Wallpaper background mode offers Pick an
  image only. Everything else is identical.
- **Failures.** No path throws to the UI: a failed registry read keeps the last seed; a failed
  wallpaper read falls back as in section 6; a failed image copy leaves the previous image and
  raises the snackbar. Nothing is written to the profile on a failure path (the RemEx-8y3qy
  fallback guard stays in force).
- **Performance.** The wallpaper bitmap is decoded once per path change and cached; the blur
  effect is applied to that bitmap, not re-rendered per frame. Aurora's cost stays within the
  budget RemEx-gtwk8 records for the dashboard.

## 10. Localisation

New strings, in all nine `Strings*.resx` files: section headers for Colour, Look, Behaviour and
Saved palettes; source names; Vibrancy; Strategy; Neutral and Monochrome; Aurora; Wallpaper
source names; Blur; Save current; the wallpaper failure message; the sample card's caption. Removed
strings: the Tone slider label, the Content and Spritz names, the Mica name and its description.
`scripts/check-localization.ps1` must report zero errors and no new warnings.

## 11. Testing and verification

- Unit: `StyleFor` for the seven names and the fallback; the 2 to 3 migration for every rule in
  section 4, including a file with all four old values at once; the reflection round-trip over
  the new fields; `SavedPalette` import of an old-format file; blur mapping bounds; the
  Windows-accent watcher's latency and that it stops while hidden (with a fake clock).
- Source guards: the six section headers appear in order in the view; no control binds to a
  seed-setting command from the Saved palettes card; the Mica string and enum value are absent;
  the splash default string is `CosmicZoom` in the three places section 8 names.
- Sweep: `scripts/ui-palette-sweep.ps1` gains two cells, Aurora in light mode on the default
  seed and Wallpaper in dark mode at blur 0.6 on the Chroma seed; the ledger in
  `docs/UI-PALETTE-SWEEP.md` gains the rows.
- Live (RemEx-bmuji, Connor): Aurora reads bolder in both modes; Wallpaper shows the real
  wallpaper and the blur slider is visible in effect; changing the Windows accent recolours the
  app within two seconds; a picked image survives a restart; Reset returns every new field to its
  default.

## 12. Follow-ups outside this spec

- Spec B: the splash and the PC and Android icons recolour from the seed (RemEx-alwfa.1), with
  the cached last seed for the splash.
- Theme sync from the phone as a fourth colour source, once RemEx-y06a0.1 lands.
- Android parity checks in the other direction (the phone gains nothing from this spec).
