# RemEx 2.5.0 — change summary

What landed in PR [#55 "V2.5 board drain"](https://github.com/clindsay94/RemEx/pull/55), the whole
of `v2.5-board-drain` since 2.4.0.

This is the readable summary. The authoritative, bullet-by-bullet record with bead IDs is the
`[Unreleased]` section of [`docs/CHANGELOG.md`](CHANGELOG.md) — 770 entries, and every claim below
traces back to one of them.

## At a glance

| | |
|---|---|
| Commits | 1,129 (`b528ff1..2f8eaa8`) |
| Files changed | 1,030 (+181,832 / -8,770) |
| Changelog entries | 770 — 373 Fixed, 196 Changed, 111 Added, 44 Internal, 26 Removed, 20 Security |
| Version | `Directory.Build.props` 2.5.0, Android `versionName` 2.5.0 |
| Previous release | [2.4.0 "Dashboard 2.0"](https://github.com/clindsay94/RemEx/releases/tag/v2.4.0), 2026-07-19 |

Where the work went, by lines changed:

| Area | Files | Added | Removed |
|---|---|---|---|
| `remex.desktop.tests` | 246 | 40,664 | 17 |
| `remex.desktop` | 200 | 34,204 | 5,030 |
| `remex.android` | 214 | 31,865 | 1,619 |
| `remex.agent.tests` | 108 | 22,766 | 121 |
| `docs` | 28 | 17,114 | 72 |
| `remex.agent` | 77 | 14,542 | 975 |
| `remex.core.tests` | 47 | 6,758 | 1 |
| `remex.core` | 52 | 5,824 | 331 |
| `scripts` | 16 | 3,850 | 167 |

Roughly 70,000 of those added lines are tests. This release added CI that actually builds and tests
the repository, which had never happened before: `.github/workflows/` previously held two Claude
assistant workflows and the localization check, and no job compiled `remex.agent`, `remex.desktop`
or `remex.core` (RemEx-5vcb, RemEx-6i1l, RemEx-t4zr, RemEx-jx7r).

---

## 1. Personalization, rebuilt around a colour instead of a name

This is the largest single theme in the release: 229 changelog entries, roughly 343 commits.

**The four named PC themes are gone as a concept.** CyberNOC, Monolith, SolarFlare and
BaseDarkGlass each carried their own 53 hand-written colour values (RemEx-07jij). RemEx used to
store your theme as a *name* and look up a list of colours under it, which is why upgrading could
change the colour of your app (RemEx-dbkzy). The PC now derives its whole palette the way the phone
does: a seed colour, a style, light or dark, and a contrast level. The four old themes survive as
presets under shorter names — Neon, Slate, Ember and Glass — and still look the way they did
(RemEx-2gjwn).

**Palette Studio.** Personalize gained a colour wheel that repaints the app live as you drag
(RemEx-5u0vy), plus a "From this PC" row that adopts your Windows accent colour or extracts the
strongest colours from your wallpaper (RemEx-rdzet). The seven palette styles are a row of live
previews instead of a dropdown of seven words (RemEx-lrxyo). Light/dark became a three-way picker
with System (RemEx-zk5bc). The preset row is a gallery of eight cards, each painted in the theme it
gives you (RemEx-2gjwn).

**Match my phone.** The phone now sends the PC its theme — seed, style, light or dark, contrast,
and whether it is running on wallpaper colour — after connecting and whenever it changes, and the
PC's Personalize sheet offers a "Match my phone" action once it knows one (RemEx-y06a0.1,
RemEx-sudp8).

**Dynamic colour became a toggle on Android.** `RemExTheme` had always taken a `dynamicColor`
parameter and nothing ever passed it, so Material You wallpaper theming was permanently on and the
RemEx amber fallback palette was unreachable on API 31+ (RemEx-2xsy, RemEx-9429).

Other work in this theme:

- Two palette styles that were implemented but unreachable — Neutral and Monochrome — are now
  offered, and the personalization pickers stopped showing English to everyone (RemEx-6byw).
- The code-built parts of the PC UI follow the theme instead of hex literals compiled into the
  control: the minimap, the colour picker, the window background, the sparkline "running hot"
  colour, card-customisation fallbacks and Home link-card tints (RemEx-qljv, RemEx-fy0a).
- The "connected" green and the warning amber come from the same Tonal Spot recipe on every colour
  style, as they already did on the phone, instead of following the decorative style (RemEx-gw3ad).
- The PC splash takes the palette's surface tone and fills the mark with a primary-to-tertiary
  gradient read from a small `last-seed.json`, so it is right before the profile has loaded
  (RemEx-alwfa.1).
- The "Hardware Sync" toggle was removed. It polled no hardware and handed no colour to the theme;
  switching it on only started a silent five-second timer (RemEx-dbjfy, follow-up RemEx-v2pbv).
- A typo in the custom accent box could leave the app unreadable and it stuck: the box checked that
  what you typed was the right *length* for a colour, not that it was a colour, so `#FF0O00` was
  accepted and saved to your swatches permanently (RemEx-07jij).
- The PC app could forget your personalization seconds after launch. If the saved layout file was
  briefly locked at startup the app quietly started from defaults, and the next routine save wrote
  those defaults over your real settings (RemEx-8y3qy).
- Importing a savefile no longer gets undone by the Personalize sheet writing back the values it
  read at startup (RemEx-waqb4).
- Opening a layout on a PC that cannot offer its background material — an Acrylic layout opened on
  Linux — no longer overwrites the saved choice (RemEx-k7891).
- UI verification stopped meaning "check all four themes". `scripts/ui-palette-sweep.ps1` drives the
  real profile through a default plus three adversarial seeds, light and dark, two contrast levels,
  and captures every screen per cell (RemEx-8q7de).

## 2. The PC shell moved onto real Material components

- **Avalonia 11.3 → 12.1** (RemEx-jcma3). Groundwork; the app should look and behave as it did.
- The top bar is a real `ColorZone` app bar carrying the drawer toggle and the current
  destination's live localized name, replacing a transparent 32px spacer that existed only so
  clicks could fall through to the window drag region (RemEx-c437b, RemEx-a3prn).
- The settings panel is a `Material.Styles.Controls.SideSheet` docked right, replacing a hand-rolled
  `Border` with a manual `translateX` slide and a separate backdrop `Border` standing in for a scrim
  (RemEx-zrlze).
- The drawer's nine destinations are a single `ListBox` of `ListBoxItem`s instead of nine
  hand-styled buttons, with the "Settings family" divider inside it so arrow-key navigation still
  reaches past the split (RemEx-zi3ua).
- The drawer header is a Material identity block: the RemEx mark, this PC's machine name and the
  paired-device summary inside a real `ColorZone` (RemEx-dnqws, RemEx-ajpug).
- In-app toasts are a Material snackbar anchored above the personalization FAB instead of an
  Avalonia `NotificationCard` behind it (RemEx-uedna).
- Confirmation prompts, the file-consent prompt and the restore-layout prompt are built on
  Material.Avalonia's dialog builders through one shared path instead of three hand-written windows
  (RemEx-x6a70.3).
- Every button declares a `Classes` attribute, closing the gap where a bare `<Button>` fell through
  to Material's raised-primary default by accident. The Personalization panel alone had nine
  (RemEx-z7pnx.1, RemEx-me22).
- The first-run tutorial is a proper paged carousel with pips, and Back/Next stop at the ends
  instead of wrapping into nothing (RemEx-9iz00.1).
- Nav destinations fade and settle in section by section the first time you open each one in a
  session (RemEx-alwfa.2).

## 3. Security

Twenty security entries, including a numbered audit series (RemEx-s032) that closed six findings:

| | Finding | Fix |
|---|---|---|
| VULN-1 | `GET /debug/logs` served up to 3,000 buffered log lines to any network-reachable caller on `0.0.0.0:5005` with no auth, and those lines retained the live 6-digit pairing PIN and paired `clientId`s in cleartext | Endpoint removed (RemEx-s032.1) |
| VULN-2 | `/ws/desktop` and `/ws/files` — live screen capture with input injection into the elevated desktop, and bulk filesystem read/write — gated only on `IsClientPaired(clientId)`, a presence check on an unguessable but not secret identifier | Cryptographic proof-of-possession required (RemEx-ng5z, RemEx-s032.2) |
| VULN-3 | `LAUNCHAPP` passed a client-supplied `TargetPath` to `ProcessStartInfo` after only a full-path and existence check, so a paired client could launch `\\attacker\share\evil.exe` as the elevated host | Allowlist enforced, network paths rejected (RemEx-s032.3) |
| VULN-4 | `AddRootFromPathAsync` pinned every new root fully writable, so a client could browse a read-only root, pick a subfolder and re-pin it as writable and deletable | New roots inherit the parent's permissions (RemEx-s032.4) |
| VULN-5 | The SPKI-pinning callback was installed only when a pin was non-empty; with an empty pin `SslStream` fell through to the OS trust manager, which the JNI trust-manager overrides force to accept any certificate | Fails closed with no pin (RemEx-s032.5) |
| VULN-6 | `GET /` returned the full remote-desktop diagnostic report to anonymous callers | Minimal `{service, status}` handshake (RemEx-s032.6) |

Separately, the launcher allowlist can no longer be rewritten over the wire, which had made VULN-3's
mitigation self-referential (RemEx-mlce, RemEx-q6xt).

**The Remote Desktop channel now actually enforces certificate pinning.** `ValidateServerCertificate`
derived the presented SPKI hash, looked it up in `PinnedCertStore`, and then returned `true`
regardless, with a comment reading "Fallback for RemoteDesktop connecting" where the rejection
belonged (RemEx-xmgw, RemEx-mlce).

**A local-process trust class was closed.** RemEx treated connections originating from the PC itself
as already trusted, because normally that is just RemEx talking to itself. That let another program
on the PC impersonate a phone and take over its file transfer, use one of your phones' file
permissions, or cancel and corrupt a transfer by knowing its reference number. A paired phone could
also answer a permission question meant for a different phone, because the PC identified a
connection by whatever name the message claimed, re-read on every message rather than fixed once the
device proved who it was (RemEx-4u0d and siblings).

**Consent got tighter.** Agreeing to receive one file no longer lets the PC send a different one:
acceptance mints a one-time ticket per file (RemEx-ccqb, RemEx-tutz). An incoming push can no longer
skip the consent prompt on the phone (RemEx-z6lh). Ending a process now verifies it is the same
*instance* the user confirmed, not just a process with the same name, which also covered Android's
kill path where there had been no check at all (RemEx-druh, RemEx-nchu, RemEx-on4n, RemEx-2s91).

**Denial-of-service on the automation port.** Anything that could reach it could make RemEx set
aside 10 MB of memory before proving who it was (RemEx-ga503).

**The Indonesian FAQ told users to pipe a script from the wrong domain into their shell.**
`Faq_Q11_Answer` in `Strings.id.resx` gave the Tailscale install step as
`curl -fsSL https://scale.com/install.sh | sh` (RemEx-mqwx).

## 4. Files and transfers

- **Speed and time remaining**, on both platforms, in every language. The estimate rounds up before
  picking a unit, says "finishing" under a second, gives up past 23 hours, and is re-read once a
  second so a stalled transfer goes blank instead of freezing on a stale number (RemEx-4lcq,
  RemEx-8c3v, RemEx-qmiv, RemEx-oiah). The PC transfer queue had been discarding byte counts at the
  boundary — `FileTransferClient` reported `bytes / total`, so a percentage was the only thing that
  crossed, and neither speed nor ETA can be derived from a fraction (RemEx-4lcq, RemEx-oiah).
- **Whole-folder transfer.** Picking a folder used to mean opening it, selecting everything inside,
  and repeating for every subfolder, and an empty folder had no way through at all (RemEx-q3twg).
- **Name collisions ask instead of failing.** A Replace / Keep both / Skip sheet with "Apply to all
  remaining", and the "Keep both" rule lives in one place so the phone cannot disagree with the host
  about it (RemEx-agpn, RemEx-12cj, RemEx-mneb, RemEx-6vd8, RemEx-cirk).
- **Consent is asked on the device that asked.** Full-browse and file-push prompts used to appear on
  the PC monitor for a decision the phone user was making, possibly from another room, so the prompt
  waited in front of nobody until it timed out (RemEx-220r, RemEx-mneb, RemEx-6bfyt).
- **Every file transfer failed with "The binary file channel is not connected"** on a phone that was
  plainly connected: browsing worked, the dashboard kept updating, and every download and upload
  failed (RemEx-6bfyt).
- A file arriving on the phone now says so and offers to open or share it. Previously the only
  outcome ever reported was a failure; a success just appeared in a folder you were never told about
  (RemEx-pwkc).
- Dropping a file on the PC can mean "send this to my phone", and the resolution now says which
  (RemEx-ru0r, RemEx-wbhc).
- File lists sort the way every other file browser sorts: `file9` before `file10` (RemEx-5mhs,
  RemEx-93bb).
- The file manager and app launcher say when there is nothing to show instead of rendering a blank
  area (RemEx-n69m).
- "Keep both" stopped blaming the name it chose for failures that had nothing to do with it: the
  guard wrapped the entire operation, including a recursive tree copy, and translated five exception
  types into `resolved_name_unusable` (RemEx-cirk, RemEx-od7s, RemEx-mu17).

## 5. Remote desktop, input and the phone as a remote

- **A disconnecting phone can no longer leave keys held down on your PC.** A key press is two
  independent messages and a chord is six, and nothing guarantees the client sends the closing half
  (RemEx-3uhp, RemEx-7rq3, RemEx-krvz, RemEx-y6x6, RemEx-73dc, RemEx-yzbb, RemEx-e2p4, RemEx-kqje).
- **The client now honours the host's "I cannot inject input" flag**, which it had never read:
  `supportsInputSimulation` was produced by the host and consumed by nothing (RemEx-jvme, RemEx-i8ty,
  RemEx-vgxv, RemEx-q9zw).
- **Clipboard from phone to PC.** Copy on the phone, tap the clipboard button on Remote Control, and
  it lands on the PC's clipboard (RemEx-hgqs, RemEx-qu3t).
- **Screenshots.** The PC had been able to take one on request for two releases with nothing on the
  phone asking for it. There is a button now, the shot is saved as a PNG in a "RemEx Screenshots"
  folder under Pictures where a gallery already indexes, and it is offered to the phone rather than
  pushed (RemEx-66rf, RemEx-tjve, RemEx-86nu, RemEx-y7my, RemEx-byij).
- **Media and volume control from the phone**, including on Linux, where the host silently dropped
  the media key range (RemEx-3cnq, RemEx-hulc). The play/pause button shows which way it will go
  instead of always being a play triangle (RemEx-xx6xf).
- **Wake-on-LAN stopped asking users to find and type their PC's MAC address**, which was the one
  setup step a non-technical user cannot do. The command palette's WoL entry, which sent
  `WakeOnLan` with no parameters on a code comment guessing the host "might use defaults", was
  removed (RemEx-izuj, RemEx-efse).
- **A failed stream left nothing to press**: several failure paths cleared the "this PC can stream"
  flag the start button was gated on (RemEx-5k4dd).
- The connection can be measured now. RemEx had always sent a timestamp with every heartbeat and
  never read one back, so nothing knew the round-trip time or how much video the PC was producing
  (RemEx-93n2).
- Two concurrent Remote Desktop clients against one shared capture service are now exercised rather
  than reasoned about (RemEx-lcp8, RemEx-c5an).

### Linux input, which was broken in several ways at once

Writing tests for the Linux argument vectors found live bugs rather than confirming correctness:

- On the ydotool backend, **every leftward or upward mouse movement did nothing at all**. Both
  `mousemove` invocations passed coordinates as bare positional arguments, and `-5` is read as an
  option cluster by `getopt_long` with the optstring `"hawx:y:"` (RemEx-nb7c).
- Clicking did nothing on ydotool: `0x00110D` to press and `0x00110U` to release, neither carrying
  an action bit, for as long as the backend existed (RemEx-nb7c).
- `--absolute` on ydotool is emulated, not implemented, so absolute pointer targets now get
  translated into what it actually wants (RemEx-r29r, RemEx-fu9n, RemEx-dyvd).
- **On 95 locales, a monitor placed above or left of the origin stopped screen capture from starting
  at all**, because `LinuxScreenCaptureService` built its ffmpeg `x11grab` argument with a plain
  interpolation (RemEx-hbma, RemEx-tiih, RemEx-wssm, RemEx-clum). Reading numbers back out of
  xdotool turned out to be culture-sensitive too, and fixing the format side did not fix it
  (RemEx-hbma, RemEx-dyvd, RemEx-j7el).
- `LinuxInputBackendRouter` built shell arguments in the same interpolate-into-a-process shape this
  repo has now shipped wrong twice, with no seam to test it (RemEx-fu9n, RemEx-7tkg, RemEx-hnin,
  RemEx-n3z6).

## 6. Connection, pairing and knowing what is going on

- **"Is my phone attached?" is now a different question from "is the loopback link up?"** Every
  status dot bound `Connection.IsConnected`, the UI's own WebSocket to its embedded host, so a user
  with zero phones paired saw a green "Connected" and the one fact the PC UI exists to convey was
  displayed nowhere (RemEx-0z7w, RemEx-porg).
- **Settings lists the phones paired with this PC** — name, first paired, last connected, and a
  light for connected right now — and you can rename or unpair from there, without the rename being
  able to reach the thing that authenticates them (RemEx-nrsv, RemEx-0z7w, RemEx-9see).
- **The PC keeps track of which phones are actually connected.** Before, a connection was known only
  to the code handling it and forgotten the moment that code moved on. The PC also now remembers
  what your phone is called beyond the one connection during which it was told.
- **The Connection screen on the phone lists paired PCs and you connect by tapping one.** There had
  been one remembered PC, whatever was typed into the form last (RemEx-k62t). Discovery can
  represent two PCs instead of resolving the first mDNS answer and stopping (RemEx-8ih5, RemEx-ado4).
  "Which PC is this?" has one answer instead of one per address, since `PinnedHostStore.listPaired`
  was keyed by address and a machine has several (RemEx-k62t, RemEx-9q03, RemEx-9x5i).
- **A readiness check.** The host can answer whether the machine is actually set up for a phone to
  reach it, and a "System status" card on Home checks administrator rights, the certificate, whether
  RemEx is accepting connections, the firewall, and start-at-sign-in. It stays collapsed to one line
  when everything is fine (RemEx-id37, RemEx-ksbm, RemEx-h5lr, RemEx-q0j7, RemEx-gpe3). A green
  port-listening row no longer reads as all-clear on a machine the phone cannot reach, since the
  firewall sits between the listener and the phone (RemEx-oxjm, RemEx-4ycq, RemEx-zmco). A firewall
  rule that could not be *read* is no longer reported as a firewall that is *refusing* (RemEx-zmco).
- **Certificate fingerprints a person can compare.** The About page shows this PC's fingerprint, and
  the cert-changed repair on Android is no longer a bare tap that clears the pin with no explanation
  (RemEx-vnps).
- **Forgetting a PC on Android cleared the pin but not the reconnect secret**, so re-pairing could
  fail on the very next connect. The pinned SPKI hash and the PAIR-1 reconnect secret live in
  separate DataStores and `removePin` cleared only the first (RemEx-1phe, RemEx-vnps, RemEx-j9ei).
- **A pairing PIN can no longer be shown as valid after it is dead** (RemEx-f66j, RemEx-scwy), and
  pairing says what it is doing instead of spinning, which matters because a PC that is on but not
  answering properly can take a minute and a half to diagnose.
- The launch-at-login switch could show "off" for a PC whose autostart is registered and working,
  and it is also the control that writes (RemEx-l6o, RemEx-id37, RemEx-h5lr).
- Settings that need a restart can say so. There were zero hits repo-wide for restart-required, so a
  setting the host reads once at startup looked identical to one that applies live (RemEx-4ve2,
  RemEx-pbp4).

## 7. Dashboard, sensors and the tray

- **The tray flyout is somewhere you can do things from.** It used to show pinned sensors and two
  buttons that both only opened the main window (RemEx-exfu7, RemEx-7n5gr, RemEx-5g5b7, RemEx-3m4ho,
  RemEx-el6gh). Right-clicking the tray icon gives a menu worth opening, rather than one offering a
  light/dark switch covering two of the four themes (RemEx-0gbjy). There is an "Open logs folder"
  item (RemEx-kjdi).
- **Sensor alerts you can see and manage**, with a bell on any card that has one set (RemEx-8wpvr).
- **Three navigation badges that had nothing behind them now do**: Files counts active transfers,
  Diagnostics counts warnings that arrived while you were elsewhere and stops counting while you are
  reading the log, and the tray flyout's presence dot carries the number of paired devices online
  (RemEx-8wpvr, RemEx-rjnbo.1).
- The eight per-category card shapes are editable, closing the last gap in the shape system
  (RemEx-mycn).
- The phone reads each second of PC readings once instead of four times (RemEx-cite).
- The sparkline hover crosshair lands on the sample you are pointing at, and sparklines take the
  theme's primary accent with a tertiary second series instead of two fixed hex colours (RemEx-83dw,
  RemEx-hf90, RemEx-qljv). Under Monochrome and Neutral the second series draws at reduced opacity
  so the two lines stay distinguishable, easing off as contrast goes up (RemEx-n2kv0).
