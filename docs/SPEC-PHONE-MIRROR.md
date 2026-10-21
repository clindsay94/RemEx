# SPEC: Phone screen mirroring and control from the PC (RemEx-lycq5)

## Decisions (Connor, 2026-10-20)
- **D3: the Android app ships as two builds.** The Play build is view-only and has no AccessibilityService in its
  manifest. The sideload/GitHub build can view and control. The Play accessibility declaration gets its own bead
  later.
- **D2, superseded: the phone screen lives on the Remote desktop page.** That page becomes a hub with three
  segments: This PC · Another PC (RemEx-dffoc) · Phone. There is no new drawer entry and no shortcut shift.
- **D7 and installer: FFmpeg ships in the Windows installer** as an LGPL build. Linux uses the distro package. The
  ffmpeg-process decoder is the shared component with RemEx-dffoc.
- The other decisions (D1, D4–D6, D8–D13) follow the spec's recommendations unless Connor says otherwise.

Status: decisions recorded 2026-10-20. Sibling: RemEx-dffoc (PC-to-PC remote desktop) shares the PC-side H.264 decoder defined in §6.

## 1. Summary

The PC's RemEx window shows a paired phone's screen live and, in a later phase and only in non-Play builds, taps and types on it. The direction rule is unchanged: the phone opens every socket. The PC only ever sends requests down a phone's existing, proven, non-loopback `/ws` session; the phone owner consents on the phone; the phone then opens a second phone-initiated socket and streams up. Nothing in this spec adds a PC-side client, a loopback client, or a change to pairing or the envelope.

## 2. Transport decision: a second phone-initiated socket, `/ws/phone-screen`

Precedent is `/ws/files`, not `/ws/desktop`.

- `remex.agent/HostBootstrapper.cs:898-954` maps `/ws/files`, evaluates auth at `:917` (`EvaluateFilesAuth`, `:1071`), and runs a proof-of-possession challenge at `:934-942` before `sessions.RunChannelAsync`. The phone side is an OkHttp WSS client with SPKI pinning taken from `PinnedHostStore` (`remex.android/.../service/FileTransferChannelClient.kt:196-203`).
- The PC asks on `/ws`, exactly like `PhoneFileRelay` (`remex.agent/Services/FileTransfer/PhoneFileRelay.cs:59-87` allowlist, `:144` `Open(clientId)`, `:167` `TryDeliverReply(senderClientId, identityProven, isLoopback, message)`), using `ClientSessionRegistry.TrySendAsync` (`remex.agent/Services/ClientSessionRegistry.cs:308`) and `IdentityProven` (`:76`, `:146`).

Why not `/ws`: it is a JSON text channel with a 4 MB message limit shared by telemetry, power, pairing and file control; base64 access units at 30 fps would contend with all of them.

Why not `/ws/desktop`: every guard on it is host-send-side. `PrecisionPacer` (`docs/REGRESSION-GUARDS.md:74`), `StreamSerial` (`:130`), the keyframe cooldown (`:136`), envelope-gated codec recovery (`:144`) and the `EvaluateDesktopAuth` loopback bypass (`:1625`) all assume the PC produces frames and the phone consumes them. Making it bidirectional would put a phone-paced stream under a PC pacer that has nothing to pace, and the loopback bypass is exactly wrong for an uplink (see §10). A new endpoint leaves every one of those guards untouched.

Pacing on the uplink: the phone's `MediaCodec` encoder emits one access unit per captured frame at the VirtualDisplay's cadence capped by the negotiated fps. The PC does no pacing; it decodes as frames arrive, keeps a bounded backlog (6, drop-oldest, mirroring `MAX_INPUT_BACKLOG` at `docs/REGRESSION-GUARDS.md:303`), and sends `phone_screen_keyframe` on overflow or decoder restart, mirroring `onKeyframeNeeded`.

