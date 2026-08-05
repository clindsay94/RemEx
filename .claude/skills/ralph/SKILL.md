---
name: ralph
description: Start a RemEx autonomous board-drain loop. Use when the user says /ralph, "run the ralph loop", "drain the board", or asks for autonomous bead work. Establishes the loop's starting state before any work begins.
---

# Ralph board-drain launcher

The loop procedure itself lives in `docs/ralph-board-drain.md` and is the authority on how to do
the work. This skill exists for the thing that goes wrong *before* the work: starting without
knowing the state.

## Why this exists

Across roughly six recorded sessions the loop was launched, began reading beads or inspecting the
tree, and was then interrupted to be handed context it could not have derived — which branch,
whether another session was sharing the working copy, what a prior loop had already closed, what
effort level to run at. Three of those sessions produced no work at all, because every interruption
discards the exploration already done and the loop never got past the starting line.

None of that context is discoverable from the repository. All of it is cheap to state up front.

## Step 1 — establish the starting state, before anything else

Ask for these together, in one message, and wait for the answer. Do not run `git status`, read
beads, or explore first: if the answers change the plan, that work is wasted, and re-deriving what
the operator already knows is the specific failure this step prevents.

1. **Branch** — which branch should the loop work on? Never `main`.
2. **Is this working copy shared with another session right now?** This decides whether wildcard
   restores are safe. If shared, only ever touch files this loop itself changed.
3. **What did the previous loop already finish?** Prevents re-opening settled work and re-reading
   beads that were closed minutes ago.
4. **Effort level and iteration cap.**
5. **Anything off-limits this run** — subsystems mid-change, a release being cut, files another
   session owns.

If the operator has already supplied all of this in their message, do not ask again — acknowledge
what you were given and go straight to step 2.

## Step 2 — read the procedure and follow it

Read `docs/ralph-board-drain.md` in full and follow it exactly. It carries the hard-won specifics:
MCP routing for token discipline, the review gate, warning-count and defect-injection rules that
each exist because a previous run got a wrong answer and believed it.

## Step 3 — the two non-negotiables

These are the ones most often skipped under time pressure, so they are restated here:

- **A bead is not done until `./scripts/verify.ps1 -Check` says VALID.** Not "the tests passed" —
  that claim cannot be checked by anyone else. `-Check` recomputes a fingerprint of every source
  file and refuses a receipt that no longer matches the code on disk.
- **Never restore with a `.` wildcard.** `git checkout -- .` and `git restore -- .` discard every
  uncommitted change in the tree, including another session's. Name the paths. To undo a defect
  injection, reverse the scoped patch you captured before injecting it.

## Step 4 — record each iteration

Append the result to `docs/ralph-state.jsonl` as described in the procedure. This is tracked on
purpose: `.ralph/` is gitignored, so state kept there does not survive a fresh worktree and cannot
be read by a parallel drain. The `.jsonl` suffix is also deliberate — `.json` would fall inside
`verify.ps1`'s source fingerprint, so recording an iteration would invalidate its own receipt.

## When to stop and ask

Stop and ask rather than guessing when: a bead needs visual or UI judgment you cannot verify, the
acceptance criteria are ambiguous enough that two readings imply different work, or the same bead
has now failed twice. Label it and move to the next bead in the same iteration — do not idle, and
do not silently defer a pile of beads and leave later iterations with nothing to do.
