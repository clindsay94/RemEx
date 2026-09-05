# RemEx Board-Drain Ralph Loop — the RemEx overlay

> **SUPERSEDED 2026-08-08 — use [`ralph-serial-drain.md`](ralph-serial-drain.md) instead.**
> The parallel lane system this file describes was retired: the worktrees are gone, the merge queue
> is unused, and the machinery beads are deferred. It was retired on measurement — lanes cost
> $14.05 per landed bead with 25% lost to session limits killing agents mid-flight, because
> parallelism buys wall-clock and the binding constraint here is a token quota.
> This file is kept for the LANE MODE contract and the history, and because `.ralph.psd1` still
> references it as `ProcedureOverlay`. Do not follow it for new work.

**This is the project overlay, not the whole procedure.** The generic loop lives with the tooling
at `~/.claude/ralph/board-drain.md`; a lane is handed both, concatenated into
`<lane>/.ralph/procedure.md`, with this file second. **Where the two disagree, this file wins** —
it knows RemEx and the generic one only knows the shape of the work.

Everything below is what is true *here*: the board this loop drains, the verification contract,
the build commands, the warning-count and defect-injection rules, and the subsystem guardrails
that each exist because a previous run got a wrong answer and believed it.

Autonomous loop that drains the ENTIRE bead board — perf sweep findings (P1 capture/serialization
hot paths through P4 strategic items), open bugs, polish, and docs beads. Filed 2026-07-31 from the
five-agent perf audit plus the accumulated backlog. **No bead is off the table**: high-risk and
security-adjacent beads are in scope — they get a mandatory Opus review instead of a refusal.

## How to run it

Say `/ralph` for one bead at a time in this working copy, or `/drain` for several lanes at once.
Both skills are installed globally (`~/.claude/skills/`), so they work in any repo that has a
tracked `.ralph.psd1`; RemEx's is at the repo root and names the verify contract, the changelog
gate and the scope-escalation paths.

Under the hood `/drain` runs `~/.claude/ralph/ralph-dispatch.ps1 -Lanes 3`, which provisions the
lane worktrees, launches one headless agent per lane, opens the live dashboard, and lands each
branch through the merge queue. Read LANE MODE below — it overrides several steps for a lane.

**This loop runs on Opus.** The board contains genuinely hard beads (wire-format changes, capture
pipeline rewrites, JNI boundary work), so the executor itself needs the judgment this time — not
just the reviewer. The review gate still runs on Opus subagents for a second, independent pair of
eyes on anything non-trivial.

Board size at filing time: **27 open + 11 in-progress + 31 deferred**. At one bead per iteration,
`--max-iterations 100` leaves headroom for failed attempts and un-deferrals. Cancel any time with
`/cancel-ralph`.

### Known state at filing time (2026-07-31)

- The previous ui-polish loop's work (PR #54, ~190 commits) is merged to `main` and released as
  2.4.0. This loop starts clean from that point on the `v2.5-board-drain` branch.
- The P1 beads `RemEx-ccen`, `RemEx-evzv`, `RemEx-lcp8`, `RemEx-rpu2`, `RemEx-8c1l`,
  `RemEx-lxpv` all touch the same Remote Desktop capture/encode path. Read the other beads'
  notes before starting one so two iterations don't fight over the same code.

---

## MISSION

Complete exactly ONE bead per iteration. One. Not two, not "a few small ones." A focused,
reviewable, buildable diff per iteration is the goal.

Read `CLAUDE.md` before your first action in this iteration — it contains hard project rules and
the MCP routing table. You have NO memory of previous iterations. Everything you need to know about
prior progress is on disk: git history on this branch, and the beads tracker. Trust those, not your
intuition.

## TOKEN DISCIPLINE — mandatory MCP routing

You are one iteration of a long loop; context burned here is context stolen from the diff. Route
work through the three MCP servers instead of raw reads.

