# RemEx UI-Polish Ralph Loop

Autonomous loop that drains the `ui-polish` bead queue (Android motion/animation + Material 3
token compliance). Filed from a four-agent read-only audit on 2026-07-21.

## How to run it

```
/ralph-loop "Read docs/ralph-ui-polish.md and follow it exactly. Complete ONE ui-polish bead this iteration." --max-iterations 100 --completion-promise "UI_POLISH_QUEUE_DRAINED"
```

The prompt argument is deliberately short. Ralph re-feeds the *same* string every iteration and the
model has no memory between them — so the instructions live here, on disk, where each fresh
iteration re-reads them and where you can edit them mid-run without restarting the loop.

Queue size at filing time was **85 beads**. At one bead per iteration, `--max-iterations 100` leaves
some headroom for failed attempts. Cancel any time with `/cancel-ralph`.

**Run the loop session on Sonnet 5.** The executor's job is bounded: read a bead whose analysis is
already done, make a focused edit, build, commit. Opus is dispatched *as a reviewer subagent* on
risk-gated beads (step 8) — that's where the judgment is needed, on a small diff, where its verdict
can still change the outcome.

### What the review gate does NOT cover

The reviewer reads diffs. It can confirm a hard snap became a spring with the right motion token.
It **cannot** tell you the spring feels right, or that a change holds up across all four themes
(CyberNOC, Monolith, SolarFlare, BaseDarkGlass) — that is a visual property no diff review
establishes. This loop delivers M3 *correctness*. Aesthetic judgement and four-theme verification
still require a human looking at the running app, or a screenshot pass afterward.

---

## MISSION

Complete exactly ONE bead from the `ui-polish` queue per iteration. One. Not two, not "a few small
ones." A focused, reviewable, buildable diff per iteration is the goal.

Read `CLAUDE.md` before your first action in this iteration — it contains hard project rules.

You have NO memory of previous iterations. Everything you need to know about prior progress is on
disk: git history on this branch, and the beads tracker. Trust those, not your intuition.

## PROCEDURE — follow in order

1. Run `git branch --show-current`. If it is not `ui-polish-loop`, run `git checkout ui-polish-loop`
   (create it from main with `git checkout -b ui-polish-loop` if it does not exist). NEVER work on
   `main`.

2. Run `git status`. If the tree is dirty, a previous iteration died mid-work. Inspect the diff and
   either finish that work or `git checkout -- .` to discard it — decide based on whether the diff
   is coherent. Do not start new work on top of an unexplained dirty tree.

3. Run `bd ready --label ui-polish`. Pick the highest-priority unblocked issue (0 is most urgent).
   If two are equal priority, prefer the smaller effort. Run `bd show <id>` and read the full issue
   including its acceptance criteria.

4. Run `bd update <id> --claim`.

5. Before editing any symbol, run `gitnexus_impact({target: "<symbolName>", direction: "upstream"})`
   to check the blast radius. If it reports HIGH or CRITICAL risk, do NOT proceed with that bead —
   run `bd update <id> --status open --append-notes "Deferred by ralph loop: impact analysis
   returned <risk>. Needs human sign-off."` and return to step 3 for a different bead.

6. Implement the change so the bead's acceptance criteria are objectively met. Match the
   surrounding code's conventions.

7. Verify. Android changes: `cd remex.android && ./gradlew assembleRelease`. PC changes:
   `dotnet build Remex.sln`. If either fails, fix it — a red build is never an acceptable stopping
   point. If tests exist for the touched area, run them.

8. **REVIEW GATE.** Decide whether this bead requires review. It does if EITHER:
   - the bead's priority is 0 or 1, OR
   - your diff touches any of: `ui/theme/Theme.kt`, `ui/theme/Type.kt`, `ui/theme/Color.kt`,
     `ui/navigation/AppNavigation.kt`, `ui/screens/DashboardInteraction.kt`,
     `res/values/themes.xml`, any `strings.xml`, or any `.resx` file.

   If neither applies, skip to step 9.

   If review is required, run `git diff` and dispatch a reviewer subagent:
   - Kotlin/Android changes → `Agent(subagent_type: "ecc:kotlin-reviewer", model: "opus")`
   - C#/Avalonia changes → `Agent(subagent_type: "ecc:csharp-reviewer", model: "opus")`

   The subagent cannot see your conversation. Give it: the bead id, title, and full acceptance
   criteria; the complete diff; the guardrails below that apply; and this instruction —

   > Review this diff against the stated acceptance criteria and guardrails. Answer three questions
   > explicitly: (1) Does it actually meet the acceptance criteria, or does it only appear to?
   > (2) Does it violate any guardrail — scope creep beyond the bead, hardcoded strings, hardcoded
   > colors, weakened elevation, altered drag/gesture logic, changed version numbers? (3) Is there
   > a correctness bug — wrong M3 role pairing, a spec that will misbehave under reduced motion, a
   > recomposition or state-hoisting error? Reply with a verdict line `VERDICT: PASS` or
   > `VERDICT: FAIL` followed by specific, actionable findings. Do not pass a diff you have doubts
   > about; do not fail one over pure style preference.

   **On FAIL:** address the findings and re-review. You get a MAXIMUM OF 2 fix rounds. If it still
   fails after the second round, run `git checkout -- .`, append the reviewer's findings verbatim
   to the bead with `bd update <id> --status open --append-notes "..."`, and end the iteration
   without committing. Do not argue with the reviewer or re-run it hoping for a different verdict.

   **On PASS:** proceed to step 9 and include `Reviewed-by: <agent> (PASS)` in the commit message.