- A staged sensor that has stopped reporting is actually dimmed in the PC's sensor drawer
  (RemEx-lki2r).
- The keyboard focus ring on Home's pinned sensor cards was drawn around the wrong rectangle
  (RemEx-kgs7g).
- A dashboard performance concern was measured rather than assumed, and the measurement said leave
  it alone, so what shipped is the evidence and a guard rather than an optimisation (RemEx-4q6l,
  RemEx-yqpa).

## 8. Diagnostics and support

- **Settings → Help builds a diagnostics report, shows you all of it, and only then offers to send
  it.** The scrubber that keeps a bundle from mailing out a credential was built and pinned first,
  and the bundle now assembles itself in a way that cannot route around the scrubber (RemEx-nwnn,
  RemEx-3rjf, RemEx-8jzu, RemEx-0iww).
- **Seven desktop failures that left no trace anywhere now reach the diagnostics export.** They
  wrote to `Debug.WriteLine` or to nothing at all, and Debug output exists only under a debugger, so
  on a user's machine they were invisible including in the export a support case is built from
  (RemEx-a8du, RemEx-43ha).
- The Diagnostics log viewer gained a right-click menu with "Copy message" and "Copy with stack", a
  "Follow tail" switch that pauses when you scroll up and offers "Jump to newest", and a post-export
  status line naming the file with an "Open folder" button (RemEx-a8du).
