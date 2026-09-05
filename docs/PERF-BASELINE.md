# Measurement: cold-start time and memory, before vs after the Material work (RemEx-gtwk8)

**Status:** automated part done. Numbers recorded below. A reversed-order, 12-launch, steady-state
Run 2 (see [2b](#2b-run-2---confirmation-pass-remex-3sju52)) found the Run 1 memory regression
**likely but not yet confirmed** — a session-dependent confound has not been ruled out; see 2b for
the hypothesis, the protocol-compatibility check, and the next run needed to settle it.
RemEx-3sju5.2 stays open. Stream frame pacing and dashboard
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
per launch (`-SettleSeconds 8`, `-SteadySeconds 20`).

**Correction (2026-09-04, post-Run-2 review):** the connection count as originally implemented
called `Get-NetTCPConnection`, which - like `Get-ScheduledTask` - goes through the NetTCPIP
module's CIM proxy, and this script already loads `UIAutomationClient`/`UIAutomationTypes`
in-process for the window poll. That combination breaks CIM for the rest of the process's life
(bd memory `uiautomation-breaks-scheduledtasks-in-process`), so **every "-1" recorded below means
the query failed, not that zero connections were established.** The table below is left as
originally recorded (it is what the run actually produced), but nothing in it should be read as
"no phone was connected." The script now shells out to `netstat.exe -ano -p TCP` instead (an
external process, no CIM involved) and reports an unresolvable count as `unknown`, never `-1`, so
a future run's connection column can actually be trusted.

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
| HEAD | eac94df | 2216 | 1513 | 1646 | 395.2 | 511.0 | 1665 | -1 (unknown, see correction above) | 398.2 | 490.7 | 1687 | -1 (unknown) |
| main | b528ff1 | 3964 | 2191 | 2413 | 331.7 | 312.2 | 1582 | -1 (unknown) | 331.8 | 311.8 | 1567 | -1 (unknown) |

11 warm samples per ref (`-Launches 12`) clears the "10 or more" bar from
`scripts/perf-baseline.ps1`'s label switch, so the header reads **"Warm P90"**, not "warm max" -
this is a real 90th percentile this time, not the slowest-of-six max Run 1's table showed.

**Verdict against the same thresholds (memory 10%, time 15%): LIKELY, NOT YET CONFIRMED.** The
direction (HEAD's private bytes higher than main's) held across two independent runs, but the
*size* of the gap is not stable in a way the known code differences explain, and connection state
during both runs is genuinely unknown (see correction above) rather than "no session" as an
earlier draft of this section incorrectly stated - so a session-dependent confound has not been
ruled out.

- **Private bytes rose in both runs, but by very different amounts, all on HEAD's side.** Run 1:
  main 302.5 -> HEAD (34341bb) 371.7 MB, +22.9%. Run 2: main 312.2 -> HEAD (eac94df) 511.0 MB,
  +63.7% at settle. `main`'s own number barely moved between runs (302.5 -> 312.2 MB, +3.2%, inside
  noise) while `HEAD`'s moved by +139.3 MB between two `HEAD` commits (`34341bb` -> `eac94df`) that
  add UI-only work (palette export/import, splash crossfade, tutorial Material vocabulary) with no
  obvious reason to cost that much resident private memory on their own. A swing that large,
  concentrated entirely on one side, with no code difference of matching size, is exactly the
  signature of a confound the two runs didn't hold constant - not proof the code itself grew by
  139 MB.
- **Hypothesis: the paired phone connects to `HEAD` but not to the pre-Material `main` build**
  (a protocol or version mismatch), so `HEAD`'s runs were measured with a live session and `main`'s
  without, and the live-session cost is what's actually being measured as "the Material
  regression." Checked from the code, read-only:
  - `git diff main..HEAD --stat -- remex.core` shows a large diff (50 files, +5547/-325) across
    months of unrelated feature work, so a version mismatch is plausible on its face.
  - `git grep -niE "ProtocolVersion|protocol_version|MinSupported"` and a direct read of
    `remex.core/Messages/ProtocolVersionPolicy.cs` on both refs shows **`Minimum = 2`,
    `Current = 3`, `BinaryFileTransferMinimum = 3` are identical on `main` and `HEAD`.** Every
    other RemexMessage.cs field added between the two refs is commented as "additive and optional,
    no protocolVersion bump" (checked by reading the surrounding diff context, not just the grep
    hits). **Conclusion: `main` and `HEAD` advertise and accept the same protocol-version range, so
    they should be equally able to pair with and talk to the same Android app.** The specific
    "protocol/version mismatch" mechanism is not supported by the code. If a session-dependent
    asymmetry is real, something other than the protocol-version gate explains why one side had a
    session and the other didn't (e.g. simply which build happened to be running when Connor's
    phone last reconnected, independent of any incompatibility) - or the private-bytes gap is not
    session-related at all and the working hypothesis is wrong.
  - **Current connection state, checked right now (read-only, via a fresh `pwsh` child running
    `netstat.exe -ano -p TCP`, no relaunch):** the installed host (currently `HEAD`, redeployed by
    Run 2's own `finally` block and already running) **does have one ESTABLISHED connection**
    (`10.0.0.3:5005 <-> 10.0.0.2:51632`). This confirms a phone does routinely connect to a `HEAD`
    build in normal use; it says nothing about whether a session was up during either run's actual
    8-20 s measurement windows, since `Measure-RemexLaunch` calls `Stop-RemexHost` before every
    launch and a phone's own reconnect timing is unknown relative to the settle/steady samples.
- **Working set (soft): +19.1% at settle, +20.0% at steady state**, corroborating private bytes'
  direction but not conclusive alone (Run 1's caveat about it being soft still applies).
- **Handles: +5.2% at settle, +7.7% at steady state.** Within the 10% noise threshold both runs -
  handle count is not part of whatever this is.
- **Steady-state (20 s) does not show the growth resolving** - working set and private both stay
  within a few MB of the 8 s settle sample for both refs, ruling out "it's just uncollected garbage
  that clears up shortly after launch." Whatever is holding the memory, it isn't a transient GC lag
  - it's either retained code-driven footprint or a retained session artifact.
- **Launch time is confirmed as a real improvement, independent of this verdict.** Run 1 measured
  `main` first / `HEAD` second (cache advantage to `HEAD`); this run reverses that (`HEAD` first /
  `main` second, cache advantage to `main` instead) and `HEAD` is still faster in both directions:
  cold -44.1%, warm median -30.9%, warm P90 -31.8%. This belongs to `RemEx-gtwk8`, already closed,
  not this bead.

**Suspects for the private-bytes growth, in priority order, and how to profile them** (not fixed
in this bead). Session-dependent causes come first because the run-to-run instability above points
at something that isn't simply "more code loaded":

1. **A live phone session's per-connection cost** - stream frame buffers, the telemetry
   broadcaster, decoder-side state, and any per-connection queues are all allocated only once a
   phone is connected, and none of that is exercised by a cold/warm launch with no phone attached.
   If `main` happened to run its 12 launches with no session and `HEAD` happened to pick one up
   partway through (even on a few of the 11 warm launches), that alone could produce an unstable,
   HEAD-concentrated private-bytes swing of this shape. Test by running the phone deliberately
   connected and idle for one ref and disconnected for another (see next run below) and diffing.
2. **Material.Avalonia's styles/`ControlTheme` resource dictionary**, loaded once at startup - a
   real, code-driven cost, but a fixed one that should be stable run-to-run, which the +69 MB (Run
   1) vs +139 MB (Run 2, on top of Run 1's HEAD number) delta argues against being the whole story.
3. **The animated dashboard background/aurora mesh and ripple/elevation visuals** (RemEx-bmuji) -
   plausible if it holds more live allocations the longer the shell has been open, though the flat
   settle-to-steady numbers argue against ongoing growth specifically.
4. **The palette AXAML/JSON export/import feature (RemEx-a7uzb)** and other UI-only work landed
   between the two `HEAD` commits - only relevant if it holds parsed palette/theme data resident
   even when the feature isn't actively used.

To profile #1 specifically: take a `dotnet-gcdump` or ETW heap snapshot of the installed
`Remex.Agent.exe` at the 20 s mark with a phone connected and idle vs. disconnected, on the same
`HEAD` build, and diff retained object graphs by type - stream/decoder/telemetry objects appearing
only in the connected snapshot would confirm #1 before touching Material at all. For #2-4, snapshot
both refs with connection state pinned identical (see next run) and diff by type; resource
dictionaries and bitmap brushes point at #2, many small similar-sized objects point at #3 or #4.

**Next run (not executed in this bead - needs Connor's go-ahead before relaunching the host
again):**

```
pwsh scripts/perf-baseline.ps1 -Refs @('main','HEAD') -Launches 12
```

run with the phone paired and left idle on the dashboard for the whole run (so the now-fixed
connection count is non-zero and roughly equal across every launch of both refs, ruling the
confound in or out directly instead of guessing from a `-1`/`unknown` column), **plus** one
`HEAD`-only run with the phone deliberately disconnected throughout, so the connected-vs-not
delta can be priced on its own before attributing anything to Material.

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
