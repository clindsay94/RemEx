# MEASURE: the tray flyout's cards are the canvas cards (RemEx-4kv0g.18.3)

Date: 2026-09-13 · Branch `feat/flyout-cards` at `2e406fd4` (this doc and the CHANGELOG are the only later changes) · PC: Release publish via `scripts/update-local-install.ps1`, `C:\Program Files\RemEx\Remex.Agent.exe`, main window parked at 2668,100 1584×992 on the second monitor · Connor's live dashboard (44 cards: 7 follow the theme, 37 carry presets — restored earlier that day, see `docs/REGRESSION-GUARDS.md` "Dashboard layout — card colour presets") · Home pins: 11 sensors (Average Effective Clock, Total CPU Utility, Vcore, VTT, VDDCR_SOC, DRAM, VDD_MISC, RAM · SPD Hub Temperature, GPU Temperature, GPU Hot Spot, GPU Power) · Theme `#579400` Vibrant Dark, contrast 0.

## Method

The flyout cannot be opened without a keystroke or a tray-icon click on this machine (the taskbar mod exposes no accessible tray icons, even to the COM UIA API), so Connor clicked the icon; everything after that went through the popup's own controls by UIA pattern (`Pin or unpin the RemEx window`, `Close`) and synthetic mouse drags on its resize grips — no keystrokes. Screenshots by `scripts/ui-snapshot.ps1 -Screenshot -Tree -WindowTitle 'Live Glance'`; sample rects derived from the UIA tree's title/value/legend Text rects with the spec-B sampler generalised to a window title (`win-sample.ps1`, copies under `.superpowers/sdd/2026-09-13-flyout-cards/eyes/`), the same four sensors sampled in the flyout and on the canvas at Card Opacity 1 (set through the main window's Personalize sheet, closed before every screenshot).

## Results

**Colours, flyout vs canvas, Card Opacity 1** (pinned popup 894 wide; canvas on Sensors; both windows sampled within the same minute):

| Sensor | Part | Flyout | Canvas | Role / preset |
|---|---|---|---|---|
| VDDCR_SOC (Tertiary, themed) | body | `#155036` | `#155036` | tertiaryContainer |
| | title ink | `#B4F0CC` | `#B4F0CC` | onTertiaryContainer |
| Average Effective Clock (Tertiary, themed, dual) | title ink | `#B4F0CC` | `#B4F0CC` | onTertiaryContainer |
| | legend 1 / 2 | `#98D3B1` / `#84DD00` | `#98D3B1` / `#84DD00` | tertiary / primary (the distance rule's next-family) |
| VTT (Smoke & Gold preset) | body | `#1C2116` | `#1C2116` | `CardBackgroundBrush` (surfaceContainer) |
| | title ink | `#9E9E9E` | `#9E9E9E` | preset LabelColor |
| GPU Hot Spot (Sunset preset) | title ink | `#BF360C` | `#BF360C` | preset LabelColor |

8 of 8 pairs bit-equal. The flyout's card is `SensorCardContent` with the family host classes on a `Border.flyout-card`; the canvas's is the same control inside `DraggableCard` — one visual, two hosts, and the numbers say so.

**Layout** (measured from the UIA tree and the screenshots):

| State | Window | Columns | Rows visible | Scroll | Height ≤ 800 |
|---|---|---|---|---|---|
| Transient, default width (`2e406fd4`) | 528×756 above the tray | 2 | 3 (+ scrollbar for 11 pins) | yes | yes (756) |
| Pinned, dragged to the max | 944×800 | 4 | 3 — all 11 cards, no scrollbar | no | — |
| Pinned, 644 wide (re-wrap check) | 644×787 | 2 | — | yes | — |
| Transient, default width (`2f84ebf6`, before fix round 2) | 420×756 | **1** | 3 | yes | yes |
| Pinned 894×787 (before fix round 2) | 894×787 | 3 | 3 of 4, **capped at 486** | yes | — |

The last two rows are what this pass found and fix round 2 corrected: the popup's content width is the window minus 56 (outer Border margin 12 + `ContentGrid` margin 16, each side) and the scrollbar, so two 212-px card columns need a 528-px window and four need 944 — the spec's "2 at 420, 4 at 900" was wrong by the chrome; and the transient cap (`TrayFlyoutGeometry.CardsMaxHeight`) was applying in pinned mode too, leaving rows hidden behind a scrollbar with empty space under the tiles. `TrayFlyoutGeometryTests` now pin both column promises by arithmetic (`DefaultWidthFitsTwoCardColumns`, `MaxWidthFitsFourCardColumns`) and `ApplyMode` lifts the cap when pinned.

## Decision

**PASS** at `2e406fd4`. Acceptance 2 (200×150 cards in a wrapping grid, columns by width — 2 at the default, 4 at the max — vertical scroll when they do not fit, capped in transient mode only), 3 (family classes on the flyout host; flyout and canvas bit-equal for themed and preset sensors), 4 (transient height 756 ≤ 800; pinned geometry restored from `C:\ProgramData\RemEx\tray_flyout_layout.json` — the store lives there, not per-user; `TrayFlyoutLayoutStoreTests` / `TrayFlyoutGeometryTests` green). Acceptance 1 (the canvas pixel-identical after the extraction) was closed in .18.1's fix round: the four spec-B cell-B cards including the Sunset card reproduce exactly on the extracted control.

Observed, for spec D2: the popup's own glass stays `GlassBaseDarkBrush` and the tiles stay labelled buttons — both are D2's scope; the pinned `MinHeight` (240) is below the chrome height (RemEx-4kv0g.18.4).

## Eyes pass

Looked at `flyout-transient-528.png`, `flyout-pinned-wide.png`, `flyout-pinned894-o100.png`, `flyout-after-narrow.png`:

1. Transient at the default size: two canvas-size cards per row, the dual-metric Effective Clock card with its legend and both value plates, Total CPU Utility in Magenta & Cyan, the voltages in Smoke & Gold, VDDCR_SOC on the tertiary tint at the app's 0.16 opacity; a scrollbar on the right; the six tiles 4+2 below; the whole popup sitting above the clock.
2. Pinned and widened to the max: four columns, all eleven pins on one screen — the Sunset temperatures, the Monochrome GPU Temp, the Magenta GPU Power — with free space under the last row rather than a hidden fourth row.
3. Re-wrap: dragging the pinned popup narrower re-flows the grid to two columns live.
4. The previous strip (name / value / 44×16 sparkline) is gone; nothing else in the popup moved — header, presence badge, gear, pin and close where they were.
5. Restored afterwards: popup closed, its stored geometry back to Connor's `pinned 706×334 @ 1845,1101`, Card Opacity 0.1585, main window on Sensors.
