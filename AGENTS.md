## RemEx Architecture & Project Rules

These rules apply to ALL agents working in this repository. They are not overridable by individual session context.

### Host = PC, Client = Android. End of Story.

- `remex.agent` is the **entire PC side** — a single interactive elevated user-session app (always elevated via `requireAdministrator` manifest; auto-started by an elevated Task Scheduler logon task) that provides all PC functionality. Android connects TO this.
- `remex.android` is the **only** network client. Nothing else is a client.
- Connection is always Android → PC.
- `remex.desktop/` holds PC-side UI code only (Views/ViewModels/Localization) and is compiled directly into `remex.agent` via a real `<ProjectReference>` — it is NOT a standalone app and NOT being removed. "Legacy" refers only to the leftover pre-rename folder/namespace name. A prior removal effort (`RemEx-d8s`) was closed without deleting it; do not add new *standalone-client* code there, but the existing UI code is live and required.
- That client link is **always non-loopback**. The PC's own UI is in the same process as the host and reaches it through DI (`EmbeddedHostServiceLocator`), not a socket — any loopback you see on the PC side is local UI plumbing, never a client connection.

<!-- AUTO-MANAGED: build-commands -->
### Build & Run

`build-remex.ps1` is the canonical cross-platform entry point (runs under `pwsh` on Windows and Linux).

```powershell
# Full release build for all platforms
./build-remex.ps1 -c release -t all

# Platform-specific targets
./build-remex.ps1 -t windows        # publish + Inno Setup installer
./build-remex.ps1 -t android        # APK + AAB via Gradle
./build-remex.ps1 -t linux          # tar.gz via installer/build-linux.sh (WSL on Windows)

# Target aliases
./build-remex.ps1 -t installer      # Windows installer only (skips publish if artifacts/ exists)
./build-remex.ps1 -t windows-client # Windows publish only (skips installer)
./build-remex.ps1 -t apk            # alias for -t android

# Incremental rebuild (skip clean, reuse artifacts/)
./build-remex.ps1 -t windows -NoClean

#Update local install 
./scripts/update-local-install.ps1 # Publishes `Remex.Agent` (self-contained, win-x64) 
```

**Build output layout:**
- Intermediate / publish: `artifacts/` (UseArtifactsOutput — not per-project `bin/` or `obj/`)
  - Windows publish: `artifacts/publish/remex.agent/{Config}_win-x64/`
- Final distributables: `build_output/windows/`, `build_output/android/`, `build_output/linux/`

**Android prerequisites** (auto-installed by the script if missing):
- Android SDK API Level 37
- NDK version `30.0.14904198`
- Requires `ANDROID_HOME` env var or `remex.android/local.properties` (`sdk.dir=...`)

**Version sync:** script reads `versionName` from `remex.android/app/version.properties` and patches `Directory.Build.props` automatically on every run.

**Android Gradle tasks (direct):**
- `./gradlew remexFreshAssembleDebug` / `remexFreshAssembleRelease` — build without bumping version.
- `./gradlew remexPublishRelease` — bumps patch version (`versionCode+1`, minor+1, patch→0) and writes back to `version.properties` before building. Use only for actual releases.
- Signing: reads `remex.signing.*` keys from `local.properties`; falls back to debug signing if absent (safe for local test builds).
- `libRemexCore.so` is resolved from `artifacts/bin/remex.core/<config>_net10.0-android_android-arm64/native/` (UseArtifactsOutput layout) with fallback to legacy `bin/`. APK output named `RemEx-V${versionName}-${variant}.apk`.
<!-- END AUTO-MANAGED -->

### Hard Rules — repeat mistakes, do not re-litigate these

These exist because the same corrections have had to be made more than once across sessions. If any
other doc, old commit message, or stale issue status disagrees with these, **these win.**

1. **There is no headless host process, and there never was one separate from the UI.** `remex.agent`
   is a single process that IS both the former host service and the UI. Never describe or design
   around a "PC-side client connecting to a PC-side host" — that pair does not exist. If you find old
   references to a "desktop client" connecting to a "desktop host", they are stale; update them.
   `remex.desktop`'s UI code itself is current and is not one of those stale references.
2. **`remex.desktop/` is permanent, not being removed.** It holds only UI code
   (Views/ViewModels/Localization) and is a real, current `<ProjectReference>` of `remex.agent`.
   "Legacy" describes the leftover folder name from a pre-rename layout, not its lifecycle status. Do
   not treat it as dead code, do not suggest deleting it, do not say it is "being phased out." A
   prior removal effort (`RemEx-d8s`) was closed **without** deleting it. The intended end state IS
   "UI code lives in remex.desktop, gets compiled into remex.agent."
3. **Verify an issue's status with `bd show <id>` before citing it as fact.** The status tables in
   docs have gone stale relative to the real tracker more than once. `bd` is the source of truth;
   docs are a cache that can lag.
4. **Never construct a `git add` path — or any case-sensitive path comparison — from memory. Copy the
   exact case from `git status` / `ls` output.** This repo is Windows-authored but must build on
   case-sensitive Linux. A case mismatch in a git pathspec **silently stages nothing** (Windows git
   is case-insensitive by default), which has previously left real fixes stranded uncommitted. In
   PowerShell, never use `-eq` / `-ne` to compare paths or namespaces that must be case-sensitive —
   use `-ceq` / `-cne`.

### Autonomous board drain — `/ralph` and `/drain`