- The system-log tab was English in every language under a translated tab header (RemEx-rg9in).
- Notifications got decided rather than assumed: the desktop had no transient notification
  capability at all, no toasts and no tray balloons, while close-to-tray defaults on, so a received
  file produced zero visible feedback (RemEx-43ha, RemEx-5wc2, RemEx-1fxt).
- A shared "busy" affordance: a spinner inside a button while a command runs, and placeholder rows
  while a list fills for the first time (RemEx-kjdi).

## 9. Accessibility and localization

- **The Android app honours the system "Remove animations" setting.** `RemExTheme` reads
  `ANIMATOR_DURATION_SCALE` through a ContentObserver, so toggling it re-themes without a restart:
  the motion scheme drops from Expressive to Standard and a `LocalReducedMotion` CompositionLocal
  lets screens suppress their own choreography (RemEx-tej8). All three splash variants render a
  single static frame instead of holding you through several seconds of unskippable animation on
  every cold start (RemEx-n39x).
- **Escape closes every dialog.** Pairing, file consent, confirmations, the restore prompt and the
  two chart dialogs all ignored it, so backing out meant reaching for the mouse, after typing a
  six-digit PIN in the pairing case (RemEx-xxifk).
- The remote-desktop pointer can be driven without seeing it; the stream surface had been a single
  opaque node to TalkBack (RemEx-55u2, RemEx-2qzc).
