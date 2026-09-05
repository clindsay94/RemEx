# UI palette sweep (RemEx-8q7de)

## Why "check all four themes" is dead

Before the seed engine (RemEx-07jij), a RemEx "theme" was a hand-authored resource dictionary —
there were exactly four of them (Cyber-NOC, Solar-Flare, Monolith, Standard Glass), and "check all
four themes" was a real, finite verification axis because the space it covered was four points.

Since RemEx-07jij every surface is *generated* from a seed colour by `DynamicColorGenerator`. The
four named themes still exist, but only as **presets** — four rows of data in
[`SeedPresetCatalog.All`](../remex.desktop/Models/SeedPreset.cs) (see lines 73-83) that hand a seed
and a scheme variant to the same generator any custom colour goes through. A user can pick any of
millions of seeds, cross it with any scheme variant, any light/dark mode, and any contrast from
-1.0 to 1.0. "Check all four themes" now checks four arbitrary points in that space and says
nothing about the rest of it — it is not incomplete, it is answering a question that stopped being
the right one to ask.

## The axis this sweep drives instead

Not "every combination" — that space is infinite. The replacement axis is the default plus three
seeds chosen to be **adversarial**, each crossed with mode and contrast:

| Seed | Hex | Why |
|---|---|---|
| Default | `#6C4CFF` (`BaseDarkGlass` preset) | The shipped default. Every other cell is a deviation from this one. |
| Chalk | `#F5F5F5` | Near-white — stresses generated text/border contrast against a barely-there background. |
| Ink | `#0B0B0F` | Near-black — the opposite stress: does anything vanish into the surface it sits on? |
| Chroma | `#00FF00` | Max chroma — the most saturated a seed can be; checks for colour clipping and glow bloom. |

Default stays fixed (dark, contrast 0 — it is the shipped point, not a variable). Each of the three
adversarial seeds is crossed with:

- **Mode**: Light, Dark
- **Contrast**: 0.0, 1.0 (the full Material contrast range's two ends)

`1 + 3 seeds × 2 modes × 2 contrasts = 13 cells`. The exact 13 are data in
[`scripts/ui-palette-sweep.ps1`](../scripts/ui-palette-sweep.ps1) — run `-ListCells` to read them
rather than trusting a second copy of the table here:

```
pwsh scripts/ui-palette-sweep.ps1 -ListCells
```

## How a cell reaches the screen

Each cell is captured against every view `--view` can open. `--view <Name>` is a Remex.Desktop
launch argument ([`StartupViewArgument`](../remex.desktop/Services/StartupViewArgument.cs),
wired in `App.axaml.cs`'s `InitializeAppAsync`) that navigates straight to a named view at
startup, instead of the original design's `Ctrl+D1..D7` / `Ctrl+OemComma` sent through
`SendKeys` — that is banned in this repo (the nav list items expose no `InvokePattern` for UI
Automation to click instead, so a keystroke was the only lever, and OS keystroke injection from an
agent is a hard no here regardless).

Because navigation now happens at launch rather than at runtime, the sweep launches the host once
**per cell × view**: stop it, write the profile, start it with `--view X`, wait for the window,
screenshot, stop it. `scripts/ui-hotreload.ps1 -Start` takes the extra `-AppArgs` string for this
(e.g. `-AppArgs '--view Settings'`); the sweep never calls `-Start` without also passing `-NoBuild`
— building mid-sweep can lock the very DLLs a running host holds, and worse, silently leaves you
screenshotting a stale binary if the build then fails.

The nine scriptable views, in `Ctrl+D1..D7` / `Ctrl+OemComma` / (no binding) order:

| `--view` name | Opens |
|---|---|
| `Home` | Home dashboard |
| `Sensors` | Sensor workspace (the canvas) |
| `Commands` | Remote control |
| `Launcher` | App launcher |
| `Processes` | Task manager |
| `Files` | File transfer |
| `Logs` | Diagnostic logs |
| `Settings` | Settings panel |
| `About` | About page |
| `Personalize` | The Personalize side sheet over Home |

`RemoteDesktop` has no `--view` entry and is not swept: it needs a connected phone to show
anything meaningful, so it stays a **manual** verification cell — check it by hand against at
least the Default and one adversarial cell when doing a full pass.

## Running it

```
pwsh scripts/ui-palette-sweep.ps1                              # every cell, every view
pwsh scripts/ui-palette-sweep.ps1 -Cells Default,Chroma-Dark-C1 # just these cells
pwsh scripts/ui-palette-sweep.ps1 -DryRun                       # print the plan, touch nothing
pwsh scripts/ui-palette-sweep.ps1 -ListCells                    # print the matrix, touch nothing
```

Windows only (it drives UI Automation through `ui-snapshot.ps1`) — on any other platform it prints
a warning and exits cleanly.

**Safety.** The script rewrites your live `dashboard_layout.json` while it runs. It is backed up to
`dashboard_layout.json.sweep-backup` before the first write and restored from that backup in a
`finally` block, whether the run finished, failed, or was interrupted. If a backup already exists
when you start a run, the script **refuses to start** — that backup is your real profile from a
previous run that died before restoring it, and running again would overwrite the one copy of it
that still exists. Move it back to `dashboard_layout.json` by hand (or delete it once you've
confirmed you don't need it) before trying again.

Output goes to a timestamped folder under `%TEMP%\remex-ui\` by default (override with `-Out`):
one `<Cell>-<View>.png` (plus a same-named UI-tree `.txt` from `ui-snapshot.ps1`) per automated
capture, and an `index.md` ledger — the same ledger format as the one below, generated fresh each
run.

## Findings ledger

This is the ledger as of RemEx-8q7de: every cell × view marked **not run**. Filling it in — running
the sweep, looking at every screenshot, and recording what was actually seen — is RemEx-bmuji's
job, not this bead's. A cell marked "not run" here has not been looked at; do not read this table
as "verified fine".

| Cell | Home | Sensors | Commands | Launcher | Processes | Files | Logs | Settings | About | RemoteDesktop |
|---|---|---|---|---|---|---|---|---|---|---|
| Default | not run | not run | not run | not run | not run | not run | not run | not run | not run | manual |
| Chalk-Light-C0 | not run | not run | not run | not run | not run | not run | not run | not run | not run | manual |
| Chalk-Light-C1 | not run | not run | not run | not run | not run | not run | not run | not run | not run | manual |
| Chalk-Dark-C0 | not run | not run | not run | not run | not run | not run | not run | not run | not run | manual |
| Chalk-Dark-C1 | not run | not run | not run | not run | not run | not run | not run | not run | not run | manual |
| Ink-Light-C0 | not run | not run | not run | not run | not run | not run | not run | not run | not run | manual |
| Ink-Light-C1 | not run | not run | not run | not run | not run | not run | not run | not run | not run | manual |
| Ink-Dark-C0 | not run | not run | not run | not run | not run | not run | not run | not run | not run | manual |
| Ink-Dark-C1 | not run | not run | not run | not run | not run | not run | not run | not run | not run | manual |
| Chroma-Light-C0 | not run | not run | not run | not run | not run | not run | not run | not run | not run | manual |
| Chroma-Light-C1 | not run | not run | not run | not run | not run | not run | not run | not run | not run | manual |
| Chroma-Dark-C0 | not run | not run | not run | not run | not run | not run | not run | not run | not run | manual |
| Chroma-Dark-C1 | not run | not run | not run | not run | not run | not run | not run | not run | not run | manual |

RemoteDesktop is marked `manual` for every cell — it is never swept by the script (see above), so
"not run" and "manual" mean different things here: a `not run` cell is waiting on RemEx-bmuji to
run the script and look; a `manual` cell needs someone to open it by hand every time, sweep or no
sweep.