9. Commit naming the bead: `ui: <what changed> (<bead-id>)`. Do not push.

10. Run `bd close <id>`.

11. Add a changelog entry to **`docs/CHANGELOG.md`** — NOT the root `CHANGELOG.md`, which is only a
    pointer stub. Put it under the `## [Unreleased]` heading in the correct Keep-a-Changelog section
    (Added/Changed/Fixed/Removed/Security). Do NOT create a new version heading and do NOT move
    entries out of `[Unreleased]` — cutting a release is the human operator's decision.

## HARD GUARDRAILS — violating any of these is a failed iteration

- **Never change the version.** Do not touch `<Version>` in `Directory.Build.props` or
  `versionName`/`versionCode` in `remex.android/app/version.properties`.
- **Never push, never merge to main, never force-push, never rewrite history.**
- **Release variant only** for Android verification (`assembleRelease`, never `assembleDebug`) —
  only the release build runs the `lintVitalRelease` gate this project depends on.
- **Every new user-facing string is a 9-file change.** Android: `res/values/strings.xml` plus all 8
  locale variants (es, fr, hi, in, pl, pt-rBR, tr, uk). PC: `Localization/Strings.resx` plus its 8
  variants. A string hardcoded inline in Kotlin or XAML is a regression. If you cannot produce a
  good translation, still add the key to every file using the English text as a placeholder AND
  file a bead labelled `i18n` to translate it properly. Locale key-set parity is currently PERFECT
  on both platforms (Android 759 keys, PC 1034) — do not be the iteration that breaks it.
- **Theme safety — ANDROID AXES ONLY.** This queue is Android. The four named themes (CyberNOC,
  Monolith, SolarFlare, BaseDarkGlass) are **PC-only and do not exist in `remex.android`** — ignore
  any bead text or instinct telling you to verify against them. Android's real axes, per
  `ui/theme/Theme.kt`, are: light/dark; the three scheme sources (custom seed, dynamic color on
  API 31+, and the static `DarkColorScheme`/`LightColorScheme` fallback on API < 31 or dynamic-off);
  `themeStyle` (7 values incl. monochrome); and `themeContrast` (0.0–1.0). Use color-scheme roles,
  never hardcoded color literals — a literal that looks fine on the default scheme fails under
  monochrome or contrast 1.0. If you cannot reason confidently about these, say so in the bead
  notes and defer rather than guess.
- **Do not touch security-critical code.** Pairing (`PairingHandler`, `PairedClientRegistry`),
  certificate pinning, the `RemexMessage` envelope / `protocolVersion`, elevation, and cert ACLs are
  out of scope for this loop entirely. If a bead requires it, defer the bead with a note.
- **Do not alter the dashboard drag/gesture system.** Several beads touch the *visual feedback* of
  dragging (lift scale, elevation, selection border). The drag, pan, long-press and multi-select
  logic in `DashboardInteraction.kt` is deliberate, was designed over several sessions, and must be
  byte-identical in your diff unless a bead explicitly says otherwise.
- **NativeAOT safety in `Remex.Core`:** no reflection, no dynamic codegen, source-generated JSON only.
- **No `ConfigureAwait(false)`** anywhere.
- **Scope discipline.** Fix the bead in front of you. If you spot an unrelated problem, do NOT fix
  it — run `bd create` to file it (label `ui-polish` if it belongs in this queue) and move on.
  Opportunistic refactoring across iterations is how this loop destroys a working app.

## KNOWN BEAD INTERACTIONS

Some beads depend on each other. If you pick one of these, read the note:

- **Reduced motion** — the `Theme.kt` reduced-motion bead is INCOMPLETE on its own.
  `DashboardCoachOverlay.kt` hardcodes `remember { MotionScheme.expressive() }` in five places and
  will silently ignore the new setting. Both beads must land for the feature to work. The splash
  suppression bead also depends on the `Theme.kt` one.
- **Dashboard card elevation** — one motion bead animates it, one M3-token bead moves it to
  `CardDefaults.draggedElevation`. Whichever you do second, reconcile rather than revert.

## IF YOU GET STUCK

If you cannot complete the claimed bead — the approach doesn't work, the acceptance criteria are
ambiguous, or the build won't go green:

1. `git checkout -- .` to leave the tree clean. Do not commit a broken or half-finished state.
2. `bd update <id> --status open --append-notes "Ralph attempt failed: <specific reason, what you
   tried, what you'd need to proceed>"`
3. If the notes show this bead has already failed twice, instead run
   `bd update <id> --defer +7d --append-notes "Deferred after 3 failed ralph attempts — needs human."`
   so the queue can drain past it.
4. End the iteration. Do NOT output the completion promise. The next iteration picks different work.

## COMPLETION

Before ending ANY iteration, run this exact check:

```
bd list --label ui-polish --status open --json
```

Output the completion promise `UI_POLISH_QUEUE_DRAINED` **only** when ALL of the following are
simultaneously and verifiably true:

1. That command returns an empty array `[]`.
2. `cd remex.android && ./gradlew assembleRelease` exits 0.
3. `dotnet build Remex.sln` exits 0.
4. `git status` shows a clean tree on `ui-polish-loop`.

You must actually RUN all four checks and see their real output in this iteration before making the
claim. Do not output the promise because you feel finished, because progress seems slow, because
the remaining work looks hard, or because you think you should stop. An unearned completion promise
is a lie to the operator and the single worst outcome of this loop. If the queue is not empty, the
correct action is always to do more work, defer a bead, or end the iteration silently.