- Icon-only toolbar buttons show a label on long-press, 25 of them, always using the same string as
  the icon's `contentDescription` (RemEx-ji98, RemEx-31wq).
- **A localization check that catches three separate failure modes on both platforms, plus a CI job
  that runs it.** RemEx ships 1,080 PC keys and 754 Android keys across nine files each and nothing
  verified any of it (RemEx-qug7, RemEx-km0i.20). A fourth axis was added for values that exist,
  are translated, are current and are referenced correctly, and still do not take the same arguments
  as their English source (RemEx-xn7l, RemEx-ozep). A separate test catches a key that code asks for
  and no `.resx` defines (RemEx-2s91, RemEx-b5kx, RemEx-fxkg).
- `string.Format` against a placeholder-free string silently drops the argument, showing the user a
  sentence with the filename, count or reason missing. That is now detected rather than renamed
  around (RemEx-p9fn, RemEx-mznc, RemEx-udq9).
- A test now catches the stale-label-on-language-switch defect after four separate fixes failed to
  prevent a fifth (RemEx-6h3q, RemEx-4f30, RemEx-si0h, RemEx-4p27, RemEx-r5pm, RemEx-6ddx).
- **Dead-string cleanup, adjudicated rather than swept.** All 233 orphaned PC keys are gone, 1,108
  base keys down to 875 (RemEx-b5kx); 20 unreachable Android keys plus 12 more and another 18, with
  two kept as evidence of *missing features* rather than dead weight (RemEx-x5dx, RemEx-a7n4,
  RemEx-qykh). Five dead PC strings pointed users at a `Remex.PC.exe` the architecture does not have
  (RemEx-o8n1). A settings hint shipped the developer's own PC name and account (RemEx-qmlq).
