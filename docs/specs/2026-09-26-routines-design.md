# Routines: design spec (RemEx 3.0)

> Bead RemEx-pp0rt.1 (epic RemEx-pp0rt). Merged from the UX and systems drafts on 2026-09-26.
> Scope: Routines on Android (editor, phone runner) and on the PC (Windows and Linux runner and the Routines page).
> Out of scope: the splash screen, schedule triggers (3.x), smart-home integration (feasibility and roadmap only, §15).

## 0. Summary, decisions and glossary

### 0.1 Summary

A routine is **one trigger followed by ordered steps**, built on the phone from one of 18 templates or from blank. Triggers that happen on the phone (arriving home, leaving home, tapping an NFC tag, tapping Run) run on the phone. Triggers that happen on the PC (a sensor passing a limit, the PC going idle, the session locking or unlocking) run inside the PC's own RemEx process, so they keep working with the phone away. Steps reuse what RemEx already does: Wake-on-LAN from the phone, waiting for the PC, delays, the power verbs, allowlisted app launches, media keys and messages.

The phone is the only editor. It syncs each PC's own PC-run routines over the existing paired channel; the PC revalidates them, runs them, and shows them on a Routines page where the person at the PC can switch one off, run it now, pause everything and read the history. Before any routine shuts down, restarts, signs out, sleeps or hibernates the PC, the PC counts down 15 seconds with a Cancel button, mirrored on the phone; only a Run now confirmed at the PC itself skips it. There is no new network ingress, no location permission and no cloud.

### 0.2 Decisions

Locked before drafting:

| # | Decision |
|---|---|
| L1 | A routine is one trigger followed by ordered steps, with starter templates. No conditions in v1 |
| L2 | Triggers: `home.arrive`, `home.leave`, `nfc.tap`, `manual`, `pc.sensor`, `pc.idle`, `pc.session`. Schedule is deferred to 3.x |
| L3 | Steps: `wake`, `waitOnline`, `delay`, `power`, `launchApp`, `media`, `notify`. No user scripts |
| L4 | Runner placement follows the trigger. The phone runner uses WorkManager and the tiles' one-shot connect; the PC runner is a hosted service in `remex.agent` executing steps in-process. TCP 8338 stays power-verbs-only |
| L5 | Shared schema and validation in `remex.core` with a Kotlin mirror guarded by round-trip tests; versioning after `CustomizationMigration`; atomic saves |
| L6 | The phone is the only editor. `routines_sync` carries only that PC's PC-run routines and a monotonic revision; the PC revalidates, stores atomically and answers per routine |
| L7 | `routine_notify` reaches the phone live, or is queued on the PC for 1 h; it is always also shown on the PC. New host-to-phone types are routed per `REGRESSION-GUARDS.md:373` |
| L8 | Additive protocol only, advertised through capabilities; no `protocolVersion` bump |
| L9 | 15 s cancellable countdown for destructive steps, single-flight per routine, Pause all on both apps |
| L10 | Smart home: feasibility chapter and roadmap only; no cloud trust path to the PC |

Decided while merging the two halves:

| # | Decision | Where |
|---|---|---|
| D1 | Every routine-issued destructive verb counts down 15 s on the PC, attended or not. Destructive = SHUTDOWN, FORCESHUTDOWN, RESTART, FORCERESTART, RESTARTTOUEFI, SIGNOUT, SLEEP, HIBERNATE. LOCK and MONITOROFF never count down. The countdown is mirrored on the phone when connected and can be cancelled from there with `routine_cancel` | §8.6 |
| D2 | Messages: `routines_sync`, `routine_sync_result`, `routine_step_request`, `routine_step_result`, `routine_notify`, `routine_notify_ack`, `routine_run_report`, `routine_cancel`, and `routine_run_request` (added in the merge so the phone can run a PC routine now, UX need 8). Every host-to-phone type starts with `routine_` and has a routing entry | §7.1, §7.7 |
| D3 | PC "Run now" exists. A destructive routine asks for confirmation on the PC, and that confirmation is presence, so it skips the countdown. Every other initiator counts down | §8.6, §9 T21 |
| D4 | Pause all on the phone propagates to every paired PC through `routines_sync.paused`; the PC shows "Paused from <phone>" | §8.7 |
| D5 | WAKEONLAN is hidden from the power picker and reserved in the v1 schema; the `wake` step covers waking the PC | §6.4 |
| D6 | `home.leave` stays. The editor warns when a leave routine has PC-targeted steps; the failure uses `pc_unreachable_away`; leave-home templates use PC-side triggers instead; a PC-side "phone away" trigger is a 3.x candidate | §1.8, §4.1, §4.3, §8.3.1 |
| D7 | Editor Test runs non-destructive steps for real and simulates destructive ones (countdown shown, verb not executed, run marked `simulated`) through a host-validated `testRun` flag. The `--routines-dry-run` switch stays, command line only | §8.9 |
| D8 | A phone-run routine for a PC other than the selected one fails with a "Switch and run" action; no automatic switching. The PC can switch off, pause or block a phone's routines but cannot delete them | §7.4.4, §7.4.5 |
| D9 | One home per phone in v1, stored as displayable facts inside the encrypted store | §6.6 |
| D10 | Limits: 12 steps, 32 routines per phone (16 PC-run per PC), name 40 characters, `notify` body 120, `delay` 1–600 s, `waitOnline` 30–300 s (default 300), `pc.idle` 1–240 min, `pc.sensor` sustain 5–600 s | §6.3–§6.5 |
| D11 | The PC nav direction fix (shared-axis direction from visual order, which also fixes About and Settings sliding the wrong way) ships in the PC-page slice | §14 S4, §17 Q1 |

### 0.3 Glossary

| Term | Meaning |
|---|---|
| Routine | One trigger plus 1–12 ordered steps |
| Phone-run, PC-run | Where the runner executes, decided by the trigger: phone triggers run on the phone, `pc.*` triggers on the PC |
| PC, host | The PC side: the single elevated in-session `remex.agent` process on Windows, the user-session process on Linux |
| Owner | The paired phone (`clientId`) whose routines a PC stores and runs |
| Automatic source | `home.arrive`, `home.leave`, `pc.sensor`, `pc.idle`, `pc.session`: nobody pressed anything |
| Person-initiated | In-app Run and Test, shortcut, widget, NFC tap, a phone run request, PC Run now |
| Destructive step | A `power` step whose verb is in the D1 set |
| Countdown | The 15 s cancellable window before a destructive step runs (§8.6) |
| Presence | A confirmation by the person at the PC. Only PC Run now provides it, and it is the only thing that skips the countdown |
| Test run | A D7 run: destructive steps are simulated |
| Dry run | PC command-line mode that logs destructive verbs instead of issuing them |
| Home | The phone's one captured home network: router, address range, DNS and domain facts; no location |
| `reachableAway` | The phone has reached this PC while not on its home network, for example over Tailscale |
| HostIdentity | 16-hex-character PC identity derived from its pinned SPKI (`security/HostIdentity.kt`) |
| Sync revision | Per-PC monotonic counter of the phone's routine set; distinct from `Routine.revision`, which counts saves of one routine |
| Reason code | Snake-case identifier for a failure, skip or state, with a message on each platform (§10.1) |
| Pause all | Stops automatic sources; person-initiated runs still work (§8.7) |

### 0.4 Principles

1. **One sentence shape.** Every routine reads as WHEN (one trigger) then THEN (numbered steps). The labels "When" and "Then" are section headers, never glued into a sentence, so nine locales never have to agree on grammar.
2. **Templates first, blank second.** A new user should never face an empty editor.
3. **Show where it runs.** Every routine carries a "Runs on" chip derived from its trigger. Users should never wonder why a PC routine worked while the phone was off.
4. **Safety is visible, not modal.** Destructive steps are tinted with the error role in the editor, the PC counts down before any routine powers it off (only a confirmed Run now at the PC skips it), Pause all is one tap on both apps.
5. **Nothing new to learn visually.** Every surface reuses an existing RemEx component, class or motion token (section 2.5).

### 0.5 Grounding: UX surfaces

Every "mirror this" claim below points at one of these.

| What | Where |
|---|---|
| Android nav: typed routes, `navItems` (4 primaries, order load-bearing), `moreItems` | `remex.android/.../ui/navigation/NavRoutes.kt:173-195`; arg-carrying route precedent `PairingRoute` `:217` |
| Android shell: NavigationBar/Rail/Drawer by window class, More sheet, animated badge | `ui/navigation/AppNavigation.kt:226-238`, `:442-540` (badge `:461-481`) |
| Android NavHost transitions from `MaterialTheme.motionScheme` | `AppNavigation.kt:833-873` |
| Android reduced motion: `LocalReducedMotion`, Expressive to Standard scheme swap at animator scale 0 | `ui/theme/Theme.kt:472-486`, `:493-513`, `:658-668` |
| Android list-detail precedent | `ui/screens/SettingsScreen.kt:108-124` (`NavigableListDetailPaneScaffold`) |
| Android expressive progress/loading components | `ui/screens/CommonComponents.kt:345-405` (`RemexLoadingIndicator`, `RemexLinearWavyProgress`) |
| Android top bar + tooltip | `ui/components/RemexFlexibleTopBar.kt:48`, `:91` |
| Android destructive classifier ("discards work") and confirm face | `ui/screens/RemoteControlScreen.kt:59-80`, `:722-760` |
| Android translucent confirm for tiles | `tile/TileConfirmActivity.kt:17-36` |
| Android ButtonGroup crash note (use ToggleButtons) | `ui/screens/PersonalizationScreen.kt:438`, `:600` |
| Android coach marks + seen key + export whitelist | `ui/screens/DashboardCoachOverlay.kt:51-72`, `:596-598`; `data/SettingsManager.kt:140-142`, `:498-503`; `data/SettingsExport.kt:75` |
| Android tutorial model | `ui/screens/TutorialScreen.kt:80-161` (`bodyArgRes` precedent `:84-89`) |
| Android FAQ list | `ui/screens/FaqScreen.kt:46-80` |
| Android Glance widgets (SizeMode.Exact) and the R8 lesson | `widget/RemoteControlWidget.kt:64-68`, `widget/AppLauncherWidget.kt:101-105`; bd memory `glance-actioncallback-r8-init` |
| PC nav ListBoxItems (Tags 0-9, 5 = Remote Desktop), entrance stagger per nth-child, 300 ms ceiling | `remex.desktop/Views/ShellView.axaml:303-440`, `:787-904`; `ViewModels/ShellViewModel.cs:1483-1658` |
| PC page host DataTemplates + transition direction | `Views/ShellView.axaml:1052-1088`; `ViewModels/ShellViewModel.cs:1436-1460` |
| PC reduced motion (user setting, Personalize > Layout > Behaviour) | `ViewModels/ShellViewModel.cs:617-622`, `remex.core/Models/DashboardProfile.cs:122`, `docs/MEASURE-personalize-tabs.md:15` |
| PC motion vocabulary | `Views/SharedAxisPageTransition.cs:55-80` (KeySpline 0.2,0,0,1; 30 dip), `App.axaml:95-113` (CubicEaseOut 150-400 ms, SpringEasing 1/120/12), `Controls/StaggeredEntrance.cs:16-46` |
| PC button vocabulary | `docs/BUTTON-VOCABULARY.md:28-84` |
| PC plate/surface rule (no `GlassBaseDarkBrush` plates in cards) | `docs/AUDIT-cohesiveness-2026-09-15.md:16` |
| PC coach mark + seen list | `Views/CanvasView.axaml:451-471`; `remex.core/Models/DashboardProfile.cs:107` |
| PC tutorial model, navigator, carousel | `Models/TutorialPage.cs`, `Services/TutorialNavigator.cs:29-101`, `ViewModels/ShellViewModel.cs:43-60`, `Views/ShellView.axaml:1587-1792` |
| PC FAQ | `ViewModels/AboutViewModel.cs:309-317` (loop to 16) |
| PC notification routing | `Services/NotificationRouter.cs:44-89` |
| PC topmost non-activating toast window | `Views/TrayBalloonWindow.axaml:7-8`, `.axaml.cs:45-53` (`PressSettleDuration`) |
| PC window may never be constructed (minimized logon start) | `docs/REGRESSION-GUARDS.md:1040-1056` |
| PC confirm host fails closed | `docs/CHANGELOG.md:4095` (`ConfirmationDialogHost`) |
| PC responsive columns (900 breakpoint) | `Converters/ResponsiveColumnWidthConverter.cs` |
| Power verbs (11) | `remex.core/Services/Command/CommandVerbs.cs:39-52`; WAKEONLAN needs a MAC `SharedCommandVerbs.cs:108-116` |
| Palette export precedent (`.remexpalette`, camelCase, `FormatVersion`, TryParse) | `Services/PaletteExchange.cs:29-75`; `docs/CHANGELOG.md:326-330` |
| FAQ parity rules | `docs/FAQ-PARITY.md` |
| Wake-on-LAN stops at sign-in (copy the wording) | Android `faq_a8`, PC `Faq_Q8_Answer` |

### 0.6 Grounding: systems

| Fact | Evidence |
|---|---|
| The PC side is ONE elevated in-session process; `remex.desktop` is UI compiled into `remex.agent`. There is no service and no pre-login plane. | `docs/ARCHITECTURE-HOST.md` (status block); `AGENTS.md` Hard Rules 1–2 |
| On Linux the agent runs as the signed-in user in the desktop session, autostarted at sign-in, nothing as root. | `docs/LINUX_INSTALL.md` "How RemEx works on Linux"; `installer/linux/remex-agent.desktop` |
| Consequence: host-run routines only exist while a user is signed in and the agent is running. Sign-out ends them. | Same |
| The phone's control connection is a single native client pointed at the **selected** PC (`settings.hostFlow`). | `RemexClientManager.kt:862-874` (`connect`), `:750-760` (`startOneShotConnect`) |
| Headless one-shot connect already exists for widgets. | `RemexClientManager.kt:737-760`, used by `widget/WidgetConnect.kt:51` |
| Tiles send commands on a process-lifetime scope and log failures. | `tile/TileCommand.kt:36`, `:51-78` (`sendTileCommand`), `:124-142` (`sendTileWake`) |
| `SendCommandAsync` is bounded (10 s send+reply) and returns immediately when disconnected. | `remex.core/Native/AndroidNativeExports.cs:~1811` comment; `TileCommand.kt:30-34` |
| 11 shared power verbs: `SHUTDOWN FORCESHUTDOWN RESTART FORCERESTART RESTARTTOUEFI SLEEP HIBERNATE SIGNOUT LOCK MONITOROFF WAKEONLAN`. Delay parameter applies to the first five. | `remex.core/Services/Command/SharedCommandVerbs.cs:62-127`; `CommandVerbs.cs:39-51` |
| 8338 dispatches only those 11 verbs, deliberately. | `remex.core/Services/Network/RemexNetworkListener.cs:42-51`; `SharedCommandVerbs.cs:25-29` |
| `LAUNCHAPP` is allowlist-gated by exact full-path match and rejects UNC/mapped drives. | `remex.agent/Services/AppLauncherService.cs:20`, `:92-122`, `:131-146`; `PingPongHandler.cs:1229-1236` |
| Launcher allowlist entries have a stable `Guid Id`. Stored in `launchers.json`. | `remex.core/Models/AppEntry.cs:6-19`; `remex.core/Services/LauncherStorageService.cs:44` |
| Sensor alerts: `SensorAlert{SensorName, Threshold, Direction, Severity}`; hysteresis in `SensorViewModel.IsAlertLive` (2% deadband); **no sustain duration** exists today; the tracker only rate-limits notifications (60 s). | `remex.core/Models/SensorAlert.cs:4-34`; `remex.desktop/ViewModels/SensorViewModel.cs:473-488`; `remex.desktop/Services/SensorAlertTracker.cs:19`, `:55` |
| Telemetry samples at 1 Hz only while something holds demand; an armed alert is expected to hold it. | `remex.agent/Services/Telemetry/TelemetryBackgroundService.cs:122-132`, `:256-276` |
| No idle or session-lock detection exists anywhere in the repo. `WindowsInteractiveSessionGuard` is keep-awake only. | `git grep` for `GetLastInputInfo`, `WTSRegisterSessionNotification`, `IdleHint`, `LockedHint` = no hits; `REGRESSION-GUARDS.md:1110-1136` |
| Linux D-Bus client already shipped: `Tmds.DBus.Protocol`. | `remex.agent/remex.agent.csproj:110`; `LinuxMediaSessionReader.cs:44`, `:678-693` |
| Host can push to a specific authenticated phone. | `remex.agent/Services/ClientSessionRegistry.cs:34`, `:219` (`TrySendAsync`) |
| PC-side revoke path tears down every per-device store in one place. | `remex.agent/Services/Security/PairedDeviceRevoker.cs:49-95` |
| Host atomic write helpers exist. | `remex.core/Services/RemexDataPaths.cs:265` (`WriteAllTextAtomic`), `:292` (async), `:347` (`SweepStagingOrphans`) |
| Client-bound types reach Kotlin only through `OnNativeMessageReceived`; families are forwarded by prefix. | `AndroidNativeExports.cs:2025-2098` (`file_` at `:2045`, `clipboard_` at `:2061`); `REGRESSION-GUARDS.md:373-384` |
| Wire payloads must have no `required` members, or a malformed message nulls the envelope and drops the whole session. | `remex.core/Models/PhoneThemeSnapshot.cs:27-36`; `PingPongHandler.cs:1508-1512` (`IsValidThemeSync`) |
| `ClientCapabilities` is the additive phone→host flag record; absence must mean the old behaviour. | `remex.core/Models/ClientCapabilities.cs:16-33`; `RemexMessage.cs:128` |
| Phone minSdk 34, targetSdk 37. | `remex.android/app/build.gradle.kts:140-141` |
| Phone FGS type `connectedDevice` only; no location permission; WorkManager is **not** a dependency today (only a JobScheduler user-initiated job). | `AndroidManifest.xml:9-29`, `:244-255`; `service/FileTransferJobService.kt` |
| Backup/device-transfer exclusions are explicit lists that must grow with new sensitive stores. | `AndroidManifest.xml:42-51`; `res/xml/backup_rules.xml:2-8`; `res/xml/data_extraction_rules.xml:3-16` |

### 0.7 UX needs and where they are answered

| # | UX need | Answer |
|---|---|---|
| 1 | Run record fields and per-step status | §8.8 |
| 2 | Final reason codes with user messages | §10.1: code, meaning, Android message, PC message, history text |
| 3 | Countdown semantics: verb set, who counts down, locked PC, screen placement, phone cancel, record after cancel | §8.6, D1, D3 |
| 4 | Capability flags the UI gates on | §7.5, UX gating map |
| 5 | Sync state per PC-run routine and the enable-toggle conflict rule | §7.4.1 items 5 and 6 |
| 6 | Pause-all propagation in both directions; manual runs while paused | §8.7, D4 |
| 7 | Single-flight | §8.1 |
| 8 | Test-run flag; running a PC routine from the phone | §8.9, §7.3.3, §7.3.8 |
| 9 | Limits | D10, §6.3–§6.5 |
| 10 | Home capture API and arrive/leave timing | §8.3.1 |
| 11 | NFC payload, existing-tag detection, size checks, other phones, unlocked phone | §8.3.2 |
| 12 | Queued notify count, original times, expiry | §7.3.5, §8.8 |
| 13 | Live progress of PC-run routines | §7.3.6 (`live` reports) |
| 14 | History retention and paging | §8.8 |
| 15 | Export schema | §6.10 |
| 16 | Routine revision and source device name | §6.2 (`revision`), §6.9 (owner name from `PairedClientNameStore`) |

### 0.8 Research note

`gitnexus` was unavailable while the systems half was written (LadybugDB read-only error), so the blast-radius notes come from `token-savior` symbol lookups and targeted `git grep`. The implementer must run `gitnexus impact` on every symbol listed under "Touches guarded code" in §14 before editing it.


---

## 1. UX flows

### 1.1 User-facing vocabulary

Labels are the copy both apps show. "PC" in copy is replaced by the target PC's display name where one exists.

| Id | Label (list chip / editor title) | Supporting text (picker) | Icon (Android `Icons.Default.*` / PC `MaterialIconKind`) |
|---|---|---|---|
| `home.arrive` | Get home / I get home | When your phone joins your home Wi-Fi | `Home` / `Home` |
| `home.leave` | Leave home / I leave home | When your phone leaves your home Wi-Fi | `DirectionsWalk` (auto-mirrored) / `Walk` |
| `nfc.tap` | Tap a tag / I tap an NFC tag | Hold your phone to a tag you set up | `Nfc` / `Nfc` |
| `manual` | Tap Run / I tap Run | In the app, a home screen shortcut or a widget | `PlayCircle` / `PlayCircleOutline` |
| `pc.sensor` | Sensor limit / A PC sensor passes a limit | For example, GPU above 85 °C for 30 seconds | `Thermostat` / `Thermometer` |
| `pc.idle` | PC idle / The PC is idle | No mouse or keyboard use for a while | `Bedtime` / `Sleep` |
| `pc.session` | PC locked or PC unlocked / The PC is locked or unlocked | When someone locks or signs in to the PC | `Lock` / `Lock` |
| `wake` | Wake / Wake the PC | Sends a Wake-on-LAN signal | `PowerSettingsNew` / `Power` |
| `waitOnline` | Wait for PC / Wait until the PC is online | Pauses until RemEx can reach the PC | `HourglassTop` / `TimerSand` |
| `delay` | Wait / Wait | Pauses for a set time | `Timer` / `TimerOutline` |
| `power` | (the verb label) / Power action | Shut down, restart, sleep, lock and more | per verb, reusing Remote Control's icons |
| `launchApp` | Open (app name) / Open an app | An app from the PC's launcher list | `Launch` (auto-mirrored) / `OpenInApp` |
| `media` | Play or pause, Next track, Previous track / Media control | Like pressing the media keys | `PlayArrow` / `PlayPause` |
| `notify` | Message / Show a message | On your phone or on the PC | `Chat` (auto-mirrored) / `MessageTextOutline` |

Power verb labels reuse existing strings (Android `rc_*`, PC `Remote_*`); Android has no Sign Out label today, so one is added. The routine icon itself is `Icons.Default.Route` / `MaterialIconKind.Routes`: a path with stops, which is what a routine is.

**Destructive classification (UX view).** The editor tints a `power` step with the error role when its verb is in the "discards work" set already used by Remote Control (`RemoteControlScreen.kt:74-80`): SHUTDOWN, FORCESHUTDOWN, RESTART, FORCERESTART, RESTARTTOUEFI, SIGNOUT. The PC countdown (1.7, §8.6) applies to that set plus SLEEP and HIBERNATE, for every initiator except a confirmed Run now on the PC (D1, D3). LOCK and MONITOROFF are never destructive and never count down. WAKEONLAN is not offered in the power picker; the `wake` step covers it (D5).

### 1.2 Where Routines lives

**Android.** `Screen.Routines` becomes the **first** entry of `moreItems` (`NavRoutes.kt:185`). It is not a fifth primary: the bar already holds four primaries plus More, the M3 maximum, and `navItems` order drives pager indices (`NavRoutes.kt:167-172`). Routines are set up once and then run by themselves; the places a user needs them day to day are the run surfaces below, not a tab. On NavigationRail/Drawer layouts `moreItems` render below the divider, so Routines is one tap away there.

**PC.** A new drawer item "Routines", `Tag="10"`, placed directly after Commands. Commands and Routines are the two "make the PC do something" pages; putting them together is the grouping a user will look for. Because Tag order no longer equals visual order, shared-axis direction must come from visual position (R-UX-03).

**Entry points.**

| Surface | Platform | What it does |
|---|---|---|
| More sheet / rail item "Routines" with a "New" badge until first opened | Android | Manage routines |
| Remote Control "Your routines" section (above POWER) | Android | Run `manual` routines targeting the connected PC as command cards |
| Home screen pinned shortcut | Android | Runs one `manual` routine without opening the app |
| Launcher long-press app shortcuts (dynamic, up to 4 most recently run `manual` routines) | Android | Same as pinned shortcut |
| Glance widget "Routine" (1x1 and 2x1, resizable) | Android | Run button + last result |
| NFC tag | Android | Runs the linked routine |
| Drawer item "Routines" | PC | View, switch off, Run now, history |
| Command palette: "Go to Routines", "Pause all routines" / "Resume routines" | PC | Keyboard route to both |
| Tray balloon for PC `notify` steps and routine problems | PC | Via `NotificationRouter` |
| PC countdown window | PC | Cancel a routine's shutdown, restart, sign-out, sleep or hibernate |

### 1.3 New-user path

1. **Discover.** The More sheet shows Routines first with a badge dot (reuses the Dashboard badge animation, `AppNavigation.kt:461-481`, TalkBack state "New"). Users who update from 2.x also get the What's New entry in About.
2. **Empty state teaches** (wireframe A2). One line of what a routine is, three featured templates chosen to cover a phone trigger, a PC trigger and a tap: "Wake my PC when I get home", "Sleep my PC when it's idle", "Tap a tag to lock my PC" (swapped for "Game night" on phones without NFC). Then "Browse all templates" (tonal) and "Start from blank" (text).
3. **Template preview.** Tapping a template card container-transforms into the editor, pre-filled. The top shows the template's one-line "why". Anything the user must supply is flagged with an error-outline "Choose an app", "Set up home", "Pick a PC". Defaults are filled for everything else (timeouts, idle minutes, sensor limit) so the user can save after supplying only what RemEx cannot guess.
4. **Fill the blanks in place.** Tapping a flagged item opens its sheet: home capture (1.4), app picker (launcher entries for the target PC), PC picker (only if more than one paired PC), sensor picker.
5. **Progressive disclosure.** Each parameter sheet shows the one or two fields that matter; the rest sit under "More options" with defaults (for example `waitOnline` timeout, `pc.sensor` sustain time).
6. **Save.** Snackbar in plain words about what happens next: "Saved. It runs the next time you get home." with a "Test now" action. For PC-run routines: "Saved and sent to Gaming PC." or, when disconnected, "Saved. It goes to Gaming PC the next time you connect."
7. **First-visit walkthrough** once the list has at least one routine (5.3).
8. **Needs attention.** If Android restricts RemEx in the background and the routine uses `home.*`, a card at the top of the editor and on the list card says "Android may delay this routine while RemEx is asleep" with the existing battery exemption action (`tutorial_battery_action`).

### 1.4 First-run moment: setting home

Opened automatically the first time a user picks `home.arrive` or `home.leave` with no home set, or from the list overflow "Home network". Wireframe A10.

| State | Shown | Primary action |
|---|---|---|
| Ready: phone on Wi-Fi and the target PC reachable on it | Short explanation ("RemEx recognises home by your Wi-Fi network, not your location. It never asks for location permission."), then the captured facts in plain rows: Router, Address range, DNS, Domain (only rows that exist), and "Gaming PC is reachable here" with a check | "Use this network as home" |
| Not on Wi-Fi | "Connect to your home Wi-Fi first." | "Try again" |
| On Wi-Fi, PC not reachable | "RemEx can only learn home while it can reach your PC. Connect to Gaming PC first." | "Connect" (goes to Connection), "Try again" |
| Home already set | Current facts, "Captured 12 Sep", and "Use this network instead" when on a different network | "Forget home" (secondary danger, confirms, lists routines that use it) |
| Saved | Sheet closes, trigger card shows "Home: router 192.168.1.1" | none |

Rules: one home per phone in v1. Capture never runs silently. Forgetting home turns every `home.*` routine to "needs attention" (not deleted). Captured facts are shown to the user because trust is the feature: it is the only way a user can tell a mis-capture (for example on a hotspot) from a real one.

### 1.5 NFC tag write flow

Opened from a `nfc.tap` trigger card ("Write tag") or the routine overflow "Write NFC tag". Wireframe A11. Uses the ModalBottomSheet; tag reading is foreground dispatch while the sheet is open.

| State | Copy | Actions | Haptic |
|---|---|---|---|
| No NFC hardware | The trigger option is disabled in the picker: "This phone has no NFC." | none | none |
| NFC off | "NFC is off." | "Turn on NFC" (system NFC settings), "Cancel" | none |
| Waiting | "Hold the back of your phone against the tag. Keep it there until it buzzes." | "Cancel" | none |
| Existing RemEx tag for another routine | "This tag runs Game night. Replace it with Movie night?" | "Replace" (primary), "Cancel" | none |
| Writing | Loading indicator only, same copy | none | none |
| Success | "Tag ready. Tapping it runs Movie night." | "Done", "Test it" | `CONFIRM` |
| Tag read-only | "This tag is locked and can't be written. Try another tag." | "Try again" | `REJECT` |
| Tag too small | "This tag is too small. Try another tag." | "Try again" | `REJECT` |
| Lost contact | "The tag moved away before writing finished. Hold still and try again." | "Try again" | `REJECT` |
| Test it | "Tap the tag now." then "It works. Tapping this tag runs Movie night." Nothing runs during a test tap | "Done" | `CONFIRM` |

After a successful write the trigger card shows "Tag written 12 Sep" and "Write another tag" (one routine may have several tags; a tag maps to one routine). Deleting a routine says in its confirm dialog that its tags stop working.

### 1.6 Power-user path

| Capability | Android | PC |
|---|---|---|
| Build from blank | FAB menu "Start from blank": editor with an empty WHEN card ("Choose a trigger") and "Add step" | n/a |
| Duplicate | Routine overflow "Duplicate": "Name (copy)", disabled until the user turns it on | n/a |
| Test and Run now | Editor toolbar "Test" runs every non-destructive step for real and **simulates** destructive ones: the countdown shows on the PC and the phone, the verb is not carried out, and history marks the run `simulated` (D7, §8.9). List overflow "Run now" is a real run. Unsaved edits: "Save and test". For a routine that runs on the PC, both go through `routine_run_request` (§7.3.8) | "Run now" on each card; confirm dialog when destructive, after which the steps run without the countdown (1.7) |
| Step-level history | Editor toolbar "History"; list "Recent runs"; run detail timeline (A8) | Detail pane history with inline step expansion (P1) |
| Export / import | List overflow "Export", "Import"; selection mode for "Export selected"; routine overflow "Share" exports one routine through the share sheet (1.9) | n/a (the phone is the only editor) |
| Pin | Routine overflow "Add to home screen" (pinned shortcut) and "Add widget" (for `manual` routines only; other triggers explain "Only routines that start with Tap Run can be pinned") | n/a |
| Reorder steps | Drag handle, or TalkBack actions Move up / Move down / Move to top / Move to bottom | n/a |
| Reorder routines | Long-press a card and drag within its "Runs on" group; order is kept | Follows the phone's order |
| Enable / disable | Switch on the list card and in the editor header | ToggleSwitch on each card; the change is reported to the phone |
| Pause all | Top bar toggle + banner (1.10) | Header ToggleSwitch + banner + palette command |
| Multiple PCs | "Controls Gaming PC" chip is a picker when more than one PC is paired; the list groups by "Runs on" | Shows only its own routines |