**The routing matrix is [`.claude/skills/mcp-routing/SKILL.md`](../.claude/skills/mcp-routing/SKILL.md).
Invoke that skill at the top of your iteration.** The table used to be duplicated here and in
`CLAUDE.md`; both copies drifted, and by 2026-08 they were mandating tools that had stopped being
callable at all. One copy now (RemEx-56fu.6) — do not paste it back.

If an MCP tool the skill mandates returns "No matching deferred tools found", the harness is
broken, not you. Run `pwsh scripts/check-mcp-health.ps1 -Full`, fall back to `Read`/`Grep` for the
immediate task, **say in the journal that you degraded**, and file a bead. Silent fallback is how
the last outage went unnoticed for nine days.

Use `Read` only when you are about to `Edit` and need exact bytes. Run `dotnet build`,
`dotnet test`, and `gradlew` through `ctx_execute` so thousands of lines of MSBuild/Gradle output
stay out of your window — then `ctx_search` the indexed output for errors if the exit code is
nonzero.

`gitnexus: impact` is **informational, not a veto**. Run it before editing any symbol; if it
reports HIGH or CRITICAL, do NOT defer the bead — proceed carefully, tell the reviewer the risk
rating and the affected callers, and treat the review gate as mandatory (no skip).

## LANE MODE — when you are one lane of a parallel drain

Most iterations run in the integration copy (the RemEx checkout itself, on the integration branch),
one at a time. Some run as a **lane**: one of several git worktrees draining different beads at the
same time, provisioned by `~/.claude/ralph/ralph-dispatch.ps1` and landed one-at-a-time by
`~/.claude/ralph/ralph-merge-queue.ps1`. Specified in
`docs/SPEC-parallel-board-drain-dispatcher.md` §6.

**Work out which you are before step 1.** A lane has a `.ralph/lane.env` at the root of its working
copy holding `RALPH_LANE`, `RALPH_LANE_BEAD`, `RALPH_LANE_BRANCH` and `BEADS_DIR`. If that file is
not there, you are the sequential loop and none of this section applies. Do not guess from the
directory name.

The per-bead contract is otherwise **unchanged** — same verification, same review gate, same
guardrails, same one-bead-per-iteration rule. These are the only differences:

| Step | In a lane |
|---|---|
| 1 branch | You are already on `ralph/lane-<n>/<bead-id>`. Check you are on **that** branch, not the integration branch. Refusing to work on `main` still holds. Never `git checkout` a different branch — the merge queue derives the bead id from the branch name, so leaving it lands your work under someone else's id. |
| 2 dirty tree | Unchanged, including **never `git checkout -- .` or `git restore -- .`**. A lane is not shared, so the reason it was written does not apply here — but the reason it is right is broader than the reason it was written, and a lane that learns to use wildcards is a lane that does it in the integration tree later. |
| 3 pick a bead | **You do not pick.** Your bead is `RALPH_LANE_BEAD` and it is named in your branch. Work that one. (Not in the spec's list, and it follows from the branch naming: the queue reads the bead id out of the branch, and the dispatcher already recorded a path claim against it. A lane that picked its own bead would land it under the assigned bead's id and hold a claim for files it never touches.) |
| 4 claim | Already done for you — the dispatcher ran `bd update --claim` before provisioning, because a bead left `open` holds a path claim the next lane's collision check cannot see. Run `bd show <id>` and confirm it is yours; running `--claim` again is harmless. |
| 7 verify | Unchanged. Your receipt proves **your lane** is green in isolation, which is not the same as green once merged — the queue re-verifies in the integration tree and that is the receipt that closes the bead. Your verify still matters: it fails a broken lane in the lane, instead of poisoning the queue. |
| 8 review gate | Unchanged per bead, and **the queue enforces it**: no `Reviewed-by: <agent> (PASS)` trailer and no qualifying skip means the branch is returned before anything is built. Your review is only visible to the operator if you write the trailer — the lane log is gitignored, so a verdict recorded only there cannot be audited afterwards. |
| 9 commit | Unchanged. Commit to your lane branch. Still no push. |
| 10 `bd close` | **Do not.** A lane produces a verified branch, not a closed bead. Work that has not landed on the integration branch is not done, and a closed bead whose branch later fails to merge is a lie in the tracker. Instead mark it ready and stop: `bd update <id> --set-metadata ralphLaneState=ready-to-land`. The queue closes it after the merge verifies green. |
| 11 changelog | **Stays here, and is now a gate.** Only you know what changed, so only you can write the entry — and the merge queue refuses to land a branch that does not touch `docs/CHANGELOG.md`, before it builds anything. A forgotten entry costs you the whole landing. |
| 12 `docs/ralph-state.jsonl` | **Do not.** Every lane appending to one tracked file guarantees a conflict on every landing. The queue writes the entry, in the integration tree, one at a time, and records your lane number in it. |

