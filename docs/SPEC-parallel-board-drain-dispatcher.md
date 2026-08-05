# SPEC — Parallel-worktree board-drain dispatcher

Status: **design, not built.** Implementation beads are filed off this document and listed at the
end. Nothing here has been implemented; where a claim is unverified, it says so.

Bead: `RemEx-56fu.5`. Parent: `RemEx-56fu` (workflow hardening from the Claude Code Insights report,
2026-08-03), suggestion 8.

## What this is for

`docs/ralph-board-drain.md` describes a **sequential** autonomous loop: one agent, one working copy,
one bead at a time, twelve steps from `bd update --claim` to `docs/ralph-state.json`. It works. Its
ceiling is that everything is serialised behind one working copy — including the parts where the
agent is doing nothing but waiting for a build.

This spec describes a dispatcher that runs several instances of that same loop concurrently, each in
its own `git worktree`, and lands their results through a single serialised merge queue.

**The per-bead contract does not change.** A lane runs `docs/ralph-board-drain.md` verbatim, with the
handful of amendments in [§6](#6-what-changes-inside-a-lane). Everything this spec adds is *around*
the loop: choosing what runs in parallel, isolating it, and merging it safely. If the parallel layer
is deleted, the sequential loop still works exactly as it does now. That is deliberate — the loop is
the asset, the dispatcher is scaffolding.

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

**Android lane token.** At most **one** lane at a time may hold Android scope. The dispatcher owns a
single token; a lane whose bead touches `remex.android/**` or `remex.core/**` (NativeAOT surface)
must acquire it before verifying, and blocks otherwise. NativeAOT + NDK is heavy enough that two
concurrent Android builds are plausibly slower than one, and Phase 0's Android contention number
sets the token count — if it turns out two is fine, the token count becomes 2. It starts at 1
because that is the safe default.

## 5. Constraint 3 — file-overlap estimation is only a heuristic

The clustering step tries to predict which files each bead will touch, so lanes do not collide. That
prediction will be wrong. Beads record intent, not file sets; an agent implementing a bead
frequently discovers it must edit something nobody anticipated — which is normal and correct
behaviour, not a failure.

So the design does not depend on the prediction being right. Three layers, in increasing order of
how much they are trusted:

**Layer 1 — estimate (advisory only).** Union of: paths named literally in the bead description and
acceptance criteria; `gitnexus impact` on symbols the bead names; files historically co-changed with
beads carrying the same labels (`git log` over closed beads). Used **only to order and cluster work**
so collisions are rarer. Never used to authorise anything. A bad estimate costs throughput, never
correctness.

**Layer 2 — claims (advisory, enforced at start).** A lane records the paths it intends to touch on
its bead, via `bd update <id> --metadata`, keyed under a `ralphLane` object. bd is already the single
shared mutable store with a server mediating access, so no new coordination primitive is introduced —
which matters, because a *tracked file* used as a claim registry would itself become the most
contended file in the repo. The dispatcher refuses to start a lane whose claimed paths intersect a
live claim. A lane that discovers it needs an unclaimed path amends its claim; if the amendment
collides with a live claim, the lane finishes what it can, stops, and returns the bead with a note.

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
   in the tracker. The lane marks the bead ready-to-land; the merge queue closes it after the merge
   verifies green.
5. **Step 12 (`docs/ralph-state.json`)** — **moved,** for a mechanical reason: every lane appending
   to one tracked file guarantees a merge conflict on every single landing. The merge queue writes
   the entry, in the integration tree, one at a time. It records the lane number alongside the
   existing fields.
6. **Review gate (step 8)** — unchanged per bead, and additionally mandatory per merge (§7).

Amendments 4 and 5 are the whole difference in the contract: **a lane produces a verified branch, not
a closed bead.**

## 7. The merge queue

One queue, one writer, in `Z:\RemEx`. Serialised by construction — the concurrency is in the lanes,
never in the landing.

For each candidate, in the order lanes finish:

1. Rebase the lane branch onto the current integration head. Conflict → layer 3 of §5; the bead goes
   back, the branch is kept as evidence, next candidate.
2. Fast-forward the integration branch to the rebased lane branch.
3. Run `./scripts/verify.ps1` **in the integration tree**. This is the receipt that counts. A lane's
   own receipt proves the lane was green in isolation; it says nothing about the lane's work combined
   with everything that landed since. The whole reason to re-verify per landing is that isolated
   green plus isolated green is not green.
4. `-Check` must say **VALID** at the moment of closing, per CLAUDE.md's verification rule.
5. PASS → `bd close <id>`, append `docs/ralph-state.json`, add the `docs/CHANGELOG.md` entry, delete
   the lane branch, release the lane.
6. FAIL → **quarantine.** Reset the integration branch to the pre-merge commit (`git reset --hard`
   to the recorded SHA, safe here because the integration tree is clean by invariant at this point
   and the SHA was captured in step 1). Reopen the bead with the failure output attached via
   `bd update --status open --append-notes`. Keep the branch. Do not retry automatically — a lane
   that verified green alone and fails on integration has found a real interaction, and that
   deserves a human or a fresh attempt with the failure in hand, not a retry loop.

**Cost, stated plainly.** One full verify per landing. At ~49s for `-Scope dotnet` that is
acceptable. `-Scope all` is not, once Android is in the loop — so the queue runs `-Scope dotnet` per
landing and `-Scope all` once per drained batch, or immediately after any landing that held the
Android token. This is a deliberate trade: it accepts that an Android-only regression can be
attributed to a batch rather than to a single commit, in exchange for a merge queue that is not
dominated by NDK builds. If Phase 0 shows Android verify is cheaper than feared, drop the
distinction and always run `-Scope all`.

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
invocations; that is the same reasoning that moved loop state to the tracked `docs/ralph-state.json`
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
| 8 | *(new)* every lane appending `ralph-state.json` conflicts every time | §6 — the merge queue writes it, serialised |

Constraints 5–8 were found while writing this spec and were not in the originating bead.

## 10. Open questions

- **Dolt concurrent writes.** Unverified. Phase 0 gate. If unsafe, the fallback is to funnel all bd
  mutations through the dispatcher process rather than letting lanes write directly — more
  plumbing, same semantics.
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

| Bead | Work |
|---|---|
| `RemEx-56fu.5.1` | Phase 0: measure serial baseline, lane contention, Dolt concurrency, ignored-file gap. Write `docs/MEASURE-parallel-lane-contention.md`. Honour the 1.8× kill criterion |
| `RemEx-56fu.5.2` | `scripts/ralph-lane-manifest.txt` + `scripts/ralph-lane-bootstrap.ps1`, including the artifacts-path assertion |
| `RemEx-56fu.5.3` | Merge queue: `-Land`, integration-tree verify, quarantine-on-fail, state/changelog writes |
| `RemEx-56fu.5.4` | Clustering and claims: layers 1 and 2 of §5, plus `-PlanOnly` |
| `RemEx-56fu.5.5` | `scripts/ralph-dispatch.ps1` end to end, plus the §6 amendments to `docs/ralph-board-drain.md` |