### 2.1 Uplink admission (all four, in this order)
1. Non-loopback only. There is no PC-side use for a loopback phone screen, so unlike `/ws/files:934` there is no bypass. A loopback attempt is rejected with 403 and logged.
2. `clientId` query parameter is paired (`PairedClientRegistry.IsClientPaired`).
3. Proof-of-possession challenge, same helper `/ws/files` uses at `:936-938`.
4. `token` query parameter matches a pending offer for that clientId in a new `PhoneScreenOfferRegistry` (PC): minted by the PC when it sent `phone_screen_request`, 32 random bytes base64url, single use, 90 s expiry, one pending offer per clientId. A reply or socket for a token the PC never minted, or minted for another phone, is dropped.

### 2.2 Messages (all new types carry the `phone_screen_` prefix; see §2.3)

On `/ws`, PC to phone (relayed only to a session with `IdentityProven` and not loopback):
- `phone_screen_request` `{requestId, streamToken, pcName, wantsControl, maxFps, maxLongEdge, bitrateKbps}`. `wantsControl` is `false` until phase 2.
- `phone_screen_stop` `{sessionId}`.

On `/ws`, phone to PC (counted only if `TryDeliverReply`-style checks pass: sender proven, non-loopback, still paired, requestId was sent to this sender):
- `phone_screen_response` `{requestId, accepted, reason, capabilities: {control, audio, partialScreen}}`. `reason` is one of `declined`, `busy`, `unsupported`, `battery`, `thermal`.
- `phone_screen_state` `{sessionId, state: started|stopped, reason}`.

On `/ws/phone-screen` (phone-initiated):
- Text frame, phone to PC, before the first video frame and on every change: `phone_screen_meta` `{sessionId, streamSerial, width, height, rotation, fps, encoderName, capturedScope: screen|app}`.
- Binary frame, phone to PC: `PhoneFrameEnvelope` header + one Annex B access unit. Header layout copies `DesktopFrameEnvelope` (`remex.core/Models/DesktopFrameEnvelope.cs:18-57`: 28 bytes, version, streamSerial int64, sequence int64, payloadLength int32) with magic `"RPXF"`. `"RDXF"` and `"RDXC"` are never reused (`docs/REGRESSION-GUARDS.md:119`).
- Text frame, PC to phone: `phone_screen_keyframe` `{sessionId}`; phase 2 adds `phone_screen_input` (§7).

The envelope stays `RemexMessage` with `protocolVersion: 2`. No bump: additive and capability-gated (`docs/REGRESSION-GUARDS.md:736`).

### 2.3 Routing to Kotlin (hard guard)
`docs/REGRESSION-GUARDS.md:386-400`: a non-`file_` client-bound type is silently dropped unless `AndroidNativeExports.OnNativeMessageReceived` forwards it. Add a `phone_screen_` prefix route exactly like `routine_*` (`remex.android/.../RemexCoreClient.kt:61-77`) and register the types in `MessageAudience.HostToClient` (`remex.core/Messages/MessageAudience.cs:70`). Acceptance for phase 1 includes a real-device round trip; unit tests cannot prove delivery.

## 3. Capture on the phone (MediaProjection)