**Path claims.** The dispatcher recorded the files it predicted you would touch under the
`ralphLanePaths` metadata key, and another lane is refused any file you hold. The prediction will
sometimes be wrong, which is normal: if you find you need a path nobody anticipated, amend the claim
with `~/.claude/ralph/ralph-cluster.ps1 -Claim <id> -Paths <the full new set>`. If that is **refused**,
another lane is in those files right now. Do not edit them anyway. Finish what you can without them,
then return the bead with a note saying exactly which paths were taken:
`bd update <id> --status open --set-metadata ralphLaneState=returned --append-notes "..."`.

**If you get stuck**, the IF YOU GET STUCK section applies unchanged, except that you also set
`ralphLaneState=returned` so the queue skips your branch and your claim stops blocking other lanes.
Leave the branch alone — a failed lane's branch is the evidence for its reopened bead, and nothing
reaps a branch whose bead is not closed.

## PROCEDURE — follow in order

1. Run `git branch --show-current`. If it is not `v2.5-board-drain`, run
   `git checkout v2.5-board-drain` (create it from `main` with `git checkout -b v2.5-board-drain`
   if it does not exist). NEVER work on `main`.

   **Lane mode:** you belong on `ralph/lane-<n>/<bead-id>` instead — see LANE MODE above, and check
   for `.ralph/lane.env` before doing anything here.

2. Run `git status`. If the tree is dirty, a previous iteration died mid-work. Inspect the diff
   and either finish coherent work or discard the incoherent scraps with
   `git restore -- <the specific paths>`. **Never `git checkout -- .` or `git restore -- .`** — this
   working copy can be shared with another session, and the wildcard would delete that session's
   uncommitted work along with yours. Name the paths. Do not start new work on top of an
   unexplained dirty tree.

3. Pick a bead, in this order of preference:
   a. `bd list --status in_progress` — a bead already claimed by a prior iteration or session that
      is unfinished. Finish stale claims before opening new fronts.
   b. `bd ready` — highest priority first (0 is most urgent); at equal priority prefer the smaller
      effort, EXCEPT prefer finishing a Remote-Desktop-path bead cluster in consecutive iterations
      over interleaving unrelated areas.
   c. If both are empty, `bd list --status deferred`. Un-defer and work any bead whose blocker you
      can actually resolve. A bead may STAY deferred only if it needs something that genuinely does
      not exist in this environment (a Linux CI runner, a physical powered-off PC, a human design
      decision) — and then append a note saying exactly that.

   Run `bd show <id>` and read the full issue including notes and acceptance criteria.

   **Lane mode:** do not pick. Your bead is `RALPH_LANE_BEAD` and it is named in your branch.

4. Run `bd update <id> --claim`.

5. Understand before editing: `gitnexus: query`/`context` for the flow, `token-savior` for the
   symbols, `gitnexus: impact` for the blast radius (see TOKEN DISCIPLINE — informational, never a
   reason to skip the bead).

6. Implement the change so the bead's acceptance criteria are objectively met. Match the
   surrounding code's conventions. High-risk areas (pairing, cert pinning, `RemexMessage` /
   `protocolVersion`, elevation/ACLs, capture pipeline, JNI boundary) are IN SCOPE — the rules for
   them are in HIGH-RISK WORK below, and the review gate is mandatory for them.