### 1.7 Running, cancelling, results

**Phone-run routine (phone trigger).**
- Ongoing notification on a new channel "Routine progress" (low importance, silent): routine name, current step in words ("Waiting for Gaming PC"), "step 2 of 3", Cancel and Open actions. On API 36+ it uses `Notification.ProgressStyle` with one segment per step; below that, a determinate bar with step granularity.
- In-app, the list card and the editor show progress travelling along the steps (3.3 M7, M8). The card's Run button becomes Stop.
- Result: success posts a "Routine results" notification that times out after 10 s; failure persists with "See what happened" (opens the run detail).
- `notify` to phone: a notification on the "Routine messages" channel. Three channels exist so users can silence progress without silencing messages.

**PC-run routine (`pc.*` trigger).** Runs with no phone. The PC page card shows the running state; when the phone is connected, the phone also shows the progress notification labelled with the PC's name, fed by live `routine_run_report` updates (§7.3.6).

**Destructive step on the PC** (a step in the countdown set, from any trigger or initiator, including a manual run from the phone; the only exception is a confirmed Run now on the PC, below; D1):
- A dedicated topmost window (P4), not an overlay in the main window, because the main window may be hidden, minimized or never constructed (`REGRESSION-GUARDS.md:1040-1056`).
- 15 s ring countdown, routine name, what will happen, where it came from ("Bedtime, from Connor's Pixel").
- One button: Cancel. It is the default and the cancel button, and the window takes focus, so Enter, Space or Esc cancels. A stray keypress can only ever cancel, which is the safe direction.
- If the owner's phone is connected it mirrors the countdown as a notification with Cancel, which sends `routine_cancel` (§7.3.7). While the PC is locked the window cannot draw over the secure desktop: the 15 s still elapse, the phone mirror is the visible surface, and the run records `countdown_unseen` (§8.6). The window opens on the primary screen's work area.
- Cancel: window closes, a tray balloon (Outcome) says "Cancelled. Bedtime did not shut down this PC.", the run records `cancelled` with `cancelledBy` (`pc` or `phone`), and every remaining step records `cancelled`.
- Timeout: the step runs; nothing else to show because the PC is going away.

**Manual runs** (in-app Run, shortcut, widget, NFC tap) confirm on the phone **and** still get the PC countdown, because the person holding the phone may not be the person at the PC (D1). **PC Run now** is the one exception: the person at the PC confirms, and that confirmation is the presence the countdown exists to ask for (D3).
- Phone: routines with destructive steps confirm before running (the translucent confirm pattern from `TileConfirmActivity.kt:17-36` for shortcut/widget/NFC; an in-app dialog otherwise).
- PC Run now: confirm through `ConfirmationDialogHost` (fails closed), with `primary danger` for work-discarding steps and `primary warning` for sleep/hibernate. **Run now on the PC is safe to offer**: every step comes from the same allowlists the Commands and Launcher pages already expose to whoever sits at the PC, and the confirm covers the destructive case. After confirming, destructive steps run without the countdown. Run now exists only in the PC's own UI; nothing on the wire can request it (§9, T21).

**Notify to phone while the phone is away** (queued on the PC, 1 h expiry): on next connect each queued message posts on "Routine messages" with its original time ("Sent 20 min ago"). Expired messages appear only in the run detail as "Expired before your phone connected".

### 1.8 Editor validation

Save stays enabled. Tapping it with problems scrolls to the first problem, outlines every problem card in the error role, and a snackbar announces "Fix 2 things before saving." (plural resource). Warnings do not block.

| Rule | Kind | Message on the card |
|---|---|---|
| No trigger | Error | "Choose what starts this routine." |
| No steps | Error | "Add at least one step." |
| Required parameter missing | Error | per parameter, for example "Choose an app." |
| `home.*` with no home | Error | "Set up your home network." |
| `wake` when the target PC has no MAC saved | Error | "Gaming PC has no MAC address saved. Add it in Connection settings." (link) |
| Step unavailable for where the routine runs (for example `wake` or `waitOnline` on a `pc.*` routine) | Error | "Only for routines that run on your phone." |
| App no longer in the PC's launcher list | Error | "Steam is no longer in Gaming PC's launcher list." |
| Step after a step that turns the PC off, on a `pc.*` routine | Error (`destructive_not_last`) | "This can't run: the step before it turns off the PC. Move the power action to the end." |
| Step after a power-off step on a phone routine, not preceded by `waitOnline` | Warning (skipped at run with `after_power_off`) | "Add Wait until the PC is online before this step." |
| Any destructive step | Info | "Gaming PC will show a 15 second countdown before doing this." |
| Second destructive step | Error (`too_many_destructive`) | "A routine can have only one shut down, restart, sign out, sleep or hibernate step." |
| `home.leave` with any PC-targeted step, unless this phone has already reached the PC from away (`reachableAway`, §8.3.1) | Warning | "Only runs if this PC is reachable away from home (for example over Tailscale)." with a link to the "Lock my PC when I'm gone" template (`tpl.home.lock`) |
| `media` on a PC not set up for key presses | Warning | reuse `rc_media_unavailable` |
| Name empty | none | auto-name from the trigger and first step |
| 12 steps reached | none | "Add step" disabled, supporting text "Routines can have up to 12 steps." |
| A phone routine's waits and steps exceed the 9-minute budget (`budget_exceeded`, §6.5) | Error | "This routine would take too long to run. Shorten the waits." |

Leaving the editor with unsaved changes asks "Discard changes?" (Discard is `danger`).

### 1.9 Export and import

Mirrors the palette precedent (`PaletteExchange.cs:29-75`): a `.remexroutines` JSON file, camelCase, indented, `formatVersion: 1`, a reader that tolerates unknown optional fields, and a TryParse that fails into a message rather than a crash.

- **Export** (SAF create document; single routine "Share" uses the share sheet): name, appearance, trigger, steps, order (format in §6.10). **Left out on purpose**: the home network facts, NFC tag bindings, the target PC's identity and pairing material, run history. The export dialog says so in one line: "Home network, tags and PC pairing are not included."
- **Import** (SAF open document or share-into-RemEx): a review sheet lists each routine with a checkbox, and per routine any fix-ups: "Pick the PC it controls", "Set up home", "Steam isn't in Gaming PC's launcher list", "Name already used: Keep both / Replace / Skip". Imported routines **always arrive switched off**.
- Errors: an unreadable file says "Couldn't read this file. It isn't a RemEx routines export." A newer format says "This file was made by a newer RemEx. Update the app to import it." The failure title is never the success title (the palette bug in `CHANGELOG.md:326-330`).

### 1.10 Pause all

| | Android | PC |
|---|---|---|
| Control | Top app bar icon toggle (`Pause` / `PlayArrow`), tooltip "Pause all routines" / "Resume routines" | Page header ToggleSwitch "Pause all", palette commands |
| Banner | Tonal banner under the top bar: "Routines are paused. Nothing runs on its own." + "Resume" | Same copy in a plate inside the page, with "Resume" |
| Effect shown | Cards dim to 60 % opacity; switches keep their own state | Same |
| Manual runs while paused | Allowed; the Run button stays, the banner stays | Run now allowed |
| Scope shown | Phone: "Paused on this phone and on Gaming PC" when propagation succeeds, or "Gaming PC will pause when it connects" | "Paused on this PC" or "Paused from Connor's Pixel" |

Semantics and propagation: §8.7 (D4). Pause stops automatic triggers only; every person-initiated run (in-app Run, Test, shortcut, widget, NFC tap, PC Run now) still works.

---

## 2. Layouts

### 2.1 Android navigation graph changes

| Route | Type | Notes |
|---|---|---|
| `Screen.Routines` | `NavDestination` (title `screen_routines_title`, icon `Route`) | First in `moreItems`. On compact, the list. On medium/expanded, a `NavigableListDetailPaneScaffold` (precedent `SettingsScreen.kt:118-124`) with list pane and editor/history as detail |
| `Screen.RoutineTemplates` | object | Gallery. On expanded it renders in the detail pane |
| `RoutineEditorRoute(routineId: String?, templateId: String?)` | data class (precedent `PairingRoute`, `NavRoutes.kt:217`) | Both null = blank. Construction validated like `PairingRoute` |
| `RoutineHistoryRoute(routineId: String?)` | data class | null = all runs |
| `RoutineRunRoute(runId: String)` | data class | Step timeline |
| Home capture, NFC write, trigger picker, add step, parameter sheets | ModalBottomSheet inside the editor/list, not routes | Short, modal, return a value |
| Shortcut / widget / NFC entry | `RoutineRunActivity` (translucent, not exported except the NFC intent filter), outside the NavHost | Confirms destructive routines like `TileConfirmActivity` |

All new routes keep nav chrome (not added to `noNavChrome`, `AppNavigation.kt:124`) except the editor on compact, which hides the NavigationBar so the floating toolbar owns the bottom edge.

### 2.2 Android wireframes

Icons are shown as bracketed words. `›` is an auto-mirrored chevron icon, not text.

**A1. Routines list (phone portrait)**
```
┌──────────────────────────────────────────┐
│ ←  Routines                  [pause] [⋮] │ RemexFlexibleTopBar (medium flexible)
│ ┌──────────────────────────────────────┐ │
│ │ Routines are paused. Nothing runs on │ │ banner, only while paused
│ │ its own.                  [ Resume ] │ │
│ └──────────────────────────────────────┘ │
│ RUNS ON THIS PHONE                       │ sticky group header
│ ┌──────────────────────────────────────┐ │
│ │ (home) Get home, open Steam   [==o]  │ │ Card; Switch
│ │ [Get home] › [Wake] › [Wait] +1      │ │ chip chain (display only)
│ │ [ok] Ran today at 18:02              │ │ last result: icon + text
│ └──────────────────────────────────────┘ │
│ ┌──────────────────────────────────────┐ │
│ │ (play) Game night            [ Run ] │ │ manual: tonal Run button
│ │ [Tap Run] › [Wake] › [Open Steam] +1 │ │
│ └──────────────────────────────────────┘ │
│ RUNS ON GAMING PC                        │
│ ┌──────────────────────────────────────┐ │
│ │ (idle) Sleep when idle        [==o]  │ │
│ │ [PC idle 30 min] › [Sleep]           │ │
│ │ [sync] Waiting to send to Gaming PC  │ │ sync state line
│ └──────────────────────────────────────┘ │
│ RECENT RUNS                     See all  │
│  [ok]  Get home, open Steam    18:02     │
│  [x]   Sleep when idle         Yesterday │
│                                  ╭────╮  │
│                                  │ +  │  │ FloatingActionButtonMenu
│                                  ╰────╯  │ (From a template / Start from blank / Import)
└──────────────────────────────────────────┘
```

**A2. Empty state**
```
┌──────────────────────────────────────────┐
│ ←  Routines                          [⋮] │
│        [illustration: trigger ── steps]  │
│  Let RemEx do things on its own          │ headlineSmall
│  Pick something that happens, then what  │ bodyMedium
│  RemEx should do. Start with one of      │
│  these:                                  │
│ ┌──────────────────────────────────────┐ │
│ │ (home) Wake my PC when I get home    │ │ featured template cards
│ │ [Get home] › [Wake] › [Wait for PC]  │ │
│ └──────────────────────────────────────┘ │
│ ┌──────────────────────────────────────┐ │
│ │ (idle) Sleep my PC when it's idle    │ │
│ └──────────────────────────────────────┘ │
│ ┌──────────────────────────────────────┐ │
│ │ (nfc) Tap a tag to lock my PC        │ │
│ └──────────────────────────────────────┘ │
│        [ Browse all templates ]          │ FilledTonalButton
│             Start from blank             │ TextButton
└──────────────────────────────────────────┘
```

**A3. Template gallery**
```
┌──────────────────────────────────────────┐
│ ←  Templates                             │
│ [All] [Home] [Gaming] [Media] [Focus] →  │ FilterChips, horizontal scroll
│ HOME                                     │
│ ┌──────────────────────────────────────┐ │
│ │ (home) Wake my PC when I get home    │ │
│ │ Your PC is ready by the time you sit │ │ "why", bodySmall
│ │ down.                                │ │
│ │ [Get home] › [Wake] › [Wait] › [Msg] │ │
│ │ Needs: Home network · MAC address    │ │ requirement tokens
│ └──────────────────────────────────────┘ │
│ ┌──────────────────────────────────────┐ │
│ │ (walk) Lock my PC when I leave       │ │
│ │ Needs: Reach your PC away from home  │ │ info icon opens explanation
│ └──────────────────────────────────────┘ │
│ ┌──────────────────────────────────────┐ │
│ │ (nfc) Movie night tag      (disabled)│ │ unavailable: 38 % content alpha
│ │ This phone has no NFC.               │ │ reason replaces "Needs"
│ └──────────────────────────────────────┘ │
└──────────────────────────────────────────┘
```
Medium/expanded: `LazyVerticalGrid(GridCells.Adaptive(320.dp))`.

**A4. Editor (builder)**
```
┌──────────────────────────────────────────┐
│ [x]  New routine                     [⋮] │ close asks "Discard changes?"
│ ┌──────────────────────────────────────┐ │
│ │ Name                                 │ │ OutlinedTextField, 40 chars
│ │ Get home, open Steam                 │ │
│ └──────────────────────────────────────┘ │
│ [Runs on this phone] [Controls Gaming PC ▾]  assist chips; picker if >1 PC
│ ┌──────────────────────────────────────┐ │
│ │ [!] Android may delay this routine   │ │ needs-attention card (conditional)
│ │ while RemEx is asleep.  [ Fix ]      │ │
│ └──────────────────────────────────────┘ │
│ WHEN                                     │ labelLarge, primary
│ ┌──────────────────────────────────────┐ │
│ │ (home)  I get home                 › │ │ primaryContainer card
│ │         Home: router 192.168.1.1     │ │
│ └──────────────────────────────────────┘ │
│    ┃                                     │ connector (outlineVariant)
│ THEN                                     │
│ ┌──────────────────────────────────────┐ │
│ │ (1) (power) Wake the PC       [⋮][≡] │ │ ≡ drag handle 48dp
│ │             Gaming PC                │ │
│ └──────────────────────────────────────┘ │
│    ┃                                     │
│ ┌──────────────────────────────────────┐ │
│ │ (2) (hourglass) Wait until online    │ │
│ │             Up to 5 min       [⋮][≡] │ │
│ └──────────────────────────────────────┘ │
│    ┃                                     │
│ ┌──────────────────────────────────────┐ │ error outline until filled
│ │ (3) (launch) Open an app      [⋮][≡] │ │
│ │             Choose an app.           │ │ error text
│ └──────────────────────────────────────┘ │
│    ┆                                     │ dashed: "next goes here"
│    [ + Add step ]                        │ FilledTonalButton
│                                          │
│  ╭──────────────────────────────╮ ╭────╮ │ HorizontalFloatingToolbar
│  │ [▶ Test] [History]  [⋮]      │ │ ✓  │ │ + VibrantFloatingActionButton (Save)
│  ╰──────────────────────────────╯ ╰────╯ │
└──────────────────────────────────────────┘
```
Step overflow: Duplicate step, Move up, Move down, Delete (undo snackbar).

**A5. Add-step sheet**
```
┌──────────────────────────────────────────┐
│                 ─────                    │ ModalBottomSheet, max width 640dp
│ Add a step                               │
│ ┌──────────────────┐ ┌─────────────────┐ │ 2-column grid of Surface tiles
│ │ (power)          │ │ (hourglass)     │ │
│ │ Wake the PC      │ │ Wait until the  │ │
│ │ Sends a Wake-on- │ │ PC is online    │ │
│ │ LAN signal       │ │                 │ │
│ └──────────────────┘ └─────────────────┘ │
│ ┌ (timer) Wait ────┐ ┌ (power) Power ──┐ │
│ ┌ (launch) Open ───┐ ┌ (media) Media ──┐ │
│ ┌ (chat) Message ──┐                     │
│ ┌──────────────────────────────────────┐ │ unavailable tiles last,
│ │ (power) Wake the PC                  │ │ disabled, with reason
│ │ Only for routines that run on your   │ │
│ │ phone.                               │ │
│ └──────────────────────────────────────┘ │
└──────────────────────────────────────────┘
```
Choosing a tile moves the sheet forward (shared axis X) to that step's parameters; "Add" commits.

**A6. Step reordering**
```
│ ┌──────────────────────────────────────┐ │
│ │ (1) (power) Wake the PC       [⋮][≡] │ │
│ └──────────────────────────────────────┘ │
│    ┊   ┌─────────────────────────────────┐ lifted: scale 1.03, level 3
│    ┊   │ (3) (launch) Open Steam   [⋮][≡]│ follows the finger
│    ┊   └─────────────────────────────────┘
│ ┌──────────────────────────────────────┐ │ neighbour slides to make room
│ │ (2) (hourglass) Wait until online    │ │ numbers update live
│ └──────────────────────────────────────┘ │
```

**A7. Trigger picker sheet**
```
│ What starts this routine?                │
│ ON YOUR PHONE                            │
│  (home)  I get home                      │ ListItem + supporting text
│          When your phone joins home Wi-Fi│
│  (walk)  I leave home                    │
│  (nfc)   I tap an NFC tag                │
│  (play)  I tap Run                       │
│          In the app, a shortcut or widget│
│ ON YOUR PC                               │
│  (therm) A PC sensor passes a limit      │
│  (idle)  The PC is idle                  │
│  (lock)  The PC is locked or unlocked    │
│ Routines that start on your PC run on    │ bodySmall
│ the PC, even when your phone is away.    │
```
Changing the trigger to one that runs elsewhere, with steps that become unavailable, asks first: "Wake the PC only works in routines that run on your phone. Remove it?"

**A8. Run history (list, then step-level detail)**
```
│ ←  History · Get home, open Steam        │
│ TODAY                                    │
│ [ok] 18:02  Got home        3 of 3 steps │
│ [x]  07:40  Test run   Stopped at step 2 │
│ YESTERDAY                                │
│ [--] 22:15  Got home      Skipped: paused│
└──────────────────────────────────────────┘
┌──────────────────────────────────────────┐
│ ←  Run at 07:40                          │
│ Get home, open Steam · Test run          │
│ Failed after 5 min 4 s                   │
│  ●  I get home             07:40:01      │ trigger node
│  │                                       │
│  ●  Wake the PC     [ok]   0.2 s         │
│  │                                       │
│  ●  Wait until online [x]  5 min         │
│  │  Gaming PC didn't come online within  │ reason message (§10.1)
│  │  5 min. If it was fully off, it may   │
│  │  be waiting at the sign-in screen.    │
│  │  [ Open Connection settings ]         │ contextual fix, if any
│  ○  Open Steam      Not run              │
│  Edited since this run                   │ when revision differs
│             [ Run again ]                │
└──────────────────────────────────────────┘
```

**A9. Phone "running" affordance**
```
In-app card                                 Notification (API 36+ ProgressStyle)
┌──────────────────────────────────────┐   ┌ RemEx · Routine progress ───── now ┐
│ (home) Get home, open Steam  [Stop]  │   │ Get home, open Steam               │
│ [Get home] › [▓Wake▓] › [Wait] +1    │   │ Waking Gaming PC · step 1 of 3     │
│ ∿∿∿∿∿∿∿∿∿∿∿───────────────────────── │   │ [■■■■■|        |          ]        │
│ Waking Gaming PC · step 1 of 3       │   │ Cancel                      Open   │
└──────────────────────────────────────┘   └────────────────────────────────────┘
                                            Countdown mirror (phone connected)
                                            ┌ RemEx · Routine progress ───── now ┐
                                            │ Gaming PC shuts down in 12 s       │
                                            │ Leave home                         │
                                            │ Cancel                             │
                                            └────────────────────────────────────┘
```

**A10. Home capture sheet**
```
│ Set your home network                    │
│ RemEx recognises home by your Wi-Fi      │
│ network, not your location. It never     │
│ asks for location permission.            │
│          [radar illustration]            │
│  Router           192.168.1.1            │
│  Address range    192.168.1.0/24         │
│  DNS              192.168.1.1            │
│  Domain           lan                    │
│  [ok] Gaming PC is reachable here        │
│  [ Use this network as home ]            │ Button (filled)
│              Not now                     │ TextButton
```

**A11. NFC write sheet**
```
│ Write an NFC tag                         │
│      [phone ))) tag illustration]        │
│ Hold the back of your phone against the  │
│ tag. Keep it there until it buzzes.      │
│          (RemexLoadingIndicator)         │
│                Cancel                    │
```

**A12. Widget and shortcut**
```
Widget 2x1                  Widget 1x1       Pinned shortcut
┌───────────────────────┐   ┌──────────┐     ( play )
│ (play) Game night     │   │  (play)  │     Game night
│ [ok] 18:02    [ Run ] │   │ Game ni… │
└───────────────────────┘   └──────────┘
```
Running: the Run button shows "Running" and the icon container uses `primaryContainer`. Config activity lists `manual` routines only.

**A13. Tablet / foldable (medium and expanded)**
```
┌──────┬────────────────────────┬──────────────────────────────────┐
│ Rail │ Routines     [pause][⋮]│ Get home, open Steam         [⋮] │
│ Dash │ RUNS ON THIS PHONE     │ WHEN  (home) I get home        › │
│ Rem  │ ▌Get home, open Steam  │ THEN  (1) Wake  (2) Wait  (3) Open│
│ Apps │  Game night            │       [ + Add step ]             │
│ Task │ RUNS ON GAMING PC      │                                  │
│ ──── │  Sleep when idle       │  ╭ [▶ Test][History][⋮] ╮ ╭ ✓ ╮ │
│ Rout │ RECENT RUNS            │  ╰──────────────────────╯ ╰───╯ │
└──────┴────────────────────────┴──────────────────────────────────┘
```
List pane fixed 360dp; detail content max width 640dp, centred.

**A14. Remote Control "Your routines"**
```
│ YOUR ROUTINES                            │ same header style as POWER
│ ┌──────────────────┐ ┌─────────────────┐ │ CommandCard look
│ │ (play) Game night│ │ (play) Start my │ │ tap: run (confirm face if
│ │                  │ │ workday         │ │ destructive, like POWER cards)
│ └──────────────────┘ └─────────────────┘ │
│ POWER                                    │ existing section
```
Shows `manual` routines whose target is the connected PC; hidden when there are none.

### 2.3 PC changes and wireframes

- Drawer: `ListBoxItem Tag="10"` after Commands, icon `Routes`, `AutomationProperties.Name` and tooltip `Nav_Routines`. The nav list gains an 11th child, so `ShellView.axaml:314-440` gets an `nth-child(11)` entrance style and the stagger is recomputed so the last item still ends within 300 ms (the ceiling stated at `:303-305`).
- PageHost: `DataTemplate DataType="vm:RoutinesViewModel"` → `views:RoutinesView` in `ShellView.axaml:1053-1084`; `ShellViewModel.NavigateToRoutines()` calls `SetTransitionAndNavigate(10, …)`.
- Transition direction compares **visual** drawer positions (a small position map), not Tags. This also fixes the existing About (Tag 6 below Settings Tag 9) inversion at `ShellViewModel.cs:1453`.
- New `RoutineCountdownWindow` (topmost, see P4). New palette entries.

**P1. Routines page, wide (content ≥ 900)**
```
┌─────────┬──────────────────────────────────────────────────────────────────────┐
│ RemEx   │ Routines                                           Pause all [○──]   │ Headline + ToggleSwitch
│ Home    │ Made in the RemEx app on your phone. They run on this PC even when   │ Body2, TextSecondary
│ Sensors │ your phone is away.                                              [?] │ coach replay (tertiary icon-button)
│ Commands│ ┌───────────────────────────────┐ ┌────────────────────────────────┐ │
│ Routines│ │ (sleep) Sleep when idle [●──] │ │ Sleep when idle                │ │ detail card
│ Launcher│ │ [PC idle 30 min] › [Sleep]    │ │ From Connor's Pixel, updated   │ │
│ Process │ │ [ok] Ran at 02:14             │ │ 12 Sep                         │ │
│ Files   │ │               [ Run now ]     │ │ WHEN  The PC is idle for 30 min│ │
│ Logs    │ └───────────────────────────────┘ │ THEN  1  Sleep                 │ │
│ ─────── │ ┌───────────────────────────────┐ │ HISTORY                        │ │
│ Settings│ │ (therm) GPU running hot [●──] │ │ [ok] Today 02:14  Idle 30 min ▸│ │ rows expand to steps
│ About   │ │ [GPU > 85 °C 30 s] › [Msg] +1 │ │ [--] Yesterday  Cancelled here │ │
│         │ │ [!] Can't run: sensor missing │ │ [x]  Mon 23:10  Failed at 1  ▸ │ │
│         │ └───────────────────────────────┘ └────────────────────────────────┘ │
└─────────┴──────────────────────────────────────────────────────────────────────┘
```
Cards: `Border` with the card surface and the plate role inside (never `GlassBaseDarkBrush`, `AUDIT-cohesiveness-2026-09-15.md:16`). Selected card: accent border. "Run now": `secondary compact`. Enable: `ToggleSwitch`.

**P2. Narrow (content < 900)**: one column; selecting a card expands its detail (the same content as the right pane) inline beneath it. Same breakpoint as `ResponsiveColumnWidthConverter` (900).

**P3. Empty**
```
│ Routines                                          Pause all [○──]   │
│                 [illustration: trigger ── steps]                    │
│   No routines run on this PC yet                                    │ Headline6
│   Routines are made in the RemEx app on your phone. Open More,     │ Body2
│   then Routines, and try "Sleep my PC when it's idle".              │
│   [ Learn about routines ]                                          │ secondary: opens the tutorial at the Routines page
```
"Learn about routines" is the first production caller of `TutorialNavigator.PositionOfPage` (deep links name a page, `TutorialNavigator.cs:83-93`).

**P4. Countdown window**
```
            ┌─────────────────────────────────────────────┐
            │   ╭──────╮                                  │  440 x 176, centred on the
            │   │  12  │   Shutting down this PC          │  primary screen's work area
            │   ╰──────╯   Bedtime, from Connor's Pixel   │  ring = arc, number = Headline4
            │              Save your work, or cancel.     │
            │                                             │
            │                         [     Cancel     ]  │  primary (the only button)
            └─────────────────────────────────────────────┘
```
Window: `Topmost`, `ShowInTaskbar=False`, `CanResize=False`, activates (so keys reach Cancel), `IsDefault` and `IsCancel` on Cancel. Surface `surfaceContainerHigh`-class plate, `CornerRadiusLarge`. Ring track `outlineVariant`, sweep `AccentPrimary`, switching to `SystemError` for the last 5 s. Must not rely on `MainWindow` existing.

**P5. Run now confirm** (`ConfirmationDialogHost`)
```
┌ Run "Cool down before damage" now? ─────────┐
│ This routine will put this PC to sleep.     │
│ Steps: Message, Message, Sleep              │
│                     [ Cancel ] [ Run now ]  │  secondary / primary warning
└─────────────────────────────────────────────┘
```

Confirming counts as presence: destructive steps then run without the 15 s countdown (D3, §8.6). Run now exists only in the PC's own UI.

### 2.4 Adaptive behaviour

| Size class | Android | PC |
|---|---|---|
| Phone portrait (compact) | List, editor and history are separate destinations; editor hides the NavigationBar; sheets full width | n/a |
| Phone landscape (compact height) | Same; editor content max width 640dp centred; floating toolbar collapses to icons only (expanded=false) when the keyboard is up | n/a |
| Foldable unfolded / tablet portrait (medium) | List-detail (A13), NavigationRail; sheets max 640dp | n/a |
| Tablet landscape (expanded) | List-detail, NavigationDrawer; gallery as 2-3 column grid in the detail pane | n/a |
| PC narrow (content < 900) | n/a | P2 single column, inline detail |
| PC wide (content ≥ 900) | n/a | P1 list (min 360) + detail |
| Large text | Chip chains wrap (`FlowRow`); card titles wrap to 2 lines then ellipsize, full text in the semantics | UI Size scaling; chips wrap in a `WrapPanel` |

### 2.5 Component mapping

| Need | Android (M3 Expressive, material3 1.5.0-alpha24) | PC (Avalonia 12.1.1 + Material.Avalonia 3.19.0) |
|---|---|---|
| Page header | `RemexFlexibleTopBar` | Existing page title/subtitle TextBlock themes |
| Routine card | `Card` (as `PersonalizationScreen.kt:277`) | `Border` card surface, plate role inside |
| Chip chain | Display-only `Surface` tokens (not `AssistChip`: they are not individually actionable), merged semantics | `Border` tokens in a `WrapPanel` |
| Enable | `Switch` with thumb icon | `ToggleSwitch` (App.axaml assists) |
| Create | `FloatingActionButtonMenu` + `ToggleFloatingActionButton` | n/a |
| Editor actions | `HorizontalFloatingToolbar` + `FloatingToolbarDefaults.VibrantFloatingActionButton` | n/a |
| Choices (above/below, locked/unlocked, media action) | ToggleButtons, not `ButtonGroup` (`PersonalizationScreen.kt:438`) | `RadioButton`s |
| Sheets | `ModalBottomSheet` | n/a |
| Progress | `RemexLinearWavyProgress`, `RemexLoadingIndicator` | `ProgressBar` (determinate, step granularity) |
| List-detail | `NavigableListDetailPaneScaffold` | Two-column grid, 900 breakpoint |
| Coach marks | `CoachPanel` pattern | CanvasView coach pattern |
| Buttons | M3 buttons | Vocabulary classes only (`primary`/`secondary`/`tertiary` + tint) |
| Tooltips | `RemexTooltip` | `ToolTip.Tip` |

