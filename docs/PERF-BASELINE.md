# Measurement: cold-start time and memory, before vs after the Material work (RemEx-gtwk8)

**Status:** automated part done. Numbers recorded below. Stream frame pacing and dashboard
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