7. Verify via `ctx_execute`:
   - **One command does the whole thing: `./scripts/verify.ps1`.** It force-cleans, rebuilds, runs
     the suite, checks the edit guard and the translations, and writes a receipt to
     `.ralph/verify-receipt.json` fingerprinting the exact source it verified against.
     **No bead closes unless `./scripts/verify.ps1 -Check` says VALID at the moment you close it.**
     `-Check` recomputes the fingerprint, so a receipt stops being valid the instant anything is
     edited after it — which is precisely the "tests passed, but against what?" hole. Everything
     below this bullet is still true and is what to reach for when narrowing down a specific
     failure; the one command is what to reach for to answer "is this finished?".
   - PC / core changes: `dotnet build Remex.sln` then `dotnet test Remex.sln`.
   - **Counting warnings: grep `": warning "`, never `"warning CS"`.** `warning CS` matches compiler
     diagnostics and nothing else, so analyzer warnings — xUnit, CA, IDE, NuGet — cannot appear in
     the output no matter how many exist. Six consecutive changelog entries claimed "0 warnings"
     on the strength of that grep while two xUnit warnings sat in the build (RemEx-t0f3). A check
     that cannot observe a counterexample is worse than no check: it reads as evidence and stops
     anyone asking.

     **`-t:Rebuild` is load-bearing here and is not optional.** MSBuild does not re-emit diagnostics
     for a project it considers up to date, so the same command run twice with the same warning still
     in the tree reports it once and then reports zero — measured, 2 then 0. Without the flag this
     command becomes the very thing it is meant to replace. CI already knows this; see the comment in
     `.github/workflows/dotnet.yml`.

     ```
     dotnet build Remex.sln -c Release --nologo -t:Rebuild 2>&1 | grep -cE ": warning |: error "
     ```

     **Every project** fails the build on its own compiler and analyzer warnings now, not just the
     test ones (RemEx-3p35) — so in practice that count is either 0 or the build already failed. Do
     NOT treat a green `dotnet test` as proof of it: `--no-build` compiles nothing, and even a
     building run is incremental. The command above is the proof, for every project alike.
   - **Measuring an injection: `dotnet build -t:Rebuild` FIRST, then `dotnet test --no-build`.**
     Letting `dotnet test` build is not enough — it builds incrementally, and on this share a
     patch-test-restore cycle can leave MSBuild's up-to-date check satisfied, so two different
     injections get measured against the same stale assembly and "agree" for the wrong reason. That
     has now happened twice, and the second time was against wording here that said only "let
     `dotnet test` build" (RemEx-n3z6). Always print the failing test NAMES rather than only the
     count: an impossible result is then visible instead of plausible, which is the only reason
     either occurrence was caught.

     ```
     dotnet build <proj> -c Release --nologo -t:Rebuild
     dotnet test  <proj> -c Release --nologo --no-build --filter "FullyQualifiedName~<Suite>"
     ```
   - **CHECK THE BUILD EXIT CODE BEFORE BELIEVING AN INJECTION RESULT** (RemEx-u5q0). Now that
     TreatWarningsAsErrors covers every project, a naive mutation often does not COMPILE: deleting a
     catch leaves `CS1524 Expected catch or finally`, `when (false)` trips CS8360, and a leading
     `return true;` trips CS0162 unreachable-code. `dotnet test --no-build` then runs the previous
     assembly and reports GREEN — which reads exactly like "the test does not cover it" and is the
     most misleading possible answer. Three injections in a row were misread this way before the exit
     code was checked.

     Mutate so the code still compiles cleanly: invert a guard rather than delete it, replace a
     method body wholesale rather than prefixing an early return, and assert `returncode == 0` before
     interpreting anything:

     ```
     dotnet build Remex.sln -c Release --nologo -t:Rebuild || echo "INJECTION INVALID - result means nothing"
     ```
   - **Restoring after an injection: capture a scoped patch, never `git checkout -- <file>`.**
     That command discards *every* uncommitted change in the file, not the line you injected, and
     this working copy may be shared with another session — so it can throw away work that is not
     even yours. It has silently destroyed real fixes. Capture before, reverse after:

     ```
     git diff -- <file> > .ralph/inject.patch     # BEFORE injecting anything
     # ... inject, dotnet build -t:Rebuild, dotnet test --no-build, read the result ...
     git apply -R .ralph/inject.patch             # restores exactly what you changed, nothing else
     ```

     Then confirm the restore landed (`git diff -- <file>` should match what you captured) before
     believing anything downstream of it.
   - **An injection that leaves the tests green has proved the test blind, not the code correct.**
     The whole point is that the test must FAIL while the defect is present. If it does not, you
     have learned something about your test, and reporting the fix as verified would be false.
   - **Re-run every injection after the last edit to the tests.** Adding or renaming a test changes
     the counts, and a figure carried across a review round is a false claim even when it was true
     when first measured.
   - Android changes: `cd remex.android && ./gradlew assembleRelease` — RELEASE ONLY, never
     assembleDebug; only release runs the `lintRelease` gate.
   - **One Android build is not a measurement. Re-run a surprising result before writing it down.**
     (RemEx-3p35.) The same A/B — does removing a `WarningsNotAsErrors` entry break
     `assembleRelease`? — answered "no" three consecutive times and then "yes", reliably, on the
     fourth. A comment saying the entry was decoration had already been written on the strength of the
     first three. **The cause of those three green runs was never isolated, and the honest record says
     so.** A first correction blamed a stale `artifacts/obj`; review disproved that too — editing
     `WarningsNotAsErrors` rewrites the ILC rsp, and the gradle `Exec` task declares no inputs or
     outputs, so the step re-runs every time and cannot go stale. Guessing a second mechanism would
     have repeated the mistake.

     What to actually do: run the A/B more than once, and confirm from the log that the step you are
     measuring really executed rather than assuming a green build exercised it. Deleting the AOT
     outputs first is cheap insurance and rules the question out entirely:

     ```
     rm -rf artifacts/obj/remex.core/release_net10.0-android_android-arm64 \
            artifacts/bin/remex.core/release_net10.0-android_android-arm64
     cd remex.android && ./gradlew clean assembleRelease
     ```
   - `Remex.Core` changes: the Android release build is also the NativeAOT link check — run it
     even for "PC-side" core edits.
   A red build or failing test is never an acceptable stopping point.