The board-drain workflow is **installed globally**, not in this repo: skills at
`~/.claude/skills/{ralph,drain}`, scripts and the generic procedure at `~/.claude/ralph/`. What
lives here is what is actually about RemEx — `.ralph.psd1` at the repo root (the verify contract,
the bead prefix, which paths force a full-suite verify and which force a code review),
`docs/ralph-board-drain.md` as the project overlay appended to the generic procedure, and
`docs/ralph-state.jsonl` as the journal. `.ralph.psd1` is tracked on purpose: lane worktrees only
materialise tracked files, so an untracked config would leave lanes unable to find their settings.

### Verification — `scripts/verify.ps1`

**`scripts/verify.ps1` is the only accepted proof that work is finished.** It force-cleans, rebuilds,
runs the suite, checks the edit guard and the translations, then writes a receipt to
`.ralph/verify-receipt.json` recording a SHA-256 fingerprint of every source file it verified against.

```powershell
./scripts/verify.ps1              # .NET solution
./scripts/verify.ps1 -Scope all   # .NET plus Android unit tests and the release lint gate
./scripts/verify.ps1 -Check       # does the last receipt still describe the code on disk?
```

- **An issue is not done until `-Check` says VALID.** "The tests passed" is not a claim anyone can
  check; a matching fingerprint is. Edit anything afterwards and the receipt is void — that is the
  point, not a bug.
- Receipts are per-machine and gitignored deliberately. They record `platform`, so a receipt written
  under WSL/Linux is refused on Windows — `artifacts/` is shared between those builds.
- **Never revert a defect injection with `git checkout -- <file>`.** It discards *every* uncommitted
  change in that file, not the line you injected, and has silently thrown away real fixes. Capture
  and reverse a scoped patch instead:
  ```bash
  git diff -- <file> > /tmp/inject.patch   # then inject the defect, run the tests
  git apply -R /tmp/inject.patch           # restores exactly what you changed
  ```
  A defect-injection run where everything stays green proves the test is blind, not that the code is
  correct. The test MUST fail with the defect present and pass once restored.

### Where a written artefact goes

**Specs, spikes, investigations and measurements go in `docs/`, NOT in `docs/superpowers/specs/`.**

`docs/superpowers/` is gitignored, so anything written there is never committed — the task closes,
the author believes the artefact exists, and the repo has nothing. It looks like the right home
because the directory exists locally and holds a pile of earlier specs, and nothing warns you at
write time; you only find out when `git add` silently stages nothing. This has caught three separate
spikes (`RemEx-0l9x`). `SPIKE-*.md` and `MEASURE-*.md` already live in `docs/` — follow that.

**After writing any artefact, confirm it is actually tracked**: `git status` should show it, or
`git check-ignore -v <path>` should print nothing. An issue closed against an untracked file is worse
than one that admits it produced nothing.

### UI verification axes — the two platforms theme completely differently

Do not apply one platform's axes to the other.

- **PC only (`remex.desktop` / `remex.agent`)** — the four named themes **CyberNOC, Monolith,
  SolarFlare, BaseDarkGlass**. Each has distinct contrast ratios and background treatments; a change
  that looks right in one can break another. **These four do not exist on Android.**
- **Android only (`remex.android`)** — Material 3 dynamic theming, no named themes. `RemExTheme`
  (`ui/theme/Theme.kt`) resolves a scheme from three mutually exclusive sources, and a change must
  hold up under all three: a **custom seed**
  (`colorSchemeFromSeed(seedColor, darkTheme, themeStyle, themeContrast)`); **dynamic color**
  (`dynamicDark/LightColorScheme(context)`, whenever `dynamicColor` is on — the `SDK_INT >= S`
  conjunct was dead at minSdk 34 and was removed in RemEx-jcl4p); and the **static fallback**
  (`DarkColorScheme`/`LightColorScheme`, used when dynamic color is off — still a real shipping path,
  now for that reason alone rather than also for API < 31). Orthogonal axes on top: `darkTheme` (light/dark/system), `themeStyle` (7
  values — `tonal_spot` default, plus expressive, vibrant, neutral, **monochrome**, fruit_salad,
  rainbow), and `themeContrast` (0.0 → 1.0). Monochrome and contrast 1.0 are the harshest tests: a
  hardcoded color literal that looks fine on the default scheme will fail there.

**Never ask for four-theme verification of an Android change** — there is nothing to verify against
and the instruction is pure noise.

### Regression Guards

Rules that exist because breaking them reintroduced a real, silent failure — black screens, dead
streams, bricked pairings — live in **[`docs/REGRESSION-GUARDS.md`](docs/REGRESSION-GUARDS.md)**.

Read it before touching Windows/Linux capture, the remote-desktop stream or pacing, the Android
H.264 decoder, SurfaceView zoom/pan, pairing and trust, or the session guard.

That file is hand-maintained on purpose. It replaced an auto-generated block here that drifted out
of sync with the code and at one point instructed agents to do the exact opposite of what the code
does. Do not regenerate it, and do not copy its guards back into this file — one authoritative copy
is the point.

### MCP Tool Discipline

Before reaching for `grep`, `Read`, or raw `Bash`, consult the decision matrix in `CLAUDE.md`:
- **Find / read symbols** → `token-savior: find_symbol`, `get_function_source`, `get_dependencies`
- **Before ANY edit** → `gitnexus: impact` (upstream blast radius)
- **Explore flows / concepts** → `gitnexus: query` or `gitnexus: context`
- **Large command output / data processing** → `context-mode: ctx_execute`

