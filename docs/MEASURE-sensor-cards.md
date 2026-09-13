# MEASURE: sensor cards on the scheme (RemEx-4kv0g.3.3)

Date: 2026-09-13 · Branch `feat/sensor-cards-scheme` at `3ab9af92` (this doc and the CHANGELOG are the only later changes) · PC: Release publish via `scripts/update-local-install.ps1`, `C:\Program Files\RemEx\Remex.Agent.exe` started with `--view Sensors`, window parked at 2668,100 1584×992 on the second monitor · Connor's live dashboard (45 cards: 7 follow the theme, 38 carry presets — nothing in the layout was changed for this pass) · Machine as in `docs/MEASURE-text-halo.md`.

## Method

Every palette change goes through the Personalize sheet's own controls by UIA pattern (Seed edit + Apply, a Strategy chip, the Base mode ComboBox by `ExpandCollapse`/`SelectionItem`, the Contrast and Card Opacity sliders by `RangeValue`), never keystrokes; the sheet is closed with `SettingsSheetCloseButton` before every screenshot because an open sheet scrims the canvas to 80 % (the first samples came back at exactly 0.8× and that is why). Screenshots by `scripts/ui-snapshot.ps1 -Screenshot -Tree`; sample rects derive from the UIA tree's title and value Text rects, never by eye:

- **body** — a 22×10 strip on the card's top row right of the title; **plate** — a 5-px strip inside the value plate's left padding; **legend swatches** — the 8×8 interior of the 9×9 Borders 13 px left of the legend labels on the dual-metric card; each read as the rect's modal colour.
- **title ink** — the most frequent colour among the title rect's pixels far (Σ|Δ| ≥ 120) from the rect's dominant colour: the fully covered glyph core, which is exact; the halo and antialiased edges are rarer.
- Expected values: `McuScheme.Build(seed, variant, dark, 0)` through the spec-A oracle for the roles the spec assigns (`<F>Container`, `on<F>Container`, series roles), compared bit-exact where the paint is opaque (opacity 1 bodies, plates on an opaque body, legend swatches, glyph cores) and described where it is a blend by design (bodies at opacity 0 and 0.5).

Cards sampled, one per family plus one preset: **RAM Used** (`RamUsedGb` → Secondary), **CPU Die (average)** (`CpuTempC` → Primary, dual-metric with Core5), **VDDCR_SOC** (`VoltageV` → Tertiary), **Samsung 980 PRO** (Sunset preset, title `#BF360C`). Scripts (outside the repo, copies in `.superpowers/sdd/2026-09-12-sensor-cards/eyes/`): `set-palette.ps1`, `cards-sample.ps1`, `cards-eyes.ps1`; screenshots and the raw log under `%TEMP%\remex-ui\cards\` (`cards-<cell>-o<opacity>.png`, `eyes-log.txt`).

## Results

Grid: `#6750A4` Tonal Spot and `#0061A4` Expressive × Light/Dark × Card Opacity {0, 0.5, 1}, plus Monochrome Dark at 1. Opacity-1 rows are the bit-exact ones.