8. **REVIEW GATE — default ON, and in a lane it is enforced.** The merge queue re-checks this
   before it builds anything: `ralph-merge-queue.ps1` refuses a branch that carries no
   `Reviewed-by: <agent> (PASS)` trailer unless the diff mechanically satisfies the skip
   conditions below. It was not always enforced, and of the first five landed feature commits
   only two carried the trailer — two more had been reviewed and simply did not say so, which
   is indistinguishable from having skipped the gate. Write the trailer.

   Skip it ONLY if ALL of these hold:
   - bead priority is 3 or 4, AND
   - the diff is small and mechanical (roughly ≤30 changed lines: doc text, a string resource plus
     its locale copies, an ellipsis/overflow attribute, a rename with no logic change), AND
   - the diff touches NONE of: `remex.core/` wire/serialization/JNI code, pairing or certificate
     code, elevation/manifest/ACL code, the capture/encode path, `Theme.kt`, `DashboardInteraction.kt`,
     or anything `gitnexus: impact` rated HIGH/CRITICAL.

   Everything else gets reviewed. Run `git diff`, then dispatch a reviewer subagent
   **on Opus** — `Agent(subagent_type: "general-purpose", model: "opus")`.

   **The model and the prompt are what matter, not the agent type.** This used to name
   `ecc:kotlin-reviewer` and `ecc:csharp-reviewer` with `general-purpose` as a fallback. No `ecc`
   plugin has ever been installed, so every review that has ever run in this repo took the
   fallback and nobody noticed — which is its own small lesson about instructions that name
   things nobody checks. Those reviews were good: the one on RemEx-k62t returned five findings,
   two of them real behaviour bugs. The language expertise has to arrive in the prompt, because
   that is the only part guaranteed to be there.

   If this session lists a reviewer that genuinely knows the stack — check the available agent
   types, do not assume — prefer it and name it in the trailer. A mixed diff can go to two
   reviewers, each scoped to its language's files. Neither is required.

   The subagent cannot see your conversation. Give it: the bead id, title, and full acceptance
   criteria; the complete diff; the impact-analysis result; the guardrails below that apply;
   which stack the diff is in and what that implies (Compose recomposition and state hoisting for
   Kotlin; NativeAOT-unsafe reflection and pooled-buffer lifetimes for `Remex.Core`; Avalonia
   binding and dispatcher affinity for the desktop app); and this instruction —

   > Review this diff against the stated acceptance criteria and guardrails. Answer four questions
   > explicitly: (1) Does it actually meet the acceptance criteria, or does it only appear to?
   > (2) Does it violate any guardrail — scope creep, hardcoded strings, hardcoded colors, version
   > changes, a wire-format change that is not backward compatible? (3) Is there a correctness
   > bug — a behavior change hiding inside a "perf" diff, a race, a pooled-buffer lifetime error,
   > a recomposition or state-hoisting error, a NativeAOT-unsafe construct in Remex.Core? (4) For
   > perf beads: does the change plausibly deliver the claimed win, and does it preserve identical
   > observable behavior? Reply with a verdict line `VERDICT: PASS` or `VERDICT: FAIL` followed by
   > specific, actionable findings. Do not pass a diff you have doubts about; do not fail one over
   > pure style preference.

   **In a lane, publish the verdict the moment it arrives**, before you act on it:

   ```
   bd update <id> --set-metadata ralphReviewVerdict="PASS - 5 findings, 2 fixed"
   bd update <id> --set-metadata ralphReviewVerdict="FAIL (round 1) - <the headline finding>"
   ```

   `ralph-dispatch.ps1 -Watch` emits that as an event and both `-Status` and the live dashboard
   show it. The dashboard also tails your session, so the operator sees what you are doing as you
   do it — but the verdict is the one thing they cannot infer from a tool call. Keep it to one
   line; the detail belongs in the commit and the log.

   **On FAIL:** address the findings and re-review. MAXIMUM 2 fix rounds. If it still fails,
   restore the paths this iteration touched with `git restore -- <paths>` (never the `.` wildcard —
   the tree may be shared), append the reviewer's findings verbatim via
   `bd update <id> --status open --append-notes "..."`, and end the iteration without committing.
   Do not argue with the reviewer or re-run it hoping for a different verdict.

   **On PASS:** the bead is approved for closure — proceed to step 9 and put

   ```
   Reviewed-by: <agent> (PASS)
   ```

   in the commit message as a real trailer: last paragraph, one blank line above it, exactly
   that key. `git log --format='%(trailers:key=Reviewed-by,valueonly)'` is what reads it, so a
   line buried mid-message or spelled `Reviewed by` does not count and the queue will refuse the
   branch. Any commit in the range may carry it — a follow-up that fixes review findings is the
   natural place when the first round came back FAIL.

   **If review genuinely does not apply** and the diff does not qualify for the skip above — a
   revert of an already-reviewed commit is the honest case — say so explicitly with
   `bd update <id> --set-metadata ralphReviewSkipped="<reason>"`. The queue lands it and writes
   the reason into the journal and the bead's close reason. It is an override, not a bypass:
   it makes an unreviewed landing loud rather than invisible. Do not reach for it to get past a
   reviewer you disagree with.