**Context7 Library References** — Use these MCP context7 library IDs to auto-fetch live documentation when working with RemEx technologies:
- **.NET** → `/dotnet/docs` (official Microsoft .NET documentation, 85k+ code snippets)
- **Avalonia UI** → `/avaloniaui/avalonia-docs` (official Avalonia framework docs, 40k+ snippets)
- **Kotlin** → `/jetbrains/kotlin-web-site` (official JetBrains Kotlin language docs, 7.6k+ snippets)
- **Jetpack Compose** → `/websites/developer_android_develop_ui_compose` (official Google Android Compose docs, 4.3k+ snippets)


### Cross-Platform Parity

This repo lives on a shared drive and must work equally on **Windows** and **CachyOS/Linux**. Any
change touching the PC side (`remex.agent`, scripts, installers, build tooling) must maintain parity:

- Every `.ps1` must work under `pwsh` on Linux **or** have a `.sh` equivalent that does the same job.
- Never hardcode Windows-only paths. Use path helpers or environment variables.
- `build-remex.ps1` is the canonical cross-platform build entry point. New build steps must be added
  for both platforms.
- Before closing a task: verify on both platforms, or explicitly state which OS was tested and file a
  follow-up issue for the other.

#### Running the test suite on Linux from a Windows box (WSL)

You can actually check parity rather than promising it. The obvious command does **not** work — the
WSL .NET install typically has only `Microsoft.NETCore.App`, not `Microsoft.AspNetCore.App`, so a
plain `dotnet test` on `remex.agent.tests` dies with *"You must install or update .NET to run this
application"*. Building self-contained bundles the ASP.NET runtime into the test output and needs no
package install or other change to the WSL system:

```bash
wsl -- bash -lc "cd /mnt/z/RemEx && dotnet test remex.agent.tests/remex.agent.tests.csproj \
  -c Release -p:RuntimeIdentifier=linux-x64 -p:SelfContained=true"
```

The same flags work for `remex.core.tests` and `remex.desktop.tests` (the desktop suite needs no
display). All three were run on Linux for RemEx-vh62 and are Linux-clean in the sense that matters:
**green, with skips rather than failures.** The skips are tests asserting Windows-only primitives, marked `[WindowsOnlyFact]` so a
Linux run stays readable — without that, permanent noise makes a real regression indistinguishable
from the usual failures, nobody runs it, and the parity rule quietly stops being enforced.

So on Linux, expect a *higher skip count* than Windows and **zero failures**. A failure is worth
looking at; a skip is the marking working. Do not hardcode the expected counts anywhere, and note
that this paragraph does not state them: it used to, and they were wrong within days, because the
suites grow.

`remex.agent.tests` will not run on Linux without `-p:RuntimeIdentifier=linux-x64
-p:SelfContained=true`, and `remex.core.tests` was measured aborting the same way without them: the
host exits with "You must install or update .NET" when
`Microsoft.AspNetCore.App` is absent, which looks like a broken machine and is not.
`scripts/verify.ps1` detects this and adds the flags for you, so this only bites a bare
`dotnet test`.

This shares the `artifacts/` directory with the Windows build, so **rebuild on Windows afterwards**
before trusting a Windows test run.

#### Windows-only tests are marked, not deleted

`WindowsOnlyFactAttribute` (in `remex.agent.tests` AND `remex.desktop.tests` — two deliberate copies,
reasoned out in the remarks on each) takes a mandatory reason and skips on non-Windows,
so a Linux run is green and a real regression stays visible instead of drowning in permanent noise.
Use it when a test asserts a genuinely Windows-only primitive — named memory-mapped files, UNC path
semantics. **Never weaken a test so it passes on both**; that trades away coverage on the platform the
code actually runs on. There is deliberately no `Theory` counterpart until something needs one; see
the note in `WindowsOnlyAttributes.cs` for the xUnit quirk that would complicate it.

### Coding Conventions

**Async — do NOT use `ConfigureAwait(false)` anywhere.** On the desktop side the captured context is
load-bearing: continuations assign bound properties after awaits that complete off the UI thread, so
the flag is actively harmful there, not merely redundant. On the ASP.NET Core host side there is no
context, so there is nothing to gain. CA2007 is suppressed in `.editorconfig` — **the rule here is
the opposite of its default advice** — and `ConfigureAwaitBanTests` enforces the ban. This is **not**
justified by "Avalonia has no `SynchronizationContext`"; that was the old reason given and it is
false (RemEx-rbfq). See `docs/ASYNC_GUIDELINES.md`.

**Null safety.** Nullable reference types are enabled in all projects. Use `Guard.NotNull(arg)`
(`remex.core/Guards/Guard.cs`) in constructors for required dependencies, and `GetRequiredService<T>()`
— never `GetService<T>()` — for DI resolution. See `docs/NULL_SAFETY_GUIDELINES.md`.

**Validation.** All network-facing input must go through the shared helpers in
`remex.core/Validation/`. See `docs/VALIDATION_GUIDELINES.md`.

**NativeAOT constraints (`Remex.Core`).** `Remex.Core` is compiled as a NativeAOT JNI library
(`libRemexCore.so`) for Android. Code there **must be NativeAOT-safe** or the Android build breaks at
link time, often with no obvious connection to the change you made.

- **No reflection** — `typeof(T).GetMethod(...)`, `Activator.CreateInstance`, `JsonSerializer` with
  non-source-generated options are all forbidden.
- **No dynamic code generation** — no `System.Linq.Expressions` compilation, no `Emit`, no runtime
  type building.
- **Trimming-safe** — use `[DynamicallyAccessedMembers]` and `[RequiresUnreferencedCode]` where
  needed. Trimming is enabled; unannotated reflection silently disappears.
