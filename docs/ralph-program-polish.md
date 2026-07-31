# RemEx Program-Polish Ralph Loop

Autonomous loop that drains the general bead backlog filed by the 2026-07-23 whole-program
investigation (plus older ready work). Unlike the ui-polish loop, this queue is NOT label-scoped:
it spans PC (Avalonia/C#), Android (Kotlin/Compose), content/i18n, code quality, and docs.

## How to run it

```
/ralph-loop "Read docs/ralph-program-polish.md and follow it exactly. Complete ONE bead this iteration." --max-iterations 100 --completion-promise "PROGRAM_POLISH_QUEUE_DRAINED"
```

The prompt argument is deliberately short. Ralph re-feeds the *same* string every iteration and the
model has no memory between them — the instructions live here, on disk, where each fresh iteration
re-reads them and where the operator can edit them mid-run without restarting the loop.

Queue size at filing time was **77 ready beads** (9 P1, 29 P2, 32 P3, 7 P4; 2 epics that the loop
must never claim). Cancel any time with `/cancel-ralph`.

---

## MISSION

Complete exactly ONE bead per iteration. One. Not two, not "a few small ones." A focused,
reviewable, buildable diff per iteration is the goal.

Read `CLAUDE.md` before your first action in this iteration — it contains hard project rules.

You have NO memory of previous iterations. Everything you need to know about prior progress is on
disk: git history on this branch, and the beads tracker. Trust those, not your intuition.

## PROCEDURE — follow in order

1. Run `git branch --show-current`. If it is not `ui-polish-loop`, run `git checkout ui-polish-loop`.
   NEVER work on `main`. (Yes, the branch name says ui-polish — the previous loop ran here and the
   operator chose to continue on the same branch/worktree.)

2. Run `git status`. If the tree is dirty, a previous iteration died mid-work. Inspect the diff and
   either finish that work or `git checkout -- .` to discard it — decide based on whether the diff
   is coherent. Do not start new work on top of an unexplained dirty tree.

3. Run `bd ready`. Pick the highest-priority unblocked issue (0 is most urgent) that is ELIGIBLE.
   If two are equal priority, prefer `bug` over `task`/`chore`, then the smaller effort.
   Run `bd show <id>` and read the full issue including acceptance criteria and comments.

   **NOT ELIGIBLE — never claim:**
   - **Epics** (`[epic]` marker, e.g. RemEx-km0i, RemEx-hb1t). Their actionable children appear in
     `bd ready` individually.
   - **Human-gated beads** (see the list below). On encountering one, defer it so the queue can
     drain past it: `bd update <id> --defer +14d --append-notes "Deferred by ralph loop: <reason —
     needs human/device/other-OS>."` Then return to the top of step 3.

4. Run `bd update <id> --claim`.

5. Before editing any symbol, run `gitnexus_impact({target: "<symbolName>", direction: "upstream"})`
   to check the blast radius. If it reports HIGH or CRITICAL risk, do NOT proceed with that bead —
   run `bd update <id> --status open --append-notes "Deferred by ralph loop: impact analysis
   returned <risk>. Needs human sign-off."` and return to step 3 for a different bead.

6. Implement the change so the bead's acceptance criteria are objectively met. Match the
   surrounding code's conventions.

7. Verify — build whatever platform you touched:
   - Kotlin/Android → `cd remex.android && ./gradlew assembleRelease` (RELEASE, never debug —
     only release runs the `lintVitalRelease` gate).
   - C#/PC (`remex.agent`, `remex.desktop`, `remex.core`) → `dotnet build Remex.sln`, and run
     `dotnet test Remex.sln` if the touched area has tests.
   - Touched both → run both.
   A red build is never an acceptable stopping point. Fix it or abandon per "IF YOU GET STUCK".

8. **REVIEW GATE.** Decide whether this bead requires review. It does if EITHER:
   - the bead's priority is 0 or 1, OR
   - your diff touches any of: `ui/theme/Theme.kt`, `ui/theme/Type.kt`, `ui/theme/Color.kt`,
     `ui/navigation/AppNavigation.kt`, `ui/screens/DashboardInteraction.kt`, any `strings.xml`,
     any `.resx` file, any file in `remex.core/`, `HostBootstrapper.cs`, or any XAML/C# under
     `remex.desktop/Themes/`.

   If neither applies, skip to step 9.

   If review is required, run `git diff` and dispatch a reviewer subagent:
   - Kotlin/Android changes → `Agent(subagent_type: "ecc:kotlin-reviewer", model: "opus")`
   - C#/Avalonia changes → `Agent(subagent_type: "ecc:csharp-reviewer", model: "opus")`

   The subagent cannot see your conversation. Give it: the bead id, title, and full acceptance
   criteria; the complete diff; the guardrails below that apply; and this instruction —

   > Review this diff against the stated acceptance criteria and guardrails. Answer three questions
   > explicitly: (1) Does it actually meet the acceptance criteria, or does it only appear to?
   > (2) Does it violate any guardrail — scope creep beyond the bead, hardcoded strings, hardcoded
   > colors, weakened elevation, altered drag/gesture logic, changed version numbers, touched
   > security-critical code? (3) Is there a correctness bug — wrong theme-role pairing, broken
   > null-safety, a recomposition/state-hoisting error, a NativeAOT-unsafe construct in Remex.Core?
   > Reply with a verdict line `VERDICT: PASS` or `VERDICT: FAIL` followed by specific, actionable
   > findings. Do not pass a diff you have doubts about; do not fail one over pure style preference.

   **On FAIL:** address the findings and re-review. You get a MAXIMUM OF 2 fix rounds. If it still
   fails after the second round, run `git checkout -- .`, append the reviewer's findings verbatim
   to the bead with `bd update <id> --status open --append-notes "..."`, and end the iteration
   without committing. Do not argue with the reviewer or re-run it hoping for a different verdict.

   **On PASS:** proceed to step 9 and include `Reviewed-by: <agent> (PASS)` in the commit message.

9. Commit naming the bead: `<area>: <what changed> (<bead-id>)` where `<area>` is one of
   `ui`, `pc`, `android`, `core`, `docs`, `i18n`, `chore`. Do not push.

10. Run `bd close <id>`.

11. Add a changelog entry to **`docs/CHANGELOG.md`** — NOT the root `CHANGELOG.md`, which is only a
    pointer stub. Put it under the `## [Unreleased]` heading in the correct Keep-a-Changelog section
    (Added/Changed/Fixed/Removed/Security). Do NOT create a new version heading and do NOT move
    entries out of `[Unreleased]` — cutting a release is the human operator's decision.

## HUMAN-GATED BEADS — defer on sight (step 3)

These need the operator, a real device, another OS, or sign-off on security-critical code. Defer
with `--defer +14d` and a note; do NOT attempt them:

- **RemEx-mlce** — PC-side cert-pin check is decorative. Certificate pinning is a CLAUDE.md
  high-risk area; changes require explicit operator sign-off.
- **RemEx-tr1s** — per-IP throttle on the /ws pairing path. Pairing flow is a high-risk area.
- **RemEx-z6lh** — v3 push-offer consent handshake. Protocol + consent semantics; needs operator
  decision and a coordinated on-device round-trip.
- **RemEx-6xe4** — PC client v3 resumable binary channel. Protocol-touching; coordinated testing.
- **RemEx-hb1t.4** — on-device verification + host deploy. Operator-only.
- **RemEx-pej2, RemEx-mt1u** — require CachyOS/Linux hardware verification.
- **RemEx-km0i.24** — aesthetic redesign; pure visual judgment, needs a human eye.
- **RemEx-whxz** — latent Linux libei stub; cannot be meaningfully verified on this machine.
- **Any other bead** whose acceptance criteria cannot be verified by builds/tests on this Windows
  machine (needs a phone in hand, Linux, or subjective visual judgment) — same deferral, state why.

## HARD GUARDRAILS — violating any of these is a failed iteration

- **Never change the version.** Do not touch `<Version>` in `Directory.Build.props` or
  `versionName`/`versionCode` in `remex.android/app/version.properties`.
- **Never push, never merge to main, never force-push, never rewrite history.**
- **Release variant only** for Android verification (`assembleRelease`, never `assembleDebug`).
- **Every new user-facing string is a 9-file change.** Android: `res/values/strings.xml` plus all 8
  locale variants (es, fr, hi, in, pl, pt-rBR, tr, uk). PC: `Localization/Strings.resx` plus its 8
  variants. A string hardcoded inline in Kotlin or XAML is a regression. If you cannot produce a
  good translation, still add the key to every file using the English text as a placeholder AND
  file a bead labelled `i18n` to translate it properly. When REMOVING keys (orphan-purge beads),
  remove from ALL 9 files so key-set parity survives. Do not be the iteration that breaks parity.
- **Theme safety — per platform, do not mix the axes.**
  - PC (`remex.desktop`): four named themes — CyberNOC, Monolith, SolarFlare, BaseDarkGlass. Use
    theme resources/tokens, never hardcoded colors. You cannot visually verify themes in this loop;
    correctness = every color you touch resolves through a theme resource that exists in all four.
  - Android: the four PC themes DO NOT EXIST here. Real axes per `ui/theme/Theme.kt`: light/dark ×
    scheme source (custom seed / dynamic color API 31+ / static fallback) × themeStyle (7 values
    incl. monochrome) × themeContrast (0.0–1.0). Use color-scheme roles, never literals — a literal
    that looks fine on the default scheme fails under monochrome or contrast 1.0.
- **Do not touch security-critical code.** Pairing (`PairingHandler`, `PairedClientRegistry`),
  certificate pinning, `RemexMessage` envelope / `protocolVersion`, `ChannelReconnectAuth`,
  elevation, and cert ACLs are out of scope entirely. If a bead requires it, defer per the
  human-gated list.
- **Do not alter the dashboard drag/gesture system.** The drag, pan, long-press and multi-select
  logic in `DashboardInteraction.kt` is deliberate and must be byte-identical in your diff unless a
  bead explicitly says otherwise.
- **NativeAOT safety in `Remex.Core`:** no reflection, no dynamic codegen, source-generated JSON
  only (`[JsonSerializable]` + context).
- **No `ConfigureAwait(false)`** anywhere. Use `Guard.NotNull` for required ctor dependencies.
- **Dead-code / purge beads: verify before deleting.** For every symbol or resource key you remove,
  confirm zero references with `token-savior: get_dependents` or a repo-wide search — including
  XAML `{x:Static}` / reflection-free bindings and Android XML references. If a "dead" item turns
  out to be referenced, note it on the bead instead of forcing the deletion.
- **Scope discipline.** Fix the bead in front of you. If you spot an unrelated problem, do NOT fix
  it — run `bd create` to file it and move on. Opportunistic refactoring across iterations is how
  this loop destroys a working app.

## KNOWN BEAD INTERACTIONS

- **RemEx-07jx (delete-without-confirmation bug) and RemEx-6p1f (confirm all destructive PC
  actions)** overlap. If you do 07jx first, implement the confirmation dialog in a reusable way;
  if 6p1f lands first it likely closes 07jx too — check before duplicating work.
- **RemEx-o8n1 / RemEx-u4kw / RemEx-k521 / RemEx-oq6l / RemEx-c0dd** all remove stale
  "service/desktop-client era" content on overlapping files. Do them one at a time and re-read the
  current file state; an earlier iteration may have already removed some strings the bead cites.
- **RemEx-b5kx (resx purge) and RemEx-3jne (Android strings purge)** must respect any keys ADDED by
  intervening iterations — regenerate the orphan list at execution time; do not trust the counts in
  the bead description.
- **RemEx-suc9 (localize hardcoded PC strings)** ADDS resx keys while purge beads REMOVE others —
  ordering is fine either way, but always recompute, never assume.

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
bd ready --json
```

Output the completion promise `PROGRAM_POLISH_QUEUE_DRAINED` **only** when ALL of the following are
simultaneously and verifiably true:

1. That command returns nothing except epics (`issue_type == "epic"`) — every non-epic entry is
   gone (closed or deferred with a note per the rules above).
2. `cd remex.android && ./gradlew assembleRelease` exits 0.
3. `dotnet build Remex.sln` exits 0.
4. `git status` shows a clean tree on `ui-polish-loop`.

You must actually RUN all four checks and see their real output in this iteration before making the
claim. Do not output the promise because you feel finished, because progress seems slow, because
the remaining work looks hard, or because you think you should stop. An unearned completion promise
is a lie to the operator and the single worst outcome of this loop. If eligible beads remain, the
correct action is always to do more work, defer a bead per the rules, or end the iteration silently.