- Both FAQs answer the same sixteen questions again, with a canonical list checked in so they cannot
  drift apart silently. They had diverged: the PC could not tell you how to change its appearance or
  replay its tutorial, and Android could not tell you the PC can be locked remotely, though all
  three features exist on both platforms (RemEx-6kr1). New entries cover elevation and why the phone
  will not connect without it (RemEx-csfz), stopping RemEx launching at sign-in (RemEx-36n4), and
  the certificate-pinning refusal after a reinstall (RemEx-a6to). The Android tutorial explained how
  to find your PC's IP address on macOS, for which there is no PC side (RemEx-oq6l, RemEx-hqcl).

## 10. Correctness and test infrastructure

- **CI now builds and tests the repository.** Nothing did before (RemEx-5vcb, RemEx-6i1l,
  RemEx-t4zr, RemEx-jx7r).
- **The test suite was writing into the machine's real pairing and file-trust stores.** A test that
  did not inject a path resolved the same machine-wide location the running host uses, so fixtures
  wrote to `C:\ProgramData\RemEx` for real. It cannot reach them at all now (RemEx-9lbg, RemEx-ln0k,
  RemEx-sc23, RemEx-rj0a, RemEx-97aa, RemEx-4u29).
- **The build can see a doc comment attached to the wrong thing.** No project set
  `GenerateDocumentationFile`, so Roslyn emitted none of the XML-doc diagnostics, which is how this
  branch shipped two of them before a human reading the file caught each one (RemEx-wssm,
  RemEx-cxel).
