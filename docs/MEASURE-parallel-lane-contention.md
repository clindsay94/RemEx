# MEASURE — parallel lane contention (Phase 0)

Bead: `RemEx-56fu.5.1`. Gate for `docs/SPEC-parallel-board-drain-dispatcher.md`.
Measured 2026-08-05 on the Windows box (16 cores, `Z:` with 405 GB free), branch
`v2.5-board-drain` at `643f5a8`.

**Verdict: PASS.** Three lanes model out at **2.89× serial bead throughput** against a
1.8× kill criterion. Build the dispatcher — with three corrections to the spec, below.

**The one thing this does not measure** is in [§7](#7-the-risk-this-did-not-measure), and it
is the risk that now dominates.

## 1. Serial baseline

Derived from git history rather than by running ten fresh beads — the loop has already
produced 197 timestamped bead commits, and re-running them would have cost hours to learn
something already recorded.

Method: gaps between consecutive `(RemEx-xxxx)` commits on `v2.5-board-drain`. A gap is one
bead's wall-clock *if the loop was running continuously*, so gaps over 45 minutes are dropped
as operator idle — well above any plausible single-bead time, well below a break.

| | |
|---|---|
| bead commits | 197 (2026-07-14 → 2026-08-05) |
| usable gaps | 181 (16 dropped as idle) |
| min / p25 | 2.5 / 11.5 min |
| **median / mean** | **18.2 / 18.2 min** |
| p75 / p90 / max | 24.4 / 31.6 / 42.6 min |
| active loop time | 54.8 h |
| **serial throughput** | **3.30 beads/hour** |

Median and mean are identical to one decimal place, so the distribution is symmetric rather
than long-tailed. That matters: a long tail would mean a few monster beads dominate and
per-bead averages mislead. They don't.

**Kill threshold: three lanes must clear 5.94 beads/hour.**

## 2. .NET build contention

`./scripts/verify.ps1 -Scope dotnet` run simultaneously in N worktrees. Each lane's own
wall-clock is recorded rather than just the batch total, so a single straggler can't hide.

| lanes | mean/lane | batch | slowdown | build throughput |
|---|---|---|---|---|
| 1 | 53.3 s | 53.5 s | 1.00× | 1.00× |
| 2 | 53.6 s | 55.2 s | 1.01× | 1.93× |
| 3 | 56.4 s | 58.0 s | **1.06×** | **2.76×** |
| 4 | 62.5 s | 63.8 s | 1.17× | 3.34× |

All lanes green (1883/1884 passing, 1 skipped) at every N.

**Four concurrent clean-build-plus-full-test-suite runs slow each other by 17%.** The spec's
central worry — lanes fighting over CPU — is not real at this scale on 16 cores. A full
`verify.ps1` is nowhere near saturating the machine.

## 3. Android build contention

Measured with `gradlew assembleRelease`, **not** `testReleaseUnitTest` — that task does not
exist in this project (see [§6](#6-defect-found-verifyps1--scope-android-has-never-worked)).
`assembleRelease` is the heavy path anyway: it drives the NativeAOT publish and
`lintVitalRelease`.

| | |
|---|---|
| cold lane, solo | **93 s** (produced a complete release APK including the 16.9 MB `libRemexCore.so`) |
| 3 concurrent, cold | 175 s and 174 s |
| slowdown | **1.88×** |
| throughput at 3 lanes | **1.59×** |

(The third lane in that run finished in 68 s because it was already warm from the solo
measurement. The two cold numbers are the honest ones.)

Android contention is real and roughly 30× worse than .NET's. It is still not bad enough to
justify serialising — see [§5](#5-correction-the-android-lane-token-is-harmful).

## 4. Throughput model

Combining the above. `remex.android/` or `remex.core/` is touched by 91 of 197 bead commits
(46%), so the expected build cost per bead is `53.3 + 0.46 × 93 = 96 s`.

**Build is 8.8% of an 18.2-minute bead. The other 91.2% is model API latency and agent
reasoning, which does not contend for cores at all.**

That single ratio is why the verdict is a pass. Contention only ever applied to a twelfth of
the work.

| lanes | build slowdown | bead slowdown | throughput | beads/hr | vs 1.8× gate |
|---|---|---|---|---|---|
| 1 | 1.00× | 1.000× | 1.00× | 3.3 | — |
| 2 | 1.20× | 1.018× | 1.97× | 6.5 | pass |
| 3 | 1.43× | 1.037× | **2.89×** | **9.5** | **pass** |
| 4 | 1.68× | 1.060× | 3.77× | 12.5 | pass |

The N=2 and N=4 Android slowdowns are interpolated from the single N=3 measurement and are
marked as guesses in the model; the N=3 row, which is the one the gate turns on, uses only
measured values.

**Merge queue load at 3 lanes:** 9.5 landings/hour × 53 s integration verify = **14%
utilisation**. The serialised merge queue is not a bottleneck and needs no batching
optimisation.

## 5. Correction — the Android lane token is harmful

The spec reserved a single exclusive token so only one lane could run an Android build at a
time, on the assumption that concurrent NativeAOT/NDK builds would be slower than serialised
ones. Measured, they are not:

- three concurrent Android builds: **1.59×** throughput
- exclusive token, one at a time: **1.00×** by construction

The token converts a measured 1.59× into 1.0×. **Remove it.** Cap total lanes instead, which
bounds CPU pressure without singling out a build type.

This also removes the Amdahl problem the token created. With 46% of beads needing an
exclusive resource, three lanes would have been capped at `1/(0.46 + 0.54/3)` = 1.56× —
*below the kill criterion*. The spec's own mitigation was the thing most likely to kill it.

## 6. Defect found — `verify.ps1 -Scope android` has never worked

`scripts/verify.ps1:377` runs `gradlew clean testReleaseUnitTest`. That task does not exist:
364 tasks enumerated, zero matching `testRelease`, and direct invocation returns *"Task
'testReleaseUnitTest' not found in root project 'RemEx' and its subprojects."* Only
`testDebugUnitTest` exists. Reproduces identically in the main repo and in a fresh worktree,
so it is not a provisioning artefact.

`-Scope android` and `-Scope all` have therefore always failed, and CLAUDE.md documents
`-Scope all` as a working command.

It survived because `-Scope dotnet` was the only scope ever run and its PASS receipt was read
as the tool working — the same "verified against what?" gap the receipt mechanism exists to
close, one level up. Filed as **`RemEx-thvf`** (P1). It is not a rename: there is a real
conflict between the release-only rule and AGP not generating a release unit-test variant here.

`verify.ps1:379` also pipes gradle output to `Out-Null` and reports only "Android unit tests
failed", which hid the actual message. Whatever the fix, surface gradle's failure text.

## 7. The risk this did not measure

**The model assumes the 91.2% non-build portion scales linearly across lanes. That is not
measured and cannot be measured from this machine.** It is model API latency and agent
reasoning, and whether three concurrent sessions get three sessions' worth of throughput
depends on API concurrency and rate limits, not on cores or disk.

Stated plainly: **Phase 0 proves the machine is not the bottleneck. It does not prove
anything is not.** If concurrent agent sessions are rate-limited, the measured 2.89× becomes
whatever the API allows, and every number above is an upper bound.

This is now the dominant unknown, and it can only be answered by running real lanes. The
recommendation is to treat the first live 3-lane run as the remaining gate: measure actual
beads/hour against the 5.94 threshold before building out clustering and claim tracking.

## 8. Provisioning findings

From standing up four real worktrees at `Z:/RemEx.lanes/probe{,2,3,4}`.

| Finding | Consequence |
|---|---|
| **`git config --global --add safe.directory <lane>` is required** | `Z:` does not record ownership, so git refuses a new worktree with "dubious ownership". `verify.ps1` failed loudly at the fingerprint stage — correct behaviour, but the lane is unusable until the exception is added. `safe.directory` already listed `Z:/RemEx-hardening-sweep` and `Z:/RemEx/.claude/worktrees/splash-refresh`, so earlier worktree experiments hit this and patched it by hand. **New manifest step.** |
| `remex.android/local.properties` absent | Must copy. Confirmed: with it copied, the lane's `assembleRelease` succeeds. |
| `remex.android/app/google-services.json` absent | Must copy when present. |
| `.beads/` and `.dolt/` absent | `bd` in a lane fails with "no beads database found". `BEADS_DIR=Z:/RemEx/.beads` fixes it and resolves to the real shared board — confirmed via `bd where` and a live `bd list` from inside a lane. |
| `.claude/settings.json`, `.claude/scripts/`, `.claude/skills/` **present** | The un-ignoring done in `RemEx-56fu` is load-bearing: lanes inherit the PostToolUse edit guard and the `/ralph` skill. Before that change every lane would have run unguarded. |
| `.ralph/` and `artifacts/` absent | Correct by design. Each lane writes its own receipt. |
| **`artifacts/` is per-lane** | Confirmed empirically: 731 MB landed at `Z:/RemEx.lanes/probe/artifacts`. The spec's correction to the originating bead's constraint 2 holds — worktrees do not share artifact roots. |
| Cold lane build costs +9 s | 58 s cold vs 49 s warm. `verify.ps1` force-cleans anyway, so there is almost no warm cache to lose. Provisioning a lane is cheap. |

## 9. Actions

1. **Proceed with the epic.** `RemEx-56fu.5.2` through `.5.5` are unblocked by this result.
2. **Amend the spec:** remove the Android lane token (§5); add `safe.directory` to the copy
   manifest (§8); replace §1's kill criterion with §7's live-lane gate, since the machine-side
   question is now answered.
3. **Fix `RemEx-thvf`** before the merge queue is built — the queue's whole contract is
   "integration verify must pass", and one of its scopes currently cannot.
4. **Treat the first live 3-lane run as the real gate** against 5.94 beads/hour.

## 10. Reproducing

Probe scripts were written to the session scratchpad, not to `scripts/` — they are
measurement one-offs, not repo tooling:

- `probe_dolt_concurrency.py` — 6 concurrent `bd` writers, distinct beads and same bead
- `probe_contention.ps1` — the N=1..4 .NET matrix
- `probe_android_contention.ps1` — concurrent `assembleRelease`

**Dolt concurrency result:** 6 concurrent `bd` processes appending to six distinct beads, then
all six to the *same* bead. 12/12 writes reported success and 12/12 survived read-back. Zero
lost writes. The shared-`BEADS_DIR` assumption in the spec is safe, and open question 1 in
spec §10 is closed.