- **Source-generated JSON** — `[JsonSerializable]` + `JsonSerializerContext` for any new serializable
  type. Never `JsonSerializer.Serialize<T>(obj)` without a source-gen context.
- Unsure whether something is NativeAOT-safe? Check `Remex.Core` for an existing pattern first.

**Elevated interactive-session app (`remex.agent` on Windows).** It runs in the signed-in user's
interactive session, **always elevated** (high integrity) — NOT as a Windows Service, NOT in
Session 0. A Task Scheduler logon task (`scripts/autostart-remex.ps1`, task name `RemEx`,
`RunLevel=Highest`, `LogonType=InteractiveToken`) starts it elevated at sign-in with no UAC prompt.
(RemEx-aep)

- **Capture and input work directly** — inside the session, screen capture and `SendInput` reach the
  user's desktop, and HIGH→HIGH UIPI is permitted so input reaches elevated windows. There is no
  session bridging and no `CreateProcessAsUser`.
- **Machine-wide config still uses `HKLM` / `ProgramData`** — `cert.pfx`, `paired_clients.json` and
  `CaptureBackendPreference` stay machine-wide so they survive across logins and stay under the
  elevated-only ACL. `HKCU` / `%APPDATA%` are valid for genuinely user-scoped state, but keep
  security-sensitive state machine-wide.
- **Elevation is load-bearing — never weaken it.** An elevated token keeps FullControl over the
  machine-wide `cert.pfx` / `paired_clients.json` (ACL = LocalSystem + Administrators, inheritance
  disabled). A medium-integrity start gets Administrators as deny-only, fails to read `cert.pfx`, and
  **bricks every SPKI-pinned pairing.** Never ship a path that auto-starts non-elevated.
  `CertificateService` has a brick canary: it logs Critical and refuses to regenerate when an existing
  `cert.pfx` is unreadable.
- **No Windows Service, no named pipes** — `LocalIpcServerService`, `RemExLocalIPC`,
  `HostControlServer/Client`, `AgentCoordinator`, `SessionBridgingCommandService` and
  `WindowsActiveSession` were deleted. The UI resolves host services in-process via
  `EmbeddedHostServiceLocator`.

**Localization.** All user-facing strings in `remex.agent` (UI labels, tooltips, error messages,
notifications) must go through `Localization/`. The app supports 8 languages with live switching —
hardcoded English strings are a regression.

- Add new strings to the appropriate `.resx` / localization file, never inline in code or XAML.
- Never use `string.Format` or interpolation directly in UI-bound properties; use localized format
  strings.
- Purely internal strings (logs, exception messages, developer-facing) may stay in English.
- **Bulk-edit `.resx` / `.xml` with a Python script and explicit UTF-8 — never PowerShell string
  interpolation.** Apostrophe mis-escaping and array flattening have written NUL bytes into
  `Strings.tr.resx` more than once. The PostToolUse guard (`.claude/scripts/guard_edit.py`) catches
  NUL bytes, duplicate keys and malformed XML at write time — but it catches the corruption, it does
  not prevent you causing it.

**Versions.** .NET in `Directory.Build.props`; Android in `remex.android/app/version.properties`.
`build-remex.ps1` syncs the former from the latter automatically.

### User Experience Standards

The target user **may not be technical.** Every user-facing element must be:

- **Plain English** — no jargon, no abbreviations, no assumed knowledge, in scripts, installers, UI
  tooltips and error messages alike.
- **Hand-holdy** — scripts print friendly status messages and tell the user exactly what to do when
  something fails. Always provide a "what to do next" step.
- **Consistent** — `build-remex.ps1` is the canonical entry point for all major build/install
  operations. Major operations belong there, not buried in sub-scripts.
- **Theme-safe** — see the UI verification axes above, and use the right platform's axes.

### Code Quality

No lazy code. Use the most correct, robust, maintainable approach. No stub methods, `TODO:` bodies, or "good enough for now" placeholders. Check existing infrastructure (`remex.core/Guards`, `remex.core/Validation`) before writing new utilities.

### Docs & CHANGELOG on Every Change

Every code change must also update `CHANGELOG.md` (Keep a Changelog format). Update affected XML doc comments, `docs/` files, and version numbers when warranted. A task is not complete until docs are updated.


## Splitting a bead: put the join in the FIRST half

**Observed four times in one drain session, every time with a defect hiding at the join**
(RemEx-hev1g). `PhonePresence`, `PairedDeviceDisplayName`, `PairingPinCountdown` and the
paired-device host facts were each shipped fully tested, mutation-verified — and consumed by nothing
in production. `ClipboardValidation` was a fifth; RemEx-hgqs wired it and it is no longer stranded.

The mechanism is the same every time. A bead is split into "the logic" and "the surface". The logic
half is the pleasant one — pure, testable, mutation-verifiable — so it lands first with a good test
count and a signed-off decision record. The surface half is the awkward one: axaml, view models, nine
resx files, four themes. It goes back on the board and sits there.

**That reads as progress while the user gets nothing**, and the tests give a false impression of
coverage because the code they cover is unreachable. Worse, every one of those four had something
wrong AT THE JOIN that no amount of testing the pure half could catch: a flag only a hidden button
could set, a countdown the timer destroyed before it rendered, a list driven from the wrong side.

So:

1. **Put the join in the first half.** Landing `PhonePresence` with one binding in one view would
   have been a smaller change than landing it with 17 tests and no caller.
2. **If a split really must strand the logic, say so on BOTH beads and make the surface bead a
   BLOCKER rather than a sibling**, so the board shows one incomplete feature rather than one done
   and one open.