- **262 Android code-quality warnings that nothing was checking have been read**, and nothing can
  add a 263rd (RemEx-cljx).
- Fail-closed confirmation tests now cover all five destructive actions plus Kill Process
  (RemEx-e1re, RemEx-w9ui, RemEx-6p1f, RemEx-07jx, RemEx-5vcb, RemEx-d5d8).
- Two command-correlation behaviours described but never tested since 2.0 are tested, and the last
  `TODO` comments in the C# tree are gone (RemEx-h01r).
- The file-transfer idle watchdog is a class of its own with the regression test it shipped without
  (RemEx-l519, RemEx-sf27).
- Eight dead `OperatingSystem.IsAndroid()` branches removed from the PC app, one of which was hiding
  a silent data-loss bug; `remex.desktop` targets `net10.0`, so they could never return true
  (RemEx-f167, RemEx-t4tc, RemEx-eca6). The PC shell also stopped compiling in an Android bottom nav
  bar, a duplicate FAB and an add-panel that could never appear (RemEx-b5kx, RemEx-f167).
- Seven verified-dead symbols removed from both platforms, each re-proved dead against the current
  tree first (RemEx-eca6, RemEx-xkmn). 53 genuinely unused imports removed (RemEx-cm7b).
- `scripts/fix-host-install.ps1` removed; it could not have worked (RemEx-aep, RemEx-6cdy).