`minSdk = 34`, `targetSdk = 37` (`remex.android/app/build.gradle.kts:140-141`), so the Android 14+ rules apply to every supported device:
- Consent every session. The result of `createScreenCaptureIntent` is single-use; `getMediaProjection` may be called once per result and the system dialog must be shown again for every new stream. There is no remembered token and the spec does not try to build one.
- A foreground service of type `mediaProjection` must be in the foreground before `getMediaProjection`/`createVirtualDisplay`. New `service/ScreenMirrorService` with `android:foregroundServiceType="mediaProjection"` and `FOREGROUND_SERVICE_MEDIA_PROJECTION`, separate from `RemexConnectionService` (`AndroidManifest.xml:346-349`, type `connectedDevice`). Keep them separate so stopping the mirror never touches the connection.
- `MediaProjection.registerCallback` before `createVirtualDisplay`; handle `onStop` (the system status bar chip's Stop, or any system-initiated stop) as a clean end: send `phone_screen_state stopped`, close the uplink, stop the service. Handle `onCapturedContentResize` and `onCapturedContentVisibilityChanged` by sending a new `phone_screen_meta` (and when invisible, pausing frames rather than streaming black).
- Partial capture: the Android 14 system dialog lets the owner pick "A single app" or "Entire screen". Use `createScreenCaptureIntent(MediaProjectionConfig.createConfigForUserChoice())` so the owner keeps that choice (decision D9). `capturedScope` in meta tells the PC which it got.
- The persistent notification (channel "Screen sharing", `POST_NOTIFICATIONS` already declared) shows "Sharing your screen with <PC name>" with a Stop action; the system also shows its own projection chip. Stop from either path, from the PC, from the app's Home card, or from a disconnect all converge on one `ScreenMirrorController.stop(reason)`.
- Secure windows (`FLAG_SECURE`: banking, password managers, DRM players) render black in the capture by design. Do not work around this; it is a mitigation (§10).
- The stream stops when the keyguard shows (`ACTION_SCREEN_OFF` receiver plus `KeyguardManager.isKeyguardLocked` check on every frame burst) and does not resume on unlock without a new request (decision D5).

## 4. Encoding on the phone (MediaCodec H.264)

- `MediaCodec` `video/avc` with `createInputSurface()`; the VirtualDisplay renders into that surface. Prefer a hardware encoder (`MediaCodecList` with `isHardwareAccelerated`), fall back to `c2.android.avc.encoder` with the bitrate halved.
- Format: `KEY_COLOR_FORMAT = COLOR_FormatSurface`, `KEY_BITRATE_MODE = VBR`, `KEY_I_FRAME_INTERVAL = 2`, `KEY_MAX_B_FRAMES = 0`, `KEY_LATENCY = 0`, `KEY_PRIORITY = 0` (realtime), profile Main or Baseline so the PC's ffmpeg decode never hits an unsupported feature. SPS/PPS are emitted with the first IDR and repeated on every keyframe (`PARAMETER_KEY_REQUEST_SYNC_FRAME` on `phone_screen_keyframe`).
- Resolution and bitrate: encode at the phone's display aspect scaled so the long edge is `min(maxLongEdge, display long edge)`; defaults `maxLongEdge = 1280`, `fps = 30`, `bitrateKbps = 4000`; presets "Sharp" 1920/60/8000, "Balanced" 1280/30/4000, "Battery" 960/15/2000 (decision D6). Dimensions are rounded to multiples of 16.
- Rotation: on a configuration change the phone bumps `streamSerial`, sends `phone_screen_meta` with the new width/height/rotation, releases and recreates the VirtualDisplay and encoder at the new size, and starts with an IDR. The PC drops any frame whose `streamSerial` is older than the last meta (same rule as `StreamSerial`, `docs/REGRESSION-GUARDS.md:130`).
- Backpressure: encoder output goes through a bounded queue (6, drop-oldest). On overflow the phone requests its own sync frame and, if overflow repeats three times in 5 s, steps down one preset and tells the PC in meta.

## 5. Audio (phase 3, only if cheap)

`AudioPlaybackCaptureConfiguration` on the same projection token, `RECORD_AUDIO` runtime permission as a separate consent, apps that set `allowAudioPlaybackCapture=false` are silent. Encode AAC-LC 128 kbps or Opus and send on the uplink with a second magic (`"RPXA"`). The PC has no audio output dependency today; that is decision D8 and blocks this phase until decided.

## 6. Decoding and rendering on the PC (shared with RemEx-dffoc)

The PC currently only encodes (`remex.agent/Services/RemoteDesktop/FFmpegH264Encoder.cs:12`, an ffmpeg child process with a once-per-process `FFmpegDetector` at `:113` and `:174`). The shared component:

- `remex.agent/Services/Video/IH264Decoder` and `FFmpegH264Decoder`: an ffmpeg child process reading Annex B on stdin (`-f h264 -fflags nobuffer -flags low_delay -probesize 32 -analyzeduration 0`) and writing raw BGRA frames on stdout (`-f rawvideo -pix_fmt bgra`), sized from the caller-supplied width/height. Raw video carries no dimensions, so a size change means restart; the caller drives that from `phone_screen_meta` (this bead) or `DesktopMeta` (RemEx-dffoc). The detector is reused as-is; a missing ffmpeg yields `IsAvailable = false` and the page shows the same guidance Remote desktop shows (Linux: `LinuxRemoteDesktopPrerequisites.cs:164-209` already reports it). Assumption: on Windows ffmpeg is found on PATH by `FindExecutable`; the installer does not bundle it.
- `remex.desktop/Controls/VideoSurface` (Avalonia): owns two `WriteableBitmap`s (Bgra8888), writes the newest decoded frame on the UI thread, swaps, and invalidates once per render tick; older frames are dropped, never queued. `Stretch=Uniform`, letterboxed. Nothing is written to disk and the bitmaps are disposed on stop.
- Rejected alternatives: FFmpeg.AutoGen (ships libav binaries, large), Media Foundation (Windows-only, breaks parity), OpenH264 (native lib plus Cisco binary terms). Hardware decode (`-hwaccel d3d11va` / `vaapi`) is phase 4.

RemEx-dffoc must consume `IH264Decoder` unchanged; name it in that bead's spec as a dependency on this one.

## 7. Control (phase 2)

Routes considered:
- `AccessibilityService.dispatchGesture` (API 24+, multi-stroke 26+): taps, long press, swipes, pinch; `performGlobalAction` for Back, Home, Recents, Notifications, Lock. Universal, no root, no PC-side ADB. This is the route.
- Shizuku / ADB (`InputManager.injectInputEvent` with shell uid): needs wireless debugging re-paired after every reboot or a PC-run adb; not viable for normal users. Out.
- Vendor SDKs (Samsung Knox Remote Control): licensed, one vendor. Out. Device Owner: factory reset. Out.

Limits of the route: arbitrary key events cannot be injected (`INJECT_EVENTS` is signature-level). Text goes in with `ACTION_SET_TEXT` on `findFocus(FOCUS_INPUT)`, falling back to clipboard set plus `ACTION_PASTE`. Setting the clipboard from the service is allowed; reading it from the background is not, so phone-to-PC clipboard is a `phone_screen_input` reply only while RemEx is foreground. The lock screen is never driven: every input is refused while `isKeyguardLocked`.

Google Play: the Accessibility API policy allows non-accessibility use only after the Play Console "Accessibility API usage" declaration, a prominent in-app disclosure before enabling, `isAccessibilityTool=false`, and a service description that states the purpose. Approval is discretionary; remote-control apps have been approved and removed under the same text. Plain verdict: control cannot be relied on to ship on Play. Recommendation (D3): product flavours `play` (no AccessibilityService in the manifest; `capabilities.control = false`) and `sideload` (control). Android 13+ "restricted settings" blocks a sideloaded app's accessibility service until the owner taps "Allow restricted settings" in App info; the enable flow must say so.

Opt-in model, copying `pcMayChangeFiles`: a DataStore key `screen_pc_may_control` (`SettingsManager.kt:938-944` pattern), off by default, read on every `phone_screen_input` (not cached per session, like `AndroidFileTransferHost.kt:68-176` and `FileHostHandler.kt:357` `pcChanges`), reported in `phone_screen_response.capabilities.control`, and the PC enables control only after this phone said `control: true` (the `_phoneAllowsChanges` gate pattern, `PhoneFileRelay.cs:596-606`). While control is active the persistent notification text changes to "Your PC is controlling this phone" and the Home card shows it.

Wire: `phone_screen_input` on the uplink, JSON, `{sessionId, seq, kind: tap|down|move|up|longPress|scroll|pinch|key|text|clipboard, x, y, dx, dy, keyName, text}` with coordinates normalised 0..1 against the meta size so rotation never misroutes a tap; every float goes through `CoordinateValidation` (`docs/REGRESSION-GUARDS.md:1639`).

## 8. Battery, thermal, network

- Hardware encoder only by default; 30 fps; the screen must stay on to capture, so the owner is told that in the consent sheet.
- Thermal: `PowerManager.addThermalStatusListener`; at SEVERE step down to "Battery", at CRITICAL stop with reason `thermal`.
- Battery: below 15% and not charging, the response is `battery` unless the owner overrides in the sheet; the stream stops at 5%.
- Network: LAN and Tailscale only, as today. Sustained send backlog steps down a preset; metered networks get a warning line in the sheet, not a block.
- Idle auto-stop (D10): 30 min without a PC interaction in view-only, 10 min in control.

## 9. Where it lives

PC (`remex.desktop/`): a new page "Phone screen", index 11, in the drawer directly after Remote desktop (D2). The "five edits that must agree" guard (`docs/REGRESSION-GUARDS.md:631`) applies: `ShellView.axaml` item and DataTemplate, `ShellView.axaml.cs` case, `_drawerNavOrder` (`remex.desktop/ViewModels/ShellViewModel.cs:425`, currently `[0, 5, 7, 2, 3, 4, 1, 10, 9, 8, 6]`), `NavIndexToTitleConverter`, plus the entrance style, tooltip shortcut and `--view PhoneScreen`. The page: phone picker (proven, non-loopback sessions only, like `PhoneFileRelay.AvailablePhones()` at `:138`), "Ask to view", a status line ("Waiting for the phone's owner", "Declined", "Install FFmpeg"), the `VideoSurface`, Stop, preset chooser; phase 2 adds a Control toggle, Back/Home/Recents, and a text field. No tray thumbnail, no Home tile until D2 settles.

Phone (`remex.android/`, Lane C of RemEx-172ba owns this tree, `docs/UI-FLOW-3.1-TRACKER.md` Lane C): land after C3 (Settings categories) and C2 (Help & diagnostics). Add a Settings category "Screen sharing" holding the control switch (sideload flavour only), default preset, and the battery rule; a Help & diagnostics entry explaining the system chip and the restricted-settings step. The consent is a notification "PC <name> wants to see your screen" (the `FileConsentManager` / `FileConsentActionReceiver` pattern in `service/`) that opens `MainActivity` on route `phone_screen_consent/{requestId}`, which shows a bottom sheet naming the PC and the options, then launches the system dialog. While active, the Home connected-PC card shows "Sharing screen" with Stop. Write these as cross-lane requests in the tracker, not as edits in Lane C's files.

Cross-platform parity: ffmpeg process and `WriteableBitmap` work on both; verify on Windows and CachyOS or file the bead for the other.

## 10. Threat model

Assets: everything on the phone's screen (OTP codes, messages, banking unless `FLAG_SECURE`), and in phase 2 the ability to act as the owner.

Attackers: a compromised PC (malware with the agent's elevation, or a person at the unlocked PC); a local non-elevated process on the PC; a second paired phone; a LAN peer.

Mitigations:
- Consent on the phone for every session via the system dialog, which the PC cannot pre-accept; the RemEx sheet names the requesting PC.
- The uplink refuses loopback and unproven sessions with no bypass (§2.1), so a local PC process cannot plant a fake phone screen, and a second paired phone cannot answer with its own screen because the token is per clientId and single use.
- Stop on keyguard (§3): a phone in a pocket never streams; the attacker only sees what the owner is actively doing.
- `FLAG_SECURE` content stays black; no workaround.
- Single-app capture remains available to the owner.
- Control is a separate off-by-default switch, re-checked per input, never on Play builds, refused on the lock screen, and cannot unlock the phone.
- Visible indicators: system projection chip, RemEx persistent notification with Stop, Home card state, and in control mode a different notification text.
- Request rate limit: one pending request per phone, 90 s expiry, so a compromised PC cannot spam consent dialogs.
- PC side: frames never touch disk, no recording or screenshot in phases 1-2, bitmaps disposed on stop, nothing shown in the tray.
- Idle auto-stop.
Residual risk: a compromised PC during a consented session sees what the owner sees. State this in the consent sheet in plain words.

## 11. Phased delivery

### Phase 1: view-only, joined (RemEx-lycq5.1 to .4)
Units 1A-1C are implementable in parallel; none closes before 1D lands (`Put the join in the first half`). 1D blocks the others.

#### 1A Core protocol
- Objective: the shared types for the request/response, meta, and frame envelope, NativeAOT-safe, routed to Kotlin.
- Context: `remex.core/Messages/MessageAudience.cs:42-108`; `remex.core/Models/DesktopFrameEnvelope.cs:18-57`; `remex.core/Models/FileTransferMessages.cs` (JSON context pattern); `remex.core/Native/AndroidNativeExports.cs` (`OnNativeMessageReceived`); `remex.android/.../RemexCoreClient.kt:61-77`; `docs/REGRESSION-GUARDS.md:386-400`.
- Constraints: no reflection, `[JsonSerializable]` plus context for every type; no `protocolVersion` bump; new magic `"RPXF"`, never `RDXF`/`RDXC`; validation of every field in `remex.core/Validation/`.
- Steps: add message records and constants; add `PhoneFrameEnvelope`; register the `phone_screen_*` types in `HostToClient`; add the prefix route in the native router and the Kotlin callback; add validation for meta (1..8192 dims, rotation in {0,90,180,270}, fps 1..120).
- Acceptance: unit tests for envelope round trip and corrupt headers; a `MessageAudience` test that every `phone_screen_*` host-to-client type is routed; a Kotlin unit test that the callback dispatches by prefix; a real-device round-trip note recorded in the bead.
- Out of scope: audio, input messages.

#### 1B Phone capture, encode, uplink, consent
- Objective: the phone answers a request, obtains consent, and streams H.264 up `/ws/phone-screen` until stopped.
- Context: `AndroidManifest.xml:32-38, 346-357`; `service/RemexConnectionService.kt`; `service/FileTransferChannelClient.kt:92-203` (pinned OkHttp pattern, `ChannelTarget`); `service/FileConsentManager.kt`, `FileConsentActionReceiver.kt`; `data/SettingsManager.kt:938-944`; `ui/screens/HomeScreen.kt` connected-PC card; `ui/screens/H264StreamDecoder.kt` (bounded backlog pattern to mirror).
- Constraints: Lane C owns the tree; UI additions go through the tracker's cross-lane list; every string in 9 locales via `scripts/android_add_strings.py`; release variant only; no debug builds.
- Steps: `ScreenMirrorService` (FGS `mediaProjection`, notification with Stop); `ScreenMirrorController` (single stop path, keyguard receiver, thermal and battery listeners); `ScreenMirrorEncoder` (§4); `PhoneScreenUplink` (pinned WSS, token, meta first, envelope frames, keyframe handling); consent notification, route and sheet; `phone_screen_response`/`state` sends; Home card state.
- Acceptance: unit tests for the controller state machine (every stop reason ends in one stopped state; a request while busy answers `busy`; keyguard refuses start and stops a running session); encoder config test (dims multiple of 16, preset mapping); uplink client test that a blank token or SPKI is refused before connecting; on-device: stream shows on the PC within 3 s of consent, rotation keeps the image upright, the system chip's Stop ends the stream with `stopped` on the PC within 2 s.
- Out of scope: control, audio, settings category (lands with Lane C coordination as a follow-up unit).

#### 1C PC endpoint, decoder, page
- Objective: the PC asks, admits the uplink, decodes and shows the stream.
- Context: `remex.agent/HostBootstrapper.cs:898-954, 1008-1078`; `remex.agent/Services/FileTransfer/PhoneFileRelay.cs:56-180, 596-606`; `remex.agent/Services/ClientSessionRegistry.cs:76-146, 308`; `remex.agent/Handlers/PingPongHandler.cs` (`file_*_response` cases); `remex.agent/Services/RemoteDesktop/FFmpegH264Encoder.cs:85-145, 174`; `remex.desktop/ViewModels/ShellViewModel.cs:425`; `remex.desktop/Views/ShellView.axaml:805-991`; `docs/REGRESSION-GUARDS.md:631, 1515-1608`.
- Constraints: no `ConfigureAwait(false)`; `Guard.NotNull`; `GetRequiredService`; the five drawer edits agree; strings via `scripts/resx_add_keys.py`; the decoder lives under `Services/Video/` as `IH264Decoder` for RemEx-dffoc; no file writes of frames.
- Steps: `PhoneScreenOfferRegistry`; `PhoneScreenRelay` (request send, reply gating with the same four checks as `TryDeliverReply`); `/ws/phone-screen` map with the §2.1 admission; `FFmpegH264Decoder`; `VideoSurface`; `PhoneScreenViewModel` and view; drawer wiring and `--view PhoneScreen`.
- Acceptance: defect-injected tests: loopback uplink admitted goes red; a token for phone A presented by paired phone B goes red; a reply from an unproven session accepted goes red; token reuse goes red. Decoder test decodes a checked-in 10-frame Annex B fixture to the expected size and skips with a stated reason when ffmpeg is absent (never a hardcoded count). `ShellNavListTests`, `NavIndexToTitleConverterTests`, `ShellViewModelNavDirectionTests` updated and green. On both OSes: the page shows the phone within 3 s.
- Out of scope: control UI, hardware decode, Home tile.

#### 1D Join and docs
- Objective: end-to-end on real hardware, docs and guards updated, changelog written.
- Context: `docs/API_CONTRACTS.md` (new §7 "Phone screen mirror"), `docs/REGRESSION-GUARDS.md` (new guard "`/ws/phone-screen` admits no loopback and no unproven session; replies count only from the phone asked", anchored to file:line), `docs/CHANGELOG.md`.
- Acceptance: `./scripts/verify.ps1 -Scope all` VALID; device round trip recorded in the bead with the PC name shown on the phone sheet; `git check-ignore -v` prints nothing for every new doc.
- Out of scope: anything in phases 2-4.

### Phase 2: control (sideload flavour)
- Objective: taps, gestures, global actions and text from the PC page, gated by the phone's switch, never on Play.
- Context: §7; `data/SettingsManager.kt:938-944`; `service/AndroidFileTransferHost.kt:68-176`; `ui/screens/SettingsScreen.kt:647, 774`; `remex.core/Validation/CoordinateValidation`; `PhoneFileRelay.cs:596-606` gate pattern; `docs/REGRESSION-GUARDS.md:1639`.
- Constraints: flavour split in `app/build.gradle.kts`; the `play` manifest has no AccessibilityService; the switch is read per input; lock screen refused; disclosure screen before the system accessibility settings deep link.
- Steps: flavours; `RemexControlService` (AccessibilityService, `isAccessibilityTool=false`, description); input dispatcher (gesture builder, global actions, set-text/paste); `phone_screen_input` on the uplink; PC Control toggle enabled only after `capabilities.control` from this phone; indicator text change.
- Acceptance: defect-injected: the PC sends input before the phone said `control: true` goes red; a Play-flavour build has no accessibility service in the merged manifest (test parses it); keyguard-locked input is dropped (unit); on-device: tap, swipe, back/home/recents, typed text in a browser field.
- Out of scope: Play declaration submission (own bead), key injection beyond global actions, clipboard read in background.

### Phase 3: audio (blocked on D8)
### Phase 4: polish (hardware decode, presets UI, per-app capture hint text, multi-phone picker)

## 12. Open decisions for Connor
- D1 Second socket `/ws/phone-screen` vs frames on `/ws`. Recommend the second socket (§2).
- D2 Drawer slot. Recommend index 11 directly after Remote desktop, Ctrl+3, shifting Files..Routines by one; update A1/A2 in the 3.1 tracker. Alternative: after Processes, no shortcut shift.
- D3 Play strategy for control. Recommend flavour split, view-only on Play, declaration as its own bead after phase 2 has run on devices for a while.
- D4 Keep the RemEx pre-consent sheet before the system dialog. Recommend keep: the system dialog does not name the PC.
- D5 Stop on keyguard, non-configurable. Recommend yes.
- D6 Defaults 1280/30/4000 with three presets. Recommend yes.
- D7 Decoder dependency: ffmpeg process now, hardware decode later. Recommend yes; Media Foundation rejected for parity.
- D8 Audio phase and its PC output dependency (none exists). Recommend defer; evaluate SDL2 or OpenAL bindings only if Connor wants audio.
- D9 Let the owner pick single-app capture. Recommend yes (`createConfigForUserChoice`).
- D10 Idle auto-stop 30 min view / 10 min control. Recommend yes.
- D11 One stream per PC at a time. Recommend yes for phases 1-3.
- D12 Android switch and consent UI placement relative to Lane C. Recommend a new Settings category "Screen sharing" and a Help entry, landed after C2/C3 via cross-lane requests.
- D13 PC never persists frames, no screenshot button in phases 1-2. Recommend yes; revisit with an explicit phone-side consent flag if wanted.