3. **Do not automate this with a reference count.** It was tried: a scan for public static classes in
   `remex.desktop/Services` and `remex.core/Services` with test references and no production ones
   returns six hits, and at least one — `FireAndForgetExtensions` — is a false positive with three
   production call sites, because extension methods are invoked by MEMBER name and never by the class
   name. A check that flags correct code is worse than no check: the only way to make it pass is to
   add a name to an allowlist, and a list that can absorb a false positive will absorb a real one
   (that exact sequence cost an iteration under RemEx-dnn2q). The reliable signal is a human noticing
   a bead whose acceptance never mentions a user.

### The other half of the rule: unwired logic is NOT automatically a defect

The section above is about what to do when you SPLIT a bead. This is about what to do when you FIND
the result of one, and it points the opposite way — because an agent who reads only the section above
will go looking for unwired helpers to flag, and most of them are fine.

A sweep across both languages found eight pieces of shipped, tested logic with no production caller
(RemEx-thwlr). **Two were defects and six were deliberate.** The distinguishing test is not "does it
have a caller":

> **It is a defect when a LIVE PATH ALREADY DOES THE SAME JOB — worse, or not at all.**
> It is deliberate when the feature that will consume it does not exist yet.

Both real findings had that shape and none of the others did:

- `PairingRouteArgs.buildPath` had no caller while `AppNavigation` interpolated the same route by
  hand (fixed, RemEx-ph4nw).
- `DiscoveredHostList.usableOnly` had no caller while `discoverHost` validated the host and not the
  port. RemEx-7gk69 fixed the DEFECT by extracting `isUsable` and wiring that into `discoverHost` —
  and `usableOnly` itself is still uncalled, which by the rule above is fine: the list it filters has
  not landed (RemEx-8ih5). Worth reading twice, because the two halves of that sentence are the whole
  distinction. The duplicated *rule* was the bug; the uncalled *helper* never was.

The six that were fine each mapped to an open feature bead that had not landed yet. Shipping the
provable arithmetic first, so the cases that break can be proven without a device, is a deliberate
practice here.

**A sweep produces candidates, not findings.** Two of the eight hits were flaws in the search itself,
and both were caught only by opening the code: `ScreenshotEncoder` is called from the same file it is
declared in (the heuristic skipped the declaring file to avoid self-references), and
`FireAndForgetExtensions` is an extension method, invoked as `task.FireAndForget(...)` and never
through the class name that was being grepped for.

### Sweeping for inert GUARDS: ask what it asserts, not where it looks

A second sweep asked whether any source-scanning test points at a file that has moved — a scan whose
target is gone passes by finding nothing. Across 54 .NET and 5 Kotlin scanning tests, **none was
stale**; all 25 non-resolving names were the sweep being wrong (runtime-created temp files, a
synthetic label, one `endsWith` suffix).

That is the useful result, because it says the sweep was aimed at the wrong question. Every inert
guard found in that session failed on **what it asserted**, not on where it looked: a regex
containing literal backspace bytes, a scan blind to class-filled containers, a predicate re-checked
by a second layer, a count that scored `createNotificationChannel(` as a construction because the
string contains `NotificationChannel(`, an assertion forbidding a command name no view model has,
probes all placed at depth 1 so a parent-only collapse passed.

So there is no sweep for this. **"Can this assertion fail?" is asked one guard at a time, by mutating
the thing it guards and watching it go red.** What generalises instead is the cheap structural
defence: an anti-vacuity assertion — `NotEmpty` on the scan's own output before comparing it — so a
scan that has stopped finding anything fails loudly instead of passing on an empty set.


<!-- The GitNexus block below is GENERATED. Anything hand-written between its markers is
     lost on the next `npx gitnexus analyze`. The bead-splitting rules above used to live inside it
     (found and moved in RemEx-thwlr) — keep hand-written guidance above this line. -->
<!-- gitnexus:start -->
# GitNexus — Code Intelligence

This project is indexed by GitNexus as **RemEx** (24340 symbols, 56143 relationships, 300 execution flows). Use the GitNexus MCP tools to understand code, assess impact, and navigate safely.

> If any GitNexus tool warns the index is stale, run `npx gitnexus analyze` in terminal first.

## Always Do

- **MUST run impact analysis before editing any symbol.** Before modifying a function, class, or method, run `gitnexus_impact({target: "symbolName", direction: "upstream"})` and report the blast radius (direct callers, affected processes, risk level) to the user.
- **MUST run `gitnexus_detect_changes()` before committing** to verify your changes only affect expected symbols and execution flows.
- **MUST warn the user** if impact analysis returns HIGH or CRITICAL risk before proceeding with edits.
- When exploring unfamiliar code, use `gitnexus_query({query: "concept"})` to find execution flows instead of grepping. It returns process-grouped results ranked by relevance.
- When you need full context on a specific symbol — callers, callees, which execution flows it participates in — use `gitnexus_context({name: "symbolName"})`.

## Never Do

- NEVER edit a function, class, or method without first running `gitnexus_impact` on it.
- NEVER ignore HIGH or CRITICAL risk warnings from impact analysis.
- NEVER rename symbols with find-and-replace — use `gitnexus_rename` which understands the call graph.
- NEVER commit changes without running `gitnexus_detect_changes()` to check affected scope.

## Resources

| Resource | Use for |
|----------|---------|
| `gitnexus://repo/RemEx/context` | Codebase overview, check index freshness |
| `gitnexus://repo/RemEx/clusters` | All functional areas |
| `gitnexus://repo/RemEx/processes` | All execution flows |
| `gitnexus://repo/RemEx/process/{name}` | Step-by-step execution trace |