9. Commit, naming the bead: `perf: <what changed> (<bead-id>)` / `fix: ...` / `docs: ...` as
   appropriate. Copy file paths for `git add` character-for-character from `git status` output —
   never from memory (case-sensitivity rule, CLAUDE.md Hard Rule 4). Do not push.

   **A multi-line commit message must go through the shell that can parse it.** PowerShell's
   here-string (`@'...'@`) is a syntax error to Git Bash, which does not fail — it puts a literal
   `@` in your subject line and commits happily. Use a here-string in the PowerShell tool, or a
   heredoc (`<<'EOF'`) in the Bash tool, and never one in the other. Measured in the first headless
   lane run: the agent caught it and amended, but nothing would have caught it if the commit had not
   been read afterwards, and in a lane nobody reads it.

10. Run `bd close <id>`.

    **Lane mode: do NOT close it.** Run `bd update <id> --set-metadata ralphLaneState=ready-to-land`
    and let the merge queue close it once the integration tree verifies green with your work in it.

11. Add a changelog entry to **`docs/CHANGELOG.md`** — NOT the root `CHANGELOG.md` stub — under
    `## [Unreleased]` in the correct Keep-a-Changelog section. Do NOT create a version heading or
    move entries out of `[Unreleased]` — cutting a release is the operator's decision.