## 11. Development machinery

Not user-facing, but a large share of the commit count (roughly 301 commits under the `ralph`
scope):

- `/drain`, a parallel board-drain dispatcher where a lane is a headless Claude Code session in its
  own git worktree, with `scripts/ralph-dispatch.ps1`, `scripts/ralph-lane-bootstrap.ps1` and
  `scripts/ralph-merge-queue.ps1` landing exactly one branch at a time (RemEx-k62t, RemEx-56fu,
  RemEx-t0f3, RemEx-u5q0, RemEx-56fu.5.2, RemEx-56fu.5.3).
- `docs/REGRESSION-GUARDS.md`, 921 lines of hand-maintained guards anchored to `file:line`, replacing
  an auto-generated block that had drifted into instructing agents to do the opposite of what the
  code does.
- New reference docs: `BUTTON-VOCABULARY.md`, `TYPOGRAPHY-VOCABULARY.md`,
  `DASHBOARD-CHARACTERISATION.md`, `PERF-BASELINE.md`, `AUDIT-fluent-removal.md`,
  `UI-PALETTE-SWEEP.md`, `MCP-ROUTING-DECISION.md`, three SPIKE documents (host audio streaming,
  monitor brightness, privacy curtain mode) and specs/plans for the personalization sheet and shell
  connection status.
- Written artefacts stopped being pointed at a gitignored directory; several beads named
  `docs/superpowers/specs/` as their output home, and that path is ignored (RemEx-0l9x).

---

## Before publishing

Two things are worth checking against this branch:

1. **Android `versionCode` is still 36**, the same value 2.4.0 shipped with
   (`remex.android/app/version.properties`). `versionName` is 2.5.0. Google Play rejects an upload
   whose `versionCode` has not increased. I have not changed it, since version bumps are yours.
2. **`docs/CHANGELOG.md` still heads this work as `[Unreleased]`.** The 2.4.0 release commit cut it
   in the same commit as the version bump (`de0a95c`), so the equivalent here is renaming it to
   `## [2.5.0] — 2026-09-10`.
