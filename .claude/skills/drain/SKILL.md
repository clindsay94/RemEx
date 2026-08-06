---
name: drain
description: Run the RemEx board drain in parallel — several lanes at once, each a headless Opus agent in its own worktree, landed through the merge queue. Use when the user says /drain, "drain the board in parallel", "run N lanes", or asks for autonomous bead work across several beads at once. For one bead at a time in the current working copy, use /ralph instead.
---

# Parallel board drain — you are the orchestrator

**You do not work beads in this skill.** Lanes do. Your job is to plan, launch, watch, land, reap,
and keep the operator informed. If you find yourself reading a bead's acceptance criteria or editing
a source file, you have left the skill.

The machinery already exists and is tested. You are driving it, not reimplementing it:

| Script | Does |
|---|---|
| `scripts/ralph-dispatch.ps1` | plan, provision, run, land, reap, status, watch |
| `scripts/ralph-lane-agent.ps1` | one lane = one headless `claude -p` session on Opus |
| `scripts/ralph-cluster.ps1` | which beads can run together, and the path claims |
| `scripts/ralph-merge-queue.ps1` | serialised landing with an integration-tree verify |
| `docs/ralph-board-drain.md` | the loop the lanes run, LANE MODE section included |
| `docs/SPEC-parallel-board-drain-dispatcher.md` | why all of it is shaped this way |

## Step 0 — orient before you touch anything

```
./scripts/ralph-dispatch.ps1 -Status
```

**Read this first, every time, including when the operator sounds like they are starting fresh.**
Lanes outlive sessions. If branches already exist you are resuming someone else's wave, and starting
a new one on top is how two agents end up in one file. What you see decides what you do:

- **nothing** → new wave, go to step 1
- **`working`** → a wave is live; skip to step 4 and watch it
- **`ready-to-land`** → skip to step 5 and land it
- **`quarantined` or `returned`** → tell the operator before anything else; these are the states
  that need a human decision, and they are why the reaper leaves the branch and worktree in place
- **`landed`** → step 6, reap it, then plan the next wave

## Step 1 — plan, and stop

```
./scripts/ralph-dispatch.ps1 -Lanes 3 -PlanOnly
```

Three lanes unless the operator says otherwise — that is the count Phase 0 measured at 2.89× serial
throughput, and the lanes share a NuGet cache and a Gradle daemon, so more is not obviously better.

**This is the one approval gate. Present the plan and wait.** Say, in plain terms: which bead goes to
which lane, what each is predicted to touch, what was not scheduled and why. Call out any bead whose
footprint is unknown — nothing in its text resolved to a file, so only the merge queue protects it.
Note anything that looks wrong to you: a bead that needs the operator's judgment, a security-adjacent
bead they may want to watch, a P1 sitting unscheduled.

Do not provision until they answer. After they do, run to completion without asking again unless a
stop condition below fires.

## Step 2 — provision and launch

```
./scripts/ralph-dispatch.ps1 -Lanes 3 -Launcher ./scripts/ralph-lane-agent.ps1 -NoWait
```

`-NoWait` on purpose: it hands control back to you so you can watch and report, instead of blocking
until every lane is done and telling the operator nothing in the meantime.

Two things will happen that are normal and are not failures. Each lane pays one cold
`verify.ps1 -Scope dotnet` before its agent starts — that is provisioning proving itself, so a
missing `local.properties` reads as a provisioning bug rather than a broken bead. And provisioning
is serial, so three lanes means three builds one after another. Tell the operator roughly how long
that will be rather than going quiet.

If the tree is dirty it refuses, and it is right to: lanes branch from HEAD, so uncommitted work
would be missing from every lane and would then conflict with every landing. Ask the operator to
commit or stash. Do not commit their work for them.

Report the lane → bead map and the log paths (`.ralph/lanes/lane-<n>-<bead>.log`) once the lanes are
up.

**Do not promise a live view of those logs.** `claude -p` prints its result when the session ends,
so a lane's log is empty for the whole time the lane is working and then appears all at once. It is
a transcript, not a progress bar. `-Watch` is the progress signal; the log is what you read
afterwards, and it is worth reading — a lane reports what it decided and what surprised it there,
and that is the only place it says so.

## Step 3 — watch

```
Monitor: pwsh -NoProfile -File ./scripts/ralph-dispatch.ps1 -Watch -IntervalSeconds 60
```

Use the Monitor tool with that command. It emits one line per state change and exits when no lane is
working, which is exactly the shape Monitor wants. Do not hand-roll a poll loop and do not sleep in
the foreground.

While waiting you may answer questions, but **do not start work of your own in the integration tree**
— a dirty tree blocks the next wave, and anything you commit moves the head the lanes will rebase
onto.

Relay transitions as they arrive, briefly. A bead averages ~18 minutes; there is no need to narrate
a quiet hour.

## Step 4 — land

As soon as anything reads `ready-to-land`, land it. Do not wait for the wave to finish — the queue
is serialised anyway, and landing early gets a real receipt sooner.

```
./scripts/ralph-dispatch.ps1 -Land
```

One full `verify.ps1` per landing, in the integration tree. That is the receipt that counts: a lane's
own receipt proves the lane was green in isolation, which says nothing about the lane's work combined
with everything that landed since.

Read the queue's own output rather than paraphrasing it. Report per bead: landed, returned, or
quarantined, and for the last two, the reason it gave.

## Step 5 — reap, then go again

```
./scripts/ralph-dispatch.ps1 -Reap
```

Removes only lanes whose bead is closed. Everything else keeps its branch and worktree because that
is the evidence. Then loop back to step 1 for the next wave — no new approval needed, the operator
approved the run.

## Stop and tell the operator when

- **Anything is quarantined.** A lane that verified green alone and failed on integration has found
  a real interaction. That deserves a human or a fresh attempt with the failure in hand, never an
  automatic retry. Report the failure output and stop the wave.
- **The same bead is returned twice.** Something about it is not working; say so rather than
  spending a third lane on it.
- **A lane goes `working` with no worktree, or its log stops.** The launcher marks a crashed lane
  `returned` on the way out, so this means something stranger happened.
- **`bd ready` is empty.** The board is drained. Say so with the numbers, do not invent more work.
- **Any refusal you cannot resolve without touching the operator's files.**

## Measure the first real run

Spec §1 leaves one gate open that no local measurement could close: Phase 0 proved the *machine* is
not the bottleneck (build is 8.8% of a bead), but nothing local can prove concurrent Claude sessions
get concurrent API throughput. **The first live three-lane run is that gate.** Record wall-clock and
beads landed, and compare beads/hour against **5.94**. If it comes in far under, say so plainly —
that is a real finding about the epic, not a bad day, and the honest response is to write it down
the same way Phase 0's negative-result instruction did.

## Hard rules for you, the orchestrator

- **Never `bd close` a bead a lane worked.** The merge queue closes it after the integration tree
  verifies green. Work that has not landed is not done.
- **Never edit inside a lane worktree.** If a lane needs fixing, that is a decision to report, not
  a diff to write. The one exception is when the operator explicitly asks you to take a lane over,
  and then say clearly that you are doing it.
- **Never resolve a merge conflict on a lane's behalf.** A resolution is a code change nobody
  reviewed and nothing verified. The queue returns the bead on purpose.
- **Never push, never merge to `main`, never force-push.**
- **Never bypass the queue** by merging a lane branch yourself, however obviously fine it looks.
  The whole design is that isolated green plus isolated green is not green.