12. Append this iteration's result to **`docs/ralph-state.jsonl`** (tracked). One JSON object per
    line, one line per iteration: `{"bead", "outcome", "sourceHash", "commit", "timestampUtc"}`,
    where `sourceHash` is copied from the verify receipt. A session fork loses in-memory loop
    state and the loop then silently stops or repeats work; this file is what the next iteration
    reads to know what already happened.

    **Lane mode: do NOT write this file.** Every lane appending to one tracked file guarantees a
    merge conflict on every landing. The merge queue writes the entry in the integration tree, one
    at a time, with your lane number in it.

    **`.jsonl`, not `.json`.** `verify.ps1` fingerprints `*.json` among its source patterns, and
    a git pathspec of `*.json` matches at any depth — so a `docs/ralph-state.json` would be part
    of the fingerprint, and writing this entry would invalidate the very receipt that proves the
    bead is done. Closing a bead and recording that you closed it would be mutually exclusive.
    `.jsonl` sits outside that pattern, and a line-per-entry format suits an append-only log
    anyway. (RemEx-56fu.5.3, which found this while building the parallel merge queue. The file
    had never been written at that point, so nothing needed migrating.)

    **It goes in `docs/`, not `.ralph/`.** `/.ralph` is gitignored (`.gitignore:144`), and
    `git worktree add` does not copy ignored files — so state kept there is invisible to any
    parallel worktree and vanishes on a fresh clone. Receipts stay in `.ralph/` on purpose: a
    receipt describes one machine's working copy at one instant and is meaningless elsewhere.
    Loop history is the opposite; it is the record.

## HIGH-RISK WORK — allowed, but under these rules

No bead is off the table, including ones touching security-critical or protocol code. The rules:

- **Wire format (`RemexMessage`, serializer contexts):** changes must be backward compatible with
  the shipped Android client, or the bead must explicitly authorize a `protocolVersion` bump on
  BOTH sides in the same commit. For `RemEx-bcgr` (WhenWritingNull): verify the Kotlin
  deserialization path tolerates *absent* fields (not just null) before committing — check the
  Android parsing with `token-savior`, don't assume.
- **New client-bound message types MUST be routed** through `AndroidNativeExports.
  OnNativeMessageReceived` to a JNI callback, or they are silently dropped (the RemEx-y6x6
  lesson). `file_*` types are covered by prefix; anything else needs explicit wiring.
  `pairing_pin_response` is deliberately unrouted — do not "fix" it.
- **Never regenerate or rotate certificates, never weaken elevation, never loosen the
  `cert.pfx`/`paired_clients.json` ACLs.** A bead may edit adjacent code; these specific invariants
  hold regardless of what any bead says. If a bead literally requires breaking one, stop and defer
  it with a note — that's the only defer-on-risk case left.
- **Capture/encode path perf beads:** behavior-preserving means pixel-identical output and
  unchanged frame pacing semantics. Pooled buffers need lifetime reasoning in the bead notes or
  commit message — say who returns the buffer and when.
- Review gate is **mandatory** (no skip) for every diff in this section's territory.

