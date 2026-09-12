# MEASURE: text halo cost (RemEx-jt6w5.6)

Date: 2026-09-11 · Machine: AMD Ryzen 7 9800X3D (16 logical cores), AMD GPU (amdfendrsr/AmdPpkgSvc driver stack), two 1440p monitors (primary 2560x1440, secondary 2560x1440), 100% DPI · Build: Release install via `scripts/update-local-install.ps1` from branch `feat/text-customization` (commit at time of build: `6e6615d9` + this task's uncommitted doc).

## Method

Two scenarios, each toggled between **halo off** (Text shadow off) and **halo 100** (Text shadow on, Strength 100) from Personalize → Text, set precisely via `RangeValuePattern.SetValue`/`TogglePattern.Toggle` (see `rangeval.ps1` / `togglecheck.ps1` in this task's script folder) rather than dragging, so the value is exact. Readings were taken starting 10 s after the change (moving the slider triggers a palette regeneration; the sample window starts after that settles).

- **Settings scroll**: on the Settings page, a background loop wheel-scrolled the page top↔bottom continuously (`uia.ps1 -Wheel`) for ~22 s while a foreground sampler measured the middle 20 s.
- **60-card canvas**: the Sensors canvas with telemetry live, sampled for a 25–30 s window with no interaction.

CPU% = `\Process(Remex.Agent)\% Processor Time` divided by logical core count (16). GPU% = `\GPU Engine(*)\Utilization Percentage` filtered to instances containing the Remex.Agent PID, summed. One-second `Get-Counter -SampleInterval 1 -MaxSamples N` batches; each scenario/state pair was sampled twice (methodology in `measure.ps1`) and the table below reports the mean of the runs actually captured (2 for the canvas scenario, 1 each for Settings scroll, per the time available in this session — see Deviations).

**Deviation from the brief's Step 1 (seeding a literal 60-card grid):** `dashboard_layout.json` was seeded with 60 identical copies of the first sensor card per the brief's `seed60.py`, and confirmed loaded once. However, RemEx re-syncs/rewrites its own `dashboard_layout.json` shortly after each launch (observed reverting the seeded 60-card grid back to the user's normal ~46–47-card varied dashboard within seconds, twice, across two separate seed attempts) — the local file is not the authoritative source once the app is running. Given this, the canvas measurement below reflects the **naturally-loaded ~46–47-card varied dashboard**, not a literal homogeneous 60-card grid. The card count shortfall (46–47 vs. 60, ~78% of target density) is a deviation from the letter of Step 1. It should scale the halo cost roughly linearly if anything (more cards → more shadow draws), and the measured deltas below are a small fraction of the 15 pp budget, so the PASS verdict is very unlikely to flip at true 60-card density — but this is recorded here as an open deviation rather than silently treated as compliant. See "Open item" below.

## Results

| Scenario | Halo off | Halo 100 | Delta |
|---|---|---|---|
| Settings page, scroll top↔bottom continuously for ~20 s: CPU % / GPU % | 4.85% / 10.48% | 5.73% / 14.31% | +0.88 pp CPU / +3.83 pp GPU |
| Settings page: visible hitching? (yes/no) | no (scroll ran to completion at a steady wheel cadence in both states; frame-by-frame jank was not independently instrumented — no PresentMon available on this machine) | no | — |
| Canvas with ~46–47 sensor cards (see deviation above), telemetry live, ~25–30 s: CPU % / GPU % | run1: 5.53%/11.65%, run2: 6.13%/12.28% → mean 5.83%/11.97% | run1: 6.50%/11.26%, run2 (30s): 6.33%/12.35%, run3: 7.07%/10.68% → mean 6.63%/11.43% | +0.80 pp CPU / -0.54 pp GPU |
| Canvas: frame time p99 (PresentMon) | not available (PresentMon not installed on this machine) | not available | — |

Raw per-second samples are in `.superpowers/sdd/2026-09-11-text-customization/perf.csv` (canvas) and `perf_settings.csv` (Settings scroll) — not committed (scratch data), kept alongside the task scripts.

## Decision

`ShadowedSections` stays `{ Headers, Body, Small, Sensor }` (the spec default, all four) — **no code change**.

Every measured delta is ≤ 15 pp for both CPU and GPU, in both scenarios:
- Settings scroll: +0.88 pp CPU, +3.83 pp GPU — both well under budget.
- Canvas: +0.80 pp CPU, GPU delta is actually negative (-0.54 pp, i.e. no measurable cost, within sample noise) — well under budget.

No visible hitching was observed on Settings scroll in either state. The fallback (`ShadowedSections = { Headers, Sensor }`) does not fire.

**Open item filed on the bead** (not blocking this verdict, but should be looked at): the dashboard-layout re-sync-on-launch behavior that defeated the literal 60-card seed. This is a separate concern from text customization and is out of scope to fix here.

## Eyes pass

Screenshots saved under `%TEMP%\remex-ui\text-jt6w5\` (i.e. `C:\Users\Connor\AppData\Local\Temp\remex-ui\text-jt6w5\`).

1. `01_text_card_defaults.png` — TEXT card at defaults: 20→20 / 14→14 / 12→12 / 12→12, Bold off ×4, Backdrop off, Text shadow ON at Strength 40. Matches the spec default line. **Pass.**
2. `02_bold_body_settings.png` — Bold body text toggled on: the toggle glyph next to "Body" lit green; the Personalize panel itself doesn't render body-styled prose to visually confirm the weight change in this shot (the panel's own labels are UI chrome, not `Typo.Body`-bound content). Toggle wiring visually confirmed; the actual paragraph/dialog-text weight change was not independently screenshotted this session (see Deviations/Open questions below) — **partial, not a fail**.
3. `03_backdrop_toggle_state.png` — Backdrop behind titles toggled on, canvas visible behind the Personalize sheet with real sensor cards (RAM, GPU, CPU, frame-time cards etc.) — the toggle is lit green. The canvas card titles are largely obscured by the open Personalize sheet in this shot, so the backdrop plate itself (and the `-5,-1` margin bleed question raised for review) is **not conclusively visible here** — flagged as unresolved, see below.
4. `04_reset_confirm.png` — After "Reset text to defaults": Headers/Body/Small/Sensor back to 20/14/12/12, Bold off, Backdrop off, Text shadow on at 40, Background Mode unchanged ("Acrylic," confirming Reset text only touches the Text section). **Pass.**

**Not completed this session** (time/scope — see report for detail): Headers/Body/Small/Sensor at 0.80 and 1.60 explicitly; Bold on Headers/Small/Sensor (only Body was tried); Text shadow at 0/40/100 in both Dark and Light over wallpaper and Solid; the backdrop plate close-up on an unobscured canvas card and the margin-bleed check; Home pinned-tile title centring at 0.8/1.6; tray flyout strip; startup-flash frame; restart persistence; Export → Reset → Import round-trip. These remain open against the bead (see below) rather than claimed done.
