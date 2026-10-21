# SPEC: PC-to-PC remote desktop (RemEx-dffoc)

## Decisions (Connor, 2026-10-20)
- **Architecture flip approved as specced.** A PC pairs with another PC the same way a phone does: the PIN shows on
  the target and the target's SPKI is pinned. Pairing is **one-way**. Trust is kept per user and encrypted. Phase 0
  replaces the CLAUDE.md Architecture section with the text in §12.
- **"Another PC" goes on the Remote desktop page.** That page becomes a hub with three segments: This PC (preview) ·
  Another PC · Phone (RemEx-lycq5). Don't add a drawer entry.
- **FFmpeg ships in the Windows installer** as an LGPL build. Linux uses the distro package. The decoder is shared
  with RemEx-lycq5. MJPEG stays as the fallback here.

Status: DRAFT for Connor's approval. Nothing here is implemented. Phase 0 cannot start until Connor
approves the CLAUDE.md text in §12, because every phase after it breaks today's hard rule 1.

## 1. What the Remote desktop page is today

The PC's Remote desktop page (drawer index 5, `remex.desktop/Views/RemoteDesktopView.axaml`,
`remex.desktop/ViewModels/RemoteDesktopViewModel.cs`) is a **viewer**, not a host-side control panel.

- `RemoteDesktopViewModel.cs:284` builds a `RemoteDesktopService`; `:443` calls
  `ConnectAsync(Connection.HostAddress)`; `:957-962` decodes every frame as JPEG with `new Bitmap(ms)`;
  `SendInputAsync` forwards pointer/key events; `:1270` handles the display catalog (multi-monitor
  already works); `:540` stops the stream when the page leaves the foreground.
- `RemoteDesktopView.axaml:73-74` Start/Stop, `:97` display picker, `:126` quality slider,
  `:172` a `TextBox` bound to `Connection.HostAddress`, `:226` the viewport.
- `Connection.HostAddress` is loopback by default, so the page streams this PC's own screen back to
  itself. Quality/FPS/scale on this page also feed `desktop_config`, which is why the 3.1 tracker
  says "quality lives on the Remote desktop page".

What the viewer's network client is missing for a remote host:

| Gap | Where | Consequence against a remote host |
| --- | --- | --- |
| No pairing handshake | `ConnectionViewModel.cs` has no `PairingClient` use; only the embedded-host PIN display (`:314`) | Host keeps `isPaired=false` (`PingPongHandler.cs:107`) |
| No `clientId`/`protocolVersion` on `/ws/desktop` | `RemoteDesktopService.cs:471-480` | `EvaluateDesktopAuth` (`HostBootstrapper.cs:1008`) rejects non-loopback with no paired id |
| No proof-of-possession | `RemoteDesktopService.cs` has no reconnect challenge handling | Closed at `HostBootstrapper.cs:834-844` |
| TOFU only for loopback | `RemoteDesktopService.cs:449`, `ConnectionViewModel.cs:1836-1838` | Correct; must stay. The remote path needs a real pin from pairing |
| JPEG only | `RemoteDesktopViewModel.cs:957` | Host would serve MJPEG unless the client advertises `supportsFrameEnvelope` |

Everything above already exists on the Android side, and most of it is C# in `remex.core/Native/`
compiled for the phone. That is the reuse.

## 2. Design summary

"Promote the loopback viewer to a real client." Keep `ConnectionViewModel` as loopback-only local
plumbing (hard rule 1 for the control channel stays true). Add a separate remote-PC session on the
viewing PC that reuses core's client classes in-process:

| Need | Reuse | Notes |
| --- | --- | --- |
| Discovery | `remex.core/Services/Network/MdnsDiscoveryService.cs:6-52` (`_remex._tcp`) | Host already advertises (`MdnsAdvertisingService.cs:155`) |
| Pairing | `remex.core/Native/PairingClient.cs:33` (ClientWebSocket ctor), `:46 StartPairingAsync`, `:96 CompletePairingAsync`, `:27 LastReconnectSecretBase64` | Same ECDH P-256 + PIN HMAC as Android |
| SPKI pin | `remex.desktop/Services/Security/PinnedCertStore.cs` (per-user `pinned_hosts.json`), `CertificatePinPolicy.cs:30-89` | Already the PC-side pin store |
| Stream | `remex.core/Native/RemexDesktopClient.cs:170 ConnectAsync(host, port, clientId, spkiHash)`, `:427 StartStreamAsync`, `:476 SendInputAsync`, `:527 RequestDisplayCatalogAsync`, `:541 SwitchTargetAsync`, `:559 RequestKeyframeAsync`, events `:69-86`, RDXF demux `:883` | Does proof-of-possession, fail-closed pinning (`:109`) |
| Control channel (phase 3 only) | `remex.core/Native/RemexNativeClient.cs:106 ConnectAsync`, `:408 SendMessageAsync` | For `clipboard_*` |
| Input types | `remex.core/Models/InputEvent.cs:29-107`, `RemexMessage.cs:580-606` | No new types |

Host side needs **no new message type and no protocolVersion bump**: a PC client is a paired client
with a reconnect secret like any phone. One additive field marks it as a PC (§5).

## 3. Discovery and pairing

- **Discovery.** Run `MdnsDiscoveryService` from the viewing PC; list instances with name, address,
  port. Manual entry reuses the address box that exists at `RemoteDesktopView.axaml:172`, moved into
  the new "Another PC" panel (§9). Exclude this machine's own instance (compare `HostId` after a
  probe, or the advertised addresses against local interfaces) so a user cannot "pair with myself".
- **Pairing.** Open a short-lived `ClientWebSocket` to `wss://<addr>:5005/ws` with a TLS callback that
  captures the SPKI (same shape as `RemoteDesktopService.AcceptSelfSignedCertificate`, but TOFU is
  allowed only for the duration of pairing and only for the host the user picked), run
  `PairingClient.StartPairingAsync`, show a 6-digit PIN entry, run `CompletePairingAsync`, then pin
  `PairingResponse.CertificateSpkiHashBase64` with `PinnedCertStore.SetPinAsync(hostId, hash)` and
  persist `LastReconnectSecretBase64`. The PIN appears on the target PC through the existing
  Settings › Connection PIN panel (`ConnectionViewModel.cs:314` `AttachEmbeddedPairingService`),
  exactly as for a phone. PIN auto-fetch (`TransportTrust`) stays phone-only: a PC client never asks
  for the PIN over the wire.
