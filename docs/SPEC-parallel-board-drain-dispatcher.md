# SPEC — Parallel-worktree board-drain dispatcher

Status: **partly built.** Phase 0 (`.5.1`), lane provisioning (`.5.2`), the merge queue (`.5.3`) and
clustering (`.5.4`) are implemented; only the dispatcher that drives them (`.5.5`) is not. Implementation
beads are listed at the end with their state. Where a claim is still unverified, it says so — and
several claims that were unverified when this was written have since been measured and corrected
inline, marked **MEASURED**.

Bead: `RemEx-56fu.5`. Parent: `RemEx-56fu` (workflow hardening from the Claude Code Insights report,
2026-08-03), suggestion 8.

## What this is for

`docs/ralph-board-drain.md` describes a **sequential** autonomous loop: one agent, one working copy,
one bead at a time, twelve steps from `bd update --claim` to `docs/ralph-state.jsonl`. It works. Its
ceiling is that everything is serialised behind one working copy — including the parts where the
agent is doing nothing but waiting for a build.

This spec describes a dispatcher that runs several instances of that same loop concurrently, each in
its own `git worktree`, and lands their results through a single serialised merge queue.

**The per-bead contract does not change.** A lane runs `docs/ralph-board-drain.md` verbatim, with the
handful of amendments in [§6](#6-what-changes-inside-a-lane). Everything this spec adds is *around*
the loop: choosing what runs in parallel, isolating it, and merging it safely. If the parallel layer
is deleted, the sequential loop still works exactly as it does now. That is deliberate — the loop is
the asset, the dispatcher is scaffolding.

> **Phase 0 ran on 2026-08-05 and the epic PASSED the gate — see
> `docs/MEASURE-parallel-lane-contention.md`.** Three lanes model out at 2.89× serial bead
> throughput against a 1.8× threshold. Three parts of this spec are now wrong and are corrected
> inline below, marked **MEASURED**: the Android lane token is harmful and removed (§4), the copy
> manifest was missing `safe.directory` (§3), and the machine-contention worry that motivated §1
> is answered. The dominant risk moved from CPU to whether concurrent agent sessions get
> concurrent API throughput, which no local measurement can settle (§1).

## 1. Do not build this yet — Phase 0 first

The premise is that concurrency buys throughput. On this repo that premise is unproven and there is
a specific reason to doubt it: `./scripts/verify.ps1 -Scope dotnet` takes ~49s of almost pure build,
and the lanes would be competing for the same CPU, the same NuGet global package cache, and the same
Gradle daemon. Three lanes each running a build that is 2.5× slower is not three lanes.

Phase 0 measures before anything is built, and carries an explicit kill criterion.

| Measurement | Method | Why it decides the design |
|---|---|---|
| Serial baseline | Wall-clock per bead over ≥10 beads of the existing loop, split into think / edit / verify / review | If verify is not the dominant term, parallelism is solving the wrong problem |
| Contention factor | `verify.ps1 -Scope dotnet` in 1, 2, 3, 4 worktrees simultaneously; record each wall-clock | Gives the real speedup curve instead of an assumed linear one |
| Android contention | Same, `-Scope android` | NativeAOT + NDK is the heaviest build; expected to be the thing that forces a lane cap |
| Dolt concurrency | N concurrent `bd update --claim` / `bd close` against one `BEADS_DIR`, assert no lost writes | The whole design assumes one shared board. If concurrent writes are unsafe, §3 changes shape |
| Ignored-file gap | `git worktree add` a throwaway lane, attempt a full `verify.ps1 -Scope all` | Produces the real copy manifest in §2 empirically, instead of a guessed one |

**Kill criterion.** If three lanes deliver less than **1.8× the serial bead throughput**, stop and
close this epic. The bottleneck is build time, and the correct response is to attack build time —
incremental verify scopes, a warm build server, cutting the `--no-incremental` clean — not to run
three slow builds at once. A dispatcher that produces 1.3× for this much machinery is a net loss.

Recording the negative result is a successful outcome for Phase 0. Write it to
`docs/MEASURE-parallel-lane-contention.md` either way.

**MEASURED — this section is answered.** Serial baseline 3.30 beads/hour (median 18.2 min, 181
samples). Four concurrent `verify.ps1 -Scope dotnet` runs slow each other by 17%; three by 6%.
Modelled throughput at three lanes: **2.89×**, comfortably past the gate.

The reason the gate passed is worth carrying: **build is 8.8% of a bead, not the dominant term.**
The other 91.2% is model API latency and agent reasoning, which does not contend for cores. This
section was written expecting CPU contention to decide the question; it was never in a position to.

**The gate that replaces it.** The model assumes that 91.2% scales linearly across lanes, and no
local measurement can establish that — it depends on API concurrency and rate limits. Phase 0
proves the *machine* is not the bottleneck; it does not prove nothing is. Treat the first live
three-lane run as the remaining gate, measuring real beads/hour against **5.94**. If concurrent
sessions are throttled, every number above is an upper bound.

## 2. Topology

```
Z:\RemEx                    integration working copy. Branch v2.5-board-drain.
                            Operator's tree. The merge queue runs HERE and nowhere else.
                            Holds the single .beads/ Dolt database.

Z:\RemEx.lanes\lane-1       git worktree, branch ralph/lane-1/<bead-id>
Z:\RemEx.lanes\lane-2       git worktree, branch ralph/lane-2/<bead-id>
Z:\RemEx.lanes\lane-N       ...
```

**Lanes live outside the repository root. This is not a style preference.**
`scripts/verify.ps1` fingerprints the source with `git ls-files -c -o --exclude-standard` —
tracked files *plus* untracked-not-ignored ones. A worktree nested at `Z:\RemEx\.worktrees\lane-1`
would appear to that enumeration as a large pile of untracked files, so every lane's receipt would
be polluted by every other lane's edits, and the receipt would stop meaning what it says. Putting
lanes at a sibling path keeps each fingerprint describing exactly one working copy.

Sibling placement then forces the `BEADS_DIR` decision in §3, because bd resolves its workspace by
walking up from the current directory and would find nothing outside the repo.

On Linux the same layout is `~/RemEx.lanes/lane-N` or any sibling path; the dispatcher derives it
from the repo root rather than hardcoding a drive.

### Branch naming and lifetime

`ralph/lane-<n>/<bead-id>` — the lane number makes concurrent branches unambiguous in `git branch`,
the bead id makes the branch self-describing after the fact. Branches are **not deleted on failure**.
A failed lane branch is the evidence for the reopened bead; the dispatcher's reaper deletes only
branches that landed successfully.

## 3. Constraint 1 — `git worktree add` does not copy ignored files

This is the constraint that shapes provisioning. `git worktree add` populates from the index; every
ignored path is simply absent. The inventory below is what that means concretely here, and each row
is a decision, not an oversight.

| Ignored path | Lane gets it? | Decision |
|---|---|---|
| `.beads/`, `.dolt/` | **No — shared, not copied** | Copying forks the board: two lanes would claim the same bead into two databases and neither would see the other. Lanes reach the one real board via `BEADS_DIR=<repo-root>/.beads`. bd's own "no beads database found" error names this variable, and `bd where` confirms the target resolves to `Z:\RemEx\.beads`. **Concurrent-write safety across lanes is unverified — Phase 0 proves it or this design changes.** |
| `.ralph/` (receipts, trx) | **No — and correctly so** | A receipt describes one working copy at one instant. Copying one into a lane would be a lie by construction. Each lane writes its own; see §5 for which receipt the merge gate actually trusts. |
| `artifacts/` | **No — deliberately cold** | Each lane pays one cold build. Copying warm output across working copies is how stale-build defects (`RemEx-t0f3`, `RemEx-n3z6`, `RemEx-u5q0`) get reintroduced. The cost is real and is exactly what Phase 0 measures. |
| `remex.android/local.properties` | **Yes — copy** | Holds `sdk.dir`. Without it the Gradle build fails at configuration time with an error that does not obviously point at provisioning. |
| `remex.android/app/google-services.json` | **Yes — copy if present** | Absent on a machine that has never built with Firebase; the bootstrap treats missing as fine, present as must-copy. |
| `.claude/settings.json`, `.claude/scripts/`, `.claude/skills/` | **Yes — already tracked** | These were un-ignored as part of `RemEx-56fu`. That change is load-bearing here: it is what gives a lane agent the PostToolUse edit guard and the `/ralph` skill. Before it, every lane would have run unguarded. |
| `.claude/*` (everything else) | No | Per-machine state. Correct to omit. |
| **`safe.directory` git config** | **Yes — a config step, not a copy** | **MEASURED, and not predicted by this design.** `Z:` does not record ownership, so git refuses any *new* worktree path with "detected dubious ownership" and `verify.ps1` dies at the fingerprint stage. Every lane needs `git config --global --add safe.directory <lane>` before first use. `safe.directory` already listed `Z:/RemEx-hardening-sweep` and `Z:/RemEx/.claude/worktrees/splash-refresh`, so earlier worktree experiments hit this and patched it by hand. |

The copy set lives in a **manifest file**, `scripts/ralph-lane-manifest.txt` — one path per line,
comments allowed — not inline in the bootstrap script. A manifest is reviewable in a diff and can be
asserted against; an implicit list inside a script silently rots the first time someone adds an
ignored config file.

`scripts/ralph-lane-bootstrap.ps1` provisions one lane: create the worktree, copy each manifest
entry (failing loudly on a missing required entry), write the lane's environment file, and run
`./scripts/verify.ps1 -Scope dotnet` once to prove the lane can build *before* an agent is given
work in it. A lane that cannot build clean is a provisioning bug, and finding it before the agent
starts is the difference between a clear error and a confusing one attributed to the bead.

## 4. Constraint 2 — shared build state

**Correcting the record: `artifacts/` is not shared between worktrees.** The bead that requested this
spec listed that as a hazard. It is not one. `Directory.Build.props:66` sets `UseArtifactsOutput`
with no explicit `ArtifactsPath`; no `Directory.Build.props` exists above the repo root; MSBuild's
default resolves `ArtifactsPath` relative to the directory containing the props file, and every
worktree has its own tracked copy of that file at its own root. Each lane therefore gets
`<lane>/artifacts/` automatically. The bootstrap asserts this rather than assuming it — one check
that the path resolves under the lane root, so a future explicit `ArtifactsPath` cannot silently
re-share it.

The sharing hazard CLAUDE.md documents is Windows↔WSL builds writing the same physical path, which
is orthogonal to worktrees and already handled: `verify.ps1` records `platform` in the receipt and
refuses a cross-platform receipt.

What *is* genuinely shared, and what to do about it:

- **NuGet global package cache** (`~/.nuget/packages`) — shared across all lanes. Concurrent restore
  is lock-protected and safe, but serialises. Accept it; do not set per-lane caches, which would
  multiply disk by N for no correctness gain.
- **MSBuild node reuse** — persistent worker nodes are keyed by toolset, not by working copy, and
  can attach to the wrong tree. Lanes set `MSBUILDDISABLENODEREUSE=1`. Cheap insurance against a
  class of bug that is very hard to diagnose after the fact.
- **Gradle daemon and `~/.gradle` caches** — shared, lock-protected, and the heaviest contender.
  Combined with `org.gradle.configuration-cache=true` in `remex.android/gradle.properties`, the
  configuration cache is keyed per project directory, so lanes do not share entries — they each pay
  a cold configuration. Mitigation is a cap, not isolation: see below.
- **`dotnet build-server`** — shut down per lane at teardown so a lingering server does not hold
  file handles in a worktree the dispatcher is about to remove.

**~~Android lane token.~~ MEASURED — removed. Do not build it.**

The plan was one exclusive token for Android scope, on the assumption that concurrent NativeAOT/NDK
builds would be slower than serialised ones. Measured, they are not:

- three concurrent `assembleRelease`: **1.59×** throughput (1.88× per-build slowdown, cold 175 s
  against a cold solo baseline of 93 s)
- exclusive token, one at a time: **1.00×** by construction

The token converts a measured 1.59× into 1.0×. Android contention is real — roughly 30× worse than
.NET's 1.06× at three lanes — but "worse than .NET" is not the same as "worse than serialising".
Cap total lanes instead; that bounds CPU pressure without singling out a build type.

Removing it also removes an Amdahl trap the token itself created. 46% of bead commits touch
`remex.android/` or `remex.core/`, so gating them on one exclusive resource caps three lanes at
`1/(0.46 + 0.54/3)` = **1.56×** — below the kill criterion in §1. The spec's own mitigation was the
thing most likely to kill the epic.

## 5. Constraint 3 — file-overlap estimation is only a heuristic

The clustering step tries to predict which files each bead will touch, so lanes do not collide. That
prediction will be wrong. Beads record intent, not file sets; an agent implementing a bead
frequently discovers it must edit something nobody anticipated — which is normal and correct
behaviour, not a failure.

So the design does not depend on the prediction being right. Three layers, in increasing order of
how much they are trusted:

Both of the first two layers are `scripts/ralph-cluster.ps1`.

**Layer 1 — estimate (advisory only).** Union of: paths named literally in the bead description and
acceptance criteria; `gitnexus impact` on symbols the bead names; files historically co-changed with
beads carrying the same labels (`git log` over closed beads). Used **only to order and cluster work**
so collisions are rarer. Never used to authorise anything. A bad estimate costs throughput, never
correctness.

Three things about it were settled by measurement rather than by design, and the plan output names
each source separately so a thin estimate is visible instead of silently thin:

- **Literal paths must be validated against the repository.** Real bead text contains
  `Windows/PowerShell` and `codex/openai`, which are shaped exactly like paths and are not paths.
  A candidate is kept only if it, or its parent directory, actually exists.
- **A fourth source was needed and is now the main one: identifiers grepped to files.** Many beads
  name a symbol and no path at all — `file_volumes_request`, `connectionClientId` — and those were
  the beads producing no estimate whatsoever. `git grep -l` resolves them (10 and 2 files
  respectively). An identifier appearing in more files than the cap is too generic to be evidence
  and contributes nothing.
- **The label co-change source is off by default because it was measured and is nearly empty**:
  nine closed `tooling` beads yield ONE distinct file between them, since most closed beads have no
  bead-id-tagged commit within reach of the log window. With it on, the label→file map balloons to
  ~1000 entries across all labels, which is diffuse enough to cause false collisions. A false
  collision costs a whole wave of throughput; a missed one costs one rebase conflict that layer 3
  already handles safely. `-UseLabelHistory` turns it on and the plan reports its yield.
- **`gitnexus impact` is used only when the index is FRESH** (it is stale as of writing). A graph
  describing code that has since changed produces a confidently wrong estimate, and the operator
  cannot tell it is wrong by looking — so the source reports itself as skipped instead.

**`docs/CHANGELOG.md` and `docs/ralph-state.jsonl` are excluded from every estimate, and that is
load-bearing.** Every bead touches the changelog — the merge queue refuses to land one that does not
(§7) — so counting it as overlap would make every bead collide with every other and nothing would
ever be parallelised. Both are `merge=union`, which is what makes ignoring them safe rather than
optimistic. This is the same trap as §6's changelog conflict, one level up.

**Layer 2 — claims (advisory, enforced at start).** A lane records the paths it intends to touch on
its bead, via `bd update <id> --set-metadata`, under a `ralphLanePaths` key. (Not the `ralphLane`
object the design first proposed: `ralphLane` was already taken by the merge queue for the lane
*number*, alongside `ralphLaneState` for the lifecycle. Three flat keys, not one object.) bd is
already the single shared mutable store with a server mediating access, so no new coordination
primitive is introduced — which matters, because a *tracked file* used as a claim registry would
itself become the most contended file in the repo. The planner refuses to schedule a bead whose
estimate intersects a claim a live lane is holding, and refuses to record a claim that collides with
one. A lane that discovers it needs an unclaimed path amends its claim; if the amendment collides,
the lane finishes what it can, stops, and returns the bead with a note.

A claim is live only while its bead's `ralphLaneState` is `working` or `ready-to-land`. A returned or
quarantined bead still has a branch, but no agent is inside it, so its paths are free again.

**Beads with no estimate at all.** A bead naming neither a file nor an identifier could touch
anything, which is not evidence of safety. Exactly one such bead goes out per wave, and only into a
lane that is otherwise empty — two beads nobody can predict are the likeliest pair to hand the merge
queue an avoidable conflict. In practice, with a long ready queue, known work fills every lane and
these are simply not scheduled; the plan says so and says why, because the real fix is to name a file
or a symbol in the bead rather than to loosen the rule.

**Layer 3 — git (the only source of truth).** Overlap is *detected*, never predicted, at land time.
Because merges are serialised (§7), exactly one lane is ever rebasing onto the integration head. A
rebase conflict is a real conflict, and the lane does not resolve it autonomously — a merge
resolution is a code change nobody reviewed and nothing verified. The lane records the conflicting
paths, returns the bead to the queue, and the next attempt starts from the new head with the
conflict noted. Conflicts become slow, not dangerous.

The honest summary: layers 1 and 2 make conflicts *rare*, layer 3 makes them *safe*. Only layer 3 is
load-bearing.

## 6. What changes inside a lane

A lane runs `docs/ralph-board-drain.md` as written, with these amendments:

1. **Step 1 (branch check)** — the lane is already on `ralph/lane-<n>/<bead-id>`, not
   `v2.5-board-drain`. The check becomes "am I on my assigned lane branch", and refusing to work on
   `main` still holds.
2. **Step 2 (dirty tree)** — the existing "never `git checkout -- .`" rule was written because the
   working copy may be shared with another session. In a lane it is not shared, but the rule stays:
   the reason it is right is broader than the reason it was written, and a lane that learns to use
   wildcards is a lane that does it in the integration tree later.
3. **Step 9 (commit)** — unchanged. Commit to the lane branch. Still no push.
4. **Step 10 (`bd close`)** — **moved.** A lane does not close its bead. Work that has not landed on
   the integration branch is not done, and a closed bead whose branch later fails to merge is a lie
   in the tracker. The lane marks the bead ready-to-land — concretely,
   `bd update <id> --set-metadata ralphLaneState=ready-to-land`, which is what the merge queue
   scans for — and the queue closes it after the merge verifies green.
5. **Step 12 (`docs/ralph-state.jsonl`)** — **moved,** for a mechanical reason: every lane appending
   to one tracked file guarantees a merge conflict on every single landing. The merge queue writes
   the entry, in the integration tree, one at a time. It records the lane number alongside the
   existing fields. (Renamed from `.json` while building the queue: `*.json` is inside
   `verify.ps1`'s fingerprint, so the journal would have invalidated its own receipt.
   RemEx-56fu.5.3.)
6. **Step 11 (`docs/CHANGELOG.md`)** — **stays in the lane, and becomes a landing gate.** Only the
   lane knows what changed, so only the lane can write the entry; the queue refuses to land a
   branch that does not touch `docs/CHANGELOG.md`, which turns CLAUDE.md's "no task is complete
   until the changelog has an entry" from a convention into something checked. That refusal
   happens before the merge, so a forgotten entry costs no build.

   This step is why `.gitattributes` now carries `docs/CHANGELOG.md merge=union`. **Measured while
   implementing the queue:** without it, every lane appends under `[Unreleased]`, every landing
   after the first hits a CHANGELOG conflict, and since the queue refuses to auto-resolve
   conflicts on principle, it hands nearly every bead straight back — one bead lands per drain and
   the epic quietly buys nothing. Union merge takes both sides, which for an append-only list is
   always the right answer.
7. **Review gate (step 8)** — unchanged per bead, and additionally mandatory per merge (§7).

Amendments 4 and 5 are the whole difference in the contract: **a lane produces a verified branch, not
a closed bead.**

## 7. The merge queue

`scripts/ralph-merge-queue.ps1`. One queue, one writer, in `Z:\RemEx`. Serialised by construction —
the concurrency is in the lanes, never in the landing. It holds a lock in `.ralph/` so a second
queue in the same tree refuses rather than resetting the first one's merges, and it reconstructs
the queue itself from `git for-each-ref refs/heads/ralph/` plus each bead's `ralphLaneState`, so a
crashed dispatcher loses nothing.

Preconditions, all refused loudly rather than assumed: on the integration branch, never `main` or
`master`, no half-finished rebase or merge, and **a clean tree** — the quarantine path resets hard,
so a dirty tree would be destroyed by the first failure.

For each candidate, in the order lanes finish (the tip commit's date, which needs no bookkeeping of
its own and cannot drift out of step with the branches):

0. Refuse the branch outright if it carries no `docs/CHANGELOG.md` entry, or changes nothing at all.
   Both are checked before the merge, so neither costs a build.
1. Rebase the lane branch onto the current integration head. Conflict → layer 3 of §5; the bead goes
   back, the branch is kept as evidence, next candidate. Skipped entirely when the integration head
   is already an ancestor, which is every first landing of a batch — and that fast path needs no
   worktree, so a lane whose worktree was reaped early can still land.
2. Fast-forward the integration branch to the rebased lane branch.
3. Run `./scripts/verify.ps1` **in the integration tree**. This is the receipt that counts. A lane's
   own receipt proves the lane was green in isolation; it says nothing about the lane's work combined
   with everything that landed since. The whole reason to re-verify per landing is that isolated
   green plus isolated green is not green.
4. `-Check` must say **VALID** at the moment of closing, per CLAUDE.md's verification rule.
5. PASS → `bd close <id>`, append `docs/ralph-state.jsonl`, remove the lane worktree, delete the
   lane branch, release the lane. The changelog entry is already there — it was gate 0.
6. FAIL → **quarantine.** Reset the integration branch to the pre-merge commit (`git reset --hard`
   to the recorded SHA, safe here because the integration tree is clean by invariant at this point
   and the SHA was captured in step 1). Reopen the bead with the failure output attached via
   `bd update --status open --append-notes`. Keep the branch **and its worktree**. Do not retry
   automatically — a lane that verified green alone and fails on integration has found a real
   interaction, and that deserves a human or a fresh attempt with the failure in hand, not a retry
   loop. The pre-merge receipt is put back as part of the reset: the failing run overwrote it, and
   leaving a FAIL receipt describing a tree that has been returned to a proven-green commit would
   throw the proof away for no reason.

   Anything the queue did *not* anticipate is caught the same way. A crash between the
   fast-forward and the close would otherwise leave the integration branch holding an unverified
   merge, so the script resets to the recorded SHA on any unhandled error too. This is not
   hypothetical — the first end-to-end run crashed in exactly that window.

**Cost, stated plainly.** One full verify per landing. This spec originally ran `-Scope dotnet` per
landing with `-Scope all` once per batch, on the assumption Android would dominate. **Phase 0
measured otherwise:** `-Scope all` is 94s against ~49s for dotnet, with the queue at 14%
utilisation — doubling a number that small does not make it a bottleneck. So the default is now
`-Scope all` per landing, which buys back what the trade gave away: an Android regression
attributed to a commit rather than to a batch. `-Scope dotnet` remains available for a batch known
to be .NET-only, and the queue then runs one `-Scope all` at the end if any landing touched
`remex.android/` or `remex.core/` — the original fallback, kept for the case it was written for.
Note that the Android *lane token* is gone entirely; Phase 0 found it actively harmful (§4).

## 8. The dispatcher

`scripts/ralph-dispatch.ps1`, cross-platform pwsh, single source of truth with no `.sh` twin — the
same rule `scripts/verify.ps1` follows, per CLAUDE.md's parity section.

```
./scripts/ralph-dispatch.ps1 -Lanes 3                 # plan, provision, run, land, reap
./scripts/ralph-dispatch.ps1 -Lanes 3 -PlanOnly       # print the clustering and exit
./scripts/ralph-dispatch.ps1 -Land                    # drain the merge queue only
./scripts/ralph-dispatch.ps1 -Reap                    # tear down finished lanes
./scripts/ralph-dispatch.ps1 -Status                  # what is running, claimed, queued
```

Phases: **plan** (read the ready queue, estimate per §5 layer 1, cluster into non-overlapping
batches, respect the Android token) → **provision** (bootstrap per §3) → **run** (launch one agent
per lane on `docs/ralph-board-drain.md` with the §6 amendments) → **land** (§7) → **reap** (delete
landed branches and their worktrees, keep quarantined ones).

`-PlanOnly` exists because the clustering is the part most likely to be wrong, and it must be
inspectable without provisioning anything.

**Constraint 4 — no heredocs.** Every script is a real file under `scripts/`. Every agent prompt is a
real file under `docs/`. The dispatcher composes by passing file paths, never by generating script
text inline. Heredocs have repeatedly produced quoting and encoding corruption in this repo; the
prohibition is absolute here and there is no case in this design that needs one.

**Failure of the dispatcher itself must not be silent.** If it dies mid-run, the state that matters —
which bead is claimed by which lane, and which branches exist — lives in bd and in `git branch`, both
of which survive the process. `-Status` reconstructs from those two sources alone and holds no
authoritative state of its own. No in-memory or `.ralph/`-resident state is trusted across
invocations; that is the same reasoning that moved loop state to the tracked `docs/ralph-state.jsonl`
in the first place.

## 9. Constraints summary

| # | Constraint | Where handled |
|---|---|---|
| 1 | `git worktree add` does not copy ignored files | §3 — inventory table, manifest file, bootstrap; `BEADS_DIR` for the board, no copy for receipts or artifacts |
| 2 | Shared `artifacts/` between builds | §4 — **not actually shared between worktrees**; asserted in bootstrap. Real sharing (NuGet, Gradle, MSBuild nodes) mitigated; Android capped by token |
| 3 | Overlap estimation is only a heuristic | §5 — three layers; only git-at-merge-time is trusted; conflicts return the bead rather than being auto-resolved |
| 4 | Write scripts to files, never heredocs | §8 — every script and prompt is a real file; dispatcher passes paths |
| 5 | *(new)* `verify.ps1` fingerprint sees untracked files | §2 — lanes placed outside the repo root |
| 6 | *(new)* the bd database is gitignored and single | §3 — `BEADS_DIR`; concurrent-write safety is a Phase 0 gate |
| 7 | *(new)* a lane's receipt does not describe the merged tree | §7 — the integration-tree receipt is the one that gates closing |
| 8 | *(new)* every lane appending `ralph-state.jsonl` conflicts every time | §6 — the merge queue writes it, serialised; and `merge=union` for the changelog, without which every landing after the first conflicts (measured) |

Constraints 5–8 were found while writing this spec and were not in the originating bead.

## 10. Open questions

- ~~**Dolt concurrent writes.**~~ **MEASURED — closed, safe.** Six concurrent `bd` processes
  appending to six distinct beads, then all six to the *same* bead: 12/12 writes reported success
  and 12/12 survived read-back. Zero lost writes. Lanes may write to bd directly as designed.
- **Agent launch mechanism.** Whether lanes are separate Claude Code sessions, background tasks, or
  subagents is deliberately unspecified. It is the most likely thing to change and the design does
  not depend on it: a lane is anything that can run the loop in a directory and exit.
- **Whether a lane should verify at all.** If the merge queue re-verifies every landing anyway, a
  lane's own verify is partly redundant. Keeping it means a broken lane fails in the lane instead of
  poisoning the queue, which is worth the duplication — but Phase 0's numbers may say otherwise.
- **Interaction with `/ralph`.** The skill is a pre-flight for the sequential loop. Whether it grows
  a parallel mode or the dispatcher stays a separate entry point is deferred until something exists
  to launch.

## 11. Implementation beads

Filed off this spec. Phase 0 blocks everything else — deliberately.

| Bead | Work | State |
|---|---|---|
| `RemEx-56fu.5.1` | Phase 0: measure serial baseline, lane contention, Dolt concurrency, ignored-file gap. Write `docs/MEASURE-parallel-lane-contention.md`. Honour the 1.8× kill criterion | **done** — 2.89×, passed |
| `RemEx-56fu.5.2` | `scripts/ralph-lane-manifest.txt` + `scripts/ralph-lane-bootstrap.ps1`, including the artifacts-path assertion | **done** |
| `RemEx-56fu.5.3` | Merge queue: `scripts/ralph-merge-queue.ps1`, integration-tree verify, quarantine-on-fail, journal writes, changelog gate | **done** |
| `RemEx-56fu.5.4` | Clustering and claims: `scripts/ralph-cluster.ps1`, layers 1 and 2 of §5 | **done** |
| `RemEx-56fu.5.5` | `scripts/ralph-dispatch.ps1` end to end, plus the §6 amendments to `docs/ralph-board-drain.md` | open |

`.5.5` inherits one loose end from `.5.3`: the queue reads `ralphLaneState` from bead metadata, and
nothing writes it yet. Until the §6 amendments land, a lane is marked ready by hand with
`bd update <id> --set-metadata ralphLaneState=ready-to-land`. The queue is deliberately strict about
this rather than inferring readiness from a branch existing — a half-finished lane must not land.