| Cell | Card (family) | Body | Ink (glyph core) | Series 1 / 2 (legend) |
|---|---|---|---|---|
| A · Tonal Spot Light · 1 | RAM Used (S) | `#E8DEF8` = secondaryContainer | `#4A4458` = onSecondaryContainer | — |
| | CPU Die (P) | `#E9DDFF` = primaryContainer | `#4D3D75` = onPrimaryContainer | `#65558F` primary / `#7E5260` **tertiary** |
| | VDDCR_SOC (T) | `#FFD9E3` = tertiaryContainer | `#633B48` = onTertiaryContainer | — |
| | Samsung 980 (Sunset) | `#F2ECF4` = surfaceContainer (`CardBackgroundBrush`, unchanged) | `#BF360C` = preset LabelColor | — |
| B · Tonal Spot Dark · 1 | RAM Used (S) | `#4A4458` | `#E8DEF8` | — |
| | CPU Die (P) | `#4D3D75` | `#E9DDFF` | `#CFBDFE` / `#EFB8C8` (tertiary) |
| | VDDCR_SOC (T) | `#633B48` | `#FFD9E3` | — |
| | Samsung 980 (Sunset) | `#211F24` = surfaceContainer | `#BF360C` | — |
| C · Expressive Light · 1 | RAM Used (S) | `#FFD8ED` | `#5F3C52` | — |
| | CPU Die (P) | `#C7EF9F` | `#2F4F11` | `#466727` / `#79536A` (**secondary** — distinct here) |
| | VDDCR_SOC (T) | `#D9E2FF` | `#334671` | — |
| | Samsung 980 (Sunset) | `#EDEDF7` | `#BF360C` | — |
| D · Expressive Dark · 1 | RAM Used (S) | `#5F3C52` | `#FFD8ED` | — |
| | CPU Die (P) | `#2F4F11` | `#C7EF9F` | `#ABD285` / `#E8B9D4` (secondary) |
| | VDDCR_SOC (T) | `#334671` | `#D9E2FF` | — |
| | Samsung 980 (Sunset) | `#1D1F27` | `#BF360C` | — |
| E · Monochrome Dark · 1 | RAM Used (S) | `#474747` | `#E2E2E2` | — |
| | CPU Die (P) | `#D4D4D4` = primaryContainer (tone 85 — Google's Monochrome dark pairs a tone-100 primary with it) | `#000000` | `#FFFFFF` / `#C6C6C6` (secondary, tone 80 — distinct by 20 tones, no grey fallback needed) |
| | VDDCR_SOC (T) | `#919191` | `#000000` | — |
| | Samsung 980 (Sunset) | `#1F1F1F` | `#BF360C` | — |

Every opacity-1 body, ink and swatch above equals the port's role value exactly (16 bodies, 16 inks, 10 swatches, 0 mismatches). At opacity 0 the body sample is the wallpaper in every cell (e.g. A: `#A7C082`, B: wallpaper greens) and at 0.5 a blend (A RAM Used `#CACEC5`, CPU Die `#D0CAC1`), while the inks, plates and legend swatches read the same exact values as at 1 — the slider moves the glass only, as specified. The Sunset card's body is `CardBackgroundBrush` in every cell and its title `#BF360C` in every cell.

**Home pinned tiles** (Tonal Spot Dark, opacity 1): the *Average Effective Clock* and *VDDCR_SOC* tiles (both Tertiary) sample `#633B48` = tertiaryContainer, the same family the *Average Effective Clock* dual card shows on the canvas; the preset-overridden tiles (Total CPU Utility, Vcore/VTT/DRAM, the temperatures) sample `#211F24` = surfaceContainer with their preset inks, unchanged.

**Backdrop pill** (Tonal Spot Dark, opacity 0, Backdrop behind titles on): RAM Used pill `#4A4E59`-ish = secondaryContainer at 85 % over the wallpaper with ink `#E8DEF8`; CPU Die pill `#4B3C6D` (primaryContainer 85 %) with ink `#E9DDFF`; VDDCR_SOC pill `#6E4A51` with ink `#FFD9E3`/`#F6D1DA` — legible on a mid-green/yellow wallpaper with the glass fully clear.

**Tray strip**: not sampled here (spec D redesigns the strip); the class binding is the one-line change reviewed in .3.2.

## Decision

**PASS.** Acceptance 1 (family colours follow the palette and the variant, live, without restart — every cell was switched through the sheet on the running app), 2 (opacity 0 clear with ink intact, 1 solid Container, popup floor untouched — pinned by `SensorCardOpacityTests`), 3 (two series never share a colour — the grid guard runs 8 seeds × 9 variants × 2 modes × 4 families; on screen: tertiary under Tonal Spot, secondary under Expressive and Monochrome), 4 (the Sunset card pixel-identical: `#BF360C` title, `CardBackgroundBrush` body, preset plates in every cell), 5 (Home tiles on the family; the phone unchanged). No value was adjusted by eye.

Observed, not a defect: Google's Monochrome dark scheme puts a tone-100 primary on a tone-85 primaryContainer, so the CPU card's white line sits on a light-grey card there — that is M3 Monochrome's own pairing, the same the phone shows.

## Eyes pass

Looked at: `cards-A…-o0/o50/o100`, `cards-B…-o0/o50/o100`, `cards-C…`, `cards-D…`, `cards-E…-o100`, `home-tonalspot-dark-o100.png`, `cards-B-6750A4-tonalspot-dark-o0-backdrop.png`.

1. Tonal Spot Light, 0 → 0.5 → 1: the three themed families read as lilac (CPU), lavender-grey (RAM) and rose (VDDCR_SOC) tints on the wallpaper at 0.5 and as solid pastel cards at 1, with dark ink; the Sunset card next to them keeps its navy plates and orange title throughout.
2. Tonal Spot Dark: the same three as deep violet / grey-violet / plum cards at 1, pale ink; CPU Die's two lines lilac + pink (tertiary), legend swatches the same two.
3. Expressive Light / Dark: CPU moss-green, RAM pink, VDDCR_SOC blue — the categories are told apart at a glance; CPU Die's second line is the pink secondary here because Expressive's secondary is a different hue.
4. Monochrome Dark: greys only, series white + light grey on the light-grey CPU card; RAM and VDDCR_SOC cards dark grey with light ink.
5. Home: the two Tertiary tiles are the same plum as the canvas's Tertiary cards; preset tiles unchanged.
6. Backdrop on at opacity 0: dark family-tinted pills under every themed title, presets' own pills under theirs; nothing unreadable over the wallpaper.
7. Restored afterwards: `#579400` Vibrant Dark contrast 0, Card Opacity 0.1585, Backdrop off, Sensors page.
