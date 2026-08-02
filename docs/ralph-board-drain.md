# RemEx Board-Drain Ralph Loop

Autonomous loop that drains the ENTIRE bead board — perf sweep findings (P1 capture/serialization
hot paths through P4 strategic items), open bugs, polish, and docs beads. Filed 2026-07-31 from the
five-agent perf audit plus the accumulated backlog. **No bead is off the table**: high-risk and
security-adjacent beads are in scope — they get a mandatory Opus review instead of a refusal.

## How to run it

```
/ralph-loop "Read docs/ralph-board-drain.md and follow it exactly. Complete ONE bead this iteration." --max-iterations 100 --completion-promise "BOARD_DRAINED"
```

The prompt argument is deliberately short. Ralph re-feeds the *same* string every iteration and the
model has no memory between them — so the instructions live here, on disk, where each fresh
iteration re-reads them and where the operator can edit them mid-run without restarting the loop.

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
work through the three MCP servers instead of raw reads:

| Need | Use | Never |
|---|---|---|
| Find a symbol / class / method | `token-savior: find_symbol` | grep / read whole files |
| Read one function's body | `token-savior: get_function_source` | Read the whole file |
| Get surrounding context to edit | `token-savior: get_edit_context` | Read the whole file |
| Who calls this? / what does it call? | `token-savior: get_dependents` / `get_dependencies` | manual import tracing |
| Blast radius before editing a symbol | `gitnexus: impact` (upstream) | editing blind |
| Understand an execution flow / concept | `gitnexus: query` | repo-wide keyword grep |
| Full 360° on one symbol | `gitnexus: context` | sequential Reads |
| Builds, tests, any noisy command | `context-mode: ctx_execute` | raw Bash output into context |
| Count / filter / aggregate anything | `context-mode: ctx_execute` (write code) | loading data into context |

Use `Read` only when you are about to `Edit` and need exact bytes. Run `dotnet build`,
`dotnet test`, and `gradlew` through `ctx_execute` so thousands of lines of MSBuild/Gradle output
stay out of your window — then `ctx_search` the indexed output for errors if the exit code is
nonzero.

`gitnexus: impact` is **informational, not a veto**. Run it before editing any symbol; if it
reports HIGH or CRITICAL, do NOT defer the bead — proceed carefully, tell the reviewer the risk
rating and the affected callers, and treat the review gate as mandatory (no skip).

## PROCEDURE — follow in order

1. Run `git branch --show-current`. If it is not `v2.5-board-drain`, run
   `git checkout v2.5-board-drain` (create it from `main` with `git checkout -b v2.5-board-drain`
   if it does not exist). NEVER work on `main`.

2. Run `git status`. If the tree is dirty, a previous iteration died mid-work. Inspect the diff
   and either finish coherent work or `git checkout -- .` to discard incoherent scraps. Do not
   start new work on top of an unexplained dirty tree.

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

4. Run `bd update <id> --claim`.

5. Understand before editing: `gitnexus: query`/`context` for the flow, `token-savior` for the
   symbols, `gitnexus: impact` for the blast radius (see TOKEN DISCIPLINE — informational, never a
   reason to skip the bead).

6. Implement the change so the bead's acceptance criteria are objectively met. Match the
   surrounding code's conventions. High-risk areas (pairing, cert pinning, `RemexMessage` /
   `protocolVersion`, elevation/ACLs, capture pipeline, JNI boundary) are IN SCOPE — the rules for
   them are in HIGH-RISK WORK below, and the review gate is mandatory for them.

7. Verify via `ctx_execute`:
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
   - **Re-run every injection after the last edit to the tests.** Adding or renaming a test changes
     the counts, and a figure carried across a review round is a false claim even when it was true
     when first measured.
   - Android changes: `cd remex.android && ./gradlew assembleRelease` — RELEASE ONLY, never
     assembleDebug; only release runs the `lintVitalRelease` gate.
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

8. **REVIEW GATE — default ON.** Skip it ONLY if ALL of these hold:
   - bead priority is 3 or 4, AND
   - the diff is small and mechanical (roughly ≤30 changed lines: doc text, a string resource plus
     its locale copies, an ellipsis/overflow attribute, a rename with no logic change), AND
   - the diff touches NONE of: `remex.core/` wire/serialization/JNI code, pairing or certificate
     code, elevation/manifest/ACL code, the capture/encode path, `Theme.kt`, `DashboardInteraction.kt`,
     or anything `gitnexus: impact` rated HIGH/CRITICAL.

   Everything else gets reviewed. Run `git diff`, then dispatch a reviewer subagent **on Opus**:
   - Kotlin/Android diff → `Agent(subagent_type: "ecc:kotlin-reviewer", model: "opus")`
   - C#/Avalonia/host diff → `Agent(subagent_type: "ecc:csharp-reviewer", model: "opus")`
   - Mixed diff → dispatch both, each scoped to its language's files.
   - If the `ecc` agents are unavailable in this session, fall back to
     `Agent(subagent_type: "general-purpose", model: "opus")` with the same instructions.

   The subagent cannot see your conversation. Give it: the bead id, title, and full acceptance
   criteria; the complete diff; the impact-analysis result; the guardrails below that apply; and
   this instruction —

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

   **On FAIL:** address the findings and re-review. MAXIMUM 2 fix rounds. If it still fails, run
   `git checkout -- .`, append the reviewer's findings verbatim via
   `bd update <id> --status open --append-notes "..."`, and end the iteration without committing.
   Do not argue with the reviewer or re-run it hoping for a different verdict.

   **On PASS:** the bead is approved for closure — proceed to step 9 and include
   `Reviewed-by: <agent> (PASS)` in the commit message.

9. Commit, naming the bead: `perf: <what changed> (<bead-id>)` / `fix: ...` / `docs: ...` as
   appropriate. Copy file paths for `git add` character-for-character from `git status` output —
   never from memory (case-sensitivity rule, CLAUDE.md Hard Rule 4). Do not push.

10. Run `bd close <id>`.

11. Add a changelog entry to **`docs/CHANGELOG.md`** — NOT the root `CHANGELOG.md` stub — under
    `## [Unreleased]` in the correct Keep-a-Changelog section. Do NOT create a version heading or
    move entries out of `[Unreleased]` — cutting a release is the operator's decision.

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
- **Theme safety — per platform.** PC changes: hold up across CyberNOC, Monolith, SolarFlare,
  BaseDarkGlass (no hardcoded colors; use theme resources). Android changes: the four PC themes DO
  NOT EXIST there — the real axes are light/dark × scheme source (seed / dynamic ≥API 31 / static
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

1. `git checkout -- .` to leave the tree clean. Never commit a broken or half-finished state.
2. `bd update <id> --status open --append-notes "Ralph attempt failed: <specific reason, what you
   tried, what you'd need to proceed>"`
3. If the notes show this bead has already failed twice, run
   `bd update <id> --defer +7d --append-notes "Deferred after 3 failed ralph attempts — needs human."`
4. End the iteration. Do NOT output the completion promise. The next iteration picks different work.

## COMPLETION

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