## CLI

| Task | Read this skill file |
|------|---------------------|
| Understand architecture / "How does X work?" | `.claude/skills/gitnexus/gitnexus-exploring/SKILL.md` |
| Blast radius / "What breaks if I change X?" | `.claude/skills/gitnexus/gitnexus-impact-analysis/SKILL.md` |
| Trace bugs / "Why is X failing?" | `.claude/skills/gitnexus/gitnexus-debugging/SKILL.md` |
| Rename / extract / split / refactor | `.claude/skills/gitnexus/gitnexus-refactoring/SKILL.md` |
| Tools, resources, schema reference | `.claude/skills/gitnexus/gitnexus-guide/SKILL.md` |
| Index, status, clean, wiki CLI commands | `.claude/skills/gitnexus/gitnexus-cli/SKILL.md` |

<!-- gitnexus:end -->

<!-- BEGIN BEADS INTEGRATION v:1 profile:minimal hash:970c3bf2 -->
## Beads Issue Tracker

This project uses **bd (beads)** for issue tracking. Run `bd prime` to see full workflow context and commands.

### Quick Reference

```bash
bd ready              # Find available work
bd show <id>          # View issue details
bd update <id> --claim  # Claim work
bd close <id>         # Complete work
```

### Rules

- Use `bd` for ALL task tracking — do NOT use TodoWrite, TaskCreate, or markdown TODO lists
- Run `bd prime` for detailed command reference and session close protocol
- Use `bd remember` for persistent knowledge — do NOT use MEMORY.md files

**Architecture in one line:** issues live in a local Dolt DB; sync uses `refs/dolt/data` on your git remote; `.beads/issues.jsonl` is a passive export. See https://github.com/gastownhall/beads/blob/main/docs/SYNC_CONCEPTS.md for details and anti-patterns.

## Agent Context Profiles

The managed Beads block is task-tracking guidance, not permission to override repository, user, or orchestrator instructions.

- **Conservative (default)**: Use `bd` for task tracking. Do not run git commits, git pushes, or Dolt remote sync unless explicitly asked. At handoff, report changed files, validation, and suggested next commands.
- **Minimal**: Keep tool instruction files as pointers to `bd prime`; use the same conservative git policy unless active instructions say otherwise.
- **Team-maintainer**: Only when the repository explicitly opts in, agents may close beads, run quality gates, commit, and push as part of session close. A current "do not commit" or "do not push" instruction still wins.

## Session Completion

This protocol applies when ending a Beads implementation workflow. It is subordinate to explicit user, repository, and orchestrator instructions.

1. **File issues for remaining work** - Create beads for anything that needs follow-up
2. **Run quality gates** (if code changed) - Tests, linters, builds
3. **Update issue status** - Close finished work, update in-progress items
4. **Handle git/sync by active profile**:
   ```bash
   # Conservative/minimal/default: report status and proposed commands; wait for approval.
   git status

   # Team-maintainer opt-in only, unless current instructions forbid it:
   git pull --rebase
   bd dolt push
   git push
   git status
   ```
5. **Hand off** - Summarize changes, validation, issue status, and any blocked sync/commit/push step

**Critical rules:**
- Explicit user or orchestrator instructions override this Beads block.
- Do not commit or push without clear authority from the active profile or the current user request.
- If a required sync or push is blocked, stop and report the exact command and error.
<!-- END BEADS INTEGRATION -->


### Common commands

- `dotnet run --project remex.agent` — run the PC side (Android connects to this).
- `dotnet test Remex.sln` — run all tests.
- `pwsh ./scripts/android-fresh.ps1 -Configuration Release` — hardened fresh Android build. Runs
  under `pwsh` on Windows and Linux: it resolves `gradlew.bat` vs the POSIX `gradlew` via `$IsWindows`
  and invokes the latter through `sh`, because git may not preserve its executable bit on a shared
  drive.
- `./installer/build-linux.sh` — build Linux packages (uses WSL on Windows).
- `dotnet run --project remex.agent -- --doctor` — check Linux PipeWire/X11/VAAPI prerequisites.

<!-- AUTO-MANAGED: architecture -->
## Architecture

- `remex.core/` — shared models, messages, validation, Guards, serialization; also compiled as `libRemexCore.so` (NativeAOT JNI) for Android. Must stay NativeAOT-safe.
  - `Validation/CoordinateValidation` — `ClampAbsolute(float, int)` / `ClampDelta(float, int)`: sanitize untrusted float coordinates from remote clients before pixel cast (rejects NaN/±Infinity).
  - `Native/RemexDesktopClient` — singleton (`RemexDesktopClient.Current`) WebSocket client for `/ws/desktop`; consumed by `remex.desktop` (legacy). Lives in Core and is NativeAOT-compiled — must remain NativeAOT-safe. Events: `FrameReceived`, `CursorShapeReceived`, `Disconnected`.
  - `Models/DesktopCursorBinaryEnvelope` — 32-byte binary cursor-position packet: magic `"RDXC"`, `Write()`/`TryRead()`, signed X/Y (negative virtual-desktop origins supported), `BinaryPrimitives`/spans (NativeAOT-safe). Demuxed from `"RDXF"` frame envelope and raw NAL start codes by leading 4 bytes. Gated by `SupportsBinaryCursor` capability; no `protocolVersion` bump.
  - `Services/BgraFrameConverter` — `TryConvertNoScale(IntPtr src, int rowPitch, int width, int height, double scale)`: row-wise `Marshal.Copy` fast path for BGRA32 staging textures (WGC/DXGI); returns `null` when a downscale is needed (caller falls back to GDI+ bilinear). No `System.Drawing`; NativeAOT-safe.