- **Trust storage on the viewing PC** (open decision 1). Recommended: per-user, in
  `RemexDataPaths.PerUserDirectory` (`remex.core/Services/RemexDataPaths.cs:121`), as a new
  `paired_hosts.json` {hostId, hostName, lastAddress, port, clientId, reconnectSecret, pairedUtc}.
  Reconnect secret wrapped with DPAPI `CurrentUser` on Windows and written `0600` on Linux.
  Rationale: `RemexDataPaths.cs:101-110` already records why pins are per-user (trust is the signed-in
  user's decision; ProgramData would share it with every account). Elevation: the agent is elevated
  but still runs as the signed-in user, so `%LOCALAPPDATA%` and DPAPI CurrentUser resolve correctly;
  nothing here needs the machine-wide ACL path, which exists for state the host must keep across
  users (`cert.pfx`, `paired_clients.json`).
- **Directional.** Pairing A→B grants A a client identity on B. It grants B nothing on A (open
  decision 4).

## 4. Transport and auth on the host

- `/ws/desktop` already requires a paired `clientId` and HMAC proof over a nonce for any non-loopback
  peer (`HostBootstrapper.cs:797`, `:829-844`, `:1008-1047`). `RemexDesktopClient` satisfies both.
- The host's `DesktopSessionRegistry.TakeOverAsync` (`HostBootstrapper.cs:813-818`) means one stream
  per clientId; a viewing PC gets one clientId per target host, generated at pairing.
- Codec: host serves H.264 only when the client sets `DesktopClientCapabilities.SupportsFrameEnvelope`
  (`DesktopDisplayModels.cs:40`, `RemoteDesktopHandler.cs:281-300`, `:2468`). Phase 1 leaves it false
  (MJPEG); phase 2 sets it true.
- Session guard: `WindowsInteractiveSessionGuard.EngageForRemoteControl(clientId)` /
  `Disengage(clientId)` (`:45`, `:70`) is per client; a PC client is just another id. No change.

## 5. A paired PC as a distinct client kind

Recommended (open decision 3): add an optional `clientPlatform` string to `PairingRequest`
(`remex.core/Models/PairingMessages.cs:5-10`; values `android`, `windows`, `linux`), additive and
ignored by old hosts. Persist it next to the name in `PairedClientNameStore` (`:66 Remember`,
`:88 Resolve`), surface it on `PairedDeviceItem` (`remex.desktop/ViewModels/PairedDeviceItem.cs:17`)
and the Settings rows (`SettingsViewModel.cs:268-300`) as an icon and a "PC" caption. Revoke reuses
`PairedDeviceRevoker.RevokeAsync` (`:52`), which already unregisters and drops live sessions; the
acceptance test is that revoking kills the viewer's stream within one keep-alive interval.

## 6. Decoding and rendering

- **Phase 1: MJPEG.** Works today on both OSes (Skia JPEG). Zero new dependency.
- **Phase 2: H.264** (open decision 2). Recommended: an **ffmpeg child-process decoder**
  (`-f h264 -i pipe:0 -fflags nobuffer -flags low_delay -f rawvideo -pix_fmt bgra pipe:1`), located
  with the existing `FFmpegDetector` (`remex.agent/Services/RemoteDesktop/FFmpegH264Encoder.cs:174`,
  search paths `:215-225`). Same optional dependency the host already uses to encode, same binary on
  Windows and CachyOS, hardware decode via `-hwaccel` later. Alternatives and why not first:
  FFmpeg.AutoGen (ships LGPL shared libs in the installer, bigger surface); OpenH264 (Cisco binary,
  software-only, good fallback if pipe latency measures above one frame); Media Foundation
  (Windows-only, no CachyOS parity, heavy P/Invoke). MJPEG stays the fallback, exactly like the phone.
- **Rendering.** Two reusable `WriteableBitmap`s (BGRA8888, like `RemoteDesktopViewModel.cs:1382`)
  swapped on the UI thread into the existing viewport `Image`; no per-frame allocation. Keyframe
  request on decoder error via `RemexDesktopClient.RequestKeyframeAsync` (host throttles reinit to one
  per 5 s, see REGRESSION-GUARDS "Keyframe throttle cooldown").
- Stream Metrics: reuse the existing descriptor/metrics plumbing (`StreamDescriptorReceived`).

## 7. Input from a real keyboard and mouse

- **Pointer.** Absolute, mapped from viewport pixels to the stream's `DesktopStreamDescriptor`
  size; the host clamps to the active display (`RemoteDesktopHandler.ClampToActiveBounds`). Buttons,
  wheel (`DeltaX/DeltaY`), and the existing stylus/pen modes carry over.
- **Keyboard.** Avalonia `KeyDown/KeyUp` → `InputEvent.KeyCode` (assumption: Windows virtual-key codes,
  consumed directly at `RemoteDesktopHandler.cs:1394-1400`; the Linux host already maps them). Text
  via `TextInput` for IME/dead keys using `InputEvent.Text`. Release every held key when the viewport
  loses focus or the window hides (the host releases at session end, `:2363`, but not on viewer blur).
- **Gotchas.** Ctrl+Alt+Del is a Secure Attention Sequence; only winlogon can raise it, so the viewer
  shows no such button and the help text says to use the PC. Win key, Alt+Tab, Alt+F4 are taken by the
  viewer's own OS unless hooked: phase 3 adds an opt-in `WH_KEYBOARD_LL` hook, Windows-only, active
  only while the viewer is fullscreen and focused (open decision 8); on Linux there is no global grab
  under Wayland, so those combos stay local and an on-screen "Send Win / Alt+Tab" row covers them.
- **Secure desktop on the target.** A UAC/credential prompt or lock screen moves input to the Winlogon
  desktop; capture freezes or goes black and `SendInput` is blocked (UIPI canary,
  `WindowsInputSimulationService.cs:234-243`). The host already detects it
  (`WindowsRemoteDesktopDiagnosticReport.cs:90`, `:142`). Phase 3 surfaces it as a coded
  `desktop_error` (additive) and the viewer shows "The PC is showing a secure prompt. Dismiss it at the
  PC." Linux host: the portal consent dialog on first capture must be accepted at the PC once per
  process lifetime (`LinuxCaptureSessionLifetime`); same banner.

## 8. Clipboard, multi-monitor, loopback

- **Clipboard** falls out cheaply only if a `/ws` control session exists: `clipboard_push`
  (client→host) and `clipboard_request`/`clipboard_content` (`RemexMessage.cs:678-693`) are additive
  and already handled by the host. Phase 3 opens a `RemexNativeClient` control session (it handles
  reconnect proof) and adds Send/Fetch clipboard buttons, text only. Not in phase 1.
- **Multi-monitor** on the target already works in the viewer (`AvailableDisplayTargets`,
  `SwitchTargetAsync`, `QueryDisplaysAsync`). On the viewing PC, fullscreen goes to the monitor the
  window is on (Avalonia `WindowState.FullScreen`).
- **Loopback.** PC-to-PC is never loopback. The self-view path keeps its TOFU and pairing bypass
  (`ConnectionViewModel.cs:1628`, `RemoteDesktopService.cs:449`) and the remote path must never inherit
  it: separate service, separate socket, pin required (fail closed via `EnsurePinnedOrThrow`).
  `EvaluateDesktopAuth` already refuses a loopback peer that names a paired id (`HostBootstrapper.cs:1043`);
  the self-view must keep connecting without a paired clientId. The §3 API_CONTRACTS rule "replies
  count only from a proven, non-loopback session of the phone that was asked" is unchanged; a paired PC
  advertises no `fileCapabilities` in phase 1, so `PhoneFileRelay` never selects it.

## 9. UI flow

**Viewing PC (Remote desktop page, index 5).** The page gets a segmented header: **This PC** (the
existing self-view, relabelled "Preview what your phone sees") and **Another PC**. "Another PC" shows
discovered PCs (mDNS), paired PCs (from `paired_hosts.json`, with Forget), and "Add by address".
Connect → if unpaired, a PIN dialog ("Type the 6 digits shown on <HostName>") → stream in the existing
viewport with the existing display picker, quality, fullscreen and window-control tools. Drawer order
is untouched (`ShellViewModel.cs:425` `_drawerNavOrder`, Lane A A1-A3 done); `--view RemoteDesktop`
opens the page as now. Home's Remote desktop Quick-launch tile is unchanged.

**Target PC.** PIN appears in Settings › Connection as today (Lane B B3 closes the panel on success
and should name the PC). Paired devices lists the PC with a monitor icon, last seen, Rename, Revoke.
Phase 3 adds a persistent "<PC name> is viewing your screen" indicator (tray + in-app) with Disconnect.

**Sequencing with RemEx-172ba.** Lane A owns the shell (done); Lane B owns `SettingsView`/`SettingsViewModel`
(B3, B4 open). Phase 1 step 6 (Paired devices icon/caption) lands after Lane B; the rest of phase 1 does
not touch Lane A or B files.

## 10. Cross-platform parity

| Role | Windows | CachyOS |
| --- | --- | --- |
| Viewer | Avalonia + Skia JPEG; ffmpeg.exe decode (phase 2); DPAPI secret; LL keyboard hook opt-in | Avalonia + Skia JPEG; ffmpeg decode; 0600 secret; no global key grab |
| Host | unchanged (DXGI/WGC, NVENC path, SendInput) | unchanged (PipeWire/portal, VAAPI/x264, portal input) |

mDNS browse needs inbound UDP 5353 on the viewer; check the installers' firewall rules (open decision 11).
Every new script keeps the `.ps1`-under-`pwsh` rule; tests Windows-only are `[WindowsOnlyFact]`.

## 11. Threat model (defensive scope)

Assets: the target PC's screen, input, clipboard, power (8338), and its paired-phone trust. Adversaries:
a compromised paired PC; a LAN attacker; malware under the viewing user's account.

| Threat | Mitigation |
| --- | --- |
| LAN MITM at first connect | PIN-bound ECDH (physical presence at the target), SPKI pin stored before any stream |
| Replay/impersonation of a paired PC | Reconnect secret + nonce HMAC on both `/ws` and `/ws/desktop`; secret never on the wire after pairing |
| Compromised paired PC drives the target | Same gates as a phone (no extra privilege); Revoke in Paired devices drops live sessions; keep-session-unlocked stays off by default; phase 3 "being viewed" indicator with one-click disconnect and optional per-PC view-only (open decision 5); audit log on engage/disengage already exists |
| Malware on the viewing PC reads `paired_hosts.json` | DPAPI CurrentUser / 0600; the file holds a client secret for one host, not the host's cert; revoke on the target recovers |
| Lateral trust (A→B implies B→A) | Pairing is directional; nothing auto-reciprocates |
| Loopback bypass leaking to remote | Separate service; `EnsurePinnedOrThrow` fail-closed; `EvaluateDesktopAuth` loopback/paired-id refusal; regression test that the remote path never sets `_allowFirstTimeTrust` |
| Secure desktop capture | Cannot be captured or driven from a high-integrity non-SYSTEM process; documented, surfaced as an error, never worked around |

Tests map to OWASP ASVS V2 (auth), V6 (crypto storage), V9 (communications). Findings are validated by
defect injection per hard rule 5.

## 12. CLAUDE.md architecture text (replace the "Architecture" section once approved)

> ## Architecture: every RemEx PC is a host; clients are the Android app and other RemEx PCs
> - `remex.agent` is **the entire PC side**: one elevated, interactive-session Avalonia app with an
>   in-process ASP.NET host (Minimal APIs, WebSocket, mDNS), started at sign-in by an elevated Task
>   Scheduler logon task. No Windows Service, no Session 0, no second process, no named pipes.
> - The PC's own UI reaches its host in-process via DI (`EmbeddedHostServiceLocator`). The only
>   loopback socket is the self-view on the Remote desktop page ("This PC"); it is local plumbing,
>   skips pairing on both sides, and must never be reused for a remote host.
> - **Clients** open every socket; a host never dials out. Two client kinds exist:
>   - `remex.android` (Kotlin + Compose + JNI → `libRemexCore.so`).
>   - **A RemEx PC viewing another RemEx PC** (RemEx-dffoc): the Remote desktop page's "Another PC"
>     panel. It pairs with the target like a phone (ECDH P-256 + 6-digit PIN on the target's screen,
>     then SPKI pinning), stores its pin and reconnect secret per user (`pinned_hosts.json`,
>     `paired_hosts.json`), and streams over `/ws/desktop` with `clientId` + proof-of-possession.
>     Pairing is directional. A paired PC gets no privilege a phone does not have.
> - `remex.desktop/` holds PC-side UI code only and is compiled into `remex.agent`.
> - `remex.core/` holds shared models, messages, validation, guards, serialization, and the client
>   classes (`PairingClient`, `RemexNativeClient`, `RemexDesktopClient`, `MdnsDiscoveryService`) used
>   by both client kinds. It is also compiled NativeAOT for Android, so it stays NativeAOT-safe.
> - Protocols: WSS `/ws` and `/ws/desktop` on 5005; TCP+TLS 8338 for scripts, gated by a paired
>   `clientId`; `RemexMessage` envelope, `protocolVersion: 2`.
> - The PC may send `file_*` requests and transfer offers DOWN a paired **phone's** session
>   (`PhoneFileRelay`); replies count only from a proven, non-loopback session of the phone that was
>   asked. A paired PC advertises no file roots. See `docs/API_CONTRACTS.md` §3.
>
> Hard rule 1 becomes: **A RemEx PC is a client only on the Remote desktop page's "Another PC" path,
> and only through the core client classes. `ConnectionViewModel` stays loopback-only. Never route the
> self-view through the remote path or the remote path through the loopback bypass.**

Also update `docs/ARCHITECTURE-HOST.md` §"One process, elevated, in your session" (`:10-45`) and
`docs/API_CONTRACTS.md` with a "PC clients" subsection under §2 and §6.

## 13. Phased delivery

### Phase 0: approve and flip the rule
- Objective: Connor approves §12; CLAUDE.md, ARCHITECTURE-HOST.md, API_CONTRACTS.md updated; the
  tests that pin hard rule 1 are re-pointed, not deleted.
- Context: `.claude/CLAUDE.md` Architecture + Hard rule 1; `docs/ARCHITECTURE-HOST.md:10-45`;
  `ConnectionViewModel.cs:1631-1642` (`IsAnotherMachine`, "sweep D1, hard rule 1") and its tests.
- Constraints: `ConnectionViewModel` keeps refusing non-loopback addresses. No code beyond tests/docs.
- Steps: (1) replace the CLAUDE.md section with §12; (2) add the API_CONTRACTS "PC clients"
  subsection; (3) find tests asserting "no PC client" by `git grep -n "hard rule 1"` and re-scope
  each to `ConnectionViewModel` only; (4) CHANGELOG.
- Acceptance: `scripts/verify.ps1` green; `InstructionFileTests` green; `git check-ignore -v` on every
  touched doc prints nothing.
- Out of scope: any runtime behaviour.

### Phase 1: pair, view and control another PC (MJPEG). THE JOIN.
- Objective: from PC A, pair with PC B using B's on-screen PIN, then view and control B over MJPEG
  from the Remote desktop page. B lists A in Paired devices and can revoke it.
- Context: §2 table; `RemoteDesktopViewModel.cs:273-300`, `:443`, `:540`, `:957`, `:1270`;
  `RemoteDesktopView.axaml:73-74`, `:97`, `:172`, `:226`; `RemoteDesktopService.cs:55-217`, `:446-480`;
  `PairingClient.cs:33-96`; `RemexDesktopClient.cs:69-86`, `:109`, `:170`, `:427-559`;
  `PinnedCertStore.cs:26-145`; `RemexDataPaths.cs:121`; `MdnsDiscoveryService.cs:6-52`;
  `PairingMessages.cs:5-10`; `PairedClientNameStore.cs:66-101`; `PairedDeviceItem.cs:17-20`;
  `SettingsViewModel.cs:268-300`; `HostBootstrapper.cs:1008-1047`; `PingPongHandler.cs:100-112`.
- Constraints: no new message types, no protocolVersion bump; `ConnectionViewModel` and
  `RemoteDesktopService` loopback behaviour unchanged (their tests stay green untouched);
  `RemexDesktopClient` is used as-is from `remex.core` (NativeAOT-safe already); all strings through
  `Localization/` in 9 languages; Guard.NotNull on ctor deps; no ConfigureAwait(false);
  REGRESSION-GUARDS "Pairing and trust" section applies; read it first.
- Steps (each verifiable):
  1. `remex.core`: add optional `clientPlatform` to `PairingRequest` with validation (max 16 chars,
     ASCII). Test: old payload without it still deserialises; host ignores unknown value.
  2. `remex.agent`: persist `clientPlatform` in `PairedClientNameStore` (sidecar field) and expose it
     on the paired-device rows the Settings view model reads. Test: round-trip through the store.
  3. `remex.desktop/Services/Network/PairedHostStore.cs`: per-user `paired_hosts.json`, DPAPI
     CurrentUser on Windows / 0600 on Linux. Tests: file mode on Linux (`[WindowsOnlyFact]` twin for
     DPAPI), redirect honoured via `HostStateDirectoryOverride`.
  4. `remex.desktop/Services/Network/RemotePcSession.cs`: `DiscoverAsync` (mDNS, excludes self),
     `PairAsync(address, pinProvider)` (PairingClient + PinnedCertStore + PairedHostStore),
     `ConnectAsync(hostId)` (RemexDesktopClient with clientId + pin, fail closed), stream/input/
     display-catalog pass-throughs, `ForgetAsync`. Tests with an in-process Kestrel host from
     `remex.agent.tests` doubles: pairing success, wrong PIN, pin mismatch refused, revoke closes.
  5. `RemoteDesktopViewModel`: introduce an `IDesktopStreamSource` abstraction over the current
     `RemoteDesktopService` and the new `RemotePcSession`; the page switches source by segment.
     Foreground gating (`:540`) applies to both. Test: `RemoteDesktopStreamForegroundGatingTests`
     still green; new test that switching segments stops the old source.
  6. `RemoteDesktopView.axaml`: "This PC / Another PC" segment, discovered+paired list, Add by
     address, PIN dialog, Forget. Paired devices rows in Settings show the PC icon and caption
     (after Lane B lands). Strings in 9 languages via `android_add_strings.py`-style script for resx.
  7. Docs: CHANGELOG, API_CONTRACTS §2/§6 PC client notes, REGRESSION-GUARDS entry "Remote PC path
     never sets allowFirstTimeTrust" anchored to file:line.
- Acceptance: on two machines (or one PC + one WSL/VM host is NOT acceptable; use the second PC or
  the CachyOS box): A pairs with B via PIN; wrong PIN fails with a plain-English message; B's screen
  appears in A at the stream's reported size; mouse click and typed text land on B; display switch
  works; B's Paired devices shows A as a PC; Revoke on B ends A's stream within 30 s; after B's
  cert changes without re-pair, A refuses with a localized "trust changed" message (fail closed);
  self-view on A still works with no pairing; `verify.ps1 -Scope all` green on Windows and the
  Linux test run green; defect injection: removing the pin check in `RemotePcSession` turns the
  "trust changed" test red.
- Out of scope: H.264, keyboard hooks, clipboard, fullscreen polish, "being viewed" indicator,
  view-only mode, file roots on a PC client.

### Phase 2: H.264 on the PC viewer
- Objective: the viewer advertises `supportsFrameEnvelope`, decodes H.264 via an ffmpeg child
  process, falls back to MJPEG exactly as the phone does.
- Context: `RemoteDesktopHandler.cs:281-300`, `:2468-2470`; `DesktopDisplayModels.cs:33-61`;
  `DesktopFrameEnvelope.cs:18`; `RemexDesktopClient.cs:559`, `:883`; `FFmpegH264Encoder.cs:174-225`;
  REGRESSION-GUARDS "Wire magic bytes", "Keyframe throttle cooldown", "Self-healing codec recovery".
- Constraints: never reuse RDXF/RDXC magic; no change to host pacing; decoder lives in
  `remex.desktop/Services/Video/` behind `IH264FrameDecoder`; detection shares `FFmpegDetector`
  (move it to a shared location or expose via DI, do not copy); MJPEG path unchanged.
- Steps: (1) move/expose `FFmpegDetector` for the desktop project; (2) `FfmpegProcessH264Decoder`
  with bounded stdin backlog (mirror Android "Bounded input backlog" guard) and BGRA stdout reader;
  (3) double-buffered `WriteableBitmap` sink; (4) capability flag + per-frame RDXF routing with
  MJPEG tagged frames rendered through the JPEG path; (5) keyframe request on decode error, at most
  one per 5 s; (6) Stream Metrics show codec, decode ms, dropped frames; (7) docs.
- Acceptance: with ffmpeg present on A and B, the stream descriptor reports H.264 and the viewer
  renders at the host's target FPS within 10% on a 1080p target; with ffmpeg absent on A, MJPEG;
  killing the ffmpeg process mid-stream recovers within 3 s without a reconnect; injected corrupt
  NAL triggers exactly one keyframe request; unit tests for the RDXF router and backlog bound.
- Out of scope: hardware decode flags, in-process decoders.

### Phase 3: input polish, fullscreen, clipboard, host indicator, view-only
- Objective: make control feel native and give the target PC's owner visibility and a brake.
- Context: §7, §8; `WindowsInputSimulationService.cs:234-243`;
  `WindowsRemoteDesktopDiagnosticReport.cs:90,142`; `RemexMessage.cs:678-693`;
  `RemexNativeClient.cs:106,408`; `WindowsInteractiveSessionGuard.cs:45,70`; tray flyout (Lane B B4).
- Constraints: LL hook Windows-only, opt-in, only while fullscreen and focused, removed on blur;
  clipboard text only; the secure-desktop error is an additive `DesktopErrorCodes` value.
- Steps: (1) held-key release on blur/hide; (2) on-screen "Send Win / Alt+Tab / Alt+F4" row;
  (3) opt-in LL hook; (4) fullscreen on current monitor with the existing immersive service;
  (5) `RemexNativeClient` control session + clipboard Send/Fetch; (6) host secure-desktop coded
  error + viewer banner; (7) host "being viewed" indicator with Disconnect (reuses the session
  guard's engage/disengage events); (8) per-PC view-only toggle on the host (refuse `desktop_input`
  from that clientId with a coded error).
- Acceptance: each step has a test; defect injection on (1) (skip release) leaves a stuck Shift on
  the host, caught by the test; UAC on B shows the banner on A and input is refused with the coded
  error, not a socket close.
- Out of scope: audio, file roots on PC clients, Android changes.

## 14. Open decisions for Connor

| # | Decision | Recommendation | Trade-off |
| --- | --- | --- | --- |
| 1 | Trust store on the viewing PC | Per-user `%LOCALAPPDATA%\Remex` (+ DPAPI / 0600) | Machine-wide ACL'd would share one user's trust with every admin account and needs the elevated-only ACL path; per-user matches `pinned_hosts.json` today |
| 2 | H.264 decoder | ffmpeg child process | No new NuGet, parity with host encode, optional dependency; costs one pipe hop (~1 frame). FFmpeg.AutoGen = ship LGPL libs; OpenH264 = software only; Media Foundation = Windows-only |
| 3 | How the host tags a PC client | Additive `clientPlatform` on `PairingRequest` persisted in the name store | Inferring from `ClientName` is free but breaks on a renamed phone; a new field is one validation rule |
| 4 | Reciprocal pairing | Directional only | Auto-reciprocal is convenient but doubles the blast radius of one compromised PC |
| 5 | Per-PC view-only on the host | Yes, phase 3, default "control allowed" | Default view-only is safer but makes the feature useless until a second setting is found |
| 6 | Where "Connect to a PC" lives | Top of the Remote desktop page (segment) | A separate drawer entry collides with Lane A's finished order; Settings › Connection hides a primary action |
| 7 | Keep the self-view | Keep, relabel "Preview what your phone sees" | Removing it kills the only way to check quality settings without a phone, and it is test-covered |
| 8 | Win-key capture | Opt-in LL hook, Windows, fullscreen only | Always-on hook surprises users and is impossible on Wayland anyway |
| 9 | Clipboard | Phase 3, text only | Needs a control session; files would re-open the consent model |
| 10 | Replace `RemoteDesktopService` with `RemexDesktopClient` for loopback too | Not now; remote only | One client class is cleaner but touches the guarded self-view and its tests |
| 11 | Firewall rule for mDNS 5353 on the viewer | Add to both installers if browse fails on a clean Windows install | Extra rule vs "discovery silently empty" |
