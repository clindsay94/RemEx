# RemEx — project instructions

This is the only instruction file for this repo. The global rules live in `~/.claude/CLAUDE.md`:
environment, token-savior / gitnexus / context-mode routing, delegation, and memory ownership. This
file covers RemEx only, and it wins where the two disagree. If an instruction here can't be
followed, fix it here. Don't quietly obey it or ignore it.

**One hand-written copy, no generated blocks.** `AGENTS.md` and the root `CLAUDE.md` were merged into
this file on 2026-09-30 (RemEx-wcvck). `InstructionFileTests` fails the build if either one comes
back, because that only happens when a tool regenerates them:
- `npx gitnexus analyze` **without `--skip-agents-md`** writes both files and adds a generated block
  that says to run `impact` before every edit. Always pass the flag.
- `bd setup claude` writes a beads block into a root `CLAUDE.md`. Don't run it. `bd setup claude
  --check` and `bd doctor` will report the Claude integration as missing. That's expected.

## Architecture: host = PC, client = Android. End of story.
- `remex.agent` is **the entire PC side**. It's one elevated, interactive-session Avalonia app with an
  in-process ASP.NET host (Minimal APIs, WebSocket, mDNS). An elevated Task Scheduler logon task
  starts it at sign-in. There's no Windows Service, no Session 0, no second process, and no named pipes.
- `remex.android` is the **only** network client (Kotlin + Compose + JNI → `libRemexCore.so`). The
  connection always runs Android → PC and is never loopback. Any loopback on the PC side is local UI
  plumbing, not a client.
- `remex.desktop/` holds PC-side UI code only (Views, ViewModels, Localization). It's compiled into
  `remex.agent` through a real `<ProjectReference>`, and the UI reaches host services in-process via DI
  (`EmbeddedHostServiceLocator`), not a socket.
- `remex.core/` holds shared models, messages, validation, guards, and serialization. It's also
  compiled NativeAOT as `libRemexCore.so` for Android, so it has to stay NativeAOT-safe.
- Protocols:
  - WSS `/ws` (telemetry, power, pairing, file transfer) and WSS `/ws/desktop` (H.264/MJPEG), both on port 5005.
  - TCP+TLS on 8338 for external scripts. It's gated by a paired `clientId` (`PairedClientChannelAuthenticator`)
    and stays bound to `IPAddress.Any`, because the phone is a separate device.
  - Messages use the `RemexMessage` JSON envelope with `protocolVersion: 2`.
  - Pairing is ECDH P-256 plus a 6-digit PIN, then SPKI pinning.

  Pairing, certificate pinning, and the envelope are security-critical: change both sides together.
- For anything finer-grained (which class does X, who calls Y), ask gitnexus or token-savior. A
  hand-written symbol map here goes stale: the last one listed a class that had been deleted months earlier.

## Hard rules: these mistakes keep repeating, so don't re-litigate them
1. **There is no headless host separate from the UI, and there never was.** Never design around a
   "PC-side client connecting to a PC-side host". Any old reference to a desktop client/host pair is
   stale, so fix it.
2. **`remex.desktop/` is permanent.** It's not dead code, it's not being phased out, and nobody should
   suggest deleting it. RemEx-d8s closed *without* deleting it. "Legacy" only means the folder kept its
   pre-rename name.
3. **Check an issue with `bd show <id>` before citing its status.** Docs lag the tracker.
4. **Never build a `git add` path, or any case-sensitive path comparison, from memory.** Copy the exact
   case from `git status` or `ls`. On Windows a case mismatch silently stages nothing, and that has
   stranded real fixes. The repo also has to build on case-sensitive Linux. In PowerShell, compare
   paths and namespaces with `-ceq`/`-cne`.
5. **Never undo a defect injection with `git checkout -- <file>`.** It throws away every uncommitted
   change in the file. Run `git diff -- <file> > inject.patch` before injecting, then
   `git apply -R inject.patch`. The test must go red with the defect in and green once it's reverted.
   If an injection run stays green, the test is blind.
6. **Specs, spikes, investigations, and measurements go in `docs/`** (next to the existing `SPIKE-*.md`
   and `MEASURE-*.md`). Never put them in `docs/superpowers/`: it's gitignored, so `git add` silently
   stages nothing (RemEx-0l9x). After writing any artefact, `git check-ignore -v <path>` must print nothing.
7. **Read `docs/REGRESSION-GUARDS.md` before touching** capture, the remote-desktop stream or its
   pacing, the Android H.264 decoder, SurfaceView zoom/pan, pairing and trust, or the session guard.
   Each guard there exists because breaking it once failed silently: a black screen, a dead stream, or
   a bricked pairing, with no log pointing back to the cause. The file is maintained by hand and
   anchored to `file:line`. Never regenerate it, and never copy its guards out.

## Workflow
- **Beads.** Every change gets a bead: `bd create`, then `bd update <id> --claim`, then `bd close <id>`
  before you report it done. The SessionStart hook (`.claude/scripts/beads-context.ps1`) injects the
  workflow plus the *titles* of every `bd remember` lesson. `bd memories <keyword>` prints one in full.
  It replaced `bd prime`, which dumped all ~90 lessons (~80 KB) on every start.
- **The verify gate.** `scripts/verify.ps1` is the only accepted proof that work is finished. It
  force-cleans, rebuilds, runs the suite, checks the edit guard and translations, and writes
  `.ralph/verify-receipt.json` with a SHA-256 fingerprint of every source file it verified.
  - Run `./scripts/verify.ps1` for .NET, or `-Scope all` to add the Android unit tests and the release lint gate.
  - `-Check` reports whether the last receipt still matches the files on disk. Work isn't done until
    it says VALID, and any later edit voids the receipt. That's by design.
  - Receipts are per-machine and gitignored, and they record the platform, so Windows refuses a
    receipt written under WSL/Linux.
- **Commits are standing-authorized** (Connor, 2026-09-02). When the gate passes, commit in the same
  turn with a conventional prefix and the bead ID. Pushing and `bd dolt push` still need an explicit
  ask. gitnexus `detect_changes` is advisory, not a second gate.
- **Docs travel with code.** Every change updates `docs/CHANGELOG.md` (Keep a Changelog format), plus
  any affected XML doc comments and `docs/` files.
- **Versions.** .NET's is in `Directory.Build.props` and Android's is in
  `remex.android/app/version.properties`. `build-remex.ps1` copies the Android version into the .NET one.
- **Headless loops.** `/ralph` (preferred, single-track) and `/drain` are installed globally, in
  `~/.claude/skills/{ralph,drain}` and `~/.claude/ralph/`. The repo only has to provide
  `scripts/verify.ps1` and the beads DB. `/drain` also needs a local `.ralph.psd1`, which hasn't been
  tracked since 2026-09-10. Without one it refuses to run. With no user around, record a HIGH or
  CRITICAL `impact` result in the bead and the journal instead of asking.

## Code intelligence in this repo
The global routing rules apply. These are the RemEx additions:
- `impact` is mandatory for anything named in `docs/REGRESSION-GUARDS.md`.
- Both servers, `gitnexus` and `token-savior`, are defined at user scope in `~/.claude.json`. There's
  deliberately no repo `.mcp.json`: it carried personal paths and was dropped on 2026-09-10. A
  SessionStart hook runs `scripts/check-mcp-health.ps1 -Hook -Quick`, and `-Full` compares mandated
  servers against callable ones and checks index freshness. It exists because both servers were
  missing from `~/.claude.json` for nine days in August while their hooks kept firing.
- `analyze` regenerates the skills in `.claude/skills/gitnexus/`, and their "run `npx gitnexus analyze`"
  lines leave out `--skip-agents-md`. Add the flag anyway.
- Context7 library IDs: .NET `/dotnet/docs`, Avalonia `/avaloniaui/avalonia-docs`, Kotlin
  `/jetbrains/kotlin-web-site`, Compose `/websites/developer_android_develop_ui_compose`.

## Build and run
`build-remex.ps1` is the canonical entry point on both Windows and Linux (under `pwsh`). Major
operations belong in it, not in sub-scripts.
```powershell
./build-remex.ps1 -c release -t all                      # everything
./build-remex.ps1 -t windows [-NoClean]                  # publish + Inno Setup installer (-NoClean = incremental)
./build-remex.ps1 -t android                             # APK + AAB via Gradle (alias: apk)
./build-remex.ps1 -t linux                               # tar.gz via installer/build-linux.sh (WSL on Windows)
./build-remex.ps1 -t installer                           # installer only (skips publish if artifacts/ exists)
./build-remex.ps1 -t windows-client                      # publish only, no installer
./scripts/update-local-install.ps1                       # publish + install Remex.Agent locally
pwsh ./scripts/android-fresh.ps1 -Configuration Release  # hardened fresh Android build, both OSes
dotnet run --project remex.agent [-- --doctor]           # run the PC side; --doctor checks Linux PipeWire/X11/VAAPI
dotnet test Remex.sln
```
- Build intermediates go to `artifacts/` (UseArtifactsOutput). The Windows publish lands in
  `artifacts/publish/remex.agent/{Config}_win-x64/`, and distributables in
  `build_output/{windows,android,linux}/`.
- Android needs SDK 37 and NDK `30.0.14904198` (the script installs them if missing), plus
  `ANDROID_HOME` or `remex.android/local.properties`.
  - `./gradlew remexFreshAssembleRelease` builds without bumping the version. `remexPublishRelease`
    bumps it, so use it for real releases only.
  - Signing reads the `remex.signing.*` keys from `local.properties` and falls back to debug signing.
  - `libRemexCore.so` is resolved from `artifacts/bin/remex.core/<config>_net10.0-android_android-arm64/native/`.

## Coding conventions
- **No `ConfigureAwait(false)` anywhere.** On the desktop side the captured context is load-bearing,
  because continuations assign bound properties. On the host side there's nothing to gain. CA2007 is
  suppressed on purpose, and `ConfigureAwaitBanTests` enforces the ban. The old justification,
  "Avalonia has no SynchronizationContext", is false (RemEx-rbfq). See `docs/ASYNC_GUIDELINES.md`.
- **Null safety.** Nullable reference types are on everywhere. Use `Guard.NotNull(arg)`
  (`remex.core/Guards/Guard.cs`) for required constructor dependencies, and `GetRequiredService<T>()`,
  never `GetService<T>()`. See `docs/NULL_SAFETY_GUIDELINES.md`.
- **Validation.** All network-facing input goes through `remex.core/Validation/`. See `docs/VALIDATION_GUIDELINES.md`.
- **NativeAOT in `Remex.Core`.** Breaking these rules breaks the Android link step, often far from the change:
  - No reflection: no `GetMethod`, no `Activator.CreateInstance`, no `JsonSerializer` without a
    source-generated context.
  - No dynamic code generation: no compiled Expressions, no `Emit`.
  - Keep code trimming-safe, with `[DynamicallyAccessedMembers]` and `[RequiresUnreferencedCode]` where needed.
  - Every serializable type gets `[JsonSerializable]` plus a `JsonSerializerContext`.

  When unsure, copy an existing Core pattern.
- **Elevation is load-bearing, so never weaken it.**
  - `cert.pfx` and `paired_clients.json` are machine-wide (HKLM/ProgramData). Their ACL grants only
    LocalSystem and Administrators, with inheritance off.
  - A medium-integrity start can't read `cert.pfx` and **bricks every SPKI-pinned pairing**.
    `CertificateService` has a brick canary that refuses to regenerate the cert. Never ship an
    auto-start that runs non-elevated.
  - Capture and `SendInput` work directly inside the session (HIGH→HIGH UIPI is allowed). There's no
    session bridging and no `CreateProcessAsUser`.
  - Autostart is `scripts/autostart-remex.ps1`: task `RemEx`, `RunLevel=Highest`, `InteractiveToken` (RemEx-aep).
- **Localization.** Every user-facing string in `remex.agent` goes through `Localization/`.
  - There are 9 languages (en, es, fr, hi, id, pl, pt-BR, tr, uk) with live switching, and 9 locale
    files on each platform.
  - Never put `string.Format` or string interpolation in UI-bound properties. Logs and exception
    messages can stay in English.
  - **Bulk-edit `.resx`/`.xml` files with a Python script and explicit UTF-8, never PowerShell string
    interpolation.** Interpolation has written NUL bytes into `Strings.tr.resx` more than once. The
    PostToolUse guard (`.claude/scripts/guard_edit.py`) catches NULs, duplicate keys, and malformed XML
    after the write, but it can't stop you causing them.
- **No lazy code.** No stubs, no `TODO:` bodies, no "good enough for now". Check `remex.core/Guards` and
  `remex.core/Validation` before writing a new utility.
- **Write for non-technical users.** Use plain English in the UI, tooltips, error messages, scripts, and
  installers. Scripts should print friendly status messages and always say what to do next.

## UI verification: the two platforms theme differently, so never mix their axes
- **PC** (`remex.desktop`, `remex.agent`):
  - The palette comes from one seed (`CustomizationSettings.AccentColor`) × `SchemeVariant` (9) ×
    `ThemeMode` (Light/Dark/System) × `ThemeContrast` (-1..1).
  - `DynamicColorGenerator` generates it on top of `Remex.Core.Theming.Mcu`, so the PC and the phone
    derive the same roles from a seed. `McuVectorTests` and `ThemeParityTest` pin both platforms.
  - The presets in `SeedPresetCatalog.All` are only starting seeds: BaseDarkGlass (default), CyberNOC,
    SolarFlare, Monolith, Daybreak, Voltage, Sorbet, and Dynamic. There's no finite list of themes to check.
  - To verify, run the 13-cell sweep defined in `scripts/ui-palette-sweep.ps1` (`-ListCells`, `-DryRun`):
    the default preset plus the Chalk, Ink, and Chroma seeds, each × light/dark × contrast 0/1.
  - Open a view with `--view <Name>` and capture it with the `ui-verify` skill (`scripts/ui-snapshot.ps1`).
    **Never** inject keystrokes, Tab, or focus changes from a script. `--view` and UIA `InvokePattern`
    on RemEx's own buttons are the only levers.
  - Don't let `TypographyVocabularyTests`' inline-font-size baseline drift upward.
- **The ~50 legacy brush keys** (`TextPrimaryBrush`, `AccentPrimaryBrush`, …) are the PC palette's role
  contract. `ThemeService.ApplyCustomization` writes every one of them and `ThemeKeyCoverageTests`
  enforces it. Never add a hex literal or a new key to a `Themes/*.axaml` preset. A new colour goes in
  `Themes/Shared/FallbackPalette.axaml` and in `ApplyCustomization`.
- **Android** (`remex.android`) uses M3 dynamic theming and has no named themes.
  - `RemExTheme` (`ui/theme/Theme.kt`) picks a custom seed (`colorSchemeFromSeed`), dynamic color, or
    the static fallback used when dynamic color is off. A change has to hold under all three.
  - Those are crossed with light/dark/system, `themeStyle` (9 values, `tonal_spot` default), and
    `themeContrast` (-1..1). Monochrome and contrast 1.0 are the harshest tests.

## Cross-platform parity (Windows ↔ CachyOS)
- Every PC-side change (agent, scripts, installers, build tooling) keeps parity. Each `.ps1` runs under
  `pwsh` on Linux or has a `.sh` twin, there are no hardcoded Windows paths, and new build steps go into
  `build-remex.ps1` for both platforms. Before closing, verify on both, or say which OS you tested and
  file a bead for the other.
- Running the Linux tests from Windows through WSL needs a self-contained build. WSL's .NET has no
  `Microsoft.AspNetCore.App`, so a bare `dotnet test` dies with "You must install or update .NET",
  which looks like a broken machine but isn't.
  `wsl -- bash -lc "cd /mnt/z/RemEx && dotnet test remex.agent.tests/remex.agent.tests.csproj -c Release -p:RuntimeIdentifier=linux-x64 -p:SelfContained=true"`.
  The same flags work for `remex.core.tests` and `remex.desktop.tests`, and `verify.ps1` adds them
  itself. This shares `artifacts/` with the Windows build, so rebuild on Windows afterwards.
- On Linux, expect more skips and zero failures. Windows-only tests are marked
  `[WindowsOnlyFact("reason")]` (there are copies in `remex.agent.tests` and `remex.desktop.tests`).
  Mark them rather than deleting them, never weaken a test so it passes on both, and never hardcode
  expected counts.

## Splitting beads, unwired logic, and inert guards
- **Put the join in the first half.** When a bead is split into "logic" and "surface", the pure half
  lands with good tests and no caller, and the surface half sits on the board. That has happened five
  times (RemEx-hev1g). `TutorialNavigator` was then re-implemented in `ShellViewModel` without tests
  and with a bug (RemEx-5m3i, 9iz00.1). Land the logic with at least one real binding. If a split
  really must strand it, say so on both beads and make the surface bead a **blocker**, not a sibling.
- **Unwired logic is a defect only when a live path already does the same job**, worse or not at all.
  It's deliberate when the feature that will use it hasn't landed yet. Of eight uncalled helpers in
  RemEx-thwlr, two were defects (duplicated rules: RemEx-ph4nw, RemEx-7gk69) and six were fine.
- **Sweeps produce candidates, not findings.** Don't automate a "no production caller" check with a
  reference count or an allowlist. Extension methods (`FireAndForgetExtensions`) and same-file callers
  (`ScreenshotEncoder`) are false positives, and an allowlist that absorbs a false positive will
  absorb a real one (RemEx-dnn2q).
- **Guards fail on what they assert, not where they look.** "Can this assertion fail?" gets answered
  one guard at a time: mutate what it guards and watch it go red. The cheap structural defence is an
  anti-vacuity check, which asserts that the scan's own output is `NotEmpty` before comparing it.

## Where knowledge lives
| Store | Owns |
|---|---|
| `bd` issues + `bd remember` | **Authoritative** for work, decisions, and technical lessons |
| This file + `docs/REGRESSION-GUARDS.md` | **Authoritative** for rules and invariants (guards are edited by hand only) |
| `~/.claude/projects/Z--RemEx/memory/` | Connor's preferences and facts about his machine. Never project rules |
| `.remember/` | Session-continuity narrative written by the remember plugin. It's automatic, so don't hand-curate it |
| token-savior, context-mode, and gitnexus indexes | Derived caches. They get rebuilt; never edit them |

`docs/old-docs/`, `docs/plans/`, and `docs/superpowers/` are gitignored local archives that exist only
on Connor's machine. `memory-store` was retired on 2026-08-09, so don't reinstate it.