- `remex.agent/` — the PC side: a single elevated interactive-session Avalonia app + in-process ASP.NET host (Minimal APIs, WebSocket, mDNS). Runs inside the signed-in user's session, always elevated; `Program.Main` owns the embedded host's Start/Stop and a `Local\RemExGuiHost` single-instance mutex. No Windows Service, no second process. (RemEx-aep)
  - `Handlers/` — `PairingHandler` (ECDH P-256 handshake), `RemoteDesktopHandler` (codec negotiation, H.264/MJPEG streaming; default 120 FPS target, max clamp `MaxTargetFps = DesktopConfig.MaxTargetFps` (360, was a literal 120 — RemEx-z377); `_targetFps = 120`); takes `IInteractiveSessionGuard`; per-session `sessionGuardClientId`; streams `desktop_cursor_shape`/`desktop_cursor_state`; enforces `StreamSerial` on send; throttles keyframe reinits to 5s cooldown; static `_ffmpegAvailableCache` probed once at construction), `PingPongHandler` (keep-alive).
  - `Services/Security/` — `PairingService` (ECDH state machine), `PairedClientRegistry` (proof-of-possession reconnect auth), `PairingThrottle` (per-IP rate limiting), `CertificateService` (SPKI management), `TransportTrust` (network-path trust classifier: loopback / Tailscale / LAN — gates PIN auto-fetch).
  - `Services/Network/` — `PairedClientChannelAuthenticator` (8338 TCP channel auth gate via `PairedClientRegistry`).
  - Local IPC is **gone** (RemEx-aep): no `RemExLocalIPC` / `RemExHostControl` pipes, no `LocalIpcServerService`/`HostControlServer`/`AgentCoordinator`. The desktop UI (same process) resolves host services from DI via `remex.desktop`'s `EmbeddedHostServiceLocator`.
  - `Services/Session/` — `IInteractiveSessionGuard` / `WindowsInteractiveSessionGuard` (ref-counted keep-awake only via `SetThreadExecutionState`; no tscon/disconnect — we live inside the session; `[SupportedOSPlatform("windows")]`); `ISessionKeepUnlockedService` (writes/reads `ProgramData\RemEx\keep-session-unlocked.flag`; wired to in-app Settings toggle, Windows-only, off by default, with localized security warning; gates the keep-awake in `RemoteDesktopHandler`).
  - `Services/RemoteDesktop/` — `IH264Encoder` / `FFmpegH264Encoder` (FFmpeg subprocess, bounded channels, on-demand keyframe); `PrecisionPacer` (hybrid coarse-sleep + `Thread.SpinWait` pacer, absolute timeline, `IDisposable` — owns a high-resolution waitable timer — shared by video frame loop and cursor loop — see Patterns).
  - `Services/ScreenCapture/` — `DxgiDesktopCapture` (DXGI Desktop Duplication API, Windows 10/11, MPO/GPU-composited content; deferred init via `EnsureInitialized()` on first capture — no idle-slot hold at construction); `DuplicationReinitThrottle` (exponential-backoff gate for `DuplicateOutput` re-init; prevents driver-wedge on display power transitions); `WindowsScreenCaptureService` wraps captures as `ScreenCaptureResult { Pixels, IsLive }` — GDI path always `IsLive = true`; DXGI path sets `IsLive = false` on stale `_lastFrame` replays; implements `WarmUpCapture()` to prime DXGI via `_dxgi.TryRecover()` AND select the WGC monitor (so `GraphicsCaptureItem.Size` is populated before the first `GetScreenSize()` call); `GetScreenSize()` reports dimensions of whichever backend actually serves the active target (WGC → DXGI → GDI); `ActiveMonitorOrigin()` supplies the monitor's virtual-desktop (possibly negative) origin for absolute cursor mapping. Host logs `"RD bootstrap: streaming WxH @ (L,T) via {backend}"` at connect time. (RemEx-6my, RemEx-4k4) `IScreenCaptureService` (in `Remex.Core`) defines `ScreenCaptureResult`, the capture contract, and `WarmUpCapture()` (default no-op; call once per client connection before `GetScreenSize()`).
  - `Services/Command/WindowsSystemCommandService` (in `Remex.Core`) is registered directly as `ISystemCommandService` — lock/monitor-off/sign-out take effect in-session (no `SessionBridgingCommandService` / `WindowsActiveSession` bridge; both deleted with the Session-0 model). `AppLauncherService` launches via a normal ShellExecute on the user's desktop.