---

## 3. Motion

### 3.1 Reduced motion sources (both must be honoured)

- **Android:** system "Remove animations" (animator scale 0). The theme already swaps `MotionScheme.expressive()` for `MotionScheme.standard()` (`Theme.kt:485-486`), so every spec sourced from `MaterialTheme.motionScheme` calms itself automatically. Anything self-driven (loops, choreographed sequences, shimmer) must also read `LocalReducedMotion` (`Theme.kt:479`) and suppress itself.
- **PC:** the in-app setting Personalize > Layout > Behaviour > Reduced motion, `ShellViewModel.IsReducedMotion` persisted as `DashboardProfile.IsReducedMotion`. Styles gate animations with a class bound to it (the `aurora-animated` pattern), and `StaggeredEntrance.ShouldPlay(key, reducedMotion)` for entrances. The countdown window is not inside the shell, so it reads the flag from the layout service's current profile.

### 3.2 Tokens

Android uses only `MaterialTheme.motionScheme` tokens, never literal springs or tweens (`AppNavigation.kt:833-841` is the rule by example). For porting to the PC, the values those tokens resolve to (androidx `MotionTokens`/`ExpressiveMotionTokens`; confirm against the library at implementation time):

| Token | Expressive (damping / stiffness) | Standard (reduced) | PC equivalent |
|---|---|---|---|
| fastSpatial | 0.6 / 800 | 0.9 / 1400 | 180 ms, KeySpline 0.2,0,0,1 |
| defaultSpatial | 0.8 / 380 | 0.9 / 700 | 280 ms, KeySpline 0.2,0,0,1 |
| slowSpatial | 0.8 / 200 | 0.9 / 300 | 450 ms, KeySpline 0.2,0,0,1 |
| fastEffects | 1.0 / 3800 | same | 150 ms CubicEaseOut |
| defaultEffects | 1.0 / 1600 | same | 200 ms CubicEaseOut |
| slowEffects | 1.0 / 800 | same | 400 ms CubicEaseOut |

The PC's only spring (`SpringEasing` 1/120/12, `App.axaml:112`) stays reserved for card lift shadows.

### 3.3 Moments

| # | Moment | Platform | What moves | Spec | Reduced motion |
|---|---|---|---|---|---|
| M1 | Empty state arrives | Android | Illustration draws its connector left to right; three featured cards rise 16dp and fade in, 50 ms stagger | connector `slowEffects`; cards `defaultSpatial` + `defaultEffects` | Everything present at once |
| M2 | Template card → editor | Android | Container transform: card bounds grow into the editor; the card's chip chain becomes the WHEN card and step cards | `SharedTransitionLayout` + `sharedBounds`, `defaultSpatial`; content fade `defaultEffects` | Cross-fade `fastEffects` |
| M3 | Step list assembling (template or opening a saved routine the first time in a session) | Android | Step cards drop from the WHEN card along the connector in order, 60 ms stagger (cap 6 animated, rest appear); connector segments draw ahead of each card | cards `defaultSpatial` (8dp travel + fade); connector `defaultEffects` | Instant |
| M4 | Add step | Android | New card grows from the "Add step" button position; connector extends to it; list items below shift | `animateItem(placementSpec = fastSpatial)` (precedent `FileManagerSheets.kt:227`), enter `scaleIn(0.92) + fadeIn` `fastSpatial`/`fastEffects` | Placement snaps via Standard scheme; no scale |
| M5 | Reorder drag | Android | Lifted card scales to 1.03 and rises to tonal level 3; neighbours slide; step numbers morph to their new values | lift `fastSpatial`; neighbours `animateItem` `fastSpatial`; haptics `CLOCK_TICK` per slot crossed, `CONFIRM` on drop | No scale; neighbours still move (it is the feedback), Standard scheme |
| M6 | Delete step | Android | Card collapses height and fades, connector closes the gap | `shrinkVertically` `fastSpatial` + `fadeOut` `fastEffects` | Fade only |
| M7 | Run progress travelling down the steps (editor, history detail live) | Android | The current step's number badge morphs into a `LoadingIndicator` shape; on completion it morphs to a check in a circle (`MaterialShapes`), and the connector segment below fills with `primary` from top to bottom before the next badge starts | morph `defaultSpatial`; connector fill `defaultEffects`; failure: badge becomes an error-container cross, `REJECT` haptic | Badge swaps icon and colour without morph; connector recolours instantly; status text unchanged |
| M8 | Running card (list) | Android | A highlight pill slides from chip to chip in the chain; `RemexLinearWavyProgress` under the card; on success the wave flattens (amplitude 0, per `CommonComponents.kt:386-391`) and the card tints `primaryContainer` for 300 ms | pill `fastSpatial`; tint `defaultEffects` | Pill jumps; plain determinate bar (amplitude 0 throughout) |
| M9 | Pause all | Android | Banner expands down from the top bar; cards fade to 60 % | `expandVertically` `defaultSpatial`; alpha `defaultEffects` | Instant |
| M10 | NFC waiting | Android | Three concentric rings pulse out from the phone illustration, 1.6 s period | `infiniteRepeatable`, `slowEffects`-paced fade/scale | Static rings; the copy carries the state |
| M11 | NFC success | Android | `LoadingIndicator` morphs into a check shape, then the success copy slides up | morph `defaultSpatial`; text `defaultEffects` | Icon swap |
| M12 | Home capture reading | Android | A single radar sweep (max 1.5 s) while facts are read; fact rows stagger in 40 ms | sweep `slowEffects`; rows `defaultSpatial` | Rows appear together; no sweep |
| M13 | History detail opens | Android | Timeline nodes fill top to bottom, 30 ms each, total ≤ 300 ms | `fastEffects` | Instant |
| M14 | More "New" badge | Android | Scale + fade in/out, identical to the disconnected badge | `fastSpatial` + `fastEffects` (`AppNavigation.kt:463-478`) | Standard scheme |
| M15 | Gallery filter change | Android | Cards reflow | `animateItem` `fastSpatial` | Snap |
| P-M1 | Routines page first visit per process | PC | Cards stagger in | `StaggeredEntrance` key `RoutinesView`, 30 ms stagger, 200 ms each, KeySpline 0.2,0,0,1 | `ShouldPlay` returns false |
| P-M2 | Navigating to/from Routines | PC | Shared axis, direction from visual drawer position | `SharedAxisPageTransition` | Existing converter returns no transition |
| P-M3 | Detail pane change | PC | Content cross-fades | 150 ms CubicEaseOut | Instant |
| P-M4 | Run now / live run on a card | PC | Chips light up in order as steps start and finish (`BrushTransition` on background) | 200 ms CubicEaseOut | Instant recolour |
| P-M5 | Countdown window enters | PC | Fade in + 16 dip rise | 280 ms KeySpline 0.2,0,0,1 | Appears |
| P-M6 | Countdown ring | PC | Arc sweeps from full to empty over 15 s, **linear** (it is time, not motion); the number pulses 1.0 → 1.06 → 1.0 on each second | ring linear; pulse 150 ms CubicEaseOut; colour to `SystemError` at 5 s, 200 ms | Ring steps once per second (discrete), no pulse, colour change instant |
| P-M7 | Countdown cancelled | PC | Window fades out after the ripple settles (`PressSettleDuration` 180 ms, `TrayBalloonWindow.axaml.cs:53`) | 150 ms fade | Hide after 180 ms, no fade |
| P-M8 | Pause all banner | PC | Banner height + opacity | 200 ms CubicEaseOut | Instant |

Motion that is **not** added on purpose: no shake on validation errors (outline + snackbar is enough, and shake reads as scolding), no confetti on first save, no looping animation on idle list cards.

---

## 4. Templates

18 templates, v1 triggers and steps only. "Fills" are parameters the user must supply; everything else has a default they can change. Names and "why" lines are localized strings; once a routine is created from a template its name is user data and never re-localized.

### 4.1 Catalog

| Id | Name | Why someone wants it | Trigger | Steps (defaults) | Fills | Needs | Runs on |
|---|---|---|---|---|---|---|---|
| **Arrive / leave home** |||||||| 
| `tpl.home.wake` | Wake my PC when I get home | Your PC is ready by the time you sit down. | `home.arrive` | `wake` → `waitOnline` (5 min) → `notify` phone "Your PC is ready." | PC (if >1) | Home network; PC MAC address; Wake-on-LAN enabled on the PC | Phone |
| `tpl.home.lock` | Lock my PC when I'm gone | Locks the PC once nobody is using it, even after your phone has left home. The recommended way to lock the PC when you leave (D6). | `pc.idle` 5 min | `power` LOCK | none | none | PC |
| `tpl.home.sleep` | Sleep my PC when I lock it | Lock the PC on your way out and it goes to sleep by itself. | `pc.session` locked | `power` SLEEP (countdown on PC, mirrored on the phone) | none | none | PC |
| `tpl.home.music` | Music on when I get home | Your music picks up where it stopped. | `home.arrive` | `waitOnline` (2 min) → `media` playPause | none | Home network; PC set up for media keys | Phone |
| **Gaming** |||||||| 
| `tpl.game.steam` | Get home, open Steam | Walk in and your library is already open. | `home.arrive` | `wake` → `waitOnline` (5 min) → `launchApp` | App (preselects a launcher entry named Steam) | Home network; MAC; launcher entry | Phone |
| `tpl.game.night` | Game night | One tap wakes the PC and opens your game. | `manual` | `wake` → `waitOnline` (5 min) → `launchApp` → `notify` phone "Ready to play." | App | MAC; launcher entry | Phone |
| **Media / movie night** |||||||| 
| `tpl.media.movie` | Movie night tag | Tap the tag by the sofa and your player opens. | `nfc.tap` | `wake` → `waitOnline` (3 min) → `launchApp` | App; write tag | NFC; MAC; launcher entry | Phone |
| `tpl.media.screenoff` | Music on, screen off | Keep the music, lose the glow. | `nfc.tap` | `media` playPause → `power` MONITOROFF | Write tag | NFC; media keys | Phone |
| `tpl.media.next` | Next track button | Skip a song from your home screen. | `manual` | `media` next | none (suggests "Add widget" after save) | Media keys | Phone |
| **Work / focus** |||||||| 
| `tpl.work.start` | Start my workday | Your work app is open when you sit down. | `manual` | `wake` → `waitOnline` (5 min) → `launchApp` → `notify` PC "Good morning." | App | MAC; launcher entry | Phone |
| `tpl.work.desk` | Desk tag | Tap the tag on your desk to get going. | `nfc.tap` | `wake` → `waitOnline` (3 min) → `launchApp` | App; write tag | NFC; MAC; launcher entry | Phone |
| **Thermal / health** |||||||| 
| `tpl.health.gpu` | GPU running hot | Know when a game or render is cooking your GPU. | `pc.sensor` GPU temperature above 85 °C for 30 s | `notify` phone "Your GPU has been above 85 °C for 30 seconds." → `notify` PC (same) | Sensor (preselects by kind + name), limit | A temperature sensor on the PC | PC |
| `tpl.health.cpu` | Cool down before damage | Sleeps the PC if the CPU stays dangerously hot. | `pc.sensor` CPU temperature above 95 °C for 60 s | `notify` phone "Your PC was too hot and is going to sleep." → `power` SLEEP (countdown) | Sensor, limit | Temperature sensor | PC |
| `tpl.health.ram` | Memory nearly full | Catch a runaway app before the PC crawls. | `pc.sensor` memory load above 90 % for 2 min | `notify` phone "Your PC's memory has been over 90 % for 2 minutes." | Sensor, limit | Memory load sensor | PC |
| **Privacy / security** |||||||| 
| `tpl.priv.tag` | Tap a tag to lock my PC | Lock up without walking back to the desk. | `nfc.tap` | `power` LOCK → `power` MONITOROFF | Write tag | NFC | Phone |
| `tpl.priv.unlock` | Tell me when my PC unlocks | Know if someone signs in while you're away. | `pc.session` unlocked | `notify` phone "Someone unlocked your PC." | none | none (messages wait up to an hour if your phone is away) | PC |
| **Power saving** |||||||| 
| `tpl.power.sleep` | Sleep my PC when it's idle | Stop paying to power an empty room. | `pc.idle` 30 min | `power` SLEEP (countdown) | none | none | PC |
| `tpl.power.screen` | Screen off when idle | Turn the monitor off without sleeping the PC. | `pc.idle` 10 min | `power` MONITOROFF | none | none | PC |

No v1 template pairs `home.leave` with a PC-targeted step: once the phone has left the LAN the PC is usually unreachable (D6). The two leave-home entries above use PC triggers instead, which is the recommended way to act on the PC when you are gone. A user-built `home.leave` routine with PC steps gets the editor warning in 1.8.

Featured on the empty state: `tpl.home.wake`, `tpl.power.sleep`, `tpl.priv.tag` (without NFC: `tpl.game.night`). `tpl.game.steam` is the example used in the tutorial.

### 4.2 Requirement tokens (gallery "Needs" line)

| Token | Satisfied when | Unsatisfied presentation |
|---|---|---|
| Home network | Home is set | Shown, fixable during setup (1.4) |
| MAC address | Target PC has a MAC saved | Shown, fix links to Connection settings |
| Launcher entry | Target PC's launcher list is known and non-empty | "Connect to your PC to choose an app" |
| Reach away from home | Shown for a user-built `home.leave` routine with PC-targeted steps, unless the phone has reached the PC from away before (`reachableAway`) | Warning icon opens: "Only runs if this PC is reachable away from home (for example over Tailscale)." |
| NFC | Phone has NFC hardware | Template disabled: "This phone has no NFC." |
| Media keys | PC reports it accepts key presses | Warning, reuse `rc_media_unavailable` |
| Sensor | PC's sensor catalog has a matching kind | Template disabled: "Gaming PC doesn't report this sensor." |

### 4.3 3.x candidates (not shipped in v1)

| Idea | Would be |
|---|---|
| Bedtime shutdown | Every night at 01:00 → `notify` PC → `power` SHUTDOWN |
| Morning wake | Weekdays 07:30 → `wake` → `waitOnline` → `launchApp` |
| Weekly restart | Sunday 04:00 → `power` RESTART |
| Screen off at night | Every night at 23:00 → `power` MONITOROFF |
| Break reminder | Every hour during 09:00-17:00 → `notify` PC "Stand up for a minute." |
| Weekend game prep | Friday 18:00 → `wake` → `waitOnline` → `launchApp` |
| Phone away (PC-side trigger) | A new PC-side trigger that fires when the owner's phone link has been gone for N minutes → `power` LOCK. The PC-side answer to "lock the PC when I leave" (D6); needs a link-drop signal that tolerates phone Doze |

---

## 5. Tutorial & FAQ

### 5.1 Android tutorial (extend `TutorialScreen.kt`)

Insert one page into `tutorialPages` (`TutorialScreen.kt:108-161`) directly **before** the "You're All Set!" page (`:147-153`), so the tour ends on the same page as today, followed by battery.

| Field | Value |
|---|---|
| emoji | U+1F501 "clockwise arrows" (every page carries an emoji, `:110-155`) |
| titleRes | `tutorial_routines_title` = "Routines" |
| bodyRes | `tutorial_routines_body` = "RemEx can act on its own when something happens. It can wake your PC when you get home, lock it once nobody is using it, or open Steam when you tap an NFC tag.\n\nOpen %1$s from the More tab and start from a template." |
| bodyArgRes | `R.string.screen_routines_title` (the RemEx-7gwa rule, `:84-89`) |
| illustration | new `TutorialIllustration.ROUTINE`: a trigger node joined by a line to three step nodes, drawn in the existing `DrawScope` style (`:485`) |

### 5.2 PC tutorial (extend `TutorialPage` + `TutorialNavigator`)

`VisiblePages` sorts by author index (`TutorialNavigator.cs:40-42`), so the new page takes author index **16** and "Finish" moves to **17**. Nothing links to 16 today (`PositionOfPage` has no production caller), so renumbering is safe; the empty-state button (P3) becomes its first caller with `PositionOfPage(visible, 16)`.

| Change | Where |
|---|---|
| `new TutorialPage(16, "Routines", "Routines run on this PC even when your phone is away.", PlatformFlags.Windows \| PlatformFlags.Linux)`; Finish becomes 17 | `ShellViewModel.cs:60` |
| New carousel panel before the Finish panel: `Tutorial_P16_Routines_Title`, `Tutorial_P16_Routines_Body`, caption `Tutorial_P16_Routines_Safety` | `ShellView.axaml` before `:1758` |
| `TutorialCarouselSourceScanTests`, `ShellViewModelTutorialPagingTests`, `TutorialNavigatorTests` counts | tests |

Copy:
- Title: "Routines"
- Body: "Routines are set up in the RemEx app on your phone. The ones that start on this PC, like going to sleep after 30 minutes idle, run here even when your phone is away.\n\nYou can see them, switch them off and check their history on the Routines page."
- Caption: "Before a routine shuts down, restarts, signs out, sleeps or hibernates this PC, you get 15 seconds to cancel. Only Run now on this PC skips it, after you confirm."

The Android-flagged page (`PlatformFlags.Android`) is not used here: the Android tutorial is a separate system (5.1).

### 5.3 First-visit walkthrough

| | Android | PC |
|---|---|---|
| When | First time the Routines list shows at least one routine (the empty state already teaches) | First visit to the Routines page |
| Mechanism | `CoachPanel`-style overlay reused from `DashboardCoachOverlay.kt:596-598`, scrim role, `AnimatedVisibility` fade | CanvasView coach pattern (`CanvasView.axaml:457-471`): scrim, curved arrow, 320-wide card, "Got it" (`primary`) |
| Seen state | `routines_coach_seen` in `SettingsManager` (precedent `:140-142`), added to the `SettingsExport` whitelist (precedent `:75`) | `"routines"` in `DashboardProfile.SeenCoachMarks` |
| Replay | Routines overflow "Show tips again" | "?" button in the page header (precedent `CanvasView.axaml:451`) |
| Step 1 | Points at the first card: "Tap a routine to change it. The switch turns it off." | Points at the first card (or the empty-state text): "This page shows the routines that run on this PC. Use the switch to turn one off, or Run now to try it." |
| Step 2 | Points at the pause action: "Pause all stops every routine from running on its own until you resume." | Points at Pause all: "To create or change routines, use the RemEx app on your phone." |
| Buttons | "Skip" / "Next", then "Got it" | "Got it" on the last card |

Also a one-time `RemexTooltip` on the first drag handle in the editor: "Drag to reorder".

### 5.4 FAQ entries (both platforms)

Six new canonical questions, numbered after the existing sixteen on both sides (Android `faq_q17`..`faq_q22`, PC `Faq_Q17`..`Faq_Q22`). Routes are platform-specific per `FAQ-PARITY.md` rule 3.

**17. What are routines?**
- Android: "A routine runs a few RemEx actions for you when something happens. For example, when you get home it can wake your PC, wait until it is online, and open Steam.\n\nEach routine has one trigger and a list of steps. Open the More tab and tap Routines to start from a template or build your own.\n\nRoutines that start on your phone run on your phone. Routines that start from something on your PC, like the PC going idle, run on the PC itself, so they keep working when your phone is not around."
- PC: "A routine runs a few RemEx actions for you when something happens. For example, when this PC has been idle for 30 minutes it can put itself to sleep.\n\nYou create and change routines in the RemEx app on your phone, under More, then Routines. The ones that start from something on this PC run here, even when your phone is not connected. The Routines page shows them, and lets you switch them off, run them and check their history."

**18. Why didn't my routine run?**
- Android: "Open the routine and tap History. Each run lists what happened at every step, and a run that never started says why.\n\nThe usual causes:\n• The routine is switched off, or all routines are paused.\n• Home was not recognised. RemEx knows home by your Wi-Fi network's router and address range, so after changing your router, set home again from the Routines menu.\n• Android is holding RemEx back in the background. If so, the routine shows a card offering to fix it.\n• The PC could not be reached. Wake-on-LAN powers the PC on, but it stops at the sign-in screen, and RemEx only starts once someone signs in.\n• You had left home and the PC could not be reached from outside your network. Routines that act when you leave need a way to reach the PC while you are away, such as Tailscale."
- PC: "On the Routines page, select the routine and look at its history. Each run shows every step, and a run that did not start says why.\n\nThe usual causes are that routines are paused on this PC or on your phone, the routine is switched off, the sensor it watches stopped reporting (for example because HWiNFO was closed), or it was already running. Routines that start on your phone, like arriving home, run on the phone, so check their history in the phone app."

**19. Does RemEx track my location?**
- Android: "No. RemEx never asks for location permission. To know when you are home, it remembers a few details of your home Wi-Fi network, captured while your PC was reachable on it: the router's address, the address range and the DNS settings. When your phone joins a network with the same details, you are home. When it leaves, you have left.\n\nThese details stay on your phone and are not included when you export routines. You can forget them at any time from the Routines menu, under Home network."
- PC: "No. The phone app works out when you arrive or leave by recognising your home Wi-Fi network, without location permission. This PC is never told where your phone is, only that a routine ran."

**20. Can a routine shut down my PC while I'm using it?**
- Android: "Not without warning. Whenever a routine is about to shut down, restart, sign out, sleep or hibernate the PC, even one you started from your phone, the PC shows a 15 second countdown with a Cancel button first. If your phone is connected, you can also cancel from the notification. The only exception is Run now on the PC itself, which asks the person at the PC to confirm instead.\n\nTo stop everything at once, use Pause all at the top of the Routines screen. Nothing runs on its own until you resume."
- PC: "Not without warning. Before a routine shuts down, restarts, signs out of, sleeps or hibernates this PC, a 15 second countdown appears with a Cancel button, even when the routine was started from a phone. Click Cancel, or press Esc or Enter, to stop it. Run now on the Routines page asks you to confirm instead, and then does not count down.\n\nTo stop all routines on this PC, turn on Pause all on the Routines page."

**21. Can I edit routines on my PC?**
- Android: "No. Routines are created and changed on your phone. Each PC receives only the routines that start on it, checks them again, and keeps running them on its own. On the PC you can switch a routine off, run it now and see its history."
- PC: "No. You create and change routines in the RemEx app on your phone, and it sends this PC the ones that start here. On this PC you can switch a routine off, run it now and see its history."

**22. How do NFC tags work with routines?**
- Android: "Any writable NFC sticker or tag works. In a routine, choose I tap an NFC tag as the trigger, tap Write tag, and hold your phone against the tag until it buzzes. After that, tapping the tag with your unlocked phone runs the routine.\n\nOther phones can't run your routines from the tag. If you write a tag that already runs another routine, RemEx asks before replacing it."
- PC: "NFC tags are a phone feature. On your phone, a routine can start when you tap an NFC tag, and its steps can then control this PC."

### 5.5 `docs/FAQ-PARITY.md` update

Change "Both platforms must answer all sixteen" to "all twenty-two", and append:

| # | Question | PC | Android |
|---|----------|----|---------|
| 17 | What are routines? | Faq_Q17 | faq_q17 |
| 18 | Why didn't my routine run? | Faq_Q18 | faq_q18 |
| 19 | Does RemEx track my location? | Faq_Q19 | faq_q19 |
| 20 | Can a routine shut down my PC while I'm using it? | Faq_Q20 | faq_q20 |
| 21 | Can I edit routines on my PC? | Faq_Q21 | faq_q21 |
| 22 | How do NFC tags work with routines? | Faq_Q22 | faq_q22 |

Counts raised in both places: `AboutViewModel.cs:311` loop bound 16 → 22; `FaqScreen.kt:79` list gains six `FaqItem`s. All 9 locale files on each platform.

---

## 6. Data model & schema

### 6.1 Entities

| Entity | Owner | Synced to host? | Stored where |
|---|---|---|---|
| `RoutineSet` (schema version + list of `Routine`) | Phone (sole editor) | Only the subset whose trigger is `pc.*` and whose `hostIdentity` equals that host | Phone: encrypted DataStore. Host: `routines.json` (per owning phone `clientId`) |
| `Routine` | Phone | As above | As above |
| `Home` (network fingerprint) | Phone | Never | Phone encrypted DataStore |
| `RoutineSecrets` (NFC tokens, shortcut HMAC key) | Phone | Never | Separate phone encrypted DataStore |
| `RoutineRun` (history record) | Whoever ran it | Host runs are reported to the owning phone (§7.3.6) | Phone: encrypted DataStore. Host: `routine_runs.json` |
| `QueuedNotify` | Host | Delivered to owning phone | Host: `routine_notify_queue.json` |
| `HostRoutineOverrides` (PC-side disable flags, block flag, owner-absent suspension) | Host | Reported in sync result | Host: inside `routines.json` |

### 6.2 `Routine` fields

All wire fields are optional at the JSON layer (no `required`), exactly as `PhoneThemeSnapshot` does it (`PhoneThemeSnapshot.cs:27-36`). Validity is enforced by the validator (§6.5), never by the deserializer.

