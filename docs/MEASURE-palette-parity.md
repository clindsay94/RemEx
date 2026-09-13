# MEASURE: palette parity, PC vs phone (RemEx-4kv0g.14)

Date: 2026-09-12 · Branch `feat/palette-parity` at `e70c63dd` (this doc, `AGENTS.md` and the CHANGELOG are the only uncommitted changes at capture time) · PC: Release publish via `scripts/update-local-install.ps1` at 17:24, `C:\Program Files\RemEx\Remex.Agent.exe`, window parked at 2668,100 1584×992 on the second monitor · Phone: Samsung SM-S948U1 (Galaxy S26 Ultra), Android 17 (SDK 37), 1440×3120, `build-remex.ps1 -t android -NoClean` → `build_output/android/RemEx-V2.5.0-release.apk` (versionCode 37) installed with `adb install -r` at 17:23:58 over wireless adb.

## Method

Nine cells: seed `#6750A4` in Tonal Spot and seed `#0061A4` in Expressive, each × {Light, Dark} × {contrast 0, contrast +1}, plus the Match-phone tuple `MatchPhoneParityTests` pins (`#0061A4`, Fidelity, Dark, −0.5). Connor set every cell on the phone by hand (Personalize → Palette = Custom, hex typed, style chip, Display Mode, contrast slider), then per cell:

1. **Phone.** `adb exec-out screencap -p` + `adb shell uiautomator dump`. The sampled elements come from the dump's bounds, never by eye: the checked *Display Mode* ToggleButton (M3 `checkedContainerColor` = `primary`), the checked *Dynamic Palette Strategy* FilterChip (`selectedContainerColor` = `secondaryContainer`), and the scaffold's left margin (`Scaffold` `containerColor` = `background`, which MCU derives identically to `surface`). The phone paints nothing with `error` on Personalize; it was sampled once, on the Match-phone cell, from the Task Manager's *Kill process* confirmation button (`containerColor = colorScheme.error`), caught with a burst of screencaps because the dialog closes on the next list refresh.
2. **PC.** UIA `InvokePattern` on *Match my phone* (never keystrokes), then `scripts/ui-snapshot.ps1 -Screenshot -Tree`. The three *Preview* pair swatches (`Primary / On Primary`, `Surface / On Surface`, `Error / On Error`: 76×44 Borders bound to `TonalRamp.PrimaryBrush` / `SurfaceBrush` / `ErrorBrush`, i.e. `DynamicColorGenerator.Generate(...).Primary/Surface/Error`) are located from the tree and read as the **modal colour of the swatch rect** — the labels carry the text-halo from the Text epic, so a single corner pixel is not reliable but the mode is. The tuple the PC adopted is read back from `%LOCALAPPDATA%\RemEx\last-seed.json` (seed, variant, mode, contrast) and cross-checked against the sheet's Source / Base mode / Contrast readouts in the UIA tree.
3. **Expected values.** `McuScheme.Build(seed, variant, isDark, contrast)` for the tuple the PC adopted, run through a 40-line console oracle referencing `remex.core` (the port is bit-exact against Google's classes on the 720-tuple grid, `McuVectorTests`). When the adopted seed is a grid seed the committed `mcu-vectors.json` row is cited as well.
4. **Compare.** PC pixels must equal the sRGB hexes exactly. Phone pixels must equal the same hexes **composited into Display P3** (see *Two things the brief did not anticipate*), exactly, except that one channel may sit on the other side of a quantisation boundary when the P3 value's fraction is within 0.1 of .5 — that case is called out in the table, everything else is a mismatch.

Scripts (outside the repo, kept with the SDD workspace): `%TEMP%\remex-ui\eyes\{cell,phone-cell,pc-cell,sample-pixels,sample-rect,sample-icc,compose}.ps1`, the oracle at `%USERPROFILE%\.claude\jobs\ea928646\tmp\McuOracle\` (`roles`, `hct`, `seed`, `p3`, `unp3`), copies under `.superpowers/sdd/2026-09-12-palette-parity/eyes/`. Screenshots: `%TEMP%\remex-ui\eyes\phone-<cell>.png` (raw, P3-tagged), `phone-<cell>.srgb.png` (ICC-converted for viewing), `pc-<cell>.png`, `side-<cell>.png` (the side-by-side composites that were looked at).

### Two things the brief did not anticipate

- **The phone does not paint with the typed hex.** With Palette = Custom the effective seed is `Hct.from(hue(hex), chromaSlider, tone(hex))` (`Theme.kt:547-556`), and the same value is what `ThemeSyncSeedResolver.staticSeedHex` puts on the wire, so Match my phone adopts the *effective* seed and the two sides agree by construction. For `#6750A4` with the chroma slider at 120 that is `#6F1AFF` (the most chromatic in-gamut colour at hue 299 / tone 40, real chroma 87.4 — hence the PC's Vibrancy reading 87 while the phone's slider says 120, RemEx-4kv0g.15). Tonal Spot's palettes have fixed chroma, so `#6F1AFF` and `#6750A4` produce identical roles and the grid row still applies; for the `#0061A4` cells the slider happened to be at the seed's own chroma and the hex came over unchanged. The table records the adopted seed, not the typed one.
- **The phone's screencap is Display P3, not sRGB.** SurfaceFlinger reports `Current color mode: ColorMode::DISPLAY_P3` on this panel regardless of the Screen-mode setting (Natural, Adaptive colour tone on or off — all tried), and `screencap` returns the composited buffer with Skia's P3 ICC profile embedded. Every phone pixel was therefore a few units off the sRGB hex (`#FDF7FF` read `#FCF7FE`, `#65558F` read `#62568B`). Converting the expected sRGB hexes *forward* with the textbook sRGB→P3 matrices (double precision, round-to-nearest) reproduces the phone's bytes exactly in 27 of the 28 phone samples, and the 28th is a .51 fraction. The inverse direction is lossy (`#62568B` → `#64558F`), so the comparison is done in P3. `sample-icc.ps1` also decodes the PNG through its embedded profile with WIC for the composites — that path is ±1 and is used for looking, not for the numbers.

## Results

`sRGB` is the port's role value for the adopted tuple (= the PC's expected pixel); `P3` is that value composited into Display P3 (= the phone's expected pixel). *Grid row* names the committed vector for the nearest grid tuple and whether it equals the sRGB column.

| Cell (adopted tuple) | Role | sRGB | PC | P3 | Phone | Match |
|---|---|---|---|---|---|---|
| 1 · `#6F1AFF` tonal_spot light c=0.0027 | primary | `#65558F` | `#65558F` | `#62568B` | `#62568B` | yes |
| | surface | `#FDF7FF` | `#FDF7FF` | `#FCF7FE` | `#FCF7FE` | yes |
| | error | `#BA1A1A` | `#BA1A1A` | `#AB2D25` | — | yes (PC) |
| | secondaryContainer | `#E8DEF8` | — | `#E6DEF6` | `#E6DEF6` | yes |
| | grid row `#6750A4` tonal_spot light 0: `#65558F` / `#FDF7FF` / `#BA1A1A` | | | | | equal |
| 2 · `#6F1AFF` tonal_spot light c=1 | primary | `#312259` | `#312259` | `#2F2356` | `#2F2356` | yes |
| | surface | `#FDF7FF` | `#FDF7FF` | `#FCF7FE` | `#FCF7FE` | yes |
| | error | `#600004` | `#600004` | `#570D0A` | — | yes (PC) |
| | secondaryContainer | `#4C465B` | — | `#4B4659` | `#4B4659` | yes |
| | grid row `#6750A4` tonal_spot light 1: `#312259` / `#FDF7FF` / `#600004` | | | | | equal |
| 3 · `#6F1AFF` tonal_spot dark c=0.0008 | primary | `#CFBDFE` | `#CFBDFE` | `#CCBEF9` | `#CCBEF9` | yes |
| | surface | `#141218` | `#141218` | `#141218` | `#141218` | yes |
| | error | `#FFB4AB` | `#FFB4AB` | `#F4B7AE` | — | yes (PC) |
| | secondaryContainer | `#4A4458` | — | `#494457` | `#494456` | yes — B channel 86.51 in P3, phone rounded down |
| | grid row `#6750A4` tonal_spot dark 0: `#CFBDFE` / `#141218` / `#FFB4AB` | | | | | equal |
| 4 · `#6F1AFF` tonal_spot dark c=1 | primary | `#F5EDFF` | `#F5EDFF` | `#F4EDFE` | `#F4EDFE` | yes |
| | surface | `#141218` | `#141218` | `#141218` | `#141218` | yes |
| | error | `#FFECE9` | `#FFECE9` | `#FCEDEA` | — | yes (PC) |
| | secondaryContainer | `#C8BFD7` | — | `#C6BFD5` | `#C6BFD5` | yes |
| | grid row `#6750A4` tonal_spot dark 1: `#F5EDFF` / `#141218` / `#FFECE9` | | | | | equal |
| 5 · `#0061A4` expressive light c=0.0045 | primary | `#466727` | `#466727` | `#4D6630` | `#4D6630` | yes |
| | surface | `#FAF8FF` | `#FAF8FF` | `#FAF8FE` | `#FAF8FE` | yes |
| | error | `#BA1A1A` | `#BA1A1A` | `#AB2D25` | — | yes (PC) |
| | secondaryContainer | `#FFD8ED` | — | `#F9D9EC` | `#F9D9EC` | yes |
| | grid row `#0061A4` expressive light 0: `#466727` / `#FAF8FF` / `#BA1A1A` | | | | | equal |
| 6 · `#0061A4` expressive light c=1 | primary | `#183300` | `#183300` | `#1F3208` | `#1F3208` | yes |
| | surface | `#FAF8FF` | `#FAF8FF` | `#FAF8FE` | `#FAF8FE` | yes |
| | error | `#600004` | `#600004` | `#570D0A` | — | yes (PC) |
| | secondaryContainer | `#613E54` | — | `#5C4053` | `#5C4053` | yes |
| | grid row `#0061A4` expressive light 1: `#183300` / `#FAF8FF` / `#600004` | | | | | equal |
| 7 · `#0061A4` expressive dark c=0 | primary | `#ABD285` | `#ABD285` | `#B3D18D` | `#B3D18D` | yes |
| | surface | `#11131A` | `#11131A` | `#111319` | `#111319` | yes |
| | error | `#FFB4AB` | `#FFB4AB` | `#F4B7AE` | — | yes (PC) |
| | secondaryContainer | `#5F3C52` | — | `#5A3E51` | `#5A3E51` | yes |
| | grid row `#0061A4` expressive dark 0: `#ABD285` / `#11131A` / `#FFB4AB` | | | | | equal |
| 8 · `#0061A4` expressive dark c=1 | primary | `#D4FDAB` | `#D4FDAB` | `#DCFCB3` | `#DCFCB3` | yes |
| | surface | `#11131A` | `#11131A` | `#111319` | `#111319` | yes |
| | error | `#FFECE9` | `#FFECE9` | `#FCEDEA` | — | yes (PC) |
| | secondaryContainer | `#E4B5D0` | — | `#DDB7CF` | `#DDB7CF` | yes |
| | grid row `#0061A4` expressive dark 1: `#D4FDAB` / `#11131A` / `#FFECE9` | | | | | equal |
| 9 · Match my phone: `#0061A4` fidelity dark c=−0.5039 | primary | `#5498DE` | `#5498DE` | `#6496D8` | `#6496D8` | yes |
| | surface | `#111418` | `#111418` | `#121418` | `#121418` | yes |
| | error | `#FF5C50` | `#FF5C50` | `#EC6758` | `#EC6758` (Kill button) | yes |
| | secondaryContainer | `#253C55` | — | `#2A3B53` | `#2A3B53` | yes |
| | grid row `#0061A4` fidelity dark −0.5: `#5598DE` / `#111418` / `#FF5C50` | | | | | primary differs by one unit because the slider landed on −0.5039, not −0.5; the port at −0.5039 is what both sides show |

28 phone samples, 27 PC samples, 0 mismatches. The contrast values are what the phone's continuous slider produced (RemEx-4kv0g.16 asks for a detent at 0); they were taken as-is because the wire carries them as-is, and the roles are computed for the value actually received.

## Decision

**PASS.** Every PC swatch equals the port's value for the tuple the phone sent, every phone pixel equals the same value composited into the panel's colour space, and where the adopted tuple is a grid tuple the committed vector row equals both (cell 9's contrast is −0.5039, one slider-width off the −0.5 row, so there the row is one unit away in primary and the port at the received value is what both sides show). No cell needed either side "matched by eye"; nothing was adjusted. Follow-ups filed, neither a colour defect: RemEx-4kv0g.15 (Vibrancy readback after Match my phone: requested vs achievable chroma), RemEx-4kv0g.16 (contrast slider detent at 0 on both platforms).

## Eyes pass

Composites `side-<cell>.png` (phone ICC-converted and scaled to the PC window's height, PC window to its right with the Personalize sheet open on the Preview section), every one looked at:

1. `#6750A4` Tonal Spot Light c0 — indistinguishable: the phone's checked *Light* pill and the PC's *Primary* swatch are the same mid purple, the phone scaffold and the PC sheet the same lavender-white, the checked *Tonal Spot* chip and the PC's Tonal Spot chip dots the same pale lilac.
2. `#6750A4` Tonal Spot Light c+1 — indistinguishable: primary goes to the same deep indigo on both, the chip's container to the same dark mauve, surface unchanged on both.
3. `#6750A4` Tonal Spot Dark c0 — indistinguishable: same pale-lavender primary pill/swatch, same near-black surface, the PC's app-launcher tiles under the sheet tinted the same violet as the phone's cards.
4. `#6750A4` Tonal Spot Dark c+1 — indistinguishable: primary lifts to the same near-white lilac on both, chip container to the same light grey-violet.
5. `#0061A4` Expressive Light c0 — indistinguishable: Expressive rotates the seed's blue to a moss green primary and a pink secondary container on both sides at once; the PC's Expressive chip dots (green / pink / blue) are the phone's chip and toggle colours.
6. `#0061A4` Expressive Light c+1 — indistinguishable: same deep forest primary, same dark plum chip, same off-white surface.
7. `#0061A4` Expressive Dark c0 — indistinguishable: same sage-green primary, same dusty-pink chip, same blue-black surface.
8. `#0061A4` Expressive Dark c+1 — indistinguishable: same pale lime primary, same pink chip, same surface.
9. Match my phone (`#0061A4` Fidelity Dark −0.5) — indistinguishable: same steel-blue primary on the phone's *Dark* pill and the PC's swatch, same navy chip, and the reduced-contrast coral `error` on the PC's swatch is the phone's Kill button.

Not exercised: the phone's `error` on the eight grid cells (it is not painted on Personalize; `ThemeParityTest` pins it for every tuple on the JVM, and the live Kill-button sample on cell 9 confirms the paint path), and light-mode `error` on the phone for the same reason.