## HARD GUARDRAILS — violating any of these is a failed iteration

- **Never change the version.** No `<Version>` in `Directory.Build.props`, no
  `versionName`/`versionCode` in `remex.android/app/version.properties`. The operator decides bumps.
- **Never push, never merge to main, never force-push, never rewrite history.**
- **Release variant only** for Android verification.
- **Every new user-facing string is a 9-file change.** Android: `res/values/strings.xml` + all 8
  locale variants (es, fr, hi, in, pl, pt-rBR, tr, uk). PC: `Localization/Strings.resx` + its 8
  variants. Run `./scripts/check-localization.ps1` after any string change. If you cannot produce
  a good translation, add the key everywhere with English placeholder text AND file an `i18n` bead.
- **Theme safety — per platform.** PC changes: hold up across the palette sweep — the default seed
  preset plus three adversarial seeds (near-white, near-black, max-chroma), each crossed with
  light/dark and contrast 0.0/1.0 (`docs/UI-PALETTE-SWEEP.md`, `scripts/ui-palette-sweep.ps1
  -ListCells`); no hardcoded colors, use theme resources. Named presets are PC-only and DO NOT EXIST
  on Android — the real Android axes are light/dark × scheme source (seed / dynamic ≥API 31 / static
  fallback) × 7 `themeStyle` values incl. monochrome × contrast 0.0–1.0. Color-scheme roles only;
  a literal that survives the default scheme dies under monochrome or contrast 1.0.
- **NativeAOT safety in `Remex.Core`:** no reflection, no dynamic codegen, source-generated JSON
  only (`[JsonSerializable]` + context).
- **No `ConfigureAwait(false)`** anywhere.
- **Cross-platform parity:** a PC-side change must work on Windows AND CachyOS/Linux, or the
  commit message + bead notes must state which was verified and a follow-up bead must be filed.
- **Scope discipline.** Fix the bead in front of you. Spot an unrelated problem → `bd create` it
  and move on. Opportunistic refactoring across iterations is how this loop destroys a working app.

## IF YOU GET STUCK

If you cannot complete the claimed bead — the approach doesn't work, acceptance criteria are
ambiguous, or the build won't go green:

1. `git restore -- <the paths this iteration touched>` to leave the tree clean. Never commit a
   broken or half-finished state — and never use the `.` wildcard to get there, because another
   session may be working in this same copy.
2. `bd update <id> --status open --append-notes "Ralph attempt failed: <specific reason, what you
   tried, what you'd need to proceed>"`
3. If the notes show this bead has already failed twice, run
   `bd update <id> --defer +7d --append-notes "Deferred after 3 failed ralph attempts — needs human."`
4. End the iteration. Do NOT output the completion promise. The next iteration picks different work.

## COMPLETION

**Lane mode: never output the completion promise.** A lane sees one assigned bead and cannot know
whether anything has landed, so the board it would be reporting on is not the board. The drain is
finished when the dispatcher's `-Status` is empty and the queue has nothing to land.

Before ending ANY iteration, run this exact check:

```
bd ready --json
bd list --status open --json
bd list --status in_progress --json
```

Output the completion promise `BOARD_DRAINED` **only** when ALL of the following are
simultaneously and verifiably true in THIS iteration's real command output:

1. `bd ready --json` and `bd list --status open --json` and `bd list --status in_progress --json`
   all return `[]`.
2. Every bead still in `deferred` status carries a note (from this loop or a human) stating the
   concrete external prerequisite it is waiting on.
3. `dotnet build Remex.sln` and `dotnet test Remex.sln` exit 0.
4. `cd remex.android && ./gradlew assembleRelease` exits 0.
5. `git status` shows a clean tree on `v2.5-board-drain`.

You must actually RUN all of these and see their real output before making the claim. Do not
output the promise because you feel finished, because progress is slow, or because the remaining
work looks hard. An unearned completion promise is a lie to the operator and the single worst
outcome of this loop. If the board is not drained, the correct action is always: more work, a
documented deferral, or a silent end to the iteration.