| Field | Type | Constraint | Notes |
|---|---|---|---|
| `id` | string | Lower-case UUID v4, 36 chars, unique within the phone's set | Generated on the phone; never reused after delete |
| `name` | string | 1–40 user-perceived characters after trim; no C0/C1 control characters; no line breaks | Shown on PC countdown, notifications, history |
| `hostIdentity` | string | Exactly 16 lower-case hex chars | `HostIdentity.keyFor(spkiPin)` (Kotlin, `security/HostIdentity.kt`); host recomputes from its own SPKI (C# port, §6.12) |
| `enabled` | bool | — | Phone-side toggle. Gates automatic triggers and shortcut, widget and NFC runs (`skipped_disabled`); in-app Run, Test and PC Run now still work |
| `revision` | int64 | ≥ 1; +1 on every save of this routine | Drives "Edited since this run" and the PC's "updated" line (UX need 16); independent of the per-PC sync revision (§7.4) |
| `appearance` | object? | `icon` ≤ 32 chars `[a-z0-9_]`; `color` `#RRGGBB` | Token set is owned by the UX half; host treats as opaque display data |
| `trigger` | `RoutineTrigger` | See §6.3 | Exactly one |
| `steps` | `RoutineStep[]` | 1–12 entries | Ordered |
| `createdAtUnixMs` | int64 | ≥ 0 | Display only, never for ordering |
| `updatedAtUnixMs` | int64 | ≥ `createdAtUnixMs` | Display only |

### 6.3 Triggers (flat record, discriminated by `type`)

`RoutineTrigger` is one flat record with optional fields, not a polymorphic JSON type: source-generated polymorphism throws on an unknown discriminator, and a throw inside `RemexMessage` deserialization drops the whole session (`PhoneThemeSnapshot.cs:28-35`). An unknown `type` therefore deserializes fine and is rejected by the validator with `unsupported_trigger`.

| `type` | Runs on | Allowed fields (all others must be absent → `field_not_allowed`) | Constraints / defaults |
|---|---|---|---|
| `home.arrive` | Phone | `homeId` | `homeId` references an existing `Home`. Settle 10 s (fixed) |
| `home.leave` | Phone | `homeId`, `leaveDebounceSeconds` | Debounce 60–1800, default 180 |
| `nfc.tap` | Phone | none | Token lives in `RoutineSecrets`, never in the routine |
| `manual` | Phone | none | Surfaces: in-app Run, pinned shortcut, home-screen widget |
| `pc.sensor` | Host | `sensorId`, `sensorLabel`, `direction`, `threshold`, `sustainSeconds` | `sensorId` 1–128 chars = `SensorReading.Id` (`TelemetryPayload.cs:39`); `sensorLabel` ≤ 64 (display snapshot); `direction` `above`/`below`; `threshold` finite double; `sustainSeconds` 5–600, default 60 |
| `pc.idle` | Host | `idleMinutes`, `ignoreWhileMediaPlaying` | `idleMinutes` 1–240; `ignoreWhileMediaPlaying` default `true` |
| `pc.session` | Host | `sessionState` | `locked` or `unlocked` |

Schedule triggers are deferred to 3.x and are not in the v1 enum.

### 6.4 Steps (flat record, discriminated by `type`)

| `type` | Phone-run routine | Host-run routine | Allowed fields | Constraints |
|---|---|---|---|---|
| `wake` | Yes (phone-native WoL, `TileCommand.kt:124` path) | **No** → `step_not_allowed_on_pc` | `mac`, `broadcastIp`, `port` | `mac` 6 bytes, stored upper-case colon form, required (`wake_no_mac` when the PC has none saved); `broadcastIp` IPv4 dotted quad, default `255.255.255.255`; `port` 1–65535, default 9. Max 1 per routine |
| `waitOnline` | Yes | **No** → `step_not_allowed_on_pc` | `timeoutSeconds` | 30–300, default 300. Max 2 per routine |
| `delay` | Yes | Yes | `seconds` | 1–600 |
| `power` | Yes (executed by host via `routine_step_request`) | Yes (in-process) | `verb`, `delaySeconds` | `verb` ∈ the shared verbs except `WAKEONLAN` (10 verbs). `WAKEONLAN` is reserved in v1 and rejected with `invalid_field` (D5): the `wake` step covers waking the PC, and waking a third machine is a later power-user feature. `delaySeconds` 0–600, only for `SHUTDOWN FORCESHUTDOWN RESTART FORCERESTART RESTARTTOUEFI` |
| `launchApp` | Yes (via host) | Yes | `appId`, `appLabel` | `appId` = `AppEntry.Id` GUID; `appLabel` ≤ 64 display snapshot. Host resolves `appId` → `TargetPath` at run time |
| `media` | Yes (via host) | Yes | `mediaAction` | `playPause`, `next`, `previous` |
| `notify` | Yes | Yes | `target`, `title`, `body` | `target` `phone`/`pc`; `title` 1–40; `body` 0–120; plain text only, control chars stripped. Max 3 per routine |

**Destructive verbs** (`SHUTDOWN FORCESHUTDOWN RESTART FORCERESTART RESTARTTOUEFI SIGNOUT SLEEP HIBERNATE`; D1): at most one per routine (`too_many_destructive`). In a PC-run routine it must be the last step (`destructive_not_last`): nothing can run after the host process is gone or suspended. In a phone-run routine steps may follow it; a host-executed step after it that is not preceded by a `waitOnline` placed after the destructive step is skipped at run with `after_power_off` (editor warning, §1.8). `LOCK` and `MONITOROFF` are not destructive and never count down.

### 6.5 Cross-field rules and limits (`RoutineLimits`, one table in core, mirrored in Kotlin)

| Rule | Value | Reason code on violation |
|---|---|---|
| Routines per phone (all PCs) | ≤ 32 | `too_many_routines` |
| PC-triggered routines per phone per PC | ≤ 16 | `too_many_routines` |
| Homes per phone | 1 in v1 (§1.4, D9) | `too_many_homes` |
| Steps per routine | 1–12 | `too_many_steps` / `invalid_field` |
| Phone-run static time budget: Σ`delay.seconds` + Σ`waitOnline.timeoutSeconds` + 60 s per host-executed step + 15 s if a destructive step exists | ≤ 540 s (fits a 10-minute Android job window with margin) | `budget_exceeded` |
| Host-run static time budget: Σ`delay.seconds` + 30 s per step + 15 s countdown | ≤ 1800 s | `budget_exceeded` |
| Phone-run routine containing a host-executed step with no preceding `waitOnline` and no connection guarantee | Allowed; at run it connects on demand and fails `pc_unreachable` if the PC is off | — |
| `pc.*` trigger combined with `wake`/`waitOnline` | Forbidden | `step_not_allowed_on_pc` |
| `routines_sync` serialized size | ≤ 64 KiB (matches the 8338 frame bound philosophy, `RemexNetworkListener.cs:59`) | `payload_too_large` |
| Duplicate `id` in one set | Forbidden | `duplicate_id` |
| Unknown `schemaVersion` greater than the reader's | Read-only on phone; whole sync rejected on host | `schema_too_new` |

### 6.6 JSON shape (wire and phone store use the same routine shape)

```json
{
  "schemaVersion": 1,
  "routines": [
    {
      "id": "3f0c2a4e-7b1d-4c55-9a60-2d7e8f1b9c10",
      "name": "Game time",
      "hostIdentity": "9f2c4be07a1d33e5",
      "enabled": true,
      "appearance": { "icon": "sports_esports", "color": "#6750A4" },
      "trigger": { "type": "home.arrive", "homeId": "b7e14c0a-52c1-4e4f-8f43-0d7c2f9a6e11" },
      "steps": [
        { "type": "wake", "mac": "0A:1B:2C:3D:4E:5F", "broadcastIp": "192.168.1.255", "port": 9 },
        { "type": "waitOnline", "timeoutSeconds": 120 },
        { "type": "launchApp", "appId": "c1d2e3f4-0000-4000-8000-00000000abcd", "appLabel": "Steam" },
        { "type": "notify", "target": "phone", "title": "PC ready", "body": "Steam is starting." }
      ],
      "createdAtUnixMs": 1790000000000,
      "updatedAtUnixMs": 1790000000000
    },
    {
      "id": "5a9d0f21-1c3e-4b7a-8e2f-6c4d3b2a1f00",
      "name": "Bedtime",
      "hostIdentity": "9f2c4be07a1d33e5",
      "enabled": true,
      "trigger": { "type": "pc.idle", "idleMinutes": 45, "ignoreWhileMediaPlaying": true },
      "steps": [
        { "type": "notify", "target": "phone", "title": "PC going to sleep", "body": "Idle for 45 minutes." },
        { "type": "power", "verb": "SLEEP" }
      ],
      "createdAtUnixMs": 1790000000000,
      "updatedAtUnixMs": 1790000500000
    }
  ]
}
```

`Home` (phone only, one per phone in v1, never exported, never synced, never backed up):

| Field | Type | Constraint |
|---|---|---|
| `id` | string | UUID v4 |
| `capturedWithHostIdentity` | string | The PC that was reachable when it was captured (16 hex) |
| `capturedAtUnixMs` | int64 | — |
| `gateways` | string[] | 1–4 default-route gateways, normalized (IPv4 dotted quad, IPv6 compressed lower-case) |
| `prefixes` | string[] | 1–8 on-link route prefixes, CIDR |
| `dnsServers` | string[] | 0–4 |
| `domain` | string? | Search domain, lower-case |
| `dhcpServer` | string? | DHCP server address |
| `ipv6Prefix48` | string? | Global IPv6 prefix truncated to /48 |

The facts are shown to the user at capture and on the trigger card ("Home: router 192.168.1.1", §1.4: trust is the feature), so they are stored as normalized values inside the AEAD-encrypted store rather than as one-way tokens (D9). They never leave the phone: they are excluded from export (§6.10), from backup and device transfer (R-SEC-10) and from logs (T11).

### 6.7 Schema version and migration

| Item | Rule |
|---|---|
| Current version | `RoutineSchema.CurrentVersion = 1` in `remex.core/Routines/RoutineSchema.cs`; Kotlin `RoutineSchema.CURRENT_VERSION = 1` |
| Migration entry | `RoutineMigration.Migrate(RoutineSet?) → (RoutineSet, warning?)` in core, modelled on `CustomizationMigration.Migrate` (`remex.desktop/Services/CustomizationMigration.cs:83-104`): arms run in order against the version the document arrived with; the final stamp happens once |
| Newer than reader | Never stamped down (`CustomizationMigration.cs:92` precedent). Phone shows it but blocks editing, with a "made by a newer RemEx" state; host rejects the whole sync with `schema_too_new` |
| Unknown trigger/step type in a supported version | Kept verbatim in the phone store (so a downgrade→upgrade round trip loses nothing); routine is marked invalid and cannot run (`unsupported_trigger` / `unsupported_step`) |
| Fallback documents | A store that failed to decrypt or parse is never written back as an empty or default document (`REGRESSION-GUARDS.md:694-705` rule). The phone surfaces "Routines could not be read" with Export-raw and Reset actions; the host keeps the unreadable file as `routines.json.unreadable-<utc>` and starts empty with a PC-visible warning |
| Version bumps | Any additive field that an older validator would reject with `field_not_allowed` requires `CurrentVersion + 1` and a migration arm, even if the arm is a pure stamp |

### 6.8 Phone storage (encrypted at rest)

| Store (Preferences DataStore name) | Content | Encryption |
|---|---|---|
| `remex_routines` | One key `doc` = AEAD(`RoutineStoreDocument` JSON): `schemaVersion`, `pausedAll`, `routines[]` (list order is the user's order, R-UX-18), `home?`, `hostSync{hostIdentity → {localRevision, ackedRevision, lastResultJson, runCursor, reachableAwayAtUnixMs}}` | Tink AES-256-GCM, associated data `"remex_routines/doc/v1"` |
| `remex_routine_secrets` | `nfcTokens{routineId → token}`, `shortcutKey` | Same AEAD, associated data `"remex_routine_secrets/<key>"` |
| `remex_routine_history` | One key per routine (`run/<routineId>`), each an AEAD blob of that routine's retained runs (§8.8), so a write rewrites one routine's history, not all of it | Same AEAD |

- Keyset: a **separate** Tink keyset `remex_routines_keyset` in SharedPreferences `remex_routines_tink_prefs`, sealed by Android Keystore key `android-keystore://remex_routines_key`, built with `AesGcmKeyManager.aes256GcmTemplate()` like `PinnedHostStore.buildAead` (`security/PinnedHostStore.kt:158-161`). `PinnedHostStore` itself is **not** modified (security-guarded, `REGRESSION-GUARDS.md:986-1020`).
- Corruption recovery mirrors `REGRESSION-GUARDS.md:986-991` (clear keyset + the three stores, retry) **but is never silent**: a recovery sets `RoutineStoreHealth.ResetAfterKeyLoss` which the routines screen shows until dismissed, and history gets a synthetic `store_reset` entry.
- The whole set is one AEAD blob so every edit is one transactional DataStore write; size cap 256 KiB.
- All three stores and the new Tink prefs file are excluded from cloud backup and device transfer (R-SEC-10).

### 6.9 Host storage

| File | Directory | Content | Write |
|---|---|---|---|
| `routines.json` | Host state directory: Windows `C:\ProgramData\RemEx` (`RemexDataPaths.cs:91`), Linux `~/.local/share/Remex` (`RemexDataPaths.cs:121`) | `{ "fileVersion": 1, "hostPaused": bool, "owners": { "<clientId>": { "revision": int64, "contentHash": "sha256-b64", "receivedAtUnixMs": int64, "lastSeenUnixMs": int64, "paused": bool, "blockedByPc": bool, "pcDisabled": ["<routineId>"], "routines": [Routine] } } }` | `RemexDataPaths.WriteAllTextAtomicAsync` (`:292`), then the same restrictive permission step `paired_clients.json` uses (Windows ACL LocalSystem + Administrators, inheritance off; Linux `0600`). `SweepStagingOrphans` at startup |
| `routine_runs.json` | Same | `{ "fileVersion": 1, "nextSeq": int64, "runs": [RoutineRun] }` retention per §8.8 | Same helper; written at run start (state `running`) and at run end |
| `routine_notify_queue.json` | Same | `{ "fileVersion": 1, "items": [QueuedNotify] }` ≤ 20 per owner, 1 h expiry | Same helper |

The ACL matters: `remex.agent` is elevated on Windows, so a routine is a way to make the elevated process act. A medium-integrity process of the same user must not be able to plant one (R-SEC-12). On Linux the agent runs as the user, so `0600` is the full protection available and equal to what any same-user process can already do.

### 6.10 Export / import file (`.remexroutines`)

Precedent: versioned exchange formats `PaletteExchange` (`remex.desktop/Services/PaletteExchange.cs:34`, `:65`) and the whitelist rule of `SettingsExport` (`data/SettingsExport.kt:6-16`).

```json
{
  "format": "remex.routines",
  "formatVersion": 1,
  "schemaVersion": 1,
  "exportedAtUnixMs": 1790000000000,
  "exportedBy": "RemEx Android 3.0.0",
  "routines": [ { "...Routine minus the excluded fields..." } ]
}
```

| Field class | Exported? | On import |
|---|---|---|
| `id` | Yes | Replaced with a new UUID (no collision with an existing set) |
| `name`, `appearance`, `trigger` params (except `homeId`), `steps` params (except `mac`) | Yes | Kept |
| `hostIdentity` | **No** | User picks the PC; routine is bound to it |
| `homeId` | **No** | Routine imported as "needs a home" and disabled |
| `wake.mac` | **No** (hardware identifier) | Filled from the chosen PC's reported MAC (`HostCapabilities.MacAddress`, `HostCapabilities.cs:96`) or left "needs setup" |
| NFC token, home facts, keys | **Never** | NFC routines imported "needs a tag written"; home routines "needs a home" |
| `enabled` | No | Every imported routine starts disabled |

Import runs the full validator; any invalid routine is listed with its reason and skipped. `formatVersion` newer than supported → refused with a visible message.

### 6.11 Reason codes

Reason codes are snake_case string constants in `remex.core/Routines/RoutineReasonCodes.cs` with a Kotlin mirror `RoutineReasonCodes.kt`. The full catalog with user-visible states is §10.1. Every code maps to a localized string on both platforms: PC resx key `Routine_Reason_<code>`, Android `routine_reason_<code>`, in all 9 locale files.

### 6.12 Code placement

| Concern | Location |
|---|---|
| Models, validator, limits, reason codes, migration, schema constant | `remex.core/Routines/` (NativeAOT-safe: no reflection, string switches, source-gen JSON) |
| Wire payload records + `MessageTypes` constants + `[JsonSerializable]` entries | `remex.core/Messages/Routines/`, `remex.core/Messages/RemexMessage.cs` (after `:403`, `:633`), `remex.core/Serialization/RemexJsonSerializerContext.cs` |
| `HostIdentity.KeyFor(string spkiPin)` C# port | `remex.core/Security/HostIdentity.cs` (vector-tested against Kotlin `HostIdentityTest`) |
| `SensorThreshold.IsLive(direction, threshold, value, wasLive)` | Moved from `SensorViewModel.IsAlertLive` (`remex.desktop/ViewModels/SensorViewModel.cs:482-488`) into `remex.core/Models/SensorAlert.cs`; `SensorViewModel` delegates. Single caller today (`:450`) |
| Host runner, stores, trigger sources, step executor, countdown | `remex.agent/Services/Routines/` (Windows sources `[SupportedOSPlatform("windows")]`, Linux sources `[SupportedOSPlatform("linux")]`) |
| PC routines page (no editing: enable toggle, Run now, Pause all, history), countdown window | `remex.desktop/ViewModels/Routines*`, `remex.desktop/Views/Routines*` |
| Kotlin mirror (pure JVM, no Android types) | `remex.android/.../routines/model/` |
| Phone runner, triggers, store, sync | `remex.android/.../routines/` |

---

## 7. Protocol

All new messages travel on the existing authenticated `/ws` control channel as `RemexMessage` envelopes (`RemexMessage.cs:53-60`), camelCase JSON, `protocolVersion` stays **2** (`REGRESSION-GUARDS.md:396-401`). Every payload field is optional at the JSON layer. Handlers are pairing-gated exactly like `theme_sync` (`PingPongHandler.cs:614`, "authenticated, not connected", `RemexClientManager.kt:98-129`). No handler may let an exception escape into the receive loop (the `IsValidThemeSync` lesson, `PingPongHandler.cs:1480-1512`).

### 7.1 Message inventory

| Type | Direction | Envelope slot | Purpose |
|---|---|---|---|
| `routines_sync` | phone → host | `routinesSync` | Full set of this phone's PC-triggered routines for this PC + revision + pause flag + run cursor |
| `routine_sync_result` | host → phone | `routineSyncResult` | Per-routine accept/reject + host state |
| `routine_step_request` | phone → host | `routineStepRequest` | Execute one host-side step of a phone-run routine |
| `routine_step_result` | host → phone | `routineStepResult` | Outcome of that step |
| `routine_notify` | host → phone | `routineNotify` | A `notify(phone)` step from a host-run routine, or a countdown heads-up |
| `routine_notify_ack` | phone → host | `routineNotifyAck` | Dequeues delivered notifications |
| `routine_run_report` | host → phone | `routineRunReport` | Host-run history records the phone has not seen, and live progress of running host runs |
| `routine_cancel` | phone → host | `routineCancel` | Cancel a host countdown / run owned by this phone |
| `routine_run_request` | phone → host | `routineRunRequest` | Run or test one of this phone's PC-run routines now, from the phone (UX need 8) |

Naming rule: **every host → phone type starts with `routine_`**, so one prefix forward in the native router covers them all (§7.7). `routines_sync` (the locked id) is phone → host and never needs routing. The reply is therefore `routine_sync_result`, not `routines_sync_result`.

### 7.2 Envelope additions (`RemexMessage`)

| Property | JSON | Type |
|---|---|---|
| `RoutinesSync` | `routinesSync` | `RoutinesSyncPayload?` |
| `RoutineSyncResult` | `routineSyncResult` | `RoutineSyncResultPayload?` |
| `RoutineStepRequest` | `routineStepRequest` | `RoutineStepRequestPayload?` |
| `RoutineStepResult` | `routineStepResult` | `RoutineStepResultPayload?` |
| `RoutineNotify` | `routineNotify` | `RoutineNotifyPayload?` |
| `RoutineNotifyAck` | `routineNotifyAck` | `RoutineNotifyAckPayload?` |
| `RoutineRunReport` | `routineRunReport` | `RoutineRunReportPayload?` |
| `RoutineCancel` | `routineCancel` | `RoutineCancelPayload?` |
| `RoutineRunRequest` | `routineRunRequest` | `RoutineRunRequestPayload?` |

### 7.3 Message contracts (API_CONTRACTS style)

These sections are appended to `docs/API_CONTRACTS.md` as a new section 8, "Routines", in the first protocol slice.

#### 7.3.1 `routines_sync` (phone → host)

Sent (a) once after the connection is authenticated **and** `host_info` shows `supportsRoutines = true`, (b) after every local edit that changes this host's PC-triggered subset or the pause flag (coalesced to ≤ 1 per 2 s), (c) as the forget-PC flush (§7.4.5).

| Field | Type | Description |
|---|---|---|
| `schemaVersion` | int | Phone's routine schema version |
| `revision` | int64 | Phone's monotonic revision **for this host** (starts at 1) |
| `paused` | bool | Phone-side Pause all (D4): pauses this owner's automatic routines on this PC; the PC shows "Paused from <phone>" |
| `routines` | `Routine[]` | The **full** PC-triggered subset for this host (0–16). Full state, not a delta |
| `runCursor` | int64 | Highest host run `seq` the phone has stored for this host (0 = none) |
| `forget` | bool | `true` only in the forget-PC flush; host deletes this owner's state and replies |
| `sentAtUnixMs` | int64 | Display only |

Errors: the host never closes the socket for a bad `routines_sync`. Every failure is a `routine_sync_result` with `status ≠ ok`.

#### 7.3.2 `routine_sync_result` (host → phone)

| Field | Type | Description |
|---|---|---|
| `revision` | int64 | Echo of the request revision |
| `storedRevision` | int64 | Revision the host now holds for this owner |
| `status` | string | `ok`, `partial`, `stale_revision`, `revision_conflict`, `schema_too_new`, `payload_too_large`, `blocked_by_pc`, `rate_limited`, `internal_error` |
| `results` | `{routineId, accepted: bool, reasonCode, detail?}[]` | One per routine in the request (empty unless `ok`/`partial`) |
| `hostPaused` | bool | PC-side Pause all (PC-wide, all owners) |
| `ownerPaused` | bool | This phone's `paused` as the PC applied it |
| `unsolicited` | bool | `true` when the PC sends this without a request because PC-side state changed (enable toggle, block, Pause all); `revision` then equals `storedRevision` |
| `pcDisabled` | string[] | Routine ids disabled on the PC by the PC user |
| `ownerSuspended` | string? | `owner_absent` when the set was auto-suspended (§7.4.4), else null |
| `idleSource` | string? | Current idle source id (§8.5.2) or null = unavailable |
| `sessionSource` | string? | Current session source id (§8.5.3) or null |
| `sensorTrigger` | bool | Telemetry available on this host |

#### 7.3.3 `routine_step_request` (phone → host)

| Field | Type | Description |
|---|---|---|
| `runId` | string | UUID of the phone run |
| `routineId` | string | Routine id |
| `routineName` | string | ≤ 40, for the PC countdown and notification text |
| `triggerType` | string | Trigger id of the run (+ `manual` for test runs) |
| `stepIndex` | int | 0–11 |
| `step` | `RoutineStep` | The step to execute: `power`, `launchApp`, `media`, or `notify` with `target = pc` |
| `testRun` | bool | `true` for an in-app Test (D7). The host enforces it: a destructive verb is never executed for such a request; the countdown runs and the result is `simulated` |
| `source` | string | Run source (§8.8), e.g. `manual.app`, `nfc.tap`, `home.arrive`; history and display only |

Host behaviour: validate the step with the same validator and the current allowlist/verb table; destructive verbs go through the countdown (§8.6), and with `testRun = true` the countdown runs but the verb is never issued (result `simulated`); idempotency key `(clientId, runId, stepIndex)` cached 10 min — a duplicate returns the cached result, or `in_progress` if still running. Rate limit 60 requests/min per client → `rate_limited`.

#### 7.3.4 `routine_step_result` (host → phone)

| Field | Type | Description |
|---|---|---|
| `runId`, `stepIndex` | string, int | Correlation |
| `outcome` | string | `succeeded`, `failed`, `cancelled`, `simulated`, `in_progress` |
| `reasonCode` | string | §10.1; `ok` on success |
| `countdownShown` | bool | Whether the PC countdown surface was actually displayed |
| `cancelledBy` | string? | `pc`, `phone`, `pause` |
| `detail` | string? | ≤ 120 chars, English diagnostic, already redacted; the phone shows the localized reason, not this |

For destructive verbs the host sends `succeeded` **immediately before** invoking `SharedCommandVerbs.TryExecuteAsync`, because the socket dies with the machine. `succeeded` therefore means "verb issued after the countdown".

#### 7.3.5 `routine_notify` (host → phone) and `routine_notify_ack` (phone → host)

| Field (`routine_notify`) | Type | Description |
|---|---|---|
| `notifyId` | string | UUID; phone de-duplicates the last 200 ids |
| `kind` | string | `step` (a `notify(phone)` step) or `countdown` (heads-up for a destructive host step) |
| `routineId`, `routineName`, `runId` | string | Correlation and display |
| `title`, `body` | string | Phone-authored text echoed back; ≤ 40 / ≤ 160 |
| `countdownEndsAtUnixMs` | int64? | Only for `kind = countdown`; the phone shows a Cancel action that sends `routine_cancel` |
| `queuedAtUnixMs`, `expiresAtUnixMs` | int64 | `expires = queued + 3600000` |

`routine_notify_ack`: `{ notifyIds: string[] }`. Delivery is at-least-once: live via `ClientSessionRegistry.TrySendAsync` (`ClientSessionRegistry.cs:219`) when the owner is authenticated, otherwise queued (≤ 20 per owner, oldest dropped with history `notify_expired`). Queue flush happens right after the `routine_sync_result` of a new connection. An item is removed only on ack or expiry. A `countdown` notify is never queued (it is meaningless after 15 s). Every notify is also shown on the PC through `NotificationRouter.Route` (`remex.desktop/Services/NotificationRouter.cs:55`), importance `Outcome`.

#### 7.3.6 `routine_run_report` (host → phone)

| Field | Type | Description |
|---|---|---|
| `runs` | `RoutineRun[]` | Host runs of this owner's routines with `seq > runCursor`, oldest first, ≤ 50 per message |
| `more` | bool | More pages follow |
| `live` | bool | `true` for an in-progress update of one running host run (step started or finished, countdown started or cancelled; UX need 13). It carries the full current record; the phone upserts by `runId` and does not advance `runCursor` for it |

Sent after `routine_sync_result` on connect (using the request's `runCursor`), when a host run ends while the owner is connected, and as `live` updates at every step transition of a running host run. `seq` is the record's last-modified sequence: a record that changes after it ended (a queued notify expiring) is re-sent with a new `seq`, and the phone upserts by `runId`. The phone advances `runCursor` only after storing the page.

#### 7.3.7 `routine_cancel` (phone → host)

`{ runId: string, reason: "user" | "pause" }`. Only runs whose owner `clientId` equals the sender are cancellable; anything else is ignored and logged. Cancelling during a countdown aborts the destructive step (`cancelled_on_phone`). Cancelling a running host run stops it before its next step.

#### 7.3.8 `routine_run_request` (phone → host)

| Field | Type | Description |
|---|---|---|
| `runId` | string | UUID chosen by the phone; deduplicated for 10 min |
| `routineId` | string | One of the sender's stored PC-run routines |
| `testRun` | bool | D7: non-destructive steps run for real, destructive ones are simulated |
| `source` | string | `manual.app` |

The PC runs its own stored copy; the phone cannot inject a definition this way (T24). It is a normal PC run, subject to single-flight, PC Pause all, block and rate limits. Destructive steps count down, because a phone request is never presence at the PC (D1). Progress reaches the phone as live `routine_run_report` updates and the final record arrives as a normal report. A request that cannot start is answered with a live report whose record has `outcome: skipped` and a reason (`routine_not_found`, `disabled_on_pc`, `paused_on_pc`, `blocked_by_pc`, `already_running`, `rate_limited`). This is the only way a PC-run routine starts from the phone.

### 7.4 Sync algorithm

#### 7.4.1 Phone side

1. Every edit that touches host H's PC-triggered subset (add, change, delete, enable toggle, or the pause flag) increments `hostSync[H].localRevision` in the **same** DataStore transaction as the edit.
2. When connected and authenticated to H and `supportsRoutines`, send `routines_sync` with `revision = localRevision`.
3. On `routine_sync_result`:
   - `ok`/`partial` with `revision == localRevision`: set `ackedRevision`, store per-routine results (rejected routines show "Not active on this PC: <reason>").
   - `stale_revision` (host holds a higher revision, e.g. the phone store was reset): set `localRevision = storedRevision + 1`, resend the full set once. The phone is authoritative; this is how it reclaims the host copy.
   - `revision_conflict` (same revision, different content — a phone bug): `localRevision + 1`, resend once, log.
   - `schema_too_new`, `payload_too_large`, `blocked_by_pc`: stop, surface state on the routines screen.
   - No reply within 10 s: retry at next connect; the routine rows show "Waiting to sync".
4. `ackedRevision < localRevision` is always visible as "Not yet on the PC" per affected routine.
5. **Sync state per routine** (UX need 5), derived on the phone: `pending` (`ackedRevision < localRevision`, or never sent), `synced` (accepted in the last result), `rejected` (with the validation code, shown through `rejected_by_pc`), `disabledOnPc` (its id is in `pcDisabled`). Set-level states: `pausedOnPc` (`hostPaused`), `blockedByPc`, `ownerSuspended`.
6. **Enable toggle conflict rule.** The phone's `enabled` and the PC's `pcDisabled` are independent flags; a routine is armed only when it is enabled on the phone **and** not disabled on the PC. Neither side ever writes the other's flag. The phone shows "Off on <PC>" with the PC's state, and only the PC can clear its own override. A PC-side change reaches a connected phone at once as an unsolicited `routine_sync_result` (R-UX-37).

#### 7.4.2 Host side

1. Reject when not authenticated (silently dropped by the pairing gate, same as `theme_sync`).
2. Size and rate checks (≤ 64 KiB; ≤ 1 per 2 s per client, excess coalesced to the latest).
3. `blockedByPc` → `blocked_by_pc`.
4. `schemaVersion > RoutineSchema.CurrentVersion` → `schema_too_new`.
5. `forget = true` → delete owner state, runs, and queue; reply `ok` with empty results.
6. `revision < stored` → `stale_revision` (includes `storedRevision`). `revision == stored` and `contentHash` equal → resend the stored result (idempotent). `revision == stored`, hash differs → `revision_conflict`.
7. `revision > stored` → validate each routine (full validator + host checks: trigger is `pc.*`, `hostIdentity == HostIdentity.KeyFor(own SPKI)`, verbs in `routinePowerVerbs`, `appId` present in `launchers.json` and passing `IsRejectedNetworkPath`, idle/session source available for `pc.idle`/`pc.session`). Accepted routines replace the owner's set; rejected ones are **not** stored (a previously stored version of a now-rejected routine is removed, so the PC never runs a definition the phone no longer holds).
8. Persist atomically **before** replying; on write failure reply `internal_error` and keep the old in-memory set (never claim `ok` for an unsaved set).
9. Reply `ok` (all accepted) or `partial`, then flush queued `routine_notify`, then `routine_run_report` pages.
10. The request's `paused` flag is applied to this owner (§8.7) whether or not the set changed; a pause-only change still bumps the phone's revision (§7.4.1 step 1). Later PC-side changes (enable toggle, block, PC Pause all) reach a connected owner as an unsolicited `routine_sync_result`.

#### 7.4.3 Idempotency and ordering

- Full-state sync + revision makes every retry safe; the host never applies an older set after a newer one.
- Replay by a network attacker is impossible (TLS + pinned SPKI + proof-of-possession reconnect, `REGRESSION-GUARDS.md:1066-1070`); the revision exists to order the phone's own messages across reconnects and queued sends, and to detect phone-side store resets.

#### 7.4.4 Multiple phones on one PC

- Host state is keyed by `clientId` (the paired device), never merged. Phone A never receives phone B's routines, runs, or notifications. `routine_cancel` only acts on the sender's runs.
- The PC UI groups routines by phone name (`PairedClientNameStore`).
- Host-wide limits: 4 concurrent runs; one destructive countdown at a time across all owners (`conflict_countdown_active` for the loser); 30 routine runs per owner per hour (`rate_limited`).
- **Owner absent:** if an owner has not authenticated for 30 days (`lastSeenUnixMs`), its set is suspended (runs recorded as skipped `owner_absent`), the PC UI says so, and the next sync from that phone lifts the suspension.

#### 7.4.5 One phone with several PCs

- Phone state is keyed by `HostIdentity` (`hostSync[hostIdentity]`); each host gets only its own `pc.*` subset.
- Phone-run routines are bound to a `hostIdentity` too. Because the native client connects only to the selected PC (`RemexClientManager.kt:867`), a phone-run routine whose host-executed step targets a PC other than the currently selected one fails with `pc_not_selected`, and the failure notification offers **Switch to <PC> and run** (user presence). `wake` still works for any PC (it only needs the stored MAC).
- **Forget/unpair PC on the phone:** if connected to that PC, send `routines_sync{forget:true}` and wait ≤ 3 s for the result before clearing pins; then delete every routine with that `hostIdentity`, its history, its `hostSync` entry, and any `Home` no longer referenced. Hook point: the same flow that calls `PinnedHostStore.forgetHost` (`security/PinnedHostStore.kt:291`) and `SettingsManager.forgetKnownHost` (`data/SettingsManager.kt:876`). If not connected, the PC keeps the set until the owner-absent suspension (30 days) or a PC-side revoke; the phone's forget screen states this.
- **Revoke on the PC:** `PairedDeviceRevoker.RevokeAsync` (`PairedDeviceRevoker.cs:49-95`) gains an `Attempt(() => routines.ForgetOwner(clientId))` step covering definitions, runs, queue and any active run (cancelled with `pc_not_paired`). The phone learns on its next connect attempt (pairing refused) and shows that PC's routines as "PC no longer paired" (they are kept until the user forgets the PC, so a mistaken revoke is recoverable by re-pairing).

### 7.5 Capability flags

| Record | Field (JSON) | Type | Meaning when absent/false |
|---|---|---|---|
| `HostCapabilities` (`HostCapabilities.cs:6-97`) | `supportsRoutines` | bool | Host predates routines: no sync, host-executed steps fail `pc_too_old` |
| | `routineSchemaVersion` | int | 0 = none |
| | `routinePowerVerbs` | string[] | Verbs this host can execute, never `WAKEONLAN` (D5) (Windows: `GetPwrCapabilities` for sleep/hibernate, `GetFirmwareType` for UEFI; Linux: logind `CanSuspend`/`CanHibernate`/`CanRebootToFirmwareSetup` = `yes`). Missing verb → editor disables it; sync rejects `power_unsupported` |
| `ClientCapabilities` (`ClientCapabilities.cs:22-34`) | `supportsRoutines` | bool | Host never sends any `routine_*` message to this client and never queues for it |
| | `routineSchemaVersion` | int | 0 = none |

`HostCapabilitiesProvider` caches its record in a `Lazy` (`HostCapabilitiesProvider.cs:36`), so only static facts go there. Idle/session source availability is dynamic (D-Bus probes complete after start) and is reported in every `routine_sync_result` instead.

UX gating map (UX need 4):

| UI gate | Source |
|---|---|
| PC supports routines | `HostCapabilities.supportsRoutines` |
| PC sensor source available | `HostCapabilities.SupportsTelemetry` (`HostCapabilities.cs:34`) plus the live sensor list from telemetry; `sensorTrigger` in `routine_sync_result` |
| PC accepts media keys | `HostCapabilities.SupportsInputSimulation` (`HostCapabilities.cs:52`), the same flag that drives `rc_media_unavailable` today (`MediaControlSection.kt:239`) |
| PC launcher entries | The existing `launcher_sync`, sent by the PC on connect and after changes (`PingPongHandler.cs:143`, `:488-509`); a stale pick is caught by revalidation (`launch_not_allowed`) |
| PC MAC known | The phone's saved MAC for that PC, else `HostCapabilities.MacAddress` (`HostCapabilities.cs:96`) |
| Power verbs offered | `routinePowerVerbs` |
| Idle and session triggers offered | `idleSource` and `sessionSource` in `routine_sync_result` |
| Phone NFC hardware and state | `NfcAdapter.getDefaultAdapter(context)` non-null, and `isEnabled` |
| Home set | Phone store (§6.6) |
| Background restricted | `ActivityManager.isBackgroundRestricted()`, or standby bucket `RESTRICTED` from `UsageStatsManager.getAppStandbyBucket()` |
| PC reachable away from home | `hostSync[H].reachableAwayAtUnixMs` (§8.3.1) |

### 7.6 Old/new compatibility matrix

| Phone | Host | Behaviour |
|---|---|---|
| 3.0 | 3.0 | Full feature |
| 3.0 | ≤ 2.x | Editor shows PC triggers disabled for that PC with "Update RemEx on <PC>"; phone-run routines run `wake`/`waitOnline`/`delay`/`notify(phone)`; any host-executed step fails `pc_too_old` (never falls back to the raw `command` verb, because that would bypass the countdown) |
| ≤ 2.x | 3.0 | Host sees no `ClientCapabilities.supportsRoutines`; it never sends `routine_*` (an old phone's router would drop them silently, `REGRESSION-GUARDS.md:380`) |
| 3.0 | 3.x newer schema | Host accepts only what its validator understands; unknown step/trigger → per-routine rejection, visible on the phone |

### 7.7 Router and audience wiring (the silent-drop guard)

- `AndroidNativeExports.OnNativeMessageReceived` (`AndroidNativeExports.cs:2025`) gains one family forward: `msg.Type.StartsWith("routine_", StringComparison.Ordinal)` → new `_onRoutineMessageMethodId` → Kotlin `RemexCallback.onRoutineMessage(json)`, with the same comment block style as the `file_`/`clipboard_` forwards. **Do not narrow it to a list.**
- `MessageAudience` (`remex.core/Messages/MessageAudience.cs:64`) declares `routine_sync_result`, `routine_step_result`, `routine_notify`, `routine_run_report` as `ClientSurface.AndroidControl` so `MessageAudienceTests` and `HostToClientRoutingTests` fail if the forward goes.

Routing entry for every host → phone type (D2, `REGRESSION-GUARDS.md:373-384`):

| Host → phone type | Native router | Kotlin consumer | Audience | Pinned by |
|---|---|---|---|---|
| `routine_sync_result` | `routine_` prefix forward → `onRoutineMessage` | `RoutineSyncClient` | `AndroidControl` | `HostToClientRoutingTests`, `MessageAudienceTests`, `MessageTypeDeliveryTests`, `RoutineMessageRoutingTest` |
| `routine_step_result` | Same forward | `RoutineWorker` result awaiter | `AndroidControl` | Same |
| `routine_notify` | Same forward | `RoutineNotificationPresenter` | `AndroidControl` | Same |
| `routine_run_report` | Same forward | `RoutineHistoryRepository` | `AndroidControl` | Same |

Phone → host types (`routines_sync`, `routine_step_request`, `routine_notify_ack`, `routine_cancel`, `routine_run_request`) land in `PingPongHandler`'s dispatcher and need no routing entry, like `theme_sync` (`PhoneThemeSnapshot.cs:10-14`).

- A new hand-written guard entry in `docs/REGRESSION-GUARDS.md` "Wire protocol and native message routing": the `routine_` prefix forward, why, and the test that pins it.

---

## 8. Runners

### 8.1 Common run model

```
Queued ──► Running(step i) ──► [Countdown (destructive only)] ──► Succeeded
   │            │                     │
   │            ├──► Failed(reason)   ├──► Cancelled(cancelled_on_pc | cancelled_on_phone | paused_*)
   │            └──► Cancelled(reason)│
   └──► Skipped(reason)  (never started: paused, disabled, already_running, cooldown, rate_limited, owner_absent)
```

- A run is created **before** any step executes and persisted in state `running`; this is what makes interruption visible (`interrupted_pc`, `interrupted_phone`).
- Steps run strictly in order; the first failed step fails the run; remaining steps are recorded `not_run`.
- Single-flight per routine: a trigger that arrives while a run of the same routine is active is recorded as Skipped `already_running` (coalesced to ≤ 1 record per routine per 60 s).
- No chaining in v1: an event caused by a routine's own step (e.g. a routine's `LOCK` producing a `pc.session locked` edge within 5 s) is tagged `causedByRun` and does not trigger any routine. This is the loop guard.
- Clocks: durations and timeouts use monotonic time (`TimeProvider` on the host, `SystemClock.elapsedRealtime` on the phone); wall time is display only.
- Sources: automatic (`home.arrive`, `home.leave`, `pc.sensor`, `pc.idle`, `pc.session`) and person-initiated (`manual.app`, `manual.shortcut`, `manual.widget`, `nfc.tap`, `manual.pcRunNow`, plus Test). Pause all blocks automatic sources only (§8.7). The countdown applies to every source except a confirmed `manual.pcRunNow` (§8.6).

### 8.2 Phone runner

| Aspect | Specification |
|---|---|
| Execution vehicle | `RoutineWorker : CoroutineWorker`, enqueued with `setExpedited(OutOfQuotaPolicy.RUN_AS_NON_EXPEDITED_WORK_REQUEST)`, unique work name `routine-run-<routineId>`, `ExistingWorkPolicy.KEEP` (single-flight). New dependency `androidx.work:work-runtime-ktx` (+ `work-testing` for tests). No new foreground service: Android 12+ forbids starting one from a network-callback broadcast (FGS background-start restrictions, https://developer.android.com/develop/background-work/services/fgs/restrictions-bg-start) |
| Input data | `runId`, `routineId`, `triggerType`, `triggeredAtElapsedMs`, `triggeredAtUnixMs`, `triggerDetail` |
| Start latency | If `startedAt − triggeredAt > 60 s`, the run carries the attribute `deferred_by_os` (shown in history: "Android started this late"). Quota exhaustion in Doze is expected for background triggers and is reported, not hidden |
| Budget | Hard stop at 540 s from start (`budget_exceeded`), which the validator already guarantees statically (§6.5) |
| Preconditions (checked at start and before every step) | Routine exists and is valid; enabled unless the source is in-app Run or Test (`skipped_disabled`); not `pausedAll` when the source is automatic (`paused_on_phone`); host still paired (`PinnedHostStore.getPin` non-null for an alias of `hostIdentity`); for host-executed steps: selected host identity equals routine host (`pc_not_selected`) |
| At-most-once | Before each step the run record is updated to `step i started`. If WorkManager restarts the worker for a `runId` whose record already shows a started step, the worker marks the run `interrupted_phone` and stops. Host-side idempotency (§7.3.3) covers a retried request |

Per-step behaviour on the phone:

| Step | Action | Timeout | Retries | Failure codes |
|---|---|---|---|---|
| `wake` | `RemexCoreClient.WakePc(mac, broadcastIp, port)` (`RemexCoreClient.kt:170`), on the same path as `sendTileWake` | 5 s | 2 (1 s apart) — magic packets are idempotent | `wake_no_mac`, `permission_local_network`, `wake_send_failed` |
| `waitOnline` | Loop: if authenticated to the routine host → success; else `RemexClientManager.startOneShotConnect` (`RemexClientManager.kt:750`), wait on `isAuthenticated` ≤ 10 s, repeat | `timeoutSeconds` | Loop is the retry | `wait_timeout` (a `wake` preceded it); `pc_unreachable` (none did, and the phone is on the home network or has reached this PC from away before); `pc_unreachable_away` (none did, the phone is away from home and has never reached this PC from away); `pc_not_selected`, `pc_not_paired` |
| `delay` | Coroutine delay; cancellable | `seconds` | — | `cancelled` |
| `power`, `launchApp`, `media`, `notify(pc)` | Ensure connected (one `startOneShotConnect` + 10 s wait if not), check `supportsRoutines`, send `routine_step_request` (with `testRun` set for Test runs) via `RemexCoreClient.SendMessage` (`RemexCoreClient.kt:228`), await `routine_step_result` by `(runId, stepIndex)` | 30 s; destructive verbs 15 s countdown + 30 s = 45 s | Up to 2 resends of the **same** request on transport loss before any result (host dedups); never after a result | `pc_unreachable` / `pc_unreachable_away` (same rule as `waitOnline`), `after_power_off`, `pc_too_old`, `step_timeout`, `transport_lost`, plus host codes |
| `notify(phone)` | Post to channel `routines` (§9 R-SEC-09 redaction) | 2 s | — | `notify_denied_phone` (recorded, run continues as succeeded-with-warning) |

Cancellation: the run's ongoing notification (when notifications are allowed) has **Cancel**, which calls `WorkManager.cancelUniqueWork` and, if a host step is in flight, sends `routine_cancel`. The in-app run sheet offers the same. Pause all cancels running phone runs from automatic sources (`paused_on_phone`); person-initiated runs continue.

### 8.3 Phone trigger evaluation

#### 8.3.1 `home.arrive` / `home.leave` (network fingerprint, no location permission)

Signals come from `LinkProperties` of Wi-Fi/Ethernet networks via `ConnectivityManager.getLinkProperties(network)` (needs only `ACCESS_NETWORK_STATE`). SSID/BSSID are never read (they would need location).

| Stage | Rule |
|---|---|
| Capture | Allowed only while authenticated to the PC over a network with `TRANSPORT_WIFI` or `TRANSPORT_ETHERNET`, **without** `TRANSPORT_VPN`, and the host address is private (RFC 1918, ULA `fc00::/7`, or link-local) and inside one of that network's route prefixes. Tailscale (`100.64.0.0/10`) never qualifies — reachability over a tunnel does not prove "home". Captured: default-route gateways, on-link route prefixes, DNS servers, search domains, DHCP server address, the global IPv6 prefix truncated to /48. All normalized and stored inside the encrypted store (§6.6). The capture API (UX need 10) is `HomeCapture.probe(): HomeCaptureResult { ready: Boolean, refusal: Refusal?, facts: { gateways, prefixes, dnsServers, domain, dhcpServer }, pcReachable: Boolean }`, which the capture sheet renders (§1.4) before the user confirms; `Refusal` is one of `not_on_wifi`, `vpn_active`, `pc_not_reachable_on_lan`. Capture failure → `fingerprint_capture_failed` with the specific missing precondition shown |
| Match | A network matches home H when it is Wi-Fi/Ethernet, not VPN, **and** (a gateway ∈ H.gateways **and** a prefix ∈ H.prefixes) **and** at least one secondary fact matches (a DNS server, the domain, the DHCP server or the IPv6 /48). If H has no secondary facts the primary pair suffices and the home card says "weak match" |
| Presence state | Per home: `UNKNOWN`, `HOME`, `AWAY`. Persisted with the time of the last transition |
| Arrive | `AWAY → HOME` when a match holds for 10 s (re-check after 10 s). `UNKNOWN → HOME` never fires (reboot/first-run at home must not wake the PC) |
| Leave | `HOME → AWAY` when no network has matched for `leaveDebounceSeconds`, confirmed by a one-shot re-check work scheduled at the debounce deadline; any match in between resets it. `UNKNOWN → AWAY` never fires |
| Flap protection | Opposite transitions closer than 300 s do not fire (recorded Skipped `flap_suppressed`); per-routine cooldown 60 s |
| Drift detection | While authenticated to a home's PC over a qualifying LAN network that does **not** match the home, raise "Your home network looks different — update it?" (`home_fingerprint_stale`) on the routines screen. This is the only way a replaced router would otherwise present: as nothing ever firing |
| Reachable away | Whenever the phone authenticates to a PC while its active network is not a home match (VPN, Tailscale, mobile data, another Wi-Fi), set `hostSync[H].reachableAwayAtUnixMs`. It suppresses the leave-home authoring warning (§1.8) and selects `pc_unreachable` over `pc_unreachable_away` |
| Timing users can rely on (FAQ) | Arrive: usually within a minute of joining home Wi-Fi (10 s settle plus Android's delivery of the network event). Leave: `leaveDebounceSeconds` (default 3 min) after the phone stops seeing home, or up to about 15 min more when RemEx is not running and mobile data stays on |

Event sources (hybrid, because `registerNetworkCallback(NetworkRequest, PendingIntent)` only reports the `onAvailable` edge — "Action to perform when the network is available", https://developer.android.com/reference/android/net/ConnectivityManager):

| Source | Covers | When registered |
|---|---|---|
| `registerNetworkCallback(request, PendingIntent)` → non-exported `RoutineNetworkReceiver` → enqueue unique `routine-net-eval` work | Arrive; leave when a new non-home network appears | Whenever ≥ 1 enabled home routine exists: `Application.onCreate`, `BOOT_COMPLETED`, `MY_PACKAGE_REPLACED`, and after every edit. Idempotent re-registration with an `Intent.filterEquals`-identical PendingIntent; unregistered when no home routine remains |
| In-process `NetworkCallback` (`onAvailable`/`onLost`/`onLinkPropertiesChanged`) | Fast leave while the process is alive (e.g. `RemexConnectionService` running, `RemexConnectionService.kt:59-94`) | While the process is alive and ≥ 1 enabled home routine exists |
| Periodic `routine-presence-check` work, 15 min | Leave when mobile data is always-on (no new network ever becomes "available") and the process is dead | Only while some home is `HOME` **and** ≥ 1 enabled `home.leave` routine exists; cancelled otherwise |

Evaluation enumerates current networks by registering a transient callback for 2 s inside the worker (the callback reports all currently satisfying networks), then unregisters — `getAllNetworks()` is deprecated.

**`home.leave` and the PC (D6).** Once the phone has left the LAN the PC is usually unreachable, so a leave routine's PC-targeted steps fail with `pc_unreachable_away` unless the PC is reachable some other way (for example Tailscale). The editor warns at authoring time (§1.8), no leave template uses PC-targeted steps (§4.1), and the recommended way to "lock the PC when I'm gone" is a PC-run `pc.idle` or `pc.session` routine. A PC-side "phone away" trigger based on the owner's link dropping is a 3.x candidate (§4.3).

#### 8.3.2 `nfc.tap`

| Aspect | Specification |
|---|---|
| Tag content | NDEF message: (1) URI record `remex://routine/<routineId>?t=<token>`; (2) Android Application Record `com.clindsay94.remex` so dispatch always lands in RemEx |
| Token | 128-bit `SecureRandom`, base64url (22 chars), per routine, stored in `RoutineSecrets`. "Rewrite tag" mints a new token; the old tag (and any clone) stops working |
| Entry point | `NfcRoutineActivity`, `exported="true"` **only** because tag dispatch requires it, guarded by `android:permission="android.permission.DISPATCH_NFC_MESSAGE"` with an `NDEF_DISCOVERED` filter on scheme `remex`, host `routine` (pattern from https://developer.android.com/develop/connectivity/nfc/nfc). No other `remex://` handler exists |
| Checks | Token compared with `MessageDigest.isEqual`; device must be unlocked (`KeyguardManager.isDeviceLocked == false`, else `nfc_device_locked`); routine enabled; not paused; 10 s per-routine debounce (double taps) |
| Outcome | Invalid token → no run, history `nfc_unknown_tag` against the routine id if it exists, toast "This tag is not valid any more". Valid → enqueue the worker (the activity is visible, so expedited quota is not an issue) and finish without UI beyond a toast |
| Writing | In-app "Write tag" uses `Ndef.writeNdefMessage`; optional "Lock tag" (`makeReadOnly`) with a warning that locking is permanent |
| Payload size and checks | URI record about 70 bytes plus AAR about 30 bytes, which fits NTAG213 (144 bytes usable). Before writing, `Ndef.isWritable` (read-only state) and `getMaxSize` (too-small state) are checked (§1.5) |
| Existing tag | The write sheet reads the tag first; a `remex://routine/<id>` URI whose `id` exists on this phone is reported as "This tag runs <name>" and replacing it asks first (§1.5). Several tags may run one routine: they share its token, so rotating it invalidates all of them |
| Other phones | Another phone holds no routine with that id and token, so the tap is refused with `nfc_unknown_tag`; without RemEx installed, the AAR opens the store listing. Nothing runs (FAQ 22) |
| Test tap | The write sheet's "Test it" verifies id and token and runs nothing |
| Manifest | `<uses-permission android:name="android.permission.NFC"/>` (normal) and `<uses-feature android:name="android.hardware.nfc" android:required="false"/>` so devices without NFC still install |

#### 8.3.3 `manual`

| Surface | Mechanism | Presence gate for routines with a destructive step |
|---|---|---|
| In-app Run | Direct enqueue from the UI | Confirm sheet naming each destructive step |
| Pinned shortcut | `ShortcutManagerCompat.requestPinShortcut` with an explicit intent to **non-exported** `RoutineShortcutActivity`; extras `routineId` + `sig = HMAC-SHA256(shortcutKey, routineId)`; activity verifies `sig` | Confirm activity (same pattern as `TileConfirmActivity`, `AndroidManifest.xml:160-162`, `TileCommand.kt:80-103`) |
| Widget | New Glance widget "Routine" configured with one routine; action is an `actionRunCallback` (our own PendingIntent, never exported); config activity only selects a routine | Same confirm activity |
| Run or Test of a PC-run routine from the phone | `routine_run_request` (§7.3.8); the PC runs its stored copy | In-app confirm dialog; the PC still counts down |

The phone-side confirm does not replace the PC countdown: every routine destructive verb still counts down on the PC (§8.6).

Deleting a routine invalidates its shortcut (`ShortcutManagerCompat.disableShortcuts` with a "Routine deleted" message) and shows the widget's "Routine removed" state; a stale `sig` or unknown id opens the app on the routines list with a message, never runs anything.

### 8.4 Host runner

| Component | Responsibility |
|---|---|
| `RoutineHostService : BackgroundService` | Registered in `HostBootstrapper`; owns stores, trigger sources, runner; starts sources only for triggers present in enabled, unpaused, unsuspended routines |
| `RoutineStore` | Load/validate/migrate `routines.json`, apply syncs, owner bookkeeping, PC overrides |
| `RoutineRunStore` | History (§8.8), `seq` allocation, startup sweep marking `running` records `interrupted_pc` |
| `RoutineNotifyQueue` | Persistent queue, expiry, ack |
| `RoutineStepExecutor` | Executes `power` via `SharedCommandVerbs.TryExecuteAsync` (`SharedCommandVerbs.cs:62`), `launchApp` via `launchers.json` lookup + `IAppLauncherService.LaunchAppAsync` (`AppLauncherService.cs:20`), `media` via the same virtual-key path the phone's media buttons use (VK `0xB3/0xB0/0xB1`, translated on Linux by `LinuxInputEventTranslator.cs:246-249`), `notify(pc)` via `NotificationRouter`, `notify(phone)` via `routine_notify` |
| `RoutineCountdownCoordinator` | §8.6 |
| Trigger sources | `SensorTriggerSource`, `IdleTriggerSource`, `SessionTriggerSource` (§8.5) |
| `RoutineRunNow` | PC page "Run now": after `ConfirmationDialogHost` confirms (destructive routines only; others start at once), starts a host run with `source = manual.pcRunNow` and the in-process flag `presenceConfirmed = true`, which skips the countdown (D3). No wire field maps to it |
| `RoutineRunRequestHandler` | `routine_run_request` (§7.3.8): starts a host run of the sender's stored routine with `presenceConfirmed = false` |

Host-run state machine per routine:

| State | Enter | Exit |
|---|---|---|
| `Armed` | Routine accepted, enabled, not paused/suspended/disabled on PC | Trigger condition met → `Firing` |
| `Firing` | Rate/cooldown checks pass | Run created → `Running` |
| `Running` | Steps execute; revalidation first (§R-SEC-06) | Run ends → `Rearming` |
| `Rearming` | Trigger-specific re-arm condition (sensor clears hysteresis; idle ends with input; session edge) | → `Armed` |
| `Suspended` | Paused (phone or PC), disabled on PC, owner absent, source unavailable | Condition cleared → `Armed` (never fires on the edge of un-pausing) |

Per-step timeouts on the host: `power` non-destructive 30 s; destructive = countdown 15 s then issue; `launchApp` 15 s; `media` 10 s; `notify` 5 s; `delay` as configured. No automatic retries on the host (a failed verb is reported, not repeated). Host-wide: 4 concurrent runs, one countdown at a time.

### 8.5 Host trigger sources

#### 8.5.1 `pc.sensor`

- Holds `TelemetryBackgroundService.AcquireDemand()` (`TelemetryBackgroundService.cs:129`) only while ≥ 1 armed `pc.sensor` routine exists; releases it otherwise, so sampling can go idle (`:256-276`).
- Subscribes to `TelemetryPublished` (`:122`), 1 Hz (`:132`). Looks up the reading by `SensorReading.Id`.
- `live = SensorThreshold.IsLive(direction, threshold, value, wasLive)` — the existing 2% relative deadband (`SensorViewModel.cs:473-488`) moved to core.
- Sustain: `breachSince` set on the first live sample; fire when `now − breachSince ≥ sustainSeconds` and armed. Any non-live sample clears `breachSince`. After firing, the routine re-arms only after a non-live sample **and** 300 s cooldown.
- Missing sensor: `breachSince` cleared; after 10 min continuously missing, one history record `sensor_unavailable` and the PC/phone show "Waiting for sensor".

#### 8.5.2 `pc.idle`

| Platform | Source (probe order; first that answers wins) | Mechanism |
|---|---|---|
| Windows | `win32.lastinput` | `GetLastInputInfo` against `GetTickCount64`, polled every 15 s. Valid because the agent runs inside the user's session. Input injected by remote-desktop streaming resets it, which is the desired behaviour |
| Linux GNOME | `gnome.idlemonitor` | Session bus `org.gnome.Mutter.IdleMonitor` `GetIdletime` (ms), polled every 15 s |
| Linux KDE and others implementing it | `freedesktop.screensaver` | Session bus `org.freedesktop.ScreenSaver.GetSessionIdleTime` (s), polled every 15 s |
| Linux X11 fallback | `x11.xss` | `XScreenSaverQueryInfo` via optional `libXss.so.1` (loaded with `NativeLibrary.TryLoad`; absent → skip) |
| Linux coarse fallback | `logind.idlehint` | System bus `org.freedesktop.login1` Session `IdleHint`/`IdleSinceHint` via `PropertiesChanged` (event-driven). Coarse: the desktop sets it only after its own idle delay, so the editor warns "idle time is approximate on this PC" (https://www.freedesktop.org/software/systemd/man/latest/org.freedesktop.login1.html) |
| None answer | `null` | `pc.idle` routines rejected at sync with `idle_source_unavailable`; editor disables the trigger for this PC |

Fire rule: `idle ≥ idleMinutes` while armed, and (`ignoreWhileMediaPlaying = false` or the media session is not `Playing`, using the existing media reader behind `HostCapabilities.SupportsMediaState`). Re-arm when idle drops below 5 s (input seen). One fire per idle period.

#### 8.5.3 `pc.session`

| Platform | Source | Mechanism |
|---|---|---|
| Windows | `win32.wts` | Dedicated thread with a message-only window (`HWND_MESSAGE`) + `WTSRegisterSessionNotification(hwnd, NOTIFY_FOR_THIS_SESSION)`; `WM_WTSSESSION_CHANGE` with `WTS_SESSION_LOCK`/`WTS_SESSION_UNLOCK` (https://learn.microsoft.com/en-us/windows/win32/api/wtsapi32/nf-wtsapi32-wtsregistersessionnotification). Initial state from `WTSQuerySessionInformation(WTSSessionInfoEx)`. Owning its own window matters: the Avalonia main window may never be constructed at a `--minimized` start (`REGRESSION-GUARDS.md:1046-1050`) |
| Linux (logind) | `logind.lockedhint` | System bus, own session object (`GetSessionByPID(own pid)`), `PropertiesChanged` on `LockedHint`. The Session `Lock()`/`Unlock()` **signals** are lock *requests* to the locker, not state, so they are not used |
| Linux fallback | `freedesktop.screensaver` / `gnome.screensaver` | Session bus `ActiveChanged(bool)` |
| None | `null` | Rejected at sync with `session_source_unavailable` |

Edges must persist 3 s before firing (collapses lock→unlock→lock flaps); ≤ 20 session-triggered runs per routine per hour.

### 8.6 Countdown and destructive-step safety

| Rule | Specification |
|---|---|
| Applies to | Every routine-issued destructive verb (§6.4, D1), from **any** trigger or initiator, PC-run or phone-run, including phone manual runs, NFC taps, shortcuts, widgets and tests. The person holding the phone may not be the person at the PC |
| Exception | A PC Run now confirmed in `ConfirmationDialogHost` (D3). The confirmation is presence at the PC, so the run's destructive step proceeds without counting down. The decision is the in-process flag `presenceConfirmed`, set only by `RoutineRunNow`; no wire message or field can set it (T21) |
| Length | 15 s, fixed in v1 |
| Surface | `RoutineCountdownWindow`: small, topmost, its own top-level window with no owner, so it does not depend on `MainWindow` existing (`REGRESSION-GUARDS.md:1040-1064`); centred on the primary screen's work area (§2.3 P4); plus a tray balloon via `NotificationRouter`. One button, **Cancel**, which is both the default and the cancel button. Text: routine name, verb, source and owner phone name, seconds left |
| Phone mirror | PC runs (triggered, or started by `routine_run_request`): `routine_notify{kind: countdown}` to the owner when connected; its Cancel sends `routine_cancel`. Phone step requests: the phone shows the countdown in its own progress notification from the moment it sends the destructive step, with the same Cancel |
| Cancel paths | PC window Cancel (Enter, Space or Esc), tray menu "Cancel routine", the phone notification, PC Pause all, phone Pause all (`routines_sync{paused:true}` arriving during the countdown) |
| After cancel | The destructive step records `cancelled` with `cancelledBy` (`pc`, `phone` or `pause`); every remaining step records `cancelled`; the run outcome is `cancelled` with reason `cancelled_on_pc` or `cancelled_on_phone` (a pause counts as a cancel from the side that paused) |
| After timeout | The verb is issued and the step records `succeeded` |
| Locked PC or no desktop | The window cannot draw over the secure desktop. The 15 s still elapse, the phone mirror (when connected) is the visible surface, and the step proceeds, because a `pc.session locked → SLEEP` routine must work. The run records the attribute `countdown_unseen` |
| Conflicts | One countdown at a time on the PC; a second destructive step is Skipped `conflict_countdown_active` |
| Test runs | `testRun` (§7.3.3, §7.3.8, D7): the countdown runs in full on the PC and the phone, then the verb is **not** issued; the step records `simulated` and the run carries the attribute `simulated` |
| Dry run | `Remex.Agent.exe --routines-dry-run` (command line only; no setting, file or wire field can enable it) logs destructive verbs instead of issuing them and shows a persistent "Dry run" banner on the PC Routines page. Used by the live verification script (§13.6) |

### 8.7 Pause all

Pause all stops **automatic** sources (`home.arrive`, `home.leave`, `pc.sensor`, `pc.idle`, `pc.session`). Person-initiated runs (in-app Run and Test, shortcut, widget, NFC tap, a phone `routine_run_request`, PC Run now) still work (§1.10).

| Where | Effect | What the other side shows |
|---|---|---|
| Phone | `pausedAll` in the phone store; automatic phone triggers are recorded Skipped `paused_on_phone` and running automatic phone runs are cancelled. The flag travels to **every** paired PC as `routines_sync.paused` (§7.3.1) on its next authenticated connection; a pause-only change bumps the revision so it syncs like any edit (D4) | PC: that phone's group shows "Paused from <phone name>", its automatic routines skip with `paused_on_phone`, and any countdown of that owner is cancelled. Phone: "Paused on this phone and on <PC>" once the result echoes `ownerPaused`, else "<PC> will pause when it connects" |
| PC | `hostPaused` in `routines.json`; every owner's automatic PC routines skip with `paused_on_pc`; a countdown in progress is cancelled (`cancelled_on_pc`) | Phone: "Paused on <PC>" from `hostPaused`, which every `routine_sync_result` carries and which is sent unsolicited when it changes. The PC's pause does **not** pause the phone's own routines |
| Recording | Skips are coalesced to one record per routine per hour | — |

### 8.8 Run history

`RoutineRun` is the same record on both sides (the PC adds `seq` and `ownerClientId`). It is exactly what the history UI renders (UX need 1).

| Field | Type | Notes |
|---|---|---|
| `runId` | string | UUID |
| `seq` | int64 | PC only; last-modified sequence (§7.3.6) |
| `ownerClientId` | string | PC only; never sent to the phone |
| `routineId`, `routineName` | string | Name snapshot |
| `routineRevision` | int64 | `Routine.revision` at run time; drives "Edited since this run" |
| `origin` | string | `phone` or `pc`: where the runner ran |
| `hostIdentity` | string | 16 hex |
| `source` | string | `home.arrive`, `home.leave`, `nfc.tap`, `manual.app`, `manual.shortcut`, `manual.widget`, `manual.pcRunNow`, `pc.sensor`, `pc.idle`, `pc.session` |
| `testRun` | bool | In-app Test (D7) |
| `sourceDetail` | object? | `{sensorName, value, unit}` for `pc.sensor`; `{idleMinutes}` for `pc.idle`; `{sessionState}` for `pc.session`; `{homeLabel}` for `home.*` |
| `triggeredAtUnixMs`, `startedAtUnixMs`, `endedAtUnixMs` | int64 | `endedAtUnixMs` is null while running |
| `outcome` | string | `running`, `succeeded`, `failed`, `cancelled`, `skipped`, `interrupted` |
| `reasonCode` | string | §10.1; `ok` on success |
| `reasonArgs` | object? | `{pc, phone, app, sensor, duration, action, detail}`, only the keys the message uses |
| `cancelledBy` | string? | `pc`, `phone`, `pause` |
| `attributes` | string[] | `simulated`, `countdown_unseen`, `deferred_by_os`, `dry_run`, `notify_queued`, `background_restricted` |
| `steps` | array | `{index, kind, status, startedAtUnixMs, endedAtUnixMs, reasonCode, reasonArgs}`; `kind` is the step `type`; `status` ∈ `pending`, `running`, `succeeded`, `failed`, `skipped`, `cancelled`, `simulated`, `expired` |
| `countdown` | object? | `{shown, startedAtUnixMs, cancelledBy}` for a destructive step |

Step status rules: after a failed step every remaining step is `skipped`; after a cancel every remaining step is `cancelled`; a simulated destructive step is `simulated`; a `notify(phone)` step whose message was queued is `succeeded` with the run attribute `notify_queued`, and becomes `expired` if the hour passes undelivered (the PC re-sends the record, §7.3.6).

| Aspect | Phone | PC |
|---|---|---|
| Retention | A run is kept while it is among the last 50 runs of its routine **or** younger than 30 days, with a hard cap of 1,000 runs (oldest dropped first) and nothing older than 90 days | Same rule with 20 per routine, 30 days and a cap of 500 |
| Storage | `remex_routine_history`, one AEAD blob per routine (§6.8) | `routine_runs.json` (§6.9) |
| Paging | The history UI pages locally, 30 rows at a time | `routine_run_report` pages of up to 50, ordered by `seq` (§7.3.6) |
| Shown | Routines screen history: the phone's own runs plus PC runs merged (badge "on PC"); PC history cached with an "As of" time when offline (R-UX-29) | PC Routines page history, grouped by phone; includes runs of that phone's routines started by `routine_run_request` |
| Sync | Receives PC runs through `routine_run_report` | Never receives phone runs; the PC shows what it ran |
| Coalescing | Skips coalesced as stated in §8.1 and §8.7 | Same |

### 8.9 Test runs and dry run

| | Test run (D7) | Dry run |
|---|---|---|
| Started by | Editor "Test" on the phone, for any saved routine | `Remex.Agent.exe --routines-dry-run` on the PC command line |
| Scope | One run | Every run on that PC for the life of the process |
| Non-destructive steps | Run for real | Run for real |
| Destructive steps | Countdown shown on the PC and the phone, verb not issued; step `simulated`, run attribute `simulated` | Countdown shown, verb logged instead of issued; run attribute `dry_run` |
| Wire | `testRun` in `routine_step_request` and `routine_run_request`; the PC enforces it | None: no message, setting or file can enable it |
| Phone-only steps | `wake` sends a real packet, `waitOnline` waits for real, `notify` posts for real | Not applicable |

---

## 9. Threat model

Scope inherits `docs/SECURITY_EXPLAINED.md` "Threat model" (in scope: LAN eavesdropping, impostor host, unauthenticated control, brute force, device-ID spoofing, local secret theft by non-privileged processes; out of scope: compromised host OS, compromised phone, public exposure without VPN). Routines add **persistent, unattended authority**: a definition authorized once keeps acting later.

| # | Threat | Mitigation | Test |
|---|---|---|---|
| T1 | Another app sends an intent to `remex://routine/...` or to a routine component to run a routine (intent spoofing) | No exported deep-link handler. The only exported routine component is `NfcRoutineActivity`, guarded by `DISPATCH_NFC_MESSAGE` (only the system NFC service holds it). Shortcut target and widget receiver are non-exported; the shortcut activity additionally verifies an HMAC `sig`. Unknown/invalid input never runs anything | `RoutineManifestExportTest` (parses `src/main/AndroidManifest.xml`: every routine component `exported=false` except `NfcRoutineActivity`, which must carry the permission); `ShortcutSignatureTest` |
| T2 | NFC tag cloned or replayed | Token is per routine and rotatable; tag dispatch requires the owner's phone unlocked (`nfc_device_locked` otherwise); destructive steps still get the PC countdown; 10 s debounce | `NfcTokenVerifierTest` (wrong token, old token after rotate, locked device, debounce) |
| T3 | NFC tag rewritten by a third party to point elsewhere | AAR pins the package; any non-matching routine id/token is rejected; worst case the user's tag stops working, which is visible | `NfcTokenVerifierTest.RejectsUnknownRoutine` |
| T4 | Sync tampering or replay on the network | Only over the authenticated `/ws` (TLS 1.3 + SPKI pin + proof-of-possession reconnect, `REGRESSION-GUARDS.md:946-950`, `:1066-1070`); host drops `routines_sync` from unauthenticated sessions; monotonic revision rejects older sets | `RoutinesSyncGateTests` (unauthenticated drop), `RoutineSyncRevisionTests` (stale, equal-hash, conflict) |
| T5 | Malicious or buggy paired phone syncs a hostile routine (e.g. unlock → shutdown) | Host revalidates everything; destructive verbs from the phone always count down with Cancel on the PC (only the PC's own confirmed Run now skips it, T21); PC user can disable per routine, Pause all, block the phone (`blocked_by_pc`), or revoke. No capability beyond what a paired phone can already do with `command` | `RoutineHostValidationTests`, `CountdownCancelTests`, `BlockedByPcTests` |
| T6 | Routine definition valid at sync, invalid at run (app removed from allowlist, verb no longer supported, phone revoked) | Revalidation at run: owner paired (`PairedClientRegistry.IsClientPaired`, `PairedClientRegistry.cs:132`), `appId` still in `launchers.json`, `IsLaunchAllowed` + `IsRejectedNetworkPath` inside `LaunchAppAsync`, verb in `routinePowerVerbs` | `RoutineRunRevalidationTests` |
| T7 | Lost phone | PC revoke deletes that owner's routines, runs, queue, cancels active runs (`PairedDeviceRevoker` step). WoL from the lost phone remains possible, as for any LAN device (magic packets are unauthenticated by design) — documented | `RevokeDeletesRoutinesTests` |
| T8 | Forget-PC on the phone leaves routines running on the PC | Connected: `forget` flush before unpair. Offline: owner-absent suspension after 30 days; PC UI shows the owner and lets the user block/revoke; the phone's forget screen states the residual | `ForgetPcFlushTest` (Android), `OwnerAbsentSuspensionTests` (host) |
| T9 | Trigger flapping or runaway loops (sensor oscillating, session lock/unlock, routine causing its own trigger) | Sustain + hysteresis + 300 s cooldown (sensor); 3 s settle + 20/h (session); 300 s opposite-transition gap (home); per-routine 60 s minimum interval; single-flight; 30 runs/h per owner; `causedByRun` suppression | `SensorTriggerSourceTests`, `SessionTriggerSourceTests`, `PresenceStateMachineTest`, `RoutineRateLimitTests`, `CausalSuppressionTests` |
| T10 | Lock-screen leakage of routine names, PC names, notify text | Channel `routines` with `lockscreenVisibility = VISIBILITY_PRIVATE` and a public version "RemEx routine update"; no PC hostnames in notification titles | `RoutineNotificationRedactionTest` |
| T11 | Logs leak secrets or network identity | Never log NFC tokens, keys, fingerprint values/tokens, notify bodies; MAC logged as `XX:XX:XX:**:**:**`; client ids via `LogRedaction.RedactClientId` (`remex.agent/Services/Security/LogRedaction.cs:18`); routine names truncated to 16 chars in logs | `RoutineLogRedactionTests` (host, captures `ILogger` output), `RoutineLogRedactionTest` (Android, `Log` wrapper) |
| T12 | Routine secrets or fingerprints leave the device via backup/device transfer | All routine DataStores and the routine Tink prefs excluded in `backup_rules.xml` and `data_extraction_rules.xml` (both `cloud-backup` and `device-transfer`), per `AndroidManifest.xml:42-51` | `BackupRulesRoutineExclusionTest` (parses both XML files) |
| T13 | Export file leaks secrets | Export whitelist excludes tokens, keys, homes, `hostIdentity`, MAC (§6.10) | `RoutineExportWhitelistTest` |
| T14 | Local non-elevated process plants a routine on Windows to drive the elevated agent | `routines.json` ACL = LocalSystem + Administrators, inheritance off; unreadable/invalid file → start empty + PC warning, never execute partial content | `RoutineStoreAclTests` (`[WindowsOnlyFact]`), `RoutineStoreCorruptFileTests` |
| T15 | 8338 widened by routines | No routine type is handled by `RemexNetworkListener`; `CommandVerbs.ScriptIngress` unchanged; host routines execute in-process only | Existing `CommandVerbDriftTests` + new `RoutineIngressIsolationTests` (source scan of `RemexNetworkListener.cs` for `routine`; asserts the 11-verb list) |
| T16 | Play policy violation (background location, exact alarms, FGS misuse) | No location permissions; no `SCHEDULE_EXACT_ALARM`/`USE_EXACT_ALARM`; no new FGS type (WorkManager expedited only); `NFC`, `RECEIVE_BOOT_COMPLETED` are normal permissions | `NoLocationOrExactAlarmPermissionTest` (manifest scan), release `lintVitalRelease` |
| T17 | Cross-owner interference on a shared PC | All host state keyed by `clientId`; notify/report/cancel scoped to owner | `MultiOwnerIsolationTests` |
| T18 | Deserialization DoS (malformed routine drops the whole session) | No `required` wire fields; flat trigger/step records; handler catches everything and answers with a result | `RoutinesSyncMalformedPayloadTests` (missing fields, wrong types, unknown discriminators keep the session alive) |
| T19 | Fingerprint spoofing (a foreign network with the same default gateway fires `home.arrive`) | Secondary-signal requirement; arrive steps that need the PC fail `pc_unreachable` on a foreign LAN; destructive steps would need the PC anyway (countdown); leave false positives bounded by debounce | `NetworkFingerprintMatcherTest` (same gateway, different DNS/DHCP → no match) |
| T20 | Wake packet MAC disclosure on foreign networks | Arrive fires only on a fingerprint match; `wake` sends to the stored broadcast address only | `PresenceStateMachineTest` (no fire from `UNKNOWN`) |
| T21 | Presence bypass: skipping the countdown remotely | Only a PC Run now confirmed in `ConfirmationDialogHost` skips the countdown (D3). The flag is in-process only; no field of `routine_step_request`, `routine_run_request` or `routines_sync` can express it. Phone manual runs, NFC, shortcuts, widgets and tests all count down (D1). A confirm dialog that cannot show declines (fails closed) | `RunNowCountdownBypassTests`, `RoutinesViewModelRunNowTests` |
| T22 | The test flag used to hide a real action or fake one | `testRun` can only make the host do less: a destructive verb is never executed under it, and history marks the run `simulated` on both sides, so a test is never mistaken for a real shutdown | `TestRunSimulationTests` |
| T23 | Dry run enabled remotely to disable safety or muddy history | Command-line switch only; no setting, file or wire field; persistent banner while active | `RoutineDryRunSwitchTests` |
| T24 | Phone asks the PC to run a definition it never synced | `routine_run_request` names one of the sender's stored routine ids; the PC runs its own stored, revalidated copy | `RoutineRunRequestTests` |

---

## 10. Failure catalog

Rule: nothing fails silently. Every code below produces a history record, whose short label is the "History text" column, and a message on every platform that took part. "Not reached" in the PC column means the PC took no part: the phone never got through to it, or the event exists only on the phone, so the phone is the one place it can show. Everything that happens on the PC also reaches the owner's phone through `routine_run_report` (§7.3.6), so PC outcomes are visible on both. Placeholders `{pc}`, `{phone}`, `{app}`, `{sensor}`, `{action}`, `{duration}`, `{date}`, `{routine}`, `{detail}`, `{n}` come from `reasonArgs` (§8.8). String keys: Android `routine_reason_<code>` and `routine_history_<code>`; PC `Routine_Reason_<code>` and `Routine_History_<code>`; all 9 locale files. `[...]` is the contextual fix action. Messages are the UX half's copy where it proposed one.

### 10.1 Reason codes

| Code | Meaning | Android message | PC message | History text |
|---|---|---|---|---|
| `ok` | Step or run succeeded | Result notification "Done" | Card shows the time it last ran | "Done" |
| **Reaching the PC** | | | | |
| `pc_unreachable` | A phone-run step needed the PC and could not reach it, and no `wake` preceded it; or the phone is at home or has reached this PC from away before | "Couldn't reach {pc}. It may be off, asleep, or on another network." [Open Connection] | Not reached | "Couldn't reach {pc}" |
| `pc_unreachable_away` | As above while the phone is away from home and has never reached this PC from away; typical for `home.leave` (D6) | "{pc} can't be reached from outside your home network. A service like Tailscale lets your phone reach it while you're away." [Open FAQ "Can I connect over the internet?"] | Not reached | "Couldn't reach {pc} away from home" |
| `wait_timeout` | `waitOnline` elapsed after a `wake` | "{pc} didn't come online within {duration}. If it was fully off, it may be waiting at the sign-in screen." [Open Connection] | Not reached | "{pc} didn't come online" |
| `wake_no_mac` | `wake` step, but no MAC is saved for the PC | "{pc} has no MAC address saved, so it can't be woken." [Open Connection] | Not reached | "No MAC address" |
| `wake_send_failed` | The phone could not send the wake packet | "Couldn't send the wake signal from this phone. Check that it's on Wi-Fi." | Not reached | "Wake signal not sent" |
| `permission_local_network` | Android's local network permission is denied | "RemEx isn't allowed to reach devices on your network." [Open settings] | Not reached | "Local network access off" |
| `pc_not_selected` | The routine's PC is not the PC RemEx is set to (D8) | "This routine controls {pc}, but RemEx is set to another PC." [Switch to {pc} and run] | Not reached | "Another PC was selected" |
| `pc_not_paired` | The phone is no longer paired with the PC, or the PC revoked it during a run | "{pc} is no longer paired with this phone." [Open Connection] | Runs in flight for a revoked phone: "Stopped: {phone} was unpaired." | "PC no longer paired" |
| `pc_too_old` | The PC does not advertise `supportsRoutines` | "{pc} needs a newer version of RemEx for this step." | Not reached (an older PC has no Routines page) | "PC needs an update" |
| `step_timeout` | No `routine_step_result` arrived in time | "{pc} didn't answer in time during {action}. It may still have done it." | The PC's history shows what the step really did | "No answer from {pc}" |
| `transport_lost` | The connection dropped mid-step | "Lost contact with {pc} during {action}. It may still have done it." | The PC's history shows what the step really did | "Connection lost" |
| `after_power_off` | A phone-run step after a power-off step, with no `waitOnline` between them | "Skipped because an earlier step turned off {pc}." [Edit routine] | Not reached | "Skipped after power-off" |
| **On the PC** | | | | |
| `launch_not_allowed` | `appId` is no longer in the launcher list, or its path is rejected | "{app} is no longer in {pc}'s launcher list." [Edit routine] | Card warning "{app} isn't in the launcher list any more." | "App not allowed" |
| `launch_failed` | The launch threw | "{pc} couldn't open {app}." | Tray (Problem) and history: "Couldn't open {app}." | "App didn't open" |
| `power_unsupported` | The verb is not in `routinePowerVerbs` | "{pc} can't {action}." [Edit routine] | Card warning "This PC can't {action}." | "Not supported" |
| `power_denied_by_os` | The operating system refused (polkit, privilege) | "{pc} refused to {action}. Its system settings don't allow it." | Tray (Problem) and history: "The system refused to {action}." | "Refused by the system" |
| `power_failed` | Any other execution error | "{pc} didn't carry out {action}." | Tray (Problem) and history: "Couldn't {action}." | "Power action failed" |
| `media_unavailable` | No input backend for media keys | "{pc} isn't set up to accept media keys." | Card warning "Media keys aren't available on this PC." | "Media keys unavailable" |
| `sensor_unavailable` | The watched sensor has been missing for 10 min | "The sensor {sensor} stopped reporting." | Card "Waiting for sensor {sensor}" | "Sensor stopped reporting" |
| `idle_source_unavailable` | The PC has no idle source | "{pc} can't tell when it's idle, so this trigger can't run there." | Page header "Idle detection isn't available on this desktop." | "Idle not detectable" |
| `session_source_unavailable` | The PC has no lock source | "{pc} can't tell when it's locked or unlocked, so this trigger can't run there." | Page header "Lock detection isn't available on this desktop." | "Lock not detectable" |
| `routine_not_found` | `routine_run_request` for a routine the PC does not hold | "{pc} doesn't have this routine yet. Connect so it can sync." | Not shown: the PC holds no such routine (logged) | "Not on the PC yet" |
| **Sync and validation** | | | | |
| `rejected_by_pc` | Display wrapper for a routine the PC refused at sync; `{detail}` is the specific code's message | "{pc} couldn't use this routine: {detail}" [Edit routine] | Page notice "{n} routines from {phone} couldn't be used here", listing each reason | "Refused by the PC" |
| `schema_too_new` | The other side uses a newer routine format | For a PC: "Update RemEx on {pc} to use this routine." For a file: "This file was made by a newer RemEx. Update the app to import it." | "Routines from {phone} need a newer RemEx on this PC." | "Newer version needed" |
| `payload_too_large` | The sync would exceed 64 KiB | "Too many routines to send to {pc} at once. Remove a few." | Through `rejected_by_pc` | "Too large to sync" |
| `stale_revision` | The PC holds a newer revision (phone store reset); resolved automatically once | Transient "Syncing with {pc}…" | Logged; the page shows the set once stored | "Resynced" |
| `revision_conflict` | Same revision, different content; resolved automatically once | Transient "Syncing with {pc}…" | Logged | "Resynced" |
| `blocked_by_pc` | The PC user blocked this phone's routines | Banner "{pc} has blocked routines from this phone." | "Routines from {phone} are blocked." [Unblock] | "Blocked by the PC" |
| `destructive_not_last` | A PC-run routine has steps after a power-off step | "This can't run: the step before it turns off the PC. Move the power action to the end." | Through `rejected_by_pc` | "Power action not last" |
| `too_many_destructive` | More than one D1 verb in one routine | "A routine can have only one shut down, restart, sign out, sleep or hibernate step." | Through `rejected_by_pc` | "Too many power actions" |
| `too_many_steps` | More than 12 steps | "Routines can have up to 12 steps." | Through `rejected_by_pc` | "Too many steps" |
| `too_many_routines` | More than 32 per phone, or 16 PC-run per PC | "You've reached the limit of 32 routines, or 16 that run on one PC." | Through `rejected_by_pc` | "Too many routines" |
| `too_many_homes` | A second home | "RemEx supports one home network." | Not reached | "Too many homes" |
| `budget_exceeded` | Phone-run waits over 9 min, or PC-run over 30 min | "This routine would take too long to run. Shorten the waits." | Through `rejected_by_pc` | "Too long" |
| `home_not_set` | A `home.*` routine with no home | "Home isn't set up, so this couldn't start." [Set up home] | Not reached | "Home not set" |
| `step_not_allowed_on_pc` | `wake` or `waitOnline` in a PC-run routine | "Only for routines that run on your phone." | Through `rejected_by_pc` | "Step not allowed on the PC" |
| `trigger_not_pc` | A phone trigger sent to the PC (a phone bug) | "This routine belongs on your phone, not {pc}." | Through `rejected_by_pc` | "Wrong runner" |
| `wrong_pc` | The routine's `hostIdentity` is another PC | "This routine belongs to a different PC." | Through `rejected_by_pc` | "Wrong PC" |
| `unsupported_trigger` | Trigger unknown to this version | "This routine uses a trigger this version of RemEx doesn't know. Update RemEx." | Through `rejected_by_pc` | "Needs a newer RemEx" |
| `unsupported_step` | Step unknown to this version | "This routine uses a step this version of RemEx doesn't know. Update RemEx." | Through `rejected_by_pc` | "Needs a newer RemEx" |
| `duplicate_id` | Two routines share an id | "This routine is damaged. Duplicate it or build it again." | Through `rejected_by_pc` | "Invalid routine" |
| `field_not_allowed` | A field that this trigger or step does not take | "This routine is damaged. Duplicate it or build it again." | Through `rejected_by_pc` | "Invalid routine" |
| `invalid_field` | A value out of range, or a reserved value such as `WAKEONLAN` in `power` (D5) | "Something in this routine isn't valid: {detail}." [Edit routine] | Through `rejected_by_pc` | "Invalid value" |
| **Skipped and cancelled** | | | | |
| `paused_on_phone` | Pause all on the phone stopped an automatic source | "Routines were paused." [Resume] | Group label "Paused from {phone}" | "Skipped: paused" |
| `paused_on_pc` | Pause all on the PC | "Routines are paused on {pc}." | Banner "Routines are paused on this PC." [Resume] | "Skipped: paused on the PC" |
| `disabled_on_pc` | Switched off on the PC | "Switched off on {pc}." | Card toggle off | "Off on the PC" |
| `skipped_disabled` | Switched off on the phone, then started by a trigger, shortcut, widget or tag | "This routine was switched off." | Card label "Switched off on {phone}" | "Skipped: switched off" |
| `owner_absent` | The phone had not connected for 30 days | "Paused on {pc} because this phone hadn't connected for 30 days. It's active again." | Group label "Suspended: {phone} not seen since {date}" | "Suspended: phone away" |
| `already_running` | Single-flight | "It was already running." | Same text in history | "Already running" |
| `cooldown` | Started again within the minimum interval | "It ran moments ago, so this start was skipped." | Same text in history | "Skipped: too soon" |
| `flap_suppressed` | Home or lock state flipped back and forth | "The trigger changed back and forth too quickly, so this was skipped." | Same text in history | "Skipped: flapping" |
| `rate_limited` | The hourly cap was reached | "It started too many times in the last hour, so RemEx held it back." | Same text in history, plus a page warning | "Skipped: too many runs" |
| `conflict_countdown_active` | Another countdown was already running | "Another countdown was already running on {pc}." | Same text in history | "Skipped: countdown busy" |
| `cancelled_on_pc` | Cancelled on the PC (window, tray or PC Pause all) | "Cancelled on {pc}." | "Cancelled here." Tray: "Cancelled. {routine} did not {action} this PC." | "Cancelled on the PC" |
| `cancelled_on_phone` | Cancelled from the phone (notification, run sheet or phone Pause all) | "Cancelled from your phone." | "Cancelled from {phone}." | "Cancelled on the phone" |
| `interrupted_pc` | RemEx on the PC exited during the run | "{pc} closed RemEx before this finished." | "Interrupted: RemEx closed before this finished." | "Interrupted" |
| `interrupted_phone` | Android restarted the worker during the run | "Android stopped this routine before it finished." | Steps it already sent appear normally in the PC's history | "Interrupted" |
| **Messages, attributes and states** | | | | |
| `notify_queued` | Attribute: `notify(phone)` while the phone was away | "Your phone wasn't connected. The message waits up to an hour." On delivery: "Sent {duration} ago" | "Message waiting for {phone}." | "Message waiting" |
| `notify_expired` | Step status `expired`: not delivered within 1 h | "The message expired before your phone connected." | "Message to {phone} expired." | "Message expired" |
| `notify_denied_phone` | Attribute: notifications are off on the phone | Banner "Notifications are off for RemEx, so results only show here." [Turn on] | Not reached | "Notifications off" |
| `background_restricted` | Attribute and state: Android restricts RemEx in the background | "Android held this routine back while RemEx was restricted in the background." [Fix] | Not reached | "Restricted by Android" |
| `deferred_by_os` | Attribute: Android started the run more than 60 s late | "Android started this late." | Not reached | "Started late" |
| `simulated` | Attribute and step status: test run, destructive step not executed (D7) | "Simulated in this test: {action} wasn't carried out." | History row "Test from {phone}: {action} not carried out." | "Simulated" |
| `dry_run` | Attribute: the PC is in dry-run mode | "{pc} is in dry-run mode, so {action} was only logged." | Banner "Dry run: power actions are logged, not carried out." | "Dry run" |
| `countdown_unseen` | Attribute: the countdown ran while the PC was locked | "Counted down while {pc} was locked." | History "Counted down while locked" | "Countdown while locked" |
| `nfc_unknown_tag` | The tag's routine or token is unknown on this phone, including any other person's phone | "This tag isn't linked to a routine any more." | Not reached | "Unknown tag" |
| `nfc_device_locked` | The tag was tapped while the phone was locked | "Unlock your phone, then tap the tag again." | Not reached | "Phone was locked" |
| `nfc_disabled` | State: NFC is off while an enabled `nfc.tap` routine exists | Banner "NFC is off, so tag routines can't run." [Turn on NFC] | Not reached | No record (state) |
| `fingerprint_capture_failed` | State: a home-capture precondition is unmet | Capture sheet states (§1.4): "Connect to your home Wi-Fi first.", "RemEx can only learn home while it can reach your PC.", VPN: "Turn off your VPN while setting home." | Not reached | No record (state) |
| `home_fingerprint_stale` | State: the home network looks changed | "Your home network looks different. Update it?" [Update] | Not reached | No record (state) |
| `store_reset` | A keystore loss forced a reset of the phone's routines | Banner "RemEx couldn't read your saved routines after a security change on this phone, so they were reset." | After the next sync: "Routines from {phone} were cleared by the phone." | "Routines reset" |
| `internal_error` | An unexpected exception (logged with the stack) | "Something went wrong. The diagnostics log has the details." [Share diagnostics] | "Something went wrong. The log has the details." [Logs] | "Error" |

### 10.2 Changes from the drafts

The UX draft's `skipped_paused` splits into `paused_on_phone` and `paused_on_pc` (the fix action differs), `skipped_in_test` becomes `simulated` (D7), and `after_power_off` now applies only to phone-run routines, because the validator rejects steps after a power-off step in a PC-run routine (`destructive_not_last`). The systems draft's host-flavoured names were renamed to the user-facing `pc_*`, `launch_*`, `power_*`, `cancelled_on_*` forms used above.

---

## 11. Parity table

| Item | Windows host | Linux host | Android |
|---|---|---|---|
| `home.arrive` / `home.leave` | n/a | n/a | `ConnectivityManager.registerNetworkCallback(NetworkRequest, PendingIntent)` + in-process `NetworkCallback` + 15-min `PeriodicWorkRequest`; `getLinkProperties` (routes, DNS, domains, DHCP server) |
| `nfc.tap` | n/a | n/a | `NfcAdapter` tag dispatch `ACTION_NDEF_DISCOVERED`, `DISPATCH_NFC_MESSAGE`-guarded activity, `Ndef.writeNdefMessage` |
| `manual` | PC Run now on the Routines page (`RoutineRunNow`, §8.4) | Same | In-app, `ShortcutManagerCompat.requestPinShortcut`, Glance `actionRunCallback` |
| `pc.sensor` | `TelemetryBackgroundService` readings (`SensorReading.Id`) | Same service, Linux sensor readings | Editor lists sensors from the telemetry stream |
| `pc.idle` | `GetLastInputInfo` / `GetTickCount64` | `org.gnome.Mutter.IdleMonitor.GetIdletime` → `org.freedesktop.ScreenSaver.GetSessionIdleTime` → `XScreenSaverQueryInfo` (libXss) → logind `IdleHint` (coarse) | Editor shows the host's `idleSource` |
| `pc.session` | `WTSRegisterSessionNotification` + `WM_WTSSESSION_CHANGE`; `WTSQuerySessionInformation(WTSSessionInfoEx)` initial | logind Session `LockedHint` `PropertiesChanged` → `org.freedesktop.ScreenSaver`/`org.gnome.ScreenSaver` `ActiveChanged` | Editor shows `sessionSource` |
| `wake` | n/a | n/a | `RemexCoreClient.WakePc` (UDP broadcast from the phone) |
| `waitOnline` | n/a | n/a | `startOneShotConnect` + `isAuthenticated` |
| `delay` | `Task.Delay` with `TimeProvider` | Same | `kotlinx.coroutines.delay` |
| `power` | `WindowsSystemCommandService` via `SharedCommandVerbs`; capability probe `GetPwrCapabilities`, `GetFirmwareType` | `LinuxSystemCommandService` (`systemctl`/`loginctl`, `LinuxSystemCommandService.cs:31-105`); probe logind `CanSuspend`/`CanHibernate`/`CanRebootToFirmwareSetup` | Sent as `routine_step_request` |
| `launchApp` | `AppLauncherService.LaunchAppAsync` → `LaunchStandard` (`AppLauncherService.cs:20`, `:166`) | Same service and code path | Picks from `launcher_sync` entries by `AppEntry.Id` |
| `media` | `SendInput` VK_MEDIA_* through the input service | uinput/portal `KEY_PLAYPAUSE`/`KEY_NEXTSONG`/`KEY_PREVIOUSSONG`, xdotool `XF86MediaPlayPause` on X11 (`LinuxInputEventTranslator.cs:246-308`) | Sent as `routine_step_request` |
| `notify(pc)` | `NotificationRouter` → in-app toast / tray balloon | Same routing and existing Linux presentation of those channels | Sent as `routine_step_request` |
| `notify(phone)` | `routine_notify` (live or queued) | Same | `NotificationManagerCompat`, channel `routines` |
| Countdown | `RoutineCountdownWindow` topmost + tray | Same; on Wayland topmost is a request the compositor may ignore — the tray balloon is the guaranteed surface | Heads-up notification with Cancel |
| Pause all | PC routines page toggle | Same | Routines screen toggle + quick action |
| Run now | `ConfirmationDialogHost` + in-process run (`RoutineRunNow`) | Same | Phone Run of a PC-run routine: `routine_run_request` |
| Test run | Countdown shown, verb not issued | Same | Editor Test; `testRun` flag |
| Storage | `C:\ProgramData\RemEx\routines.json`, admin-only ACL | `~/.local/share/Remex/routines.json`, `0600` | Tink-AEAD DataStores |

---

## 12. Performance & battery budget

| Area | Budget | Rationale | How measured |
|---|---|---|---|
| Phone, no routines | 0 registrations, 0 work | Nothing registered unless a routine needs it | `adb shell dumpsys connectivity` (no RemEx PendingIntent request), `adb shell dumpsys jobscheduler` filtered for `remex` |
| Phone, home routines, idle | 1 PendingIntent network request; wakes only on network-available edges (typically 5–20/day), each ≤ 2 s CPU in a worker | Event-driven | `dumpsys batterystats --charged com.clindsay94.remex` wakeup + job counts over a 24 h soak; Battery Historian |
| Phone, leave fallback | ≤ 96 periodic checks/day, each ≤ 300 ms, **only** while `HOME` and a leave routine exists | Bounded by WorkManager 15-min minimum | Same soak; job count in `dumpsys jobscheduler` |
| Phone, NFC | 0 background cost (system dispatch) | — | — |
| Phone, runs | Connection attempts only when a run needs the PC; `waitOnline` ≤ 1 attempt per 10 s | — | Run history timings + logcat |
| Phone, total added drain | < 0.5 % battery/day with 4 home routines and the app closed | Target | 24 h A/B soak (routines on/off), same device, screen-off, Wi-Fi |
| Host, no routines | 0 timers, 0 D-Bus subscriptions, no telemetry demand | Sources start lazily | `dotnet-counters monitor -p <pid> System.Runtime` (cpu-usage, working-set, timer-count) 30 min |
| Host, `pc.idle` | Windows: one 15 s poll (~µs). Linux: one 15 s D-Bus call or zero polling on logind signals | — | Same counters; `pidstat -p <pid> 5` on Linux |
| Host, `pc.session` | Event-driven, one parked thread on Windows | — | Thread count via counters |
| Host, `pc.sensor` | Holds telemetry demand → existing 1 Hz sampling cost (no new sampler) | Reuses `TelemetryBackgroundService` | CPU with 1 armed sensor routine vs none, 30 min, compared to the perf-audit baseline (`remex-perf-audit` skill) |
| Host memory | ≤ 5 MB working-set increase with 16 routines × 3 owners and full history | Small JSON stores, bounded history | Working-set delta via counters |
| Host disk writes | Only on sync, run start/end, queue change (no periodic writes) | — | Process Monitor / `inotifywait` over a 1 h soak |

---

## 13. Test plan by layer

### 13.1 Core (`remex.core.tests`)

| Test | Proves |
|---|---|
| `RoutineValidatorTests` | Every row of §6.3–§6.5: lengths, ranges, allowed fields, destructive-last, budgets, per-type field rules, unknown types → codes |
| `RoutineMigrationTests` | v1 stamp; newer version untouched; unknown types preserved; fallback never persisted |
| `RoutineWireRoundTripTests` | Each new payload survives `RemexJsonSerializerContext` round trip; plus hand-written JSON **exactly as the Kotlin builder emits** (the `PhoneThemeSnapshotRoundTripTests.cs:54-63` pattern) |
| `RoutineFixtureParityTests` | Golden fixtures in `remex.core.tests/Fixtures/Routines/*.json` are byte-identical to `remex.android/app/src/test/resources/routines/*.json` (drift guard for the Kotlin mirror) |
| `RoutineFixtureValidationTests` | Each fixture's expected verdict (`*.valid.json` / `*.invalid.<code>.json`) matches the C# validator |
| `RoutineReasonCodeParityTests` | C# code list equals the Kotlin list (read from a fixture the Kotlin test also asserts) |
| `HostIdentityVectorTests` | C# `HostIdentity.KeyFor` equals Kotlin `HostIdentity.keyFor` on shared vectors |
| `SensorThresholdTests` | Moved `IsLive` keeps today's behaviour (port the existing SensorViewModel cases) |
| `RoutinesSyncMalformedPayloadTests` | Missing fields, wrong types, unknown discriminators deserialize to rejectable payloads, never null envelopes |
| `HostToClientRoutingTests` / `MessageAudienceTests` (extended) | `routine_` prefix forward exists; audience declares the four host→phone types |
| `RoutineIngressIsolationTests` (alongside the existing `CommandVerbDriftTests`) | `RemexNetworkListener` never mentions routines; `ScriptIngress` is the 11 verbs |

### 13.2 Host runner (`remex.agent.tests`, fake clock/sources)

All sources are behind interfaces (`ISensorFeed`, `IIdleSource`, `ISessionStateSource`, `IPowerExecutor`, `IRoutineUi`) and time is `TimeProvider` (tests use `FakeTimeProvider`, new test-only package `Microsoft.Extensions.TimeProvider.Testing`).

| Test | Proves |
|---|---|
| `SensorTriggerSourceTests` | Sustain, hysteresis clear, 300 s cooldown, missing sensor record, demand acquired/released |
| `IdleTriggerSourceTests` | Fire at N min; one fire per idle period; re-arm on input; media-playing suppression; source-null rejection |
| `SessionTriggerSourceTests` | 3 s settle; flap collapse; 20/h cap; causal suppression after a routine `LOCK` |
| `RoutineRunnerStateMachineTests` | All transitions of §8.1/§8.4; single-flight; 4-run concurrency; first failure stops the run |
| `CountdownCancelTests` | 15 s; each cancel path; one countdown host-wide; locked-session path proceeds with `countdown_unseen`; a confirmed Run now does not count down |
| `RoutineSyncRevisionTests` | stale/equal/conflict/greater; atomic write before reply; `internal_error` on write failure keeps old set |
| `RoutineHostValidationTests` | `wrong_pc`, `launch_not_allowed`, `power_unsupported`, source availability, `step_not_allowed_on_pc` |
| `RoutineRunRevalidationTests` | App removed after sync; owner revoked; verb vanished |
| `RoutineStepRequestIdempotencyTests` | Duplicate `(runId, stepIndex)` returns cached result; `in_progress` while running |
| `RoutineNotifyQueueTests` | Live vs queued, 20 cap, 1 h expiry record, ack removal, countdown never queued, persisted across restart |
| `RoutineRunStoreTests` | Retention, `seq`, startup sweep → `interrupted_pc`, report paging by cursor |
| `MultiOwnerIsolationTests`, `OwnerAbsentSuspensionTests`, `BlockedByPcTests`, `RevokeDeletesRoutinesTests` | §7.4.4 and T7/T8/T17 |
| `RoutineStoreCorruptFileTests`, `RoutineStoreAclTests` (`[WindowsOnlyFact]`), `RoutineStoreLinuxPermissionTests` (Linux-only) | T14 and §6.9 |
| `WtsSessionStateSourceTests` (`[WindowsOnlyFact]`), `LogindSessionSourceTests` (D-Bus message parsing against captured `PropertiesChanged` bodies) | Source adapters |
| `RoutineLogRedactionTests` | T11 |
| `RoutinePowerVerbProbeTests` | Capability probe mapping (`CanHibernate` values, firmware type) |
| `RoutineStepExecutorTests` | `power` goes through `SharedCommandVerbs.TryExecuteAsync`, `launchApp` resolves `appId` then calls `LaunchAppAsync`, `media` emits the right virtual key, `notify(pc)` routes via `NotificationRouter`; no network hop |
| `RoutinesSyncGateTests` | `routines_sync` / `routine_step_request` / `routine_cancel` from an unauthenticated session are dropped |
| `CausalSuppressionTests` | An edge caused by a routine's own step within 5 s triggers nothing |
| `RoutineRateLimitTests` | 4 concurrent runs, one countdown, 30 runs/h/owner, 60 step requests/min, sync coalescing |
| `RoutineDryRunSwitchTests` | Only the command-line switch enables dry run; banner state exposed; destructive verbs not issued |
| `RoutineReasonCodeLocalizationTests` | Every reason code has `Routine_Reason_<code>` and `Routine_History_<code>` in all 9 PC resx files |
| `TestRunSimulationTests` | `testRun` never issues a destructive verb, shows the countdown, records `simulated`; non-destructive steps execute |
| `RunNowCountdownBypassTests` | A confirmed Run now skips the countdown; every wire-initiated path (`routine_step_request`, `routine_run_request`, triggers) counts down |
| `RoutineRunRequestTests` | Runs only the sender's stored routine; unknown id gives `routine_not_found`; respects pause, block and single-flight; emits live reports |
| `PauseAllPropagationTests` | `routines_sync.paused` pauses only that owner's automatic routines; person-initiated runs proceed; `ownerPaused`/`hostPaused` echoed and sent unsolicited on change |
| `UnsolicitedSyncResultTests` | PC toggle, block and Pause all changes reach a connected owner at once |
| `LiveRunReportTests` | Step transitions and countdown start/cancel produce live reports; the cursor is unaffected; an expired notify re-sends the record with a new `seq` |

### 13.3 Android (JVM unit tests unless noted)

| Test | Proves |
|---|---|
| `RoutineValidatorTest` (Kotlin mirror) + `RoutineFixtureTest` | Same fixtures, same verdicts as C# |
| `NetworkFingerprintMatcherTest` | Primary+secondary rule; VPN excluded; Tailscale capture refused; weak fingerprint flag; fact normalization |
| `PresenceStateMachineTest` | `UNKNOWN` never fires; arrive settle; leave debounce reset; 300 s flap gap |
| `LeaveDetectionSchedulingTest` (`work-testing`) | Periodic check only while `HOME` + leave routine; cancelled otherwise |
| `RoutineWorkerTest` (`work-testing`, fake core client) | Step sequencing, timeouts, retries, at-most-once restart, `pc_not_selected`, budget stop, pause cancels |
| `NfcTokenVerifierTest` | T2/T3 |
| `ShortcutSignatureTest` | T1 |
| `RoutineManifestExportTest`, `NoLocationOrExactAlarmPermissionTest`, `BackupRulesRoutineExclusionTest` | T1, T16, T12 (file-parsing tests, the `DeadSdkGuardTest` precedent) |
| `RoutineStoreCryptoTest` (instrumented, release variant on device) | AEAD round trip, keyset-loss recovery is **reported** (`store_reset`) |
| `RoutineSyncClientTest` | Revision bump in same transaction as edit; stale → storedRevision+1 resend once; forget flush |
| `RoutineNotificationRedactionTest` | Private visibility + public version |
| `RoutineMessageRoutingTest` | `onRoutineMessage` dispatch by type; unknown `routine_*` types ignored, not crashing |
| `ForgetPcFlushTest` | T8 |
| `NetworkRegistrationTest` | PendingIntent callback registered on app start, `BOOT_COMPLETED`, `MY_PACKAGE_REPLACED` and edits; unregistered when no home routine remains (fake `ConnectivityManager`) |
| `RoutineManualSurfacesTest` | In-app, shortcut and widget entry points enqueue the same unique work; destructive routines route through the confirm activity |
| `RoutineExportWhitelistTest`, `RoutineImportTest` | §6.10 exclusions; imported routines get new ids, are disabled, and invalid ones are listed with reasons |
| `RoutineReasonCodeLocalizationTest` | Every reason code has `routine_reason_<code>` and `routine_history_<code>` in all 9 `values*/strings.xml` |
| `HomeCaptureApiTest` | Capture result facts and `pcReachable`; refusals for not on Wi-Fi, VPN, Tailscale and PC not on the LAN |
| `ReachableAwayTest` | Authenticating over a non-home network sets `reachableAwayAtUnixMs`; the leave warning is suppressed; `pc_unreachable_away` versus `pc_unreachable` selection |
| `NfcExistingTagTest` | An existing RemEx tag is recognised with its routine name; read-only and too-small tags are refused before writing |
| `CountdownMirrorNotificationTest` | `routine_notify{kind: countdown}` and the phone's own destructive step requests post a countdown notification whose Cancel sends `routine_cancel` |
| `RoutineRetentionTest` | 50 per routine or 30 days, 1,000 cap, 90-day limit |
| `PhoneRunPcRoutineTest` | Run and Test of a PC-run routine send `routine_run_request` and render live reports |

### 13.4 Security regression tests

T1–T20 each name their test in §9. All of them run in `scripts/verify.ps1 -Scope all`; the Android ones are JVM tests except `RoutineStoreCryptoTest`.

### 13.5 Protocol tests

`RoutineProtocolEndToEndTests` (host, in-process `/ws` with a fake phone client): connect → capabilities → `routines_sync` → `routine_sync_result` → trigger fire → `routine_notify` live → disconnect → trigger fire → queued → reconnect → flush → `routine_notify_ack` → `routine_run_report` paging. Plus old-client simulation (no `ClientCapabilities.supportsRoutines` → host sends nothing).

### 13.6 Live verification script

Prerequisites and safety:

0. **Check the foreground app first.** `Get-Process | Where-Object MainWindowTitle | Select-Object ProcessName, MainWindowTitle`. If a game or full-screen app is in front, stop and ask Connor before any UI driving, clicks, app restarts or focusing screenshots.
1. Host: `pwsh scripts/update-local-install.ps1` (elevated). Run the installed `C:\Program Files\RemEx\Remex.Agent.exe --routines-dry-run` — never `dotnet <dll>` (firewall prompt, refuses inbound).
2. Phone build: `pwsh ./scripts/android-fresh.ps1 -Configuration Release`, install the release APK on the AVD `Samsung_Galaxy_S26_Ultra` and on the wireless phone (`adb connect`, may need two attempts). Emulator reaches the PC on its LAN IP.
3. Record the run log in the bead.

| # | Scenario | Device | Expected |
|---|---|---|---|
| L1 | Pair, open Routines, create manual routine `LOCK` + `notify(pc)` | AVD | PC locks; PC toast; history succeeded on phone and PC |
| L2 | Manual routine `power SHUTDOWN` (dry-run host) → cancel on PC | AVD | Countdown window + tray; Cancel → `cancelled_on_pc` both sides |
| L3 | Same, cancel from phone heads-up | Phone | `cancelled_on_phone` |
| L4 | Same, let it expire | AVD | `succeeded` with attribute `dry_run`; PC log shows the verb that would run |
| L5 | `pc.idle` 1 min → `MONITOROFF`; stop touching input | Host | Fires once at ~60 s; re-arms after input |
| L6 | `pc.session locked` → `notify(phone)`; press Win+L | Host + phone | Phone notification; PC history |
| L7 | Repeat L6 with phone in airplane mode; re-enable within 1 h | Phone | Notification delivered once on reconnect; ack clears queue |
| L8 | `pc.sensor` CPU load above (current − 5) for 10 s → `notify(phone)` | Host | Fires after sustain; not again until it clears + 300 s |
| L9 | `home.arrive`/`home.leave` with `leaveDebounceSeconds` 60: capture home on AVD Wi-Fi; `adb shell svc wifi disable`; wait; `enable` | AVD | Leave after ~60 s; arrive ~10 s after re-enable; no fire after reboot at home |
| L10 | Same on the physical phone by toggling Wi-Fi with the app swiped away | Phone | Fires; history shows `deferred_by_os` if Android delayed it |
| L11 | NFC: write tag, tap (unlocked); rewrite tag, tap old clone | Phone + NTAG213 | Runs; old tag → `nfc_unknown_tag` toast |
| L12 | Pinned shortcut and widget for a destructive routine | Phone | Confirm activity appears; run proceeds only after confirm |
| L13 | Remove the app from the PC launcher list, run a `launchApp` routine | AVD | `launch_not_allowed` on phone and PC |
| L14 | Revoke the phone on the PC | Host | PC routines for that phone gone; phone shows "PC no longer paired" |
| L15 | Phone Pause all while a host routine would fire | Both | Skipped `paused_on_phone` on PC history |
| L16 | Linux host (CachyOS, GNOME and KDE sessions): L5, L6, L8 | Linux PC | `idleSource`/`sessionSource` reported correctly; triggers fire |
| L17 | PC keyboard: Tab order Pause all → list → detail, arrows, Space toggles, Shift+F10 menu (R-UX-48) | Host (`ui-verify` UIA tree) | Order and actions as specified; focus ring visible |
| L18 | Large text: Android font scale 200 %, PC maximum UI Size (R-UX-51) | Both | No clipped text; chips wrap |
| L19 | Test run of a routine ending in `SHUTDOWN` on a host **without** dry run | AVD | Countdown on PC and phone; the PC stays on; history `simulated` on both |
| L20 | PC Run now of a `SLEEP` routine, confirmed (dry-run host) | Host | Confirm dialog, no countdown, history source `manual.pcRunNow` |
| L21 | Performance soak per §12: 24 h phone A/B with 4 home routines (`dumpsys batterystats`, `dumpsys jobscheduler`), 30 min PC counters with no routines and with one of each PC trigger (`dotnet-counters`, `pidstat` on Linux) | Both | Every number within its §12 budget; results recorded in the S6 bead |

Screenshots: PC routines page and countdown via the `ui-verify` skill in light and dark across at least two seeds; Android routines screens via `adb exec-out screencap -p` on both devices.

### 13.7 UX tests

The Compose, axaml-guard, structural and unit tests named in the R-UX rows of §16 are part of this plan and run in the same gates (`scripts/verify.ps1 -Scope all`). The structural scans (`RoutinesReducedMotionScanTest`, `RoutinesColorLiteralScanTest`, `RoutinesNoConcatScanTest`) cover both codebases.

---

## 14. Implementation slices

**Definition of done for every slice** (in addition to its own acceptance):

- `scripts/verify.ps1 -Check` VALID, run once for the slice after the last change (not per change); `-Scope all` whenever Android code changed.
- `gradlew assembleRelease` green including `lintVitalRelease` (release variant only, never debug).
- `scripts/check-localization.ps1` green: every new string in all 9 files per platform (PC `Strings*.resx`, Android `values*/strings.xml`), reason-code strings included.
- `csharp-reviewer` pass for C# changes; `kotlin-reviewer` pass for Kotlin changes; findings addressed.
- `ui-verify` screenshots of changed PC pages in light and dark across ≥ 2 seeds; Android on-device screenshots (AVD + phone).
- FAQ parity: any FAQ entry added on one platform is added on the other and to `docs/FAQ-PARITY.md`.
- `docs/CHANGELOG.md` entry.
- Hand-written `docs/REGRESSION-GUARDS.md` entries for every new silent-failure risk the slice introduces (listed per slice).
- No version bump (.NET `<Version>`, Android `versionCode`/`versionName`).
- Beads: slice bead closed; commit with a conventional prefix and the slice's bead ID (for example `feat(routines): …` followed by the ID in parentheses).

**Touches guarded code** (run `gitnexus impact` first, record risk in the bead): `AndroidNativeExports.OnNativeMessageReceived`, `MessageAudience`, `PingPongHandler` message switch, `HostCapabilities`/`HostCapabilitiesProvider`, `ClientCapabilities`, `PairedDeviceRevoker.RevokeAsync`, `SensorViewModel.IsAlertLive` (moved), `backup_rules.xml`/`data_extraction_rules.xml`, `AndroidManifest.xml`, the phone forget-PC flow around `PinnedHostStore.forgetHost`, and `ShellViewModel`'s page-transition direction (`ShellViewModel.cs:1436-1460`, R-UX-03).

| # | Slice | Scope | Delivers (R-ids, §16) | Acceptance (in addition to DoD) | New regression guards |
|---|---|---|---|---|---|
| S1 | **Core, PC step executor, phone editor and manual routines** | Core models, validator, migration, limits, reason codes and fixtures; Kotlin mirror; `HostIdentity` C# port; capabilities (`supportsRoutines`, `routinePowerVerbs`, `ClientCapabilities.supportsRoutines`); `routine_step_request` and `routine_step_result` with `testRun`, and `routine_cancel`; PC step executor; countdown window, the phone's countdown mirror for its own requests, test-run simulation, `--routines-dry-run`; phone encrypted store; Routines in More with the New badge, list, empty state, gallery (the `manual` templates), editor with validation (§1.6–§1.8), in-app Run and Test; phone steps `wake`, `waitOnline`, `delay`, `notify(phone)`; Pause all on the phone; history and run detail; the three notification channels; Android coach mark; the `routine_` router forward and audience; backup exclusions. The trigger picker offers `manual` only; each later slice adds its trigger family | R-UX-01, 05, 07–11, 15–20, 27, 28, 30–32, 34, 38, 39, 41, 45, 46, 49–58; R-SYS-01–05, 10–13, 23–25, 28, 29, 31, 34, 42; R-SEC-05, 09–11, 13, 16, 19, 20, 22 | L1–L4, L13, L19 pass; §13.1 tests plus the executor, countdown, idempotency, test-run and dry-run tests green; an old PC shows `pc_too_old` | `routine_` prefix forward; the countdown window must not depend on `MainWindow`; no `required` members in routine payloads; a test run never issues a destructive verb |
| S2 | **Phone manual surfaces and NFC** | Pinned shortcut, dynamic shortcuts, widget, confirm activity, Remote Control "Your routines"; NFC trigger, write sheet with existing-tag detection and pre-write checks, token rotation; manifest changes; the NFC and widget templates | R-UX-06, 13, 14, 24–26; R-SYS-17, 18, 41; R-SEC-01–04 | L11, L12; T1–T3 tests; manifest export test; widget taps work in a release build | `NfcRoutineActivity` must keep `DISPATCH_NFC_MESSAGE`; the shortcut `sig` check |
| S3 | **Home presence** | Home capture sheet and API, fingerprint match, presence state machine, PendingIntent receiver, in-process callback, periodic leave fallback, boot and package-replaced re-registration, drift prompt, `reachableAway`, the leave-home authoring warning and `pc_unreachable_away`, the background-restricted card, the WorkManager periodic work; the home templates | R-UX-12; R-SYS-14–16, 39, 40; R-SEC-14 (phone side), 16, 17 | L9, L10; §13.3 presence tests; 24 h battery soak within §12 | "PendingIntent network callbacks only report availability, so leave detection needs the fallback"; "registrations die on reboot and on update" |
| S4 | **PC-run routines and the PC Routines page** | `routines_sync` and `routine_sync_result` (including unsolicited results), `routine_run_request`, PC store with ACL, owner bookkeeping (block, PC disable, owner-absent), revoke hook, forget flush, Pause-all propagation, `pc.idle` and `pc.session` sources on Windows and Linux, PC runner, PC history and `routine_run_report` with live updates; PC Routines page: drawer item and the nav direction fix (R-UX-03), list and detail, enable toggle, Run now with presence, Pause all, coach mark, palette commands; the idle and session templates | R-UX-02–04, 21, 29, 35–37, 40, 47, 48 and the PC side of 49–54, 56; R-SYS-06–09, 19, 21, 22, 27, 32, 35–38 and the PC side of 25, 42; R-SEC-06–08, 12, 14, 15, 21, 23 | L5, L6, L14–L17, L20; §13.2 tests; WSL Linux test run green with skips only; `ui-verify` of the Routines page | "The session source owns its own message-only window"; "logind Lock/Unlock signals are requests, not state"; "Run now's presence flag has no wire representation" |
| S5 | **Sensor trigger and PC-to-phone messages** | Move `IsAlertLive` to core; `pc.sensor` source holding telemetry demand; `routine_notify` live, queued and acknowledged, including the countdown heads-up for PC runs; queued-message presentation on the phone; the sensor templates | R-UX-33 and the PC-run part of 34; R-SYS-20, 26 | L3 (PC-run variant), L7, L8; queue tests | "Sensor routines must hold telemetry demand or they never see a sample" |
| S6 | **Export and import, tutorial, FAQ, docs** | `.remexroutines` export and import with the review sheet; Android and PC tutorial pages; FAQ 17–22 on both platforms and `docs/FAQ-PARITY.md`; `docs/API_CONTRACTS.md` section 8; `docs/SECURITY_EXPLAINED.md` routines section; the perf measurement report in the bead | R-UX-22, 23, 42–44; R-SYS-30, 33; R-SEC-18 | Export whitelist and import tests; `FaqParityTests`; L18; perf numbers within §12 | — |
| S7 (post-3.0) | **Matter bridge spike** | §15 | — | Separate spec and security review | — |

Each slice ships a user-reachable path (AGENTS.md "put the join in the FIRST half"): S1 already runs a routine end to end; no slice lands logic without its caller.

---

## 15. Smart-home feasibility & roadmap

### 15.1 Options

| Option | Infrastructure | Cloud path to the PC | Certification | Ecosystems | Effort |
|---|---|---|---|---|---|
| **Local Matter bridge** (sidecar on the PC exposing routines as OnOff endpoints) | Node.js sidecar (matter.js / matterbridge); LAN IPv6 link-local, mDNS (`_matter._tcp`), UDP 5540 | No RemEx cloud. But each commissioned ecosystem controller holds fabric-admin authority and can relay remote commands from its own cloud (e.g. Apple home hubs relay away-from-home control via iCloud) | Not required to function for personal use; CSA certification only for a "Works with" badge or distribution as a certified product | SmartThings, Google Home, Alexa, Apple Home, Home Assistant | Low–medium |
| Cloud connectors (SmartThings Schema, Google Home cloud-to-cloud, Alexa Smart Home skill) | Public HTTPS endpoint + OAuth2 authorization server per ecosystem, operated by RemEx | **Yes**, by design | Per-ecosystem review/certification | One integration per ecosystem; no Apple equivalent | Medium–high ×3 |
| SmartThings Edge driver (Lua on the hub, LAN socket to the PC) | Physical SmartThings hub; invite-only driver channel | No (hub-local) | None (developer account) | SmartThings only | Low–medium |
| Google Home Local Home SDK | Still requires the full cloud-to-cloud integration; local execution is an optimization on top | Yes | Same as cloud-to-cloud | Google only | Medium–high |

### 15.2 Findings (cited)

- Matter devices are controlled locally; each controller is its own fabric (multi-admin). https://www.home-assistant.io/integrations/matter/
- CSA "Joint Fabric" work (Matter 1.6) further formalizes cross-ecosystem co-administration. https://www.theverge.com/tech/950679/matter-1-6-spec-smart-home-joint-fabric-apple-amazon-google
- Test vendor IDs 0xFFF1–0xFFF4 are for testing; Google documents commissioning/control of test devices via a Developer Console project. https://developers.home.google.com/matter/test
- SmartThings offers self-test certification and "Works with SmartThings" via CSA testing. https://developer.smartthings.com/docs/certification/certification-with-csa
- CSA membership: Adopter about $7,500/yr plus per-product fees; lab testing adds several thousand dollars per product. https://csa-iot.org/become-member/
- No mature .NET Matter stack was found. matter.js (Node/TypeScript) underpins **matterbridge**, which runs on Linux, macOS and Windows without a hub, ships without CSA certification, and is reported working with Apple Home, Google Home, Alexa, SmartThings and Home Assistant. https://matterbridge.io/README.html
- connectedhomeip (C++) builds for Linux/Darwin; Windows tooling is weak in practice. https://tomasmcguinness.com/2025/03/05/the-matter-chip-tool-doesnt-work-on-windows-so-i-use-an-raspberry-pi-instead/
- Matter needs IPv6 on the LAN and fails behind VLAN/firewall setups that block mDNS/UDP 5540. https://matterbridge.io/README.html
- PASE secures commissioning; CASE secures operational traffic. https://docs.silabs.com/matter/latest/matter-fundamentals-security/
- A commissioned admin (and therefore that admin's cloud) is fully trusted by the device; the device does not verify its controller's trustworthiness. https://tsapps.nist.gov/publication/get_pdf.cfm?pub_id=956487
- Apple remote access to home accessories goes through a home hub and iCloud. https://support.apple.com/en-us/102557
- SmartThings Schema needs the developer's OAuth2 server and HTTPS endpoint. https://developer.smartthings.com/docs/devices/cloud-connected/get-started
- Alexa Smart Home skills require OAuth2 account linking and a hosted skill endpoint. https://developer.amazon.com/docs/alexaplus/account-linking/account-linking-for-sh-and-other.html
- Google Local Home SDK is layered on the mandatory cloud-to-cloud integration. https://developers.home.google.com/local-home/overview

### 15.3 Recommendation

1. **Direction: local Matter bridge; reject cloud connectors.** Cloud connectors require exactly the RemEx-operated OAuth/cloud trust path the product avoids, and cost three separate integrations. SmartThings Edge is a SmartThings-only fallback, not a strategy.
2. **Be precise about "no cloud trust path".** A Matter bridge adds no RemEx cloud, but every ecosystem the user commissions becomes a controller whose own cloud can relay commands to the bridge. That is intrinsic to Matter. The design therefore limits what that path can do rather than pretending it does not exist:
   - Exposure is **opt-in per routine**, off by default.
   - Only routines whose steps are all non-destructive (`launchApp`, `media`, `notify`, `LOCK`, `MONITOROFF`, `delay`) may be exposed; destructive verbs are never exposable, so a compromised ecosystem cloud cannot power the PC off or restart it.
   - Each exposure is shown on the PC routines page with the list of commissioned fabrics and a "Remove all smart-home access" action (decommission).
3. **Architecture: matter.js sidecar, not a .NET port.** Run matterbridge/matter.js as a child process of `remex.agent`, **unelevated**, talking to the agent over its redirected stdin/stdout (JSON lines). No loopback socket, no named pipe, nothing another local process can connect to (loopback is not a trust boundary here, `REGRESSION-GUARDS.md:970-978`). The sidecar's Matter listener (UDP 5540 + mDNS) is new LAN ingress; it is CASE-authenticated but must be called out in `docs/SECURITY_EXPLAINED.md` and default-off.
4. **Endpoint model:** one OnOff "momentary switch" endpoint per exposed routine (turning on runs the routine, then the endpoint resets to off after the run ends; the run outcome is reflected in the endpoint's state and in RemEx history with trigger `smarthome.matter`). Scenes add ecosystem-specific friction and give no extra capability.
5. **Certification:** ship uncertified as an explicitly "experimental" feature for personal use; revisit CSA membership only if download numbers justify a certified product.
6. **Waking the PC from a smart home** is out of reach for a bridge that runs on the PC. It would need an always-on device (e.g. a Raspberry Pi running the same sidecar with only a `wake` action) — a later option, not in the roadmap below.
7. **Roadmap placement:** after the 3.0 core ships and stabilizes. 3.1: no smart-home work (bug fixes, schedule triggers). 3.2: Matter bridge spike behind a feature flag with its own spec, threat model update and a Windows + Linux IPv6/mDNS compatibility test matrix. 3.3: general availability if the spike's security review and field testing pass. The new trigger id for this path is `smarthome.matter` (reserved now, not in the v1 enum).

---

## 16. Requirements & traceability

### 16.1 Traceability

One row per requirement: acceptance, the named test that proves it, platform, and the slice that delivers it (§14). Test kinds: **unit** (JVM / xUnit), **structural** (source scan), **axaml-guard** (xUnit over parsed .axaml), **Compose** (androidTest), **live** (a numbered scenario of the §13.6 script, on a release build or the installed PC app; never while Connor is gaming). "(new)" marks tests that do not exist yet. Tests named only in the R-UX rows are part of the test plan (§13.7).

| ID | Requirement | Acceptance | Test | Platform | Slice |
|---|---|---|---|---|---|
| R-UX-01 | Routines is first in `moreItems` | `moreItems[0] == Screen.Routines`; rendered first in More sheet and rail | unit `NavRoutesRoutinesPlacementTest` (new) | Android | S1 |
| R-UX-02 | PC drawer item after Commands, Tag 10 | ListBoxItem Tag 10 is the 4th child; has `AutomationProperties.Name`, tooltip, `nav-item` class; `nth-child(11)` entrance exists and last delay+duration ≤ 300 ms | axaml-guard `ShellNavRoutinesTests` (new) | PC | S4 |
| R-UX-03 | Shared-axis direction follows visual order | Commands → Routines animates forward, Routines → Launcher forward, About ↔ Settings correct | unit `ShellViewModelNavDirectionTests` (new) | PC | S4 |
| R-UX-04 | PageHost renders RoutinesView | DataTemplate for `RoutinesViewModel` present | axaml-guard `ShellNavRoutinesTests` | PC | S4 |
| R-UX-05 | "New" badge on More until first open | Badge visible with stateDescription "New" until Routines opened once; never again after | Compose `MoreNewBadgeTest` (new) | Android | S1 |
| R-UX-06 | Remote Control "Your routines" | Section lists `manual` routines targeting the connected PC; absent when none; destructive ones use the confirm face | Compose `RemoteControlRoutinesSectionTest` (new) | Android | S2 |
| R-UX-07 | Empty state teaches | Empty list shows 3 featured templates (NFC one swapped without NFC), Browse all, Start from blank; no FAB menu overlap | Compose `RoutinesEmptyStateTest` (new) | Android | S1 |
| R-UX-08 | Template creates nothing until Save | Opening then leaving a template leaves the routine count unchanged | unit `RoutineEditorViewModelTest.templateDoesNotPersistUntilSave` (new) | Android | S1 |
| R-UX-09 | Save with problems guides | Save scrolls to the first problem, outlines all, snackbar count uses plurals | Compose `RoutineEditorValidationTest` (new) | Android | S1 |
| R-UX-10 | Runs-on chip and step availability | Chip text derives from trigger; unavailable step tiles disabled with the reason string | unit `StepAvailabilityTest` (new) | Android | S1 |
| R-UX-11 | Trigger change that invalidates steps asks first | Changing to a `pc.*` trigger with a `wake` step shows the remove prompt; cancel keeps the old trigger | Compose `TriggerChangeTest` (new) | Android | S1 |
| R-UX-12 | Home capture only when possible | Capture button enabled only on Wi-Fi with the PC reachable; facts shown; "no location" line present | Compose `HomeCaptureSheetTest` (new, fake network source) + live | Android | S3 |
| R-UX-13 | NFC availability states | No hardware: option disabled with reason; NFC off: settings action | Compose `NfcTriggerOptionTest` (new, fake adapter) | Android | S2 |
| R-UX-14 | NFC write states | Every row of 1.5 reachable from the reducer; success/failure haptics fire | unit `NfcWriteReducerTest` (new) + live on phone | Android | S2 |
| R-UX-15 | Duplicate | Copy named "Name (copy)", switched off, same steps | unit `RoutineRepositoryTest.duplicate` (new) | Android | S1 |
| R-UX-16 | Test run | Available for any saved routine; unsaved edits offer "Save and test"; non-destructive steps run for real, destructive steps are simulated (countdown shown on PC and phone, verb not executed, run marked `simulated`) | Compose `RoutineTestRunDialogTest` (new) + `TestRunSimulationTests` | Android, PC | S1 |
| R-UX-17 | Step reorder, touch and TalkBack | Drag reorders; custom actions Move up/down/top/bottom exist and announce "Step 2 of 4" | Compose `StepReorderTest` (new) | Android | S1 |
| R-UX-18 | Routine list reorder persists | Order within a Runs-on group survives restart | unit `RoutineOrderTest` (new) | Android | S1 |
| R-UX-19 | Enable switch | Card and editor switches share state; stateDescription on/off | Compose `RoutineCardSemanticsTest` (new) | Android | S1 |
| R-UX-20 | Pause all on Android | Toggle shows banner, dims cards, persists, Resume clears; manual Run still available | Compose `PauseAllTest` (new) | Android | S1 |
| R-UX-21 | Pause all on PC | ToggleSwitch + banner + palette commands "Pause all routines"/"Resume routines" | axaml-guard `RoutinesViewTests` (new) + unit `CommandPaletteRoutinesTests` (new) | PC | S4 |
| R-UX-22 | Export format | `.remexroutines`, `formatVersion` 1, camelCase; no home facts, tag bindings or host identity in output | unit `RoutineExchangeTest.exportExcludesPrivateFields` (new) | Android | S6 |
| R-UX-23 | Import safety and errors | Imported routines switched off; fix-ups listed; bad file shows the failure title, newer version shows the update message | unit `RoutineExchangeTest.import*` (new) + Compose `ImportReviewSheetTest` (new) | Android | S6 |
| R-UX-24 | Pinned shortcut | Offered only for `manual`; tap runs without opening the app; destructive confirms in the translucent activity; deleted routine disables the shortcut with a message | unit `RoutineShortcutsTest` (new) + live (release build) | Android | S2 |
| R-UX-25 | Widget | Config lists only `manual` routines; widget shows last result and running state; taps work in a **release** build | live (release, per `glance-actioncallback-r8-init`) + unit `RoutineWidgetStateTest` (new) | Android | S2 |
| R-UX-26 | Dynamic app shortcuts | Up to 4 most recently run `manual` routines, updated after each run | unit `RoutineShortcutsTest.dynamic` (new) | Android | S2 |
| R-UX-27 | History list | Newest first, grouped by day, outcome as icon + text | Compose `RoutineHistoryListTest` (new) | Android | S1 |
| R-UX-28 | Run detail | Every non-success step shows its reason message and contextual fix; "Edited since this run" when revisions differ | Compose `RoutineRunDetailTest` (new) | Android | S1 |
| R-UX-29 | PC routine history on phone | Visible when connected; offline shows cached with "As of" time | unit `PcRoutineHistoryRepositoryTest` (new) | Android | S4 |
| R-UX-30 | Progress notification | Ongoing, silent, step x of n, Cancel/Open; ProgressStyle segments on API 36+ | unit `RoutineNotificationBuilderTest` (new) + live | Android | S1 |
| R-UX-31 | Result notification | Success times out after 10 s; failure persists with "See what happened" | unit `RoutineNotificationBuilderTest` | Android | S1 |
| R-UX-32 | Three channels | "Routine progress", "Routine results", "Routine messages" created with localized names | unit `RoutineChannelsTest` (new) | Android | S1 |
| R-UX-33 | Queued messages | Shown on next connect with "Sent N min ago"; expired ones only in run detail | unit `QueuedMessagePresenterTest` (new) + live | Android | S5 |
| R-UX-34 | Countdown mirrored on phone | When connected, notification "Gaming PC shuts down in N s" with working Cancel, for host runs and for the phone's own step requests | unit `CountdownMirrorNotificationTest` (new) + live L3 | Android | S1, S5 |
| R-UX-35 | PC page has no editing | RoutinesView contains no TextBox, no add/edit/delete commands; header copy says the phone is the editor; the only actions are enable toggle, Run now, Pause all and history | axaml-guard `RoutinesViewTests.NoEditingControls` (new) | PC | S4 |
| R-UX-36 | PC Run now | Confirms through `ConfirmationDialogHost` when destructive (danger/warning tint by verb); declines when no visible parent; a confirmed Run now runs destructive steps without the countdown | unit `RoutinesViewModelRunNowTests` (new) | PC | S4 |
| R-UX-37 | PC enable toggle | Toggle updates the card, is sent to the phone, and a rejected routine's toggle is disabled with its reason | unit `RoutinesViewModelTests` (new) | PC | S4 |
| R-UX-38 | Countdown window | Topmost, activates, Cancel is default and cancel; shows when `MainWindow` was never constructed; closes 180 ms after Cancel | unit `RoutineCountdownPresenterTests` (new) + live | PC | S1 |
| R-UX-39 | Countdown announcements | UIA name set; live region announces at 15, 10 and 5 s, not every second | axaml-guard `RoutineCountdownWindowTests` (new) + live (UIA dump) | PC | S1 |
| R-UX-40 | PC coach mark | Shown once, keyed `"routines"`; "?" replays | unit `RoutinesViewModelCoachTests` (new) | PC | S4 |
| R-UX-41 | Android coach | Shown once after first routine exists; `routines_coach_seen` in the export whitelist | unit `SettingsExportTest` (extend) + Compose `RoutinesCoachTest` (new) | Android | S1 |
| R-UX-42 | Android tutorial page | Page present before "You're All Set!", `bodyArgRes` names Routines by its own title | unit `TutorialPagesTest` (new) | Android | S6 |
| R-UX-43 | PC tutorial page | Author index 16 Routines, Finish 17; carousel has the panel; paging counts updated; empty-state link lands on it | unit `TutorialCarouselSourceScanTests`, `ShellViewModelTutorialPagingTests`, `TutorialNavigatorTests` (extend) | PC | S6 |
| R-UX-44 | FAQ parity | 22 entries each side; every row of `FAQ-PARITY.md` resolves to a key in all 9 files on both platforms | unit `FaqParityTests` (new, parses the table) | both | S6 |
| R-UX-45 | TalkBack card description | Merged: name, trigger, step count (plurals), on/off, last result | Compose `RoutineCardSemanticsTest` | Android | S1 |
| R-UX-46 | Touch targets | Every interactive element in routines UI ≥ 48dp, including drag handles and chips in sheets | Compose `RoutinesTouchTargetTest` (new, `assertTouchHeightIsAtLeast`) | Android | S1 |
| R-UX-47 | PC names and classes | Every Button/ToggleSwitch in RoutinesView and the countdown window has `AutomationProperties.Name`; every Button has a vocabulary class | axaml-guard: `ButtonVocabularyTests.EveryButtonDeclaresAClass` (covers new files), `RoutinesViewTests` (new) | PC | S4 |
| R-UX-48 | PC keyboard | Tab order: Pause all → list → detail; arrows move in the list; Space toggles enable; context menu (Shift+F10) has Run now, Turn off, History; focus ring visible | axaml-guard `RoutinesViewKeyboardTests` (new) + live L17 | PC | S4 |
| R-UX-49 | Contrast across seeds | Outcome/status tokens and destructive tints ≥ 4.5:1 ink on container for every built-in seed × light/dark × contrast −0.5/0/0.5/1 | unit `AccentForegroundContrastTests` (extend) / `RoutineStatusContrastTest` (new, JVM) | both | S1, S4 |
| R-UX-50 | Not colour alone | Every outcome shows an icon and text; destructive steps also carry the verb label | Compose `RoutineHistoryListTest`; axaml-guard `RoutinesViewTests` | both | S1, S4 |
| R-UX-51 | Large text | Font scale 200 % (Android) and max UI Size (PC): no clipped text, chips wrap | Compose `RoutinesLargeFontTest` (new, fontScale 2) + live L18 | both | S1, S4 |
| R-UX-52 | Reduced motion | Android: every `infiniteRepeatable` / choreographed animation in routines UI is gated on `LocalReducedMotion`; PC: every routines animation class-gated on `IsReducedMotion`, countdown reads the profile flag | structural `RoutinesReducedMotionScanTest` (new, both repos) | both | S1, S4 |
| R-UX-53 | No colour literals | No `Color(0x…)`/`#RRGGBB` in routines UI; roles only | structural `RoutinesColorLiteralScanTest` (new) | both | S1, S4 |
| R-UX-54 | Destructive tint | Discards-work `power` steps use the error role in editor, list tokens and PC chips | Compose `StepCardTintTest` (new); axaml-guard `RoutinesViewTests` | both | S1, S4 |
| R-UX-55 | Editor messages | Every rule in 1.8 produces its message; warnings never block Save | unit `RoutineValidatorMessagesTest` (new) | Android | S1 |
| R-UX-56 | Localization completeness | All new keys in 9 files per platform; `scripts/check-localization.ps1` passes; no sentence built by concatenation (chip chain is separate tokens, counts are plurals/format strings) | script + structural `RoutinesNoConcatScanTest` (new) | both | S1, S4 |
| R-UX-57 | Routine names are data | A routine created in English keeps its name after switching the app to Polish; PC shows names verbatim | unit `RoutineNameTest` (new) | both | S1 |
| R-UX-58 | Unsaved changes | Leaving the editor with edits asks "Discard changes?" | Compose `RoutineEditorDiscardTest` (new) | Android | S1 |
| R-SYS-01 | Shared schema in core with Kotlin mirror | Fixtures byte-identical and verdicts equal on both sides | `RoutineFixtureParityTests`, `RoutineFixtureValidationTests`, `RoutineFixtureTest` | Core, Android | S1 |
| R-SYS-02 | Field constraints and limits per §6.2–§6.5 | Every constraint has a passing and a failing fixture | `RoutineValidatorTests`, `RoutineValidatorTest` | Core, Android | S1 |
| R-SYS-03 | Destructive step at most once and last | Fixture with destructive mid-routine rejected `destructive_not_last` | `RoutineValidatorTests` | Core, Android | S1 |
| R-SYS-04 | Schema migration never stamps down or persists fallbacks | Newer doc untouched; failed read never written back | `RoutineMigrationTests`, `RoutineStoreCorruptFileTests` | Core, Host, Android | S1 |
| R-SYS-05 | Wire payloads tolerate malformed input without dropping the session | Malformed `routines_sync` answered with a result; socket stays open | `RoutinesSyncMalformedPayloadTests` | Core, Host | S1 |
| R-SYS-06 | Full-state sync with monotonic revision | stale/equal/conflict/greater cases behave per §7.4 | `RoutineSyncRevisionTests`, `RoutineSyncClientTest` | Host, Android | S4 |
| R-SYS-07 | Host persists before replying | Write failure → `internal_error`, old set kept | `RoutineSyncRevisionTests` | Host | S4 |
| R-SYS-08 | Per-owner isolation on a shared PC | Phone B receives nothing of phone A | `MultiOwnerIsolationTests` | Host | S4 |
| R-SYS-09 | Per-host partition on the phone | Only host H's `pc.*` subset sent to H | `RoutineSyncClientTest` | Android | S4 |
| R-SYS-10 | Capability-gated protocol, no `protocolVersion` bump | Old client never receives `routine_*`; envelope version stays 2 | `RoutineProtocolEndToEndTests` | Core, Host | S1 |
| R-SYS-11 | `routine_` prefix forward to Kotlin | Removing the forward fails a test | `HostToClientRoutingTests`, `MessageAudienceTests` | Core | S1 |
| R-SYS-12 | Phone runner single-flight, ordered steps, budget | Second trigger during a run → `already_running`; 540 s stop | `RoutineWorkerTest` | Android | S1 |
| R-SYS-13 | At-most-once host-executed steps | Worker restart after a started step → `interrupted_phone`, no resend | `RoutineWorkerTest`, `RoutineStepRequestIdempotencyTests` | Android, Host | S1 |
| R-SYS-14 | Home presence without location | Arrive/leave per §8.3.1; no location permission in manifest | `PresenceStateMachineTest`, `NoLocationOrExactAlarmPermissionTest` | Android | S3 |
| R-SYS-15 | Leave detection works with always-on mobile data | Periodic fallback scheduled only while `HOME` + leave routine | `LeaveDetectionSchedulingTest` | Android | S3 |
| R-SYS-16 | Network callbacks survive reboot/update | Re-registered on boot and package replace | `NetworkRegistrationTest` | Android | S3 |
| R-SYS-17 | NFC tag format and token check | Valid tap runs; invalid token refused with record | `NfcTokenVerifierTest` | Android | S2 |
| R-SYS-18 | Manual surfaces (app, shortcut, widget) | Each enqueues a run; destructive routines show confirm | `RoutineManualSurfacesTest` + L12 | Android | S2 |
| R-SYS-19 | Host runner state machine | All transitions of §8.4 | `RoutineRunnerStateMachineTests` | Host | S4 |
| R-SYS-20 | `pc.sensor` sustain + hysteresis + cooldown, demand held only when needed | Per §8.5.1 | `SensorTriggerSourceTests` | Host (Win, Linux) | S5 |
| R-SYS-21 | `pc.idle` with platform sources and media suppression | Per §8.5.2; unavailable source advertised and rejected | `IdleTriggerSourceTests` + L5/L16 | Host (Win, Linux) | S4 |
| R-SYS-22 | `pc.session` lock/unlock with settle and flap cap | Per §8.5.3 | `SessionTriggerSourceTests`, `WtsSessionStateSourceTests`, `LogindSessionSourceTests` | Host (Win, Linux) | S4 |
| R-SYS-23 | Steps execute in-process on the host | Executor calls `SharedCommandVerbs`/`AppLauncherService`; no network hop | `RoutineStepExecutorTests` | Host | S1 |
| R-SYS-24 | 15 s cancellable countdown for every routine destructive verb except a confirmed PC Run now | Countdown shown (or recorded `countdown_unseen`), mirrored on the phone, every cancel path works | `CountdownCancelTests`, `CountdownMirrorNotificationTest` + L2–L4 | Host, Android | S1 |
| R-SYS-25 | Pause all on both apps stops automatic sources | Automatic triggers recorded skipped, running automatic runs cancelled, person-initiated runs proceed | `RoutineRunnerStateMachineTests`, `RoutineWorkerTest`, `PauseAllPropagationTests` + L15 | Host, Android | S1, S4 |
| R-SYS-26 | `routine_notify` live, queued (1 h), acked, mirrored on PC | Per §7.3.5 | `RoutineNotifyQueueTests` + L7 | Host, Android | S5 |
| R-SYS-27 | Host run history reported to phone by cursor | Pages delivered once; cursor advances only after store | `RoutineRunStoreTests`, `RoutineProtocolEndToEndTests` | Host, Android | S4 |
| R-SYS-28 | History retention and interruption records | Caps enforced; `interrupted_*` on restart | `RoutineRunStoreTests`, `RoutineWorkerTest` | Host, Android | S1 |
| R-SYS-29 | Every failure has a reason code and a visible state | Each §10.1 code has a localized string on both platforms and at least one test producing it | `RoutineReasonCodeLocalizationTests` (PC), `RoutineReasonCodeLocalizationTest` (Android) + per-code tests | All | S1–S6 |
| R-SYS-30 | Export/import format versioned with whitelist | Round trip; excluded fields absent; imported routines disabled | `RoutineExportWhitelistTest`, `RoutineImportTest` | Android | S6 |
| R-SYS-31 | Power verb capability reflects the machine | Probe mapping correct; unsupported verb disabled in editor | `RoutinePowerVerbProbeTests` | Host (Win, Linux) | S1 |
| R-SYS-32 | Host-run budgets and concurrency | ≤ 4 concurrent, one countdown, 30 runs/h/owner | `RoutineRateLimitTests` | Host | S4 |
| R-SYS-33 | Performance budgets of §12 | Measured numbers recorded in the S6 bead are within budget | live L21 (`dumpsys batterystats`, `dotnet-counters`) | Host, Android | S6 |
| R-SEC-01 | No spoofable entry points | Only `NfcRoutineActivity` exported, with `DISPATCH_NFC_MESSAGE` | `RoutineManifestExportTest` | Android | S2 |
| R-SEC-02 | Shortcut launches are signed | Unsigned/invalid `sig` never runs | `ShortcutSignatureTest` | Android | S2 |
| R-SEC-03 | NFC tokens are unguessable and rotatable | 128-bit token; rotate invalidates old | `NfcTokenVerifierTest` | Android | S2 |
| R-SEC-04 | NFC requires an unlocked device | Locked → `nfc_device_locked` | `NfcTokenVerifierTest` | Android | S2 |
| R-SEC-05 | Sync only on authenticated sessions | Unauthenticated `routines_sync` dropped | `RoutinesSyncGateTests` | Host | S1 |
| R-SEC-06 | Host revalidates at receipt and at run | App removal/revocation/verb loss caught at run | `RoutineHostValidationTests`, `RoutineRunRevalidationTests` | Host | S4 |
| R-SEC-07 | Host verifies `hostIdentity` | Routine for another PC → `wrong_pc` | `RoutineHostValidationTests` | Host | S4 |
| R-SEC-08 | Revoke/forget delete routines on both sides | Per §7.4.5 | `RevokeDeletesRoutinesTests`, `ForgetPcFlushTest` | Host, Android | S4 |
| R-SEC-09 | Lock-screen redaction | Private visibility + public version | `RoutineNotificationRedactionTest` | Android | S1 |
| R-SEC-10 | Routine data excluded from backup and device transfer | Both XML files list every routine store | `BackupRulesRoutineExclusionTest` | Android | S1 |
| R-SEC-11 | Log redaction | No tokens/keys/fingerprints/bodies; masked MAC; redacted client id | `RoutineLogRedactionTests`, `RoutineLogRedactionTest` | Host, Android | S1–S6 |
| R-SEC-12 | Host routine files are admin-only (Windows) / `0600` (Linux) | ACL/permissions verified after every write | `RoutineStoreAclTests`, `RoutineStoreLinuxPermissionTests` | Host | S4 |
| R-SEC-13 | 8338 unchanged | No routine handling in the listener; 11-verb list intact | `RoutineIngressIsolationTests`, `CommandVerbDriftTests` | Core | S1 |
| R-SEC-14 | Rate limits and flap protection | Per T9 | `RoutineRateLimitTests`, `PresenceStateMachineTest`, `SensorTriggerSourceTests`, `SessionTriggerSourceTests` | Host, Android | S3, S4 |
| R-SEC-15 | No chaining / loop guard | Routine-caused events never trigger routines | `CausalSuppressionTests` | Host | S4 |
| R-SEC-16 | Play policy | No location, no exact alarms, no new FGS type | `NoLocationOrExactAlarmPermissionTest`, `lintVitalRelease` | Android | S1–S3 |
| R-SEC-17 | Home facts stay on the phone; capture refuses VPN and Tailscale | Facts only inside the encrypted store; absent from export, backup and logs; VPN/Tailscale capture refused | `NetworkFingerprintMatcherTest`, `HomeCaptureApiTest`, `BackupRulesRoutineExclusionTest` | Android | S3 |
| R-SEC-18 | Export excludes secrets and identifiers | Per §6.10 | `RoutineExportWhitelistTest` | Android | S6 |
| R-SEC-19 | Phone store corruption is never silent | Key loss → `store_reset` banner + record | `RoutineStoreCryptoTest` | Android | S1 |
| R-SEC-20 | Dry-run is command-line only | No setting or wire field enables it; banner shown | `RoutineDryRunSwitchTests` | Host | S1 |
| R-SYS-34 | Test runs simulate destructive steps | Countdown shown, verb not issued, step `simulated`, run attribute `simulated`; non-destructive steps execute | `TestRunSimulationTests`, `RoutineWorkerTest` + L19 | Host, Android | S1 |
| R-SYS-35 | The phone can run or test a PC-run routine | `routine_run_request` starts the PC's stored copy; live progress and the final report reach the phone | `RoutineRunRequestTests`, `PhoneRunPcRoutineTest` | Host, Android | S4 |
| R-SYS-36 | Pause all on the phone reaches every paired PC | `routines_sync.paused` pauses that owner's automatic routines; the PC shows "Paused from <phone>"; the phone shows the propagation state | `PauseAllPropagationTests`, `RoutineSyncClientTest` | Host, Android | S4 |
| R-SYS-37 | PC-side changes reach a connected phone at once | Enable toggle, block and PC Pause all produce an unsolicited `routine_sync_result` | `UnsolicitedSyncResultTests` | Host | S4 |
| R-SYS-38 | Live progress of PC runs on the phone | Step and countdown transitions produce live reports; the cursor is unaffected | `LiveRunReportTests` | Host, Android | S4 |
| R-SYS-39 | Home capture API | Returns displayable facts and `pcReachable`; refuses off Wi-Fi, over VPN or Tailscale, and when the PC is not on the LAN | `HomeCaptureApiTest` | Android | S3 |
| R-SYS-40 | Leave-home warning and away failure | Warning shown for `home.leave` with PC-targeted steps unless `reachableAway`; failures pick `pc_unreachable_away` correctly | `ReachableAwayTest`, `RoutineValidatorMessagesTest` | Android | S3 |
| R-SYS-41 | NFC tag checks | An existing RemEx tag is named; read-only and too-small tags are refused before writing; other phones refuse the tag | `NfcExistingTagTest`, `NfcTokenVerifierTest` | Android | S2 |
| R-SYS-42 | History retention and paging | Phone keeps 50 per routine or 30 days (cap 1,000, 90-day limit); PC keeps 20 per routine or 30 days (cap 500) | `RoutineRetentionTest`, `RoutineRunStoreTests` | Host, Android | S1, S4 |
| R-SEC-21 | Only a confirmed PC Run now skips the countdown | The in-process confirm skips it; every wire-initiated path counts down | `RunNowCountdownBypassTests` | Host | S4 |
| R-SEC-22 | The test flag can only reduce what executes | No destructive verb executes under `testRun`; history marks the run `simulated` | `TestRunSimulationTests` | Host | S1 |
| R-SEC-23 | A run request cannot inject a definition | Only the sender's stored routine ids run; unknown ids are refused | `RoutineRunRequestTests` | Host | S4 |

### 16.2 String estimate

| Area | Android keys | PC keys |
|---|---|---|
| Nav, list, empty state, banners, Pause all | 22 | 16 |
| Trigger labels, supporting text, parameters | 34 | 12 (only `pc.*` summaries; the PC has no editor) |
| Step labels, supporting text, parameters (power verbs reuse `rc_*`/`Remote_*`; +1 Sign Out on Android) | 40 | 14 |
| Editor chrome, validation (1.8), dialogs | 32 | 0 |
| Home capture | 14 | 0 |
| NFC flow | 16 | 0 |
| Templates (18 × name + why, 8 default messages, 7 requirement tokens) | 51 | 0 |
| History, run detail, outcomes, trigger sources | 20 | 16 |
| Reason messages and history labels (§10.1, 73 codes × 2) | 146 | 146 |
| Notifications (3 channels × name + description, progress/result/countdown text) | 14 | 0 |
| Run now confirm, countdown window | 0 | 10 |
| Export / import | 12 | 0 |
| Shortcuts, widget | 8 | 0 |
| Accessibility descriptions and plurals | 14 | 8 |
| Tutorial, coach, FAQ, palette | 2 + 6 + 12 = 20 | 3 + 3 + 12 + 3 = 21 |
| **Total** | **≈ 457** (≈ 4,113 entries across 9 files) | **≈ 243** (≈ 2,187 entries across 9 files) |

---

## 17. Open questions

Each has a recommended answer. The UX half's Q1–Q5 and the systems half's questions on "Switch and run", countdown scope, PC deletion and dry run were decided in the merge (§0.2) and are not repeated here.

**Review (2026-09-26): every recommendation below is accepted as written.** Implementation follows them; Q1's nav direction fix is filed as its own bead that S4 depends on. S1 is implemented as several beads (core and protocol, PC executor and countdown, phone store and runner, phone UI) that land together as one user-reachable slice.

| # | Question | Recommendation |
|---|---|---|
| Q1 | Should the PC nav direction fix (shared-axis direction from visual order, R-UX-03, which also fixes About and Settings sliding the wrong way) be split into its own bead? | Yes. File it as a standalone bug bead that S4 depends on. It is an existing defect with its own test (`ShellViewModelNavDirectionTests`), it can ship in 2.x before Routines, and keeping it separate means reverting Routines cannot bring back the About/Settings inversion. S4 then only adds the Routines position to the map. |
| Q2 | `routine_run_request` was added during the merge. Keep it as its own message, or make it a mode of `routine_step_request`? | Keep it separate. It names a stored routine rather than carrying a step, so it cannot inject a definition (T24), and its PC handling (single-flight, pause, block, live reports) differs from a single step. It is phone-to-PC, so it needs no routing entry. |
| Q3 | Should switching a routine off on the phone also stop its NFC tag, shortcut and widget? | Yes (`skipped_disabled`). Only in-app Run, Test and PC Run now ignore the switch, because they are deliberate actions on that exact routine. |
| Q4 | `home.leave` can take up to about 15 minutes plus the debounce when RemEx is not running and mobile data stays on. Acceptable for v1? | Yes, stated in the editor and in FAQ 18. It is fast whenever the connection service is running, which is the common case at home. |
| Q5 | Linux desktops with no idle source (no Mutter, no ScreenSaver interface, no X11). | Report the trigger as unavailable and reject it for that PC. Do not add `/dev/input` polling (privilege and Wayland security problems). |
| Q6 | Persist the PC's notify queue across agent restarts? | Yes. "Notify the phone, then shut down" is a headline pattern; the message must survive the shutdown within its hour. |
| Q7 | Include the PC's MAC address in exports? | No. It is refilled from the chosen PC's reported MAC on import. |
| Q8 | Use the PC's mDNS presence as an extra home signal? | No for v1. The PC is usually asleep when arrive matters. |
| Q9 | Allow chaining, where one routine's effect triggers another? | No in v1 (causal suppression, §9 T9). Chaining needs cycle detection and belongs with conditions later. |
| Q10 | When should smart-home work start? | A 3.2 spike behind a flag, per §15.3, after 3.0 is stable. |
| Q11 | Owner-absent suspension period. | 30 days, shown on the PC page with the last-seen date; the next sync lifts it. |
| Q12 | Make the countdown length configurable? | No in v1; a fixed 15 s. Revisit with user feedback. |
| Q13 | Offer the PC-side "phone away" trigger in 3.x (D6)? | Yes, as the first 3.x trigger after schedule. It needs a link-drop signal that tolerates phone Doze (for example no authenticated session for N minutes while the phone was last seen on the home network), which is its own design. |
