# Measurement: cold-start time and memory, before vs after the Material work (RemEx-gtwk8)

**Status:** automated part done. Numbers recorded below. The Run 1 memory regression is now
**confirmed** by a reversed-order, 12-launch, steady-state Run 2 (see [2b](#2b-run-2---confirmation-pass-remex-3sju52)) —
RemEx-3sju5.2 stays open for the fix. Stream frame pacing and dashboard
frame cost stay a manual pass on Connor's device (RemEx-bmuji) — see [Manual](#manual) below.

---

## 1. What this measures, and how

`scripts/perf-baseline.ps1` deploys two git refs to the **same installed path**
(`%ProgramFiles%\RemEx`) through each ref's own `scripts/update-local-install.ps1`, so the exe
path never changes and no new Windows Firewall prompt fires (see bd memory
`env-host-testing-needs-installed-exe`). For each ref it:

1. Adds a detached git worktree at that commit under `%TEMP%`.
2. Runs that worktree's own `update-local-install.ps1 -NoRestart` in a **separate `pwsh.exe`
   process** (see [Gotcha](#gotcha-cim-and-uiautomation) below) and times the publish+copy.
3. Launches the installed `Remex.Agent.exe` 7 times. Launch 1 (right after the deploy) is the
   true **cold start**; launches 2-7 are **warm** restarts of an already-JITted, already-disk-cached
   binary. Each launch is timed from `Start-Process` to the main window appearing in the UI
   Automation tree (polled every 50 ms, the same `RootElement` children-filtered-by-`ProcessId`
   pattern as `scripts/ui-snapshot.ps1`), then left running 8 seconds before `WorkingSet64`,
   `PrivateMemorySize64` and handle count are sampled via `Get-Process` and the process is
   stopped again.
4. Writes `perf-<ref>.json` (every raw sample) and folds all refs into `perf-summary.md`.

Whatever happens, a `finally` block redeploys **HEAD from the calling checkout** (not a worktree
that is about to be deleted), removes every worktree the script created, and restarts the
installed host only if it was already running when the script started. UI Automation here is
read-only — no keystrokes, no profile writes, no build inside `ui-hotreload`'s dev loop.

```
pwsh scripts/perf-baseline.ps1                      # main vs HEAD, 7 launches, 8s settle (defaults)
pwsh scripts/perf-baseline.ps1 -Refs a,b -Launches 10 -SettleSeconds 5
```

### Gotcha: CIM and UI Automation in the same process

`update-local-install.ps1` calls `Get-ScheduledTask`/`Stop-ScheduledTask`, which go through the
`ScheduledTasks` module's CIM (WMI) proxy. `perf-baseline.ps1` loads `UIAutomationClient` /
`UIAutomationTypes` (for the window poll). Loading both **in the same PowerShell process**
reproduces, 100% of the time on this machine: `Get-ScheduledTask` throws *"The type initializer
for 'Microsoft.Management.Infrastructure.Native.ApplicationMethods' threw an exception."* for
the rest of that process's life. The fix is `Invoke-UpdateLocalInstall` in the script: every
`update-local-install.ps1` call runs in its own fresh `pwsh.exe` process rather than via the
call operator in-process. Verified fixed by two clean full runs after the change.

## 2. This run's numbers

| Ref | Commit | Cold Start (ms) | Warm Median (ms) | Warm P90 (ms) | Working Set (MB) | Private (MB) | Handles |
|---|---|---|---|---|---|---|---|
| main | b528ff1 | 4210 | 2204 | 2398 | 326.1 | 302.5 | 1557 |
| HEAD | 34341bb | 2349 | 1496 | 1640 | 382.7 | 371.7 | 1653 |

`main` = `b528ff1` (pre-Material, tip of `main` at measurement time). `HEAD` = `34341bbe`
(tip of `v2.5-board-drain`, all Material work in scope for RemEx-3sju5). Raw per-launch samples:
`%TEMP%\remex-ui\perf-20260904-185359\perf-main.json` and `perf-HEAD.json`.

**Noise threshold for this run:** warm median/P90 within 15% is noise; working set / private
memory within 10% is noise. Anything outside that is a real change.

**Verdict:**

- **Cold start and warm launch time both improved** well outside the 15% band (cold -44%, warm
  median -32%). **Read this with a caveat, not as a clean win**: this run always measures `main`
  first and `HEAD` second, so `HEAD`'s launches benefit from a warmer OS file cache / SSD
  prefetch left behind by `main`'s own publish and seven launches immediately before it. The
  launch-time numbers are not a clean before/after signal from this run alone — no cold-start
  regression is indicated, but the improvement should not be credited to the Material work
  without re-running with the ref order reversed (or interleaved) to separate the two effects.
- **Memory regressed outside noise.** Working set +17.4% (326.1 -> 382.7 MB), private +22.9%
  (302.5 -> 371.7 MB), handles +6.2% (1557 -> 1653). Both memory figures clear the 10% threshold.
  Filed as **RemEx-3sju5.2** with these numbers, per the parent bead's "any regression gets its
  own bead" acceptance criterion. Likely cause: Material.Avalonia's larger styles/`ControlTheme`
  resource dictionary loaded at startup, plus the dashboard's animated background/elevation/
  staggered entrance (RemEx-bmuji) holding more allocations live once the shell is up.

### Caveats on reading the table

- **Warm P90 is the slowest warm launch, not a percentile.** With the default seven launches
  there are six warm samples, and the P90 index lands on the last one. Treat the column as
  "warm max" until a run uses `-Launches 12` or more.
- **Memory is sampled eight seconds after the window appears, with no forced collection and no
  steady-state check.** The Material build runs an animated dashboard background that the
  pre-Material build does not, so part of the working-set growth can be uncollected gen0/gen1
  rather than retained footprint. Private bytes is the number to trust; working set is soft.
- **Connection state was not recorded.** The app auto-connects to the paired phone on every
  launch, so a live session in one ref's run and not the other's can move tens of megabytes.
  The next run should record whether a session was up at each sample.
- **Order.** `main` ran first and `HEAD` second, so `HEAD` had a warmer file cache; this flatters
  the launch times and can touch working set through shared image pages, but not private bytes.
  A reverse-order run (`-Refs @('HEAD','main')`) is cheap and should precede any fix on
  RemEx-3sju5.2.

## 2b. Run 2 - confirmation pass (RemEx-3sju5.2)

Per the Gate's confirmation protocol (RemEx-3sju5.2 notes, 2026-09-04): reverse the ref order,
raise `-Launches` to 12 (11 warm samples instead of 6, so "Warm P90" is an honest percentile
rather than a max), add a second, later memory sample at steady state, and record established TCP
connections per sample as a live-session proxy. `scripts/perf-baseline.ps1` now takes both samples
per launch (`-SettleSeconds 8`, `-SteadySeconds 20`) and records
`Get-NetTCPConnection -OwningProcess <pid> -State Established` (count, or `-1` if the query itself
errors) alongside each one.

```
pwsh scripts/perf-baseline.ps1 -Refs @('HEAD','main') -Launches 12
```

`HEAD` = `eac94df` (tip of `v2.5-board-drain` at measurement time - several more Material/UI beads
landed on top of the `34341bb` measured in Run 1: palette AXAML/JSON export, the splash-to-shell
crossfade, and the tutorial's Material vocabulary, among others). `main` = `b528ff1`, unchanged.
`HEAD` was measured **first** this time (`main` second), the opposite of Run 1, specifically to
separate the launch-time improvement from the OS-file-cache-order effect flagged in Run 1's
caveats. Raw data: `%TEMP%\remex-ui\perf-20260904-195152\perf-HEAD.json` / `perf-main.json`.

| Ref | Commit | Cold Start (ms) | Warm Median (ms) | Warm P90 (ms) | Working Set @Settle (MB) | Private @Settle (MB) | Handles @Settle | Conn @Settle | Working Set @Steady (MB) | Private @Steady (MB) | Handles @Steady | Conn @Steady |
|---|---|---|---|---|---|---|---|---|---|---|---|---|
| HEAD | eac94df | 2216 | 1513 | 1646 | 395.2 | 511.0 | 1665 | -1 | 398.2 | 490.7 | 1687 | -1 |
| main | b528ff1 | 3964 | 2191 | 2413 | 331.7 | 312.2 | 1582 | -1 | 331.8 | 311.8 | 1567 | -1 |

11 warm samples per ref (`-Launches 12`) clears the "10 or more" bar from
`scripts/perf-baseline.ps1`'s label switch, so the header reads **"Warm P90"**, not "warm max" -
this is a real 90th percentile this time, not the slowest-of-six max Run 1's table showed.

**Verdict against the same thresholds (memory 10%, time 15%): CONFIRMED, and worse than Run 1
measured.**

- **Private bytes (the trustworthy number - immune to file-cache order): +63.7% at settle
  (312.2 -> 511.0 MB), +57.4% at steady state (311.8 -> 490.7 MB).** Both far exceed the 10%
  threshold, and both exceed Run 1's +22.9% by a wide margin. Confirmed regression.
- **Working set (soft - shared image pages, can include uncollected gen0/gen1): +19.1% at settle
  (331.7 -> 395.2 MB), +20.0% at steady state (331.8 -> 398.2 MB).** Also outside the 10% band and
  corroborates private bytes' direction, though its own magnitude is not conclusive on its own
  (Run 1's caveat about it being soft still applies).
- **Handles: +5.2% at settle (1582 -> 1665), +7.7% at steady state (1567 -> 1687).** Both within
  the 10% noise threshold, consistent with Run 1's +6.2% - handle count is not part of the
  regression.
- **Steady-state (20 s) does not show the growth resolving.** Working set and private both stay
  within a few MB of the 8 s settle sample for both refs (HEAD: 395.2->398.2 MB set, 511.0->490.7
  MB private; main: 331.7->331.8 MB set, 312.2->311.8 MB private) - private even drops slightly
  by steady state on both refs. This rules out "it's just uncollected garbage that clears up
  shortly after launch": the gap is retained footprint, not a transient GC lag.
- **Connections: -1 at every sample, both refs, both settle and steady.** `Get-NetTCPConnection`
  with `-State Established` throws a non-terminating "no matching connection" error (converted to
  terminating by `-ErrorAction Stop`, caught, returned as `-1`) when there are none - no phone was
  paired/connected during this automated run, on either ref, at either sample. Because the value
  is identical across both refs and both samples, connection state does **not** invalidate this
  comparison (the caveat in Run 1 was that a *differing* count would); it just means this run
  cannot yet be used to price an active-session cost.
- **Launch time is now confirmed as a real improvement, not a cache-order artifact.** Run 1
  measured `main` first / `HEAD` second (`HEAD` benefited from a warm file cache); this run
  reverses that (`HEAD` first / `main` second, so `main` gets the cache advantage this time) and
  `HEAD` is still faster in both directions: cold -44.1% (3964 -> 2216 ms), warm median -30.9%
  (2191 -> 1513 ms), warm P90 -31.8% (2413 -> 1646 ms). This closes the open question from Run 1's
  caveats; the launch-time win belongs to `RemEx-gtwk8`, already closed, not this bead.

**First suspects for the private-bytes growth, and how to profile them** (not fixed in this bead):
Material.Avalonia's styles/`ControlTheme` resource dictionary is loaded once at startup and is
large enough by itself to plausibly account for tens of MB, but the growth from Run 1 (+69 MB
private) to Run 2 (+199 MB private) tracks the extra Material-vocabulary work landed in between -
the splash-to-shell crossfade and its assets, the tutorial's Material rewrite, and the
palette AXAML/JSON export/import feature (RemEx-a7uzb) which can hold parsed palette/theme data
resident. The animated dashboard background/aurora mesh and its ripple/elevation visuals remain a
suspect too, especially since the regression does not resolve by the 20 s steady sample. To
profile: take a `dotnet-gcdump` or ETW heap snapshot of the installed `Remex.Agent.exe` at the 20 s
mark on both refs and diff retained object graphs by type - resource dictionaries and any bitmap
brushes should show up as large, distinct allocations if they are the cause; if the dump instead
shows many small, similar-sized objects it points at the palette import/export data structures or
the animated background's per-frame state instead.

## 3. Machine context

- CPU: AMD Ryzen 7 9800X3D (8 cores / 16 logical)
- RAM: 95.6 GB
- OS: Windows 11 Pro, build 26200.9278
- RemEx.Agent was already running (installed, unrelated build) when the script started; the
  script restarted it on HEAD at the end, per its own "only restart if it was running before"
  contract.

## 4. Manual

Not measured here — both need a live device/session pass. Tracked under **RemEx-bmuji**.

- **Remote-desktop stream frame pacing during an active session with the new chrome.** The
  `PrecisionPacer` fix (RemEx-ccen) tuned core usage from 99% to 18%
  (`docs/REGRESSION-GUARDS.md:83`, `docs/CHANGELOG.md`) — **that must not regress** with the new
  Material chrome painting over the same stream. Measure: CPU% of the pacer's own spin time (not
  `Process.TotalProcessorTime` — see the CHANGELOG entry on why that metric is wrong) during an
  active 90 Hz and 120 Hz stream, with the Material shell open around the stream view.
- **Dashboard frame cost with the animated background, elevation and staggered entrance.**
  Measure: frame time / dropped frames on the dashboard immediately after navigation (staggered
  entrance animation playing) and at steady state (idle, animated background still running),
  on both a light and dark theme.
