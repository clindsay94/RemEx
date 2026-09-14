# MEASURE: Personalize as five tabs, the palette chips, the Layout move (RemEx-4kv0g.4.4)

Date: 2026-09-14 · Branch `feat/personalize-tabs` at `81ff7621` (this doc and the CHANGELOG are the only later changes; the first half of the pass ran on `b2e52c09`, which is where the chip-width finding came from) · PC: Release publish via `scripts/update-local-install.ps1`, `C:\Program Files\RemEx\Remex.Agent.exe`, main window parked at 2668,100 1584×992 on the second monitor, sheet 440 wide · Connor's live profile: seed `#0061A4` (hue 254, chroma 48.34), variant Fidelity, contrast −0.5039, Dark, Card Opacity 0.16875, corner radius 32, body font GoogleSansCode Nerd Font (monospace) · 11 Home pins, 8 built-in presets, 2 user palettes, 44 launcher entries.

## Method

Everything through UIA patterns and synthetic mouse — no keystrokes: the gear FAB (`GearFab`, InvokePattern) opens the sheet, `TabItem`s are switched with SelectionItemPattern (`uia.ps1 -Click '<tab>'`), the Colour tab's Base-mode `ComboBox` with ExpandCollapse + SelectionItem (`mode.ps1`), sliders with RangeValuePattern (`range.ps1`), the Snap switch with TogglePattern, scrolling with the wheel over the sheet. Screenshots + UIA trees by `scripts/ui-snapshot.ps1 -Screenshot -Tree`; chip colours sampled from the screenshots at fixed fractions of each chip button's UIA rect (primary field 15–20 % / 78 %, secondary 85 % / 22 %, tertiary 85 % / 78 %) with `sample-rect.ps1`, compared against `McuOracle roles 0061A4 <variant> <mode> -0.5038607120513916 primary secondary tertiary`. Persistence read from `%LOCALAPPDATA%\Remex\dashboard_layout.json`. Copies of scripts and captures: `.superpowers/sdd/2026-09-13-personalize-tabs/eyes/`.

**Caveat that shaped the pass:** Connor was playing a (windowed) game on the first monitor. A UIA Select/SetValue hands RemEx keyboard focus, and the game's keypresses then land in the app — the sheet twice jumped tabs on its own right after an automation call, once scrolled to a checklist, and the Flyout apps list ended the night with more ticks than it started with. None of that is a code fault; it is a rule for future passes (`~/.claude/projects/Z--RemEx/memory/feedback-no-ui-driving-while-gaming.md`). The measurements below were all taken from screenshots and file reads, which the stray input cannot fake.

## Results

**Tabs.** UIA tree of the open sheet: `Tab` with five `TabItem`s `Colour · Palettes · Surfaces · Text · Layout` at y=308, 90 px each (3828→4238, inside the 440 sheet, no scrolling strip). The strip stays at y=308 while a tab's own `ScrollViewer` scrolls (Colour tab wheeled 12 notches: chips moved from y=507 to the top, the strip did not). First open after a clean restart lands on Colour; close (`SettingsSheetCloseButton`) and reopen lands on the tab that was showing (Layout → Layout, Colour → Colour). The Reset footer sits under the tab content (bottom of the sheet on long tabs, directly under the card on the short Surfaces tab — the host Grid sizes to content under the `MaxHeight`, it does not stretch to it). Nothing runs off the window at 992 px: the Colour tab's scrollbar foot and the Reset button are inside the sheet (`c-colour-sheet.png`), which is the invariant the `Padding`-not-`Margin` fix and the new guard entry protect.

**Contents** (by UIA key/name, per screenshot): Colour — Base mode, Source, wheel, Hue, Seed/Apply, Recent, Vibrancy, Contrast, Strategy chips, Preview ramps. Palettes — Built-in (8 chips), Your palettes (2 cards: chip + rename box + delete), Palette name + Save current, Copy as AXAML / Export as JSON / Import JSON. Surfaces — Background Mode, Card Opacity, Corner Radius, Glow Strength (wallpaper rows and Window opacity are hidden by their existing `IsVisible` guards in Acrylic mode). Text — Headers / Body / Small text / Sensor text with Bold switches, Backdrop behind titles, Text shadow + Strength, Reset text to defaults, FONTS: Page Title Font, Content Font. Layout — Snap to Grid, Grid Size, PINNED SENSORS checklist, UI Size, FLYOUT (Popup opacity, Cards, Buttons, Apps), BEHAVIOUR (Splash Screen, Reduced motion).

**Chips, Dark (Fidelity selected, contrast −0.5039)** — 9 variants × 3 regions, all bit-equal to the oracle:

| Variant | primary / secondary / tertiary (screen = oracle) |
|---|---|
| Tonal Spot | `#6E97C7` / `#8895A7` / `#A28CB0` |
| Expressive | `#7A9F57` / `#B287A0` / `#8193C4` |
| Fruit Salad | `#00A4AE` / `#4BA0A7` / `#6E97C7` |
| Rainbow | `#5598DD` / `#8895A7` / `#A28CB0` |
| Vibrant | `#0098FD` / `#8793B6` / `#8E90C4` |
| Neutral | `#8C94A2` / `#90949C` / `#8895A7` |
| Monochrome | `#949494` / `#949494` / `#949494` |
| Fidelity | `#5498DE` / `#7F96B4` / `#D67E37` |
| Content | `#5498DE` / `#7F96B4` / `#B87ECF` |

Ink black on all nine (tone ≥ 50). Selected chip (Fidelity) carries one accent ring — the tile button's — and the same size as its neighbours. Preset chips: Glass / Neon / Ember / Slate / Daybreak / Voltage / Sorbet / Dynamic with their own fills; Daybreak (selected, dark primary) takes white ink, the light ones black.

**Chips, Light** (Base mode → Light through the combo; `d-variant-chips-light.png`): 27/27 bit-equal again — Tonal Spot `#446E9C`/`#606C7D`/`#786386`, Fidelity `#206EB2`/`#566D89`/`#A55710`, Monochrome `#6B6B6B`×3, etc. — and the ink flips to white on every chip (tone 40). Back to Dark afterwards; the file read `themeMode: Dark` after the debounce.

**Chip width — the finding.** On `b2e52c09` (three per row, 104 wide) every name longer than five characters broke mid-word: "Expre/ssive", "Monoc/hrome", "Daybr/eak", "Volta/ge" (`c-variant-chips.png`, `c-preset-chips.png`). Cause: the label inherits Connor's monospace body font, ≈7.8 px per character at 13 px bold, so "Monochrome" is 78 px against a 44 px label area. Connor chose two chips per row over smaller type or a name below the fill; on `81ff7621` the chip buttons measure 173×64 (chip ≈155, label area ≈80 px) and every variant and preset name sits on one line (`d-variant-chips.png`, `d-palettes-sheet.png`); "limey wimey" wraps at its space.

**Layout tab → file.** Snap to Grid toggled off → `isSnapToGridEnabled: false`, on → `true`; Grid Size 50 → 70 → `gridSize: 70`, → 50 → `50` (each read after the 3 s debounce); `pinnedSensorIds` stayed at 11 throughout and `glassOpacity` untouched by any of it (`CurrentProfile`-based saves). Pin toggling was not exercised live — it would have changed Connor's Home — `LayoutSettingsViewModelTests` cover add / remove / the canvas-made-pin case.

**Settings page** (`d-settings2.png/.txt`): headers CONNECTION · GENERAL · Sensor Alerts · BACKUP & RESTORE · SHARED FOLDERS · FILE-SHARING TRUST · HELP; zero matches for Snap / Grid Size / Pinned.

## Decision

**PASS** at `81ff7621`. Acceptance 1 (five tabs in order, strip fixed while content scrolls, nothing off-window, tab remembered), 2 (every control present with its bindings — the review's scripted attribute diff, and this pass by eye), 3 (chips: geometry C, fills bit-equal 54/54 across Dark and Light, ink by tone, one ring; width per Connor's amendment), 4 (Snap / Grid work from the Layout tab and persist through `CurrentProfile`; gone from Settings), 5 (nine resx, gate PASS, guard entry, this doc), 6 (host 47 lines, tab files ≤ 236).

Observed, not fixed: the Reset footer's position depends on tab length (under the card on Surfaces, at the sheet foot elsewhere); user-palette cards show the name on the chip and in the rename box beneath. Both are cosmetic and go to the carried-minors bead (RemEx-4kv0g.17).

## Eyes pass

Looked at `c-firstopen-sheet.png`, `c-Palettes/Surfaces/Text/Layout-sheet.png`, `d-variant-chips.png`, `d-palettes-sheet.png`, `d-variant-chips-light.png`, `d-settings2.png`:

1. The sheet: title, subtitle, the Language card, then five tabs on one line; the Colour tab a single card column — Base mode on top, the wheel, hue, seed, recents, vibrancy, contrast, the chips, the ramps.
2. Chips as pills (Connor's radius 32): primary field with the name, the two accents stacked on the right; the selected one ringed in the accent; in Light the fills darken and the names go white.
3. Palettes: eight built-ins two per row, the two user palettes as cards with their rename box and delete cross under the chip; name field, Save current, the three exchange buttons; Reset at the foot.
4. Surfaces is short — four rows and the Reset right under them; Text is the RemEx-jt6w5 block with FONTS beneath; Layout is Snap / Grid / the pinned checklist in its own scroll, UI Size, then FLYOUT and BEHAVIOUR as sub-headed groups.
5. Settings without its Layout and Pinned sensors sections; Connection / General / Alerts / Backup / Shared folders / Trust / Help in place.
6. Restored afterwards: Dark, snap on, grid 50, sheet closed, drawer collapsed, Home view. The three-then-seven ticked Flyout apps and the blue Fidelity theme are Connor's to confirm (see Method).
