# Perf remediation tracker

Temporary. It drives the `/ralph` loop for the 2026-09-23 perf audit and is **deleted when the loop finishes**. Umbrella bead: `RemEx-4j8ls`. Branch: `perf/audit-remediation`. Row detail (evidence, fix, locations, guard notes): `.superpowers/perf-audit/PRIORITIES.md` (gitignored, local), keyed by the same `P<t>-<n>` ID.

## Procedure

This section is the ralph `ProcedureOverlay`. It wins over `~/.claude/ralph/board-drain.md` wherever they disagree:
- **The work source is this file, not `bd ready`.** No per-row beads. Every commit cites `RemEx-4j8ls`.
- **No per-row commits.** Rows are checkpointed with `git add`. Commits happen only at group gates.
- **The journal is the Log section** at the bottom of this file. The CHANGELOG is written only at gates.

### Roles and models
| Role | Model | How |
|---|---|---|
| Looper (reads, confirms, implements `Impl=sonnet` rows, runs verify, keeps this file) | Sonnet 5 | the session itself |
| Implementer for `Impl=opus` rows (critical to operation: guard-noted, capture, stream, decoder, SurfaceView, pairing, session guard, JNI/wire, input) | Opus 5.5 | `implementer` agent, `model: opus` |
| Verifier for every row | Opus 5.5 | `csharp-reviewer` (C#) or `kotlin-reviewer` (Kotlin), `model: opus` |
| Tie-breaker when looper and verifier conflict | Fable 5.1 | `general-purpose` agent, `model: fable`, read-only |

### Picking the iteration
The **current group** is the first group below that still has a row that is not `committed`.
- If it has a `todo` row → **row iteration** on the first `todo` row (for P3, the first `todo` batch).
- If it has none → **group gate**.
- If every group is done → **wrap-up**.

### Row iteration
1. Grep PRIORITIES.md for the row ID, then Read only that row (offset/limit).
2. **Confirm the claim** at the cited code with token-savior (`find_symbol`, `get_function_source`, `get_edit_context`) and gitnexus `context`. If it does not hold, set Status `refuted` and write the reason in Note. For P0/P1 rows the Opus verifier must agree (send it the reason, not a diff). Then log and end the iteration.
3. If the row has a guard note, read that section of `docs/REGRESSION-GUARDS.md` (Grep the heading, Read with offset/limit). If a symbol has callers outside its file, run gitnexus `impact` and put the risk level in Note.
4. **Implement.**
   - `Impl=sonnet`: the looper edits (Read the file right before the Edit).
   - `Impl=opus`: dispatch `implementer` with `model: opus`. Give it the row ID, the PRIORITIES.md path, the guard section, and this Procedure's rules. It returns at most 300 words and does not commit.
   - Test-first where the behaviour is testable (a hidden-window gate, a queue bound, a cache cap).
5. **Scoped verify.** Output goes to `.ralph/perf/verify.log`; print only the summary line.
   - C# only: `scripts/verify.ps1 -Scope dotnet -NoClean`
   - Kotlin only: `scripts/verify.ps1 -Scope android -NoClean` (release variant only)
   - Anything in `remex.core/Native`, or both platforms: `-Scope all -NoClean`
6. **Opus verification.**
   - Write `git diff -- <row files>` to `.ralph/perf/<ID>.diff`.
   - Dispatch the reviewer with `model: opus`, giving it: the diff path, the row ID, the PRIORITIES.md path, and "return at most 300 words: verdict, then findings as path:line + one line each".
   - Fix what it finds and re-review the new diff. At most 2 rounds.
   - **Conflict** (you dispute a finding, the reviewer says it is unsure, or 2 rounds do not converge): dispatch the Fable tie-breaker with the diff path, the finding verbatim, your position in 3 lines, and the code refs. It returns a ruling in at most 150 words. Follow it, and add a line under Rulings.
7. **Checkpoint:** `git add <each changed file by name>`. Set Status `staged`, fill in Files (short paths), and add a one-line Note if there's something worth keeping. Unstaged `git diff` is always exactly the current row.
8. If the result can only be seen on a device or on screen (phone backgrounded, window minimized, battery, animation), add a line under **Live checks for Connor**. Never drive the UI, never focus windows, never relaunch the installed host.
9. Append one Log line: `<date> <ID> <staged|refuted|blocked> - <≤12 words>`.

**P3 batches:** the same steps, applied to every row in the batch. Run one verify and one Opus review for the whole batch. If one row in the batch fails, mark it `blocked` without failing the batch.

### Group gate
1. `scripts/verify.ps1 -Scope all` (full), then `scripts/verify.ps1 -Check`. Both must pass. Output goes to files; print summaries only.
2. gitnexus `detect_changes` is advisory. Note anything surprising in the Log.
3. Add one CHANGELOG.md line for the group, under the existing unreleased section, in the file's own style.
4. `git add CHANGELOG.md docs/PERF-TRACKER.md`. Commit with `perf(<group id>): <one-line summary> (RemEx-4j8ls)`, a `Reviewed-by: Opus 5.5 (per-row)` trailer and the session's Co-Authored-By trailer.
5. Set the group's `staged` rows to `committed <sha7>` in this file. That edit rides in the next commit.
6. After the last group of a tier (P0d, P1, P2, P3): `git switch main && git merge --ff-only perf/audit-remediation && git switch perf/audit-remediation`. Never push.

### Wrap-up (when every group is done)
1. Post one summary: blocked rows with their reasons, all Live checks, and the Rulings.
2. `bd remember` 2-4 distilled lessons (symptom, cause, fix; each ≤4 lines).
3. `git rm docs/PERF-TRACKER.md`. Delete the local `.ralph.psd1` and `.ralph/perf/`. Commit `chore(perf): remove remediation tracker (RemEx-4j8ls)`, fast-forward `main`, then `bd close RemEx-4j8ls`.
4. Output `<promise>PERF-DONE</promise>`.

### Stop rules
- A row that fails verify or review twice after fixes → `blocked` with the reason. Move on; do not idle.
- Never output the promise while any P0-P3 row is still `todo` or `staged`.

### Hard rules
- Never `git add -A`, `git add .`, `git checkout -- .` or `git restore -- .`. Name the paths.
- Never bump a version, push, or run Dolt sync.
- Android: release variant only (`compileReleaseKotlin`, `assembleRelease`).
- **Tokens:** read with token-savior and gitnexus; `Read` only right before an `Edit`; build and verify output go to files; diffs are handed over as paths; subagent returns stay ≤300 words; subagents never spawn subagents.

## Groups

### P0a: Idle and background burn

| ID | Finding | Platform | Impl | Status | Files | Note |
|---|---|---|---|---|---|---|
| P0-1 | Android Task Manager 4 s poll keeps running when the app is backgrounded or the screen is off… | Android + PC host | sonnet | committed ce68e66 | TaskManagerScreen.kt | REWORKED: original `currentStateAsState()`+`LaunchedEffect` gate was a no-op (Compose pauses the frame clock on ON_STOP, so the effect can't re-run until ON_START); replaced with `LifecycleStartEffect` (a lifecycle observer, not recomposition-driven). kotlin-reviewer PASS, confirmed the mechanism is sound |
| P0-2 | Aurora, gradient and presence-pulse infinite animations have no hidden or minimized gate (du-1… | PC desktop UI | opus | committed ce68e66 | ShellViewModel.cs, MainWindow.axaml.cs, DashboardBackgroundControl.axaml, ShellPresencePulseTests.cs, DashboardBackgroundGatingTests.cs (new) | new `IsWindowVisible` ANDed into aurora/gradient/pulse gates alongside reduced-motion; csharp-reviewer PASS round 2 (2 LOW, not blocking) |
| P0-3 | Desktop RD stream keeps receiving and decoding after leaving the RD page or hiding to tray (du… | PC desktop UI | opus | committed ce68e66 | RemoteDesktopViewModel.cs, ShellViewModel.cs, ShellPresencePulseTests.cs, RemoteDesktopStreamForegroundGatingTests.cs (new) | foreground-gated stream via existing StopStreamAsync/StartStreamCommand (reuses P0-2's IsWindowVisible); 2 review rounds fixed 2 MEDIUM races (command busy-state, in-flight-start-race); own follow-up fix for a stale-flip re-check bug caught by the existing tests, not re-reviewed |
| P0-4 | Phone RD stream is not paused on background: host keeps capturing, encoding and sending at ful… | Android + PC host | opus | committed ce68e66 | RemoteDesktopBackgroundGate.kt (new), RemoteDesktopViewModel.kt, RemoteDesktopScreen.kt, RemoteDesktopBackgroundGateTest.kt (new) | lifecycle observer stops/restarts stream via existing desktop_stop path, disarms/re-arms frame-arrival watchdog; no host-side change needed. 2 review rounds fixed a cross-thread visibility bug (@Volatile), a pending-restart-vs-pause race (job tracking + Main-thread serialization), and a Stop-vs-pending-restart race |
| P0-5 | Host pushes the full 60-100 KB telemetry envelope every second to a backgrounded or screen-off… | Android + PC host | opus | committed ce68e66 | RemexMessage.cs, TelemetryPauseGate.cs (new), PingPongHandler.cs, TelemetryPauseTests.cs (new), TelemetryBackgroundGate.kt (new), RemexClientManager.kt, TelemetryBackgroundGateTest.kt (new), CHANGELOG.md, libs.versions.toml, build.gradle.kts | new telemetry_pause/telemetry_resume message types, per-connection host-side gate (TaskCompletionSource, no polling), ProcessLifecycleOwner-driven client; hardware widget placement suppresses the pause; lastSentSnapshot guard confirmed untouched; parallel csharp+kotlin reviews both PASS; fixed a flaky test and a failed-send desync (gate rollback on send failure) |
| P0-6 | Widget cache writes the 60-100 KB telemetry JSON to SharedPreferences every render interval, a… | Android | sonnet | committed ce68e66 | WidgetDataCache.kt | 3 review rounds: round 1 caught a coroutine compile error and a HIGH data-loss bug (gating the one-shot launcher_sync write, not just the widget-update, on widget presence); round 2 caught my own comment lying about code that still gated the telemetry write too. Final: writes unconditional (throttled), only the widget-render/update calls are gated on widget presence |
| P0-7 | Secondary sockets stay open while idle: /ws/files keeps TLS with 10 s OkHttp pings after the l… | Android + core | opus | committed ce68e66 | FileTransferChannelClient.kt, FileTransferEngine.kt, AndroidFileTransferHost.kt, FileHostHandler.kt, RemexClientManager.kt, RemoteDesktopViewModel.kt, DesktopSocketIdlePolicy.kt (new), 2 new test files | idle-close for /ws/files (60s fg/5s bg, lease mechanism spans the full ack-wait) and /ws/desktop catalog (45s, pure DesktopSocketIdlePolicy); review CONFIRMED both critical guards (RG:403 ack-before-complete, catalog-vs-DesktopStart race) hold in the code, not just claimed; fixed 1 MEDIUM (orphaned receive-sink after PC death defeated idle-close for the process lifetime) |
| P0-8 | FileTransferEngine.drainLoop polls every 750 ms for the process lifetime once started and re-f… | Android | sonnet | committed ce68e66 | FileTransferEngine.kt, FileTransferJobService.kt | drainLoop suspends on the queue StateFlow instead of polling; 3 review rounds fixed a job-never-retires race (callback registered after idle already reached) and a cross-thread visibility bug (scope read before assignment across the IO/Main boundary, fixed with @Volatile + reordering) |
| P0-9 | Desktop Task Manager polls every 2 s and rebuilds its ObservableCollection while the window is… | PC desktop UI | sonnet | committed ce68e66 | TaskManagerViewModel.cs, TaskManagerWindowVisibilityGatingTests.cs (new) | PollAsync waits on IsWindowVisible's PropertyChanged instead of polling while hidden; 2 review rounds PASS (round 2 added a missing regression test) |
| P0-10 | Host 1 Hz telemetry sampler (~450 sensors; WMI can block) and media-session sampler (cross-pro… | PC host | sonnet | committed ce68e66 | DemandHold.cs (new), SamplingDemand.cs (new), TelemetrySnapshotGate.cs, TelemetryBackgroundService.cs, MediaSessionBackgroundService.cs, PingPongHandler.cs, HostBootstrapper.cs, TelemetryDemandCoordinator.cs (new), App.axaml.cs, SamplingDemandTests.cs (new), TelemetryDemandCoordinatorTests.cs (new) | lease-based consumer-count gate (connected phone, visible dashboard, tray pins, armed alerts) with a null-safe always-sample default; PeriodicTimer spacing and sensor-alert-keeps-sampling requirements confirmed; 2 review rounds fixed a HIGH pause/resume race that could permanently silence a phone's telemetry stream |
| P0-11 | Android reconnect heartbeat runs forever with no network, screen or lifecycle gate: 5 s wake w… | Android | opus | committed ce68e66 | ReconnectGate.kt (new), RemexClientManager.kt, ReconnectGateTest.kt (new) | heartbeat waits on network+foreground(or screen-on+widget) instead of polling every 5s; review fixed a fail-open bug (registration failure could stick the gate closed forever); DESIGN TRADEOFF NOTED not fixed: a backgrounded phone with no widget stops reconnecting until foregrounded, which also stalls AndroidFileTransferHost/PC-browses-phone until then - intentional per this row's own goal, flagged for Connor's awareness, not a bug |

### P0b: Leaks and unbounded growth

| ID | Finding | Platform | Impl | Status | Files | Note |
|---|---|---|---|---|---|---|
| P0-19 | RemoteDesktopView attaches its cached VM twice (DataContextChanged and AttachedToVisualTree) b… | PC desktop UI | opus | committed 8585c2a | RemoteDesktopView.axaml.cs | AttachViewModel no-ops when the vm is already attached (ReferenceEquals guard); csharp-reviewer PASS, confirmed no path is missed (1 LOW: no test, no Avalonia.Headless harness in this project) |
| P0-20 | RD cursor-shape caches never evict: the host bumps ShapeSerial on every shape change and both… | PC desktop UI + Android | opus | committed 8585c2a | LruCache.cs (new), RemoteDesktopViewModel.cs (both platforms), CursorShapeCache.kt (new), 2 new test files, RemoteDesktopView.axaml.cs (P0-19) | both caches capped at 32 (LRU), cleared on stream stop/disconnect/display switch; parallel csharp+kotlin reviews both PASS, dispose-while-on-screen safety fully traced and confirmed (2 MEDIUM/LOW findings are coverage gaps not bugs, deferred) |
| P0-21 | Android thumbnail map is never cleared and copies the whole map on each insert (C16, ui-17) | Android | sonnet | committed 8585c2a | FileTransferViewModel.kt | soft cap 200, oldest-evicted; browse-time filter drops thumbnails for paths not in new listing; round 1 caught HIGH (eviction never released requestedThumbnails dedup key, permanent blank thumbnails past the cap) - fixed by releasing the dedup key alongside each evicted cache entry; round 2 kotlin-reviewer PASS (1 MEDIUM: eviction logic untested, deferred - extract to testable helper per RemEx-ivkq convention; 1 LOW fixed inline: MAX_CACHED_THUMBNAILS moved to companion object const val) |

### P0c: Stream-path and startup wins

| ID | Finding | Platform | Impl | Status | Files | Note |
|---|---|---|---|---|---|---|
| P0-15 | Thumbnail request reads the whole file and decodes it at full resolution to make a 128 px JPEG… | PC host | sonnet | committed 731df4d | ThumbnailService.cs | SKCodec sampled decode instead of full-res SKBitmap.Decode; JPEG (1/8-step) and WebP (any scale) get real savings, PNG/GIF/BMP fall through to full-size decode (no regression, no saving); csharp-reviewer PASS, 1 MEDIUM caught+fixed (sampled decode copied Unpremul alpha verbatim, unlike the old path which always premultiplies - transparent PNG/WebP would have shown hidden colour through what should read transparent; fixed by forcing Premul when source is Unpremul), 1 LOW deferred (no ThumbnailService tests exist) |
| P0-16 | fps StateFlow changes on every frame and is collected at the RemoteDesktopScreen root, recompo… | Android | opus | committed 731df4d | RemoteDesktopViewModel.kt | throttled the _fps StateFlow write to ~1 Hz (window/rate computation in recordFrameTimestamp still runs every frame, unaffected); armFrameWatchdog resets the throttle so the first value after a stream start publishes promptly; kotlin-reviewer PASS (1 LOW: throttle uses wall-clock currentTimeMillis so an NTP clock-jump-back stalls it, pre-existing weakness shared with the window math, deferred) |
| P0-17 | Desktop RD viewer allocates per frame: fresh MemoryStream, ms.ToArray(), payload.ToArray(), a… | PC desktop UI | opus | committed 731df4d | RemoteDesktopService.cs, RemoteDesktopViewModel.cs | receive loop reuses one buffer instead of a fresh MemoryStream per message, single payload copy instead of two; VM applies frames through a latest-wins _pendingFrame slot instead of unconditional Dispatcher.Post per frame; meta/stream-descriptor clears route through the same slot (deviation from handoff, reasoned and confirmed safe by review) so a coalesced apply can't reorder a clear ahead of a newer frame; csharp-reviewer PASS, StreamSerial guard and dispose-ordering both traced clean, 1 LOW pre-existing (not new) double-dispose-after-Dispose edge case deferred |
| P0-18 | Every profile load re-applies the full theme and rewrites last-seed.json with no unchanged che… | PC desktop UI | opus | committed 731df4d | DashboardLayoutService.cs, ThemeService.cs, DashboardLayoutSidecarSkipTests.cs (new) | ApplyCustomization skipped on LoadAsyncCore's success path when loaded content-equals the already-applied UserSettings (serialize-and-compare, not record equality - List members compare by reference); sidecar rewrite skipped only when both the apply was skipped AND this service already wrote that exact content this session, preserving the RemEx-alwfa.1 first-sidecar guarantee; round 1 csharp-reviewer FAIL on missing tests only (logic itself confirmed sound: guard compliance, equality soundness, startup sequence, caller impact all verified clean) - fixed by adding 6 tests (test-writer); round 2 PASS with 1 MEDIUM (background sidecar writes in 2 tests could outlive the test and leak into the next) fixed inline, 1 LOW (no second-load-still-skips-sidecar test) deferred |

### P0d: Stream and input stalls

| ID | Finding | Platform | Impl | Status | Files | Note |
|---|---|---|---|---|---|---|
| P0-12 | Half-open control socket: neither native ClientWebSocket sets KeepAliveTimeout, and the Androi… | core | opus | todo | | |
| P0-13 | One 16-slot JNI queue carries frames and all data callbacks: a full queue blocks network recei… | core + Android | opus | todo | | |
| P0-14 | Desktop work queue is unbounded with one consumer; with the PC unreachable each queued pointer… | core | opus | todo | | |

### P1: high impact / large effort, or medium / cheap

| ID | Finding | Platform | Impl | Status | Files | Note |
|---|---|---|---|---|---|---|
| P1-1 | mDNS self-heal resets the backoff on any pinned hit, so an mDNS-visible but refusing PC is ret… | Android | opus | todo | | |
| P1-2 | Android quality-slider drag sends one desktop_config per tick; host rebuilds the encoder each… | Android + PC host | opus | todo | | |
| P1-3 | Personalize slider ticks each post a full theme apply with no latest-wins coalescing (ds-1, du… | PC desktop UI | sonnet | todo | | |
| P1-4 | Linux: input runs inline on the /ws receive loop and RunToolWithOutput's 2 s timeout never fir… | PC host (Linux) | sonnet | todo | | |
| P1-5 | Desktop file-transfer chunks travel as Base64-in-JSON although a binary channel exists (ds-11) | PC desktop UI + PC host | opus | todo | | |
| P1-6 | Release publish ships ~102-105 MB of PDBs, mostly native SkiaSharp/HarfBuzz, into publish, ins… | build | sonnet | todo | | |
| P1-7 | Every FFmpegH264Encoder construction spawns `where`/`which ffmpeg` and waits up to 1.5 s; one… | PC host | opus | todo | | |
| P1-8 | H.264 decode thread wakes every 2 ms (500/s) while streaming, whether or not a frame is queued… | Android | opus | todo | | |
| P1-9 | Splash animation (about 3 s) replays on every NavHost creation; the comment claiming it skips… | Android | sonnet | todo | | |
| P1-10 | Keep-awake SetThreadExecutionState engage and disengage run on different pool threads after aw… | PC host | opus | todo | | |
| P1-11 | Cursor loop runs its own 90 Hz PrecisionPacer spin whether or not the cursor moves, and keeps… | PC host | opus | todo | | |
| P1-12 | Every cursor move forces a full cursor-shape capture at up to 90 Hz (ac-16) | PC host | opus | todo | | |
| P1-13 | MJPEG re-encodes and re-sends an identical full JPEG every tick on a static screen (ac-12) | PC host | opus | todo | | |
| P1-14 | Probe cache key includes fps, so slider changes spawn fresh ffmpeg probes (ac-24) | PC host | opus | todo | | |
| P1-15 | Revisiting a folder re-requests every thumbnail; no cache on either side (ah-68, C15) | PC host + Android | sonnet | todo | | |
| P1-16 | DashboardViewModel parses the full telemetry JSON at 1 Hz whenever the VM exists, not only whi… | Android | sonnet | todo | | |
| P1-17 | Two-finger wheel scroll sends one message per touch sample, no throttle (ui-21) | Android + core | opus | todo | | |
| P1-18 | Task Manager process-list parse, filter and sort run on the main thread every poll (ui-34) | Android | sonnet | todo | | |
| P1-19 | ConnectionOrbCard reads an infinite-transition glow in composition (ui-4) | Android | sonnet | todo | | |
| P1-20 | MorphPolygonShape has no equals/hashCode, so outlines rebuild on every recomposition, includin… | Android | sonnet | todo | | |
| P1-21 | Connection chip infinite pulse keeps RenderThread active while connected (ui-8) | Android | sonnet | todo | | |
| P1-22 | Native core logs every routine connect step at ERROR, 5-10 lines per attempt, repeated by the… | core + Android | sonnet | todo | | |
| P1-23 | Every launch spawns powershell.exe to enumerate all firewall filters, uncached (ah-27) | PC desktop UI + PC host | sonnet | todo | | |
| P1-24 | permessage-deflate is not enabled on /ws despite repetitive telemetry JSON (ah-20) | PC host + core | sonnet | todo | | |
| P1-25 | last-seed.json is rewritten atomically on every ApplyAndSave, including slider ticks that do n… | PC desktop UI | opus | todo | | |
| P1-26 | Staging drawer ListBox sits in a bare ScrollViewer, realizing every sensor ever seen (du-4, du… | PC desktop UI | sonnet | todo | | |
| P1-27 | Wallpaper bitmap (~15 MB) stays resident after leaving Wallpaper mode and is decoded at up to… | PC desktop UI | sonnet | todo | | |
| P1-28 | Last decoded RD frame stays resident after stop or disconnect (du-65) | PC desktop UI | opus | todo | | |
| P1-29 | --minimized logon start still builds MainWindow and the whole ShellView tree and decodes the w… | PC desktop UI | opus | todo | | |
| P1-30 | Personalize sheet content (CustomizationVm with system-font enumeration, HctColorWheel's 26 to… | PC desktop UI | sonnet | todo | | |
| P1-31 | Pairing-PIN IPC poll runs every 2 s on the UI thread for the process lifetime although embedde… | PC desktop UI | opus | todo | | |
| P1-32 | Transfer queue stores never prune terminal entries and re-read/rewrite the whole JSON on every… | Android + PC host | opus | todo | | |

### P2: medium impact, medium effort

| ID | Finding | Platform | Impl | Status | Files | Note |
|---|---|---|---|---|---|---|
| P2-1 | Base-theme ResourceInclude is removed and re-inserted on every apply though unchanged, possibl… | PC desktop UI | opus | todo | | |
| P2-2 | Saved-palette tiles regenerate on every ApplyAndSave, including unrelated changes (ds-5) | PC desktop UI | sonnet | todo | | |
| P2-3 | DynamicColorGenerator rebuilds the constant success/warning schemes on every call (ds-6) | PC desktop UI | sonnet | todo | | |
| P2-4 | BgraFrameConverter allocates a fresh full-frame byte[] on the LOH every capture tick (C2, ac-4) | PC host | opus | todo | | |
| P2-5 | Full-resolution GPU readback, then CPU downscale (ac-13) | PC host | opus | todo | | |
| P2-6 | Synchronous Map(D3D11_MAP_READ) right after CopyResource stalls the capture thread on the GPU… | PC host | opus | todo | | |
| P2-7 | No back-pressure from socket send to capture/encode; overwritten frames are still captured and… | PC host | opus | todo | | |
| P2-8 | Keyframe requests are honored only by a full ffmpeg respawn (ac-25) | PC host | opus | todo | | |
| P2-9 | Telemetry resends every sensor's static metadata (name, unit, category, ids) every second (C5,… | core + PC host | opus | todo | | |
| P2-10 | Each mouse move or scroll builds JSON, crosses JNI, allocates an envelope and byte[] and retur… | Android + core | opus | todo | | |
| P2-11 | Telemetry history copies the whole map and two 40-point lists per sensor per tick with no key… | Android | sonnet | todo | | |
| P2-12 | 17 MB native library load and JNI init run on the main thread in onCreate (ui-11) | Android | opus | todo | | |
| P2-13 | No app-specific Baseline Profile (ui-9) | Android | sonnet | todo | | |
| P2-14 | "One ResourcesChanged per apply" is defeated: ~12 tree-wide notifications per apply; font and… | PC desktop UI | sonnet | todo | | |
| P2-15 | Every apply replaces every palette brush, restarting 200-400 ms BrushTransitions even when the… | PC desktop UI | opus | todo | | |
| P2-16 | Wallpaper BlurEffect re-runs over every dirty region, including 1 Hz telemetry repaints (du-15… | PC desktop UI | sonnet | todo | | |
| P2-17 | Every navigation rebuilds the page tree and each canvas card eagerly builds a 23-item ContextM… | PC desktop UI | sonnet | todo | | |
| P2-18 | Launcher icon upgrade re-extracts known-unfixable icons on every start, on the UI thread (ah-6… | PC desktop UI | sonnet | todo | | |
| P2-19 | First paint waits on synchronous pre-Avalonia work: embedded host StartAsync, stale-port recla… | PC desktop UI + PC host | opus | todo | | |
| P2-20 | Linux input fallback forks one xdotool/ydotool per pointer sample or scroll detent (ah-57) | PC host (Linux) | sonnet | todo | | |
| P2-21 | A duplicate file_transfer_start TransferId overwrites the map entry, orphaning the first FileS… | PC host | sonnet | todo | | |
| P2-22 | Warm Linux PipeWire session double-copies frames and polls via Task.Run every 5 ms (ac-6) | PC host (Linux) | opus | todo | | |

### P3: cheap polish (batched: one verify and one review per batch)

| Batch | Rows | Platform | Impl | Status | Files | Note |
|---|---|---|---|---|---|---|
| P3-B1 | P3-1 | core + Android | opus | todo | | |
| P3-B2 | P3-2, P3-3, P3-4 | core | opus | todo | | |
| P3-B3 | P3-5, P3-6, P3-7, P3-8, P3-9, P3-10 | Android | opus | todo | | |
| P3-B4 | P3-11, P3-12, P3-13, P3-14, P3-15, P3-16 | Android | opus | todo | | |
| P3-B5 | P3-17, P3-18, P3-19, P3-20, P3-21 | Android | sonnet | todo | | |
| P3-B6 | P3-22, P3-23, P3-24, P3-25, P3-26, P3-27 | build | opus | todo | | |
| P3-B7 | P3-29, P3-30, P3-31, P3-32 | build | sonnet | todo | | |
| P3-B8 | P3-28, P3-51, P3-52, P3-54, P3-55, P3-56 | PC desktop UI | opus | todo | | |
| P3-B9 | P3-57, P3-58, P3-59, P3-60, P3-61, P3-62 | PC desktop UI | sonnet | todo | | |
| P3-B10 | P3-63, P3-64, P3-65, P3-66, P3-67, P3-68 | PC desktop UI | opus | todo | | |
| P3-B11 | P3-33, P3-34, P3-35, P3-36, P3-37, P3-38 | PC host | opus | todo | | |
| P3-B12 | P3-40, P3-41, P3-43, P3-44, P3-45, P3-46 | PC host | opus | todo | | |
| P3-B13 | P3-47, P3-48, P3-49, P3-50 | PC host | sonnet | todo | | |
| P3-B14 | P3-39 | Android + PC host | opus | todo | | |
| P3-B15 | P3-42 | PC host (Linux) | opus | todo | | |
| P3-B16 | P3-53 | PC desktop UI + PC host | sonnet | todo | | |

P3 row titles:

- P3-1: Android desktop-stream receive copies each H.264 frame through a fresh uncapped MemoryStream, ms.ToArray() an…
- P3-2: Multi-frame JSON receive accumulators (telemetry, v2 file chunks) are not pooled and grow onto the LOH (C7)
- P3-3: No JavaScriptEncoder set, so base64 '+' and non-ASCII escape as \uXXXX on all serialization (C8)
- P3-4: Command ingress decodes the payload to a string before deserializing and serializes the response to a string…
- P3-5: Transfer queue list copied per 256 KiB frame; each emission recomposes the File Manager body (C22, ui-16)
- P3-6: AndroidFileTransferHost handles file control messages one at a time; DROP_OLDEST(8) drops later requests (ar-…
- P3-7: Each reconnect makes two whole-file DataStore writes and decrypts every stored pin (ar-17)
- P3-8: Legacy v2 transfer progress posts a system notification per message, no rate limit (ar-19, ui-19)
- P3-9: Release APK keeps Log.d/Log.v; frame-sampled debug logs fire several times a second while streaming (ar-21)
- P3-10: AndroidFileTransferHost.start() leaks two DataStore collectors and a FileHostHandler per service re-creation…
- P3-11: First composition takes the null-prefs branch and is torn down (ui-12)
- P3-12: AppLauncherWidget decodes every icon although only selected apps show (ui-14)
- P3-13: Thumbnails decoded on the main thread during composition, no cache across recompositions (ui-18)
- P3-14: Trackpad inertia sends moves every 16 ms, twice the normal rate (ui-22)
- P3-15: Gesture loop allocates a filtered pointer list per touch event (ui-23)
- P3-16: Decoder double-scans P-frame payloads and copies whole IDRs (ui-26)
- P3-17: Launcher grid and Recent carousel both re-decode base64 icons (ui-27)
- P3-18: Now-playing sheet and docked mini-player recompose their whole body every 1 s while playing (ui-28, ui-29)
- P3-19: Tutorial pager reads currentPageOffsetFraction in composition (ui-30)
- P3-20: Coach overlay demos read Animatable values in composition (ui-31)
- P3-21: Background auto-polls toggle isRefreshing, animating pull-to-refresh every 4 s (ui-35)
- P3-22: Bundled ML Kit barcode model is ~6.8 MB (18% of the APK) for one QR-only scan (C29)
- P3-23: Firebase Analytics and Crashlytics auto-initialize on every process start, including QS-tile and widget cold…
- P3-24: Blanket `{ *; }` keep rules on ML Kit, datatransport, App Startup, WorkManager and Glance pin 1.12 MB of Glan…
- P3-25: ReadyToRun more than doubles the CsWinRT projection (+29 MB), off the startup path (C32)
- P3-26: Resident host/desktop process runs on unconfigured GC defaults (C35)
- P3-27: NativeAOT libRemexCore.so (16.1 MB) built with no size or GC tuning (C36)
- P3-28: 1.3 MB CHANGELOG embedded, then re-read and split on every About open and language switch for 12 bullets (C37…
- P3-29: compose ui-tooling is `implementation`, shipping in release with an exported PreviewActivity (C40)
- P3-30: No localeFilters: resources.arsc carries 87 library locales against the app's 9 (C41)
- P3-31: 778 KB of MSIX tile and splash PNGs embedded into Remex.Desktop.dll, never loaded (C45)
- P3-32: Whole scripts/ dev tree, including .remember session state, copied into every publish and Program Files (C46)
- P3-33: Frames captured then discarded on the MJPEG fallback branch (ac-5)
- P3-34: Per-frame backend selection takes the display lock and runs LINQ with capturing lambdas (ac-8)
- P3-35: Cursor-shape poll drives DXGI duplication even when WGC serves the target (ac-15)
- P3-36: Each H.264 frame goes out as two SendAsync calls over TLS (ac-17)
- P3-37: GetScreenSize re-enumerates every monitor on each call on the GDI tier (ac-18)
- P3-38: AdaptiveScaleController is recreated on every encoder rebuild, losing its 5 s state (ac-26)
- P3-39: Every resume from background forces an encoder respawn though a natural IDR is near (ac-29)
- P3-40: Teardown blocks a request thread on _inputProcessingTask.Wait(2 s) and the ffmpeg writer Wait(250 ms) (ac-33)
- P3-41: Loopback /ws/desktop sessions are never removed from _activeSessions, leaking a CTS per session (ac-35)
- P3-42: Linux MJPEG encoder makes a full-res SKBitmap copy and a new destination bitmap per frame (ac-36)
- P3-43: A screenshot during a live stream swaps the cached frame's scale (ac-37)
- P3-44: DXGI pointer-shape refresh copies each new shape three times; inverted cursors mishandled (ac-38)
- P3-45: HWiNFO-absent hosts throw and catch FileNotFoundException on every telemetry tick (ah-4)
- P3-46: mDNS goodbye+announce on every NetworkAddressChanged, no debounce or change check (ah-8, ah-37)
- P3-47: Artwork fallback re-extracts and re-encodes the same app icon on every track change (ah-15, ah-55)
- P3-48: Per-message LogDebug in the /ws receive loop formats arguments with Debug off (ah-23)
- P3-49: PairedDeviceActivityStore rewrites its whole file on every authenticated connect (ah-24, ah-40)
- P3-50: Bootstrap LoggerFactory with a Console provider is never disposed in a console-less WinExe (ah-32, ah-66)
- P3-51: OS ColorValuesChanged re-runs the full theme apply without checking whether light/dark changed (ds-7)
- P3-52: Profile saves write to disk when the content is unchanged (ds-9)
- P3-53: File-transfer progress reformats text and hops threads per message (50-170/s) (ds-12)
- P3-54: PhonePresenceMonitor's 3 s timer runs while hidden and always reformats (ds-14)
- P3-55: Wallpaper pick decodes at full resolution and copies again at scale 1.0 (ds-15)
- P3-56: Startup update check fires on every launch, including minimized logon starts (ds-17)
- P3-57: Sensor-alert trip has no hysteresis, so a sensor at its threshold re-trips repeatedly (ds-18)
- P3-58: System-font list re-enumerated on every CustomizationViewModel build (ds-19)
- P3-59: Lazy ActivityService constructor does sync disk I/O on the first caller's thread, possibly an agent socket th…
- P3-60: Home "Recent activity" uses a non-virtualizing ItemsControl, realizing all 60 rows (ds-21)
- P3-61: SparklineControl.Render allocates pens, brushes, geometry and point lists per paint (du-5, du-38)
- P3-62: Base64ToImageConverter decodes a new, never-disposed Bitmap on every binding evaluation (du-11, du-36)
- P3-63: Diagnostic Logs VM and view stay subscribed after navigation (LogAdded, ScrollToEndRequested), with O(n) shif…
- P3-64: TryResolveFont forces a glyph typeface on every apply, including the 1.6 MB Nabla folder URI (du-24, du-49)
- P3-65: ImmersiveHost keeps a hidden duplicate RemoteDesktopView once fullscreen has been used (du-53)
- P3-66: Hidden tray flyout keeps pinned-sensor tiles bound to live SensorViewModels and updating every tick (du-56)
- P3-67: Every flyout show rebuilds pinned tiles N+1 times (Clear plus per-item Add) (du-57)
- P3-68: Each apply re-requests the backdrop with a new TransparencyLevelHint array and DWM calls in Mica mode (du-63)

## Live checks for Connor

(none yet)

## Rulings

(none yet)

## Deferred (P4, not in this loop)

- P4-1: JNI hands each video frame over as a freshly allocated Java byte[] (ui-25)
- P4-2: Telemetry is deserialized, re-serialized to a UTF-16 string, copied into the JVM and parsed again in Kotlin e…
- P4-3: launchers.json re-read and deserialized, icons included, on every connect, sync request, add/remove and launc…
- P4-4: Legacy v2 base64 file path round-trips every 64 KiB chunk through JSON and JNI into the unbounded outbound qu…
- P4-5: Every /ws/files data frame is copied twice on send and twice on receive (C24)
- P4-6: v3 download receive does the disk write, SHA-256 and a queue copy on the OkHttp reader thread (C25)
- P4-7: Gradle Exec tasks declare no inputs or outputs, so restore and ILC run on every Android build (C42)
- P4-8: Unused ASP.NET Core MVC/Razor/Blazor/SignalR and VisualBasic assemblies (~8.5 MB) ship in the self-contained…
- P4-9: Linux and macOS Avalonia backends ship in the Windows publish (~4.4 MB) (C44)
- P4-10: Process-list payload carries fields Android discards (path, publisher, version, user, install date) (ui-36)
- P4-11: GDI tier allocates two Bitmaps and a full frame per tick (ac-7)
- P4-12: Fresh byte[] per encoded access unit lands on the LOH at 1080p+ (ac-9)
- P4-13: Linux raw H.264 path makes 2-4 extra full-frame copies through Skia (ac-11)
- P4-14: Encoder output drained one access unit per tick; burst-drain helper underused (ac-20)
- P4-15: Linux WaitForNextFrameAsync allocates a linked CTS and timer per tick and signals timeout by exception (ac-21)
- P4-16: Every /ws/desktop viewer runs its own ffmpeg encoder and BGRA pipe (ac-31)
- P4-17: Viewers share one singleton capture target and cached-frame scale (ac-32)
- P4-18: Every viewer runs its own 90 Hz cursor loop and 10 Hz shape poll (ac-34)
- P4-19: Linux telemetry re-walks /sys/class/hwmon and re-reads name/label and /proc files every tick (ah-5, ah-38)
- P4-20: Windows fallback PerformanceCounter reads re-query the category on each NextValue (ah-6)
- P4-21: Process-list poll opens each process several times and throws for protected processes (ah-7, ah-47)
- P4-22: Windows telemetry tick copies the ~450-sensor list 3-4 times and rebuilds hash sets every second (ah-11, ah-5…
- P4-23: Media sampler builds a fresh set of WinRT RCWs every second and relies on finalizers (ah-13; also in ah-43)
- P4-24: media_artwork reply re-encodes up to 2 MB of cover art to base64/JSON per request per client (ah-14, ah-54)
- P4-25: LinuxPortalInputInjector's 100 ms poll loop is unreachable given serialized callers (ah-61)
- P4-26: Sensor presentation (History update, sparkline invalidation) runs while the window is hidden (du-3)

## Refuted (audit)

- **RP1** (strict): InMemoryLogSink shifts the 3000-entry list under a global lock per append; one dispatcher hop per entry. The mechanism exists, but the cost premise is wrong.
- **RP2** (strict): no baseline/startup profile and no profileinstaller. profileinstaller already ships as a transitive dependency.
- **ac-1**: unchanged desktop re-encoded via cached-frame replay. The capture loop checks IsLive and skips stale replays (RemoteDesktopHandler.cs:661-706).
- **ac-10**: capture backend resources stay resident for the connection. Intentional warm-session design.
- **ac-30**: keep-awake can leak across threads. Refuted there as process-wide, but ah-2/ah-42 confirm it and the spot-check found no dedicated thread; kept as P1-10.
- **ah-16**: a fresh ~74 KB telemetry frame is built every second. Already lazy; built only when a client asks (RemEx-jyuem, TelemetryBackgroundService.cs:158-181).
- **ah-39**: HWiNFO FileNotFoundException every tick. This refutation is wrong: TryEnsureHwInfoOpen swallows the exception (WindowsTelemetryService.cs:653-656), so `_hwinfoAvailable` never flips; kept as P3-45 (ah-4).
- **ah-46**: fallback PerformanceCounter re-queries the whole category. The code holds single pre-built counters; the framework-internal claim survives as P4-20 (ah-6), contested.
- **ah-62**: Windows TypeText allocates an INPUT[] per code-point group. Bursts are short and infrequent.
- **ar-22**: two implicit control-socket keepalives; the link-quality ping does not exist. Documentation and dead-code issue, not a saving; keepalives are needed for NAT.
- **du-47**: standalone 2 s PIN poll runs despite embedded pairing. Refuted on risk (it may be a required fallback), not on facts; C33 (strict) confirms the poll; kept as P1-31 with that caveat.
**Tier counts:** P0 21 rows (42 findings), P1 32 (48), P2 22 (34), P3 68 (83), P4 26 (31). 169 rows cover all 238 confirmed findings; 0 dropped and 6 demoted at spot-check; 11 refuted.

## Log

- 2026-09-23 setup - tracker generated from PRIORITIES.md; loop not started
- 2026-09-23 P0-1 staged - lifecycle-gated Task Manager auto-refresh poll
- 2026-09-23 P0-2 staged - window-visible gate on aurora/gradient/pulse animations, 2 review rounds
- 2026-09-23 P0-3 staged - RD stream foreground-gated, 2 review rounds + 1 self-caught fix
- 2026-09-23 P0-1 reworked - original fix was a no-op, LifecycleStartEffect fixes it, re-reviewed PASS
- 2026-09-23 P0-4 staged - phone RD stream paused in background, 2 review rounds fixed 3 races
- 2026-09-23 P0-5 staged - telemetry pause/resume, new msg types, parallel cs+kt reviews, 2 MEDIUMs fixed
- 2026-09-23 P0-6 staged - widget cache write skips no-consumer case, 3 review rounds fixed a HIGH data-loss bug
- 2026-09-23 P0-7 staged - idle-close for /ws/files and /ws/desktop catalog, both critical guards CONFIRMED, 1 MEDIUM fixed
- 2026-09-23 P0-8 staged - drainLoop suspends instead of polling, 3 review rounds fixed 2 real concurrency bugs
- 2026-09-23 P0-9 staged - Task Manager poll gated on IsWindowVisible, 2 review rounds
- 2026-09-23 P0-10 staged - telemetry/media sampler consumer-count gate, 2 review rounds fixed a HIGH silent-stream race
- 2026-09-24 P0-11 staged - reconnect heartbeat gated on network+foreground/widget, fixed a fail-open bug; last row in P0a, group gate next
- 2026-09-24 P0a group gate - full verify + -Check both pass; detect_changes advisory (critical/breadth-driven, nothing unexpected); committed ce68e66 (47 files); not merged to main yet (P0b/c/d remain in the P0 tier)
- 2026-09-24 P0-19 staged - AttachViewModel no-ops on already-attached vm, closes a double-subscribe leak
- 2026-09-24 P0-20 staged - cursor-shape LRU caches (both platforms), parallel reviews confirmed dispose-safety
- 2026-09-24 P0-21 staged - thumbnail map soft-capped at 200, oldest-evicted; round 1 caught a HIGH (evicted keys left dead in the dedup map, permanent blank thumbnails); fixed and round 2 PASS; last row in P0b, group gate next
- 2026-09-24 P0b group gate - full verify + -Check both pass (one transient suite-ordering hang on an unrelated test, confirmed pre-existing/unrelated by isolated re-run with and without P0b changes); detect_changes advisory (5 processes touched, all within P0-19/20/21 scope, nothing unexpected); committed 8585c2a (10 files); not merged to main yet (P0c/P0d remain in the P0 tier)
- 2026-09-24 P0-15 staged - thumbnail decode sampled via SKCodec instead of full-res, csharp-reviewer PASS, 1 MEDIUM alpha-premultiplication bug caught and fixed
- 2026-09-24 P0-16 staged - fps StateFlow throttled to ~1 Hz, window/rate math still per-frame; kotlin-reviewer PASS, 1 LOW pre-existing clock-jump weakness deferred
- 2026-09-24 P0-17 staged - RD receive loop reuses one buffer instead of per-message MemoryStream, VM applies frames through a latest-wins slot instead of unconditional Post; csharp-reviewer PASS, StreamSerial guard and dispose-ordering traced clean, 1 LOW pre-existing deferred; P0-18 remains in P0c
- 2026-09-24 P0-18 staged - theme apply and sidecar rewrite skipped on content-equal profile loads, fixing startup's double-apply; round 1 FAIL was test-coverage-only (logic confirmed sound), fixed with 6 new tests; round 2 PASS, 1 MEDIUM test-isolation fix applied inline; last row in P0c, group gate next
- 2026-09-24 P0c group gate - full verify + -Check both pass; detect_changes advisory (high risk driven by LoadAsyncCore's breadth touching 8 BuildSavefileAsync-adjacent processes, all within P0-15/16/17/18 scope, nothing unexpected); committed 731df4d (9 files); not merged to main yet (P0d remains in the P0 tier)