- `remex.android/` — the only client: Kotlin + Jetpack Compose + JNI → `libRemexCore.so`.
  - `data/NsdDiscoveryManager` — mDNS discovery (`_remex._tcp.`); resolves via concurrent `registerServiceInfoCallback` unconditionally (the pre-34 `resolveService()`/`resolveMutex` fallback was unreachable at minSdk 34 and was deleted — RemEx-jcl4p).
  - `security/PinnedHostStore` — Tink AES-256-GCM AEAD encrypted storage for paired host SPKI hashes and PAIR-1 reconnect secrets; two DataStores (`remex_pinned_hosts`, `remex_reconnect_secrets`); Android Keystore-backed keyset; corruption self-recovery.
  - `ui/screens/H264StreamDecoder` — `MediaCodec` H.264 decoder in **SYNCHRONOUS mode**: a single dedicated `H264DecodeLoop` thread polls `dequeueInputBuffer`/`dequeueOutputBuffer` (async `setCallback` is deliberately NOT used — on the deferred-configure path the Qualcomm c2 decoder reaches RUNNING but `onInputBufferAvailable` never fires, so the codec is never fed and the stream stays black). Renders to a **SurfaceView**; bounded backlog (`MAX_INPUT_BACKLOG = 6`, drop-oldest, then `onKeyframeNeeded`). Deferred `configure()` — waits for the first SPS+PPS-bearing IDR, then configures with explicit `csd-0` (SPS) / `csd-1` (PPS); the codec adopts the SPS-declared resolution so a wrong width/height hint can't matter. **Mid-stream SPS reconfigure (RemEx-aep):** when a later AU carries an SPS whose raw bytes differ from the configured `csd-0` (cheap `containsNalType` pre-check keeps P-frames on the fast path), it `stop()/configure()/start()`s for the new resolution — this is the fix for the scale-up black screen. `KEY_MAX_INPUT_SIZE` is sized for the full-screen (4K-bounded, 8 MiB-capped) max, not the initial hint, and `KEY_MAX_WIDTH`/`KEY_MAX_HEIGHT` are set for adaptive playback (explicit reconfigure is the fallback). MUST NOT set `KEY_COLOR_FORMAT` (Qualcomm `c2.qti.avc.decoder` rejects `COLOR_FormatSurface` → zero output), `KEY_LOW_LATENCY`, or `KEY_OPERATING_RATE` (shrink the DPB pool → stall). Pre-keyframe P-frames dropped; IDR units queued with `BUFFER_FLAG_KEY_FRAME`; `onInitFailure` on non-transient decoder error (owner reconnects). (RemEx-bqc / RemEx-kx4 / RemEx-x0b / RemEx-aep / #2b)
  - `ui/screens/RemoteDesktopViewModel` — stream config, display-target selection, cursor shape overlay, frame-arrival watchdog. `desktopMetaReady` signal gates the orientation-aware initial fit until the host's real stream metadata (dimensions, origin, backend) arrives; prevents the initial zoom computing against a placeholder resolution.
  - `ui/screens/RemoteDesktopScreen` — Jetpack Compose UI, gesture handling (tap/scroll/pinch), immersive full-screen.
  - `ui/screens/ConnectionViewModel` — NSD discovery lifecycle; `discoveryJob: Job?` ensures one in-flight discovery at a time.
- `remex.desktop/` — PC-side UI code only, compiled directly into `remex.agent` via a real `<ProjectReference>`; permanent, not being removed (see "Host = PC, Client = Android" above).

Protocols: WSS `/ws` (port 5005, telemetry/power/pairing/file transfer), WSS `/ws/desktop` (port 5005, H.264/MJPEG remote desktop), TCP+TLS 8338 (external script ingress — requires paired `clientId` via `PairedClientChannelAuthenticator`; `CommandRequest` JSON must include `ClientId` field). The former `RemExLocalIPC` / `RemExHostControl` named pipes are gone (single process; UI↔host is in-process DI). Messages use the `RemexMessage` JSON envelope with `protocolVersion: 2`. Pairing uses ECDH P-256 + 6-digit PIN, then SPKI certificate pinning. Wire message types include `MessageTypes.DesktopKeyframeRequest` (`"desktop_keyframe_request"`) for client-to-host on-demand IDR keyframe requests.

<!-- END AUTO-MANAGED -->



<!-- AUTO-MANAGED: git-insights -->
## Git Insights

- Open PRs against `main`. (There is deliberately no "current branch" recorded here — it goes stale within days and this file has already shipped a wrong one. Run `git branch --show-current`.)
- Hottest areas by recent history: `remex.agent` (PC side), `remex.android`, `remex.agent.native.linux`. `remex.desktop` sees little independent change but is not being removed — it's the permanent PC-side UI project.
- Gitignored (do not commit): AI tool dirs (`.gemini/`, `.superpowers/`, `.antigravitycli/`), `.claude/auto-memory/dirty-files*`, `.claude/settings.local.json`, `.beads/proxieddb/`, `.beads-credential-key`, `.dolt/`, `*.db`. Only `.beads/issues.jsonl` is tracked (passive Beads export).

<!-- END AUTO-MANAGED -->

<!-- AUTO-MANAGED: best-practices -->
## Best Practices

- Run `gitnexus_impact` before editing any symbol; warn on HIGH/CRITICAL risk; run `gitnexus_detect_changes()` before committing.
- Prefer `token-savior` / `gitnexus` / `context-mode` MCP tools over raw `grep`/`Read`/`Bash` for analysis (see decision matrix in `CLAUDE.md`).
- No placeholder/stub code; file a beads issue for out-of-scope work and implement in-scope correctly.
- Coordinate any change to pairing, certificate pinning, or the `RemexMessage` envelope across both Android and host — these are security-critical.

<!-- END AUTO-MANAGED -->

<!-- AUTO-MANAGED: release-gate -->
## RemEx 2.0 Release Gate — MET (historical)

2.0 shipped; the repo is now at 2.4.x. The full gate record (P0–P3 bead tables, design
decisions, definition of done) is archived at `docs/OLD DOCS/AGENTS-2.0-archive.md`.
Two decisions from it still bind:
- **Port 8338 stays `IPAddress.Any` (authenticated-remote).** Android connects from a separate device — loopback-binding breaks all remote access.
- **`remex.desktop` stays.** RemEx-d8s closed WITHOUT deleting it; it is a live `<ProjectReference>` of `remex.agent`.
<!-- END AUTO-MANAGED -->

<!-- MANUAL -->
## Custom Notes

Add project-specific notes here. This section is never auto-modified.

<!-- END MANUAL -->
