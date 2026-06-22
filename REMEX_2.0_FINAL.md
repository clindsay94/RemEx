# REMEX 2.0 — FINAL RELEASE-GATE HANDOFF PACKAGE

**Status:** NO-SHIP (see PART C). **Audit type:** read-only; no code modified. **Source of truth:** the beads referenced below — each order references its bead for symptom/root-cause; this file carries the *edit logic*.

**Boundaries audited:** JNI managed↔unmanaged + NativeAOT · Named-Pipe IPC (Session 0) · NSD/mDNS discovery · RemoteDesktop streaming (`/ws/desktop`) · Pairing + cert pinning (`/ws`) · Protocol envelope + TCP script ingress (8338).

**Verification depth:** the three most consequential S0 claims (PROTO-1 unauthenticated 8338 power commands, IPC-1 Everyone-writable pipe leaking the pairing PIN, PAIR-1 bearer-clientId auth) were re-confirmed by direct source read after the boundary agents reported them. PROTO-1 confirmed LIVE: `RemexNetworkListener` binds `IPAddress.Any` and is a running hosted service (`ExternalNetworkListenerService`, `HostBootstrapper.cs:96`).

---

## GLOBAL EXECUTION ORDER

Same-file orders are contiguous and chained by `blocked-by` so each chunk compiles independently. Apply top-to-bottom. `Seq` is the strict sequential position.

| Seq | Bead | Code | Sev | File(s) | Part |
|----|------|------|-----|---------|------|
| 1 | RemEx-htt | PROTO-1 | S0 | `Remex.Core/Services/Network/RemexNetworkListener.cs` | B |
| 2 | RemEx-4ky | PROTO-2 | S0 | `…/RemexNetworkListener.cs` | B |
| 3 | RemEx-jny | PROTO-5 | S1 | `…/RemexNetworkListener.cs` | A |
| 4 | RemEx-a75 | PAIR-5 | S0 | `RemEx.Host/HostBootstrapper.cs` | B |
| 5 | RemEx-m1i | IPC-1 | S0 | `RemEx.Host/Services/IPC/LocalIpcServerService.cs` | B |
| 6 | RemEx-n6u | IPC-2 | S0 | `…/LocalIpcServerService.cs` | B |
| 7 | RemEx-4ic | IPC-3 | S1 | `…/LocalIpcServerService.cs` | B |
| 8 | RemEx-79h | IPC-7 | S2 | `…/LocalIpcServerService.cs` | A |
| 9 | RemEx-qg2 | IPC-5 | S1 | `RemEx.Host/Services/IPC/IpcHostServer.cs` | A |
| 10 | RemEx-oj8 | IPC-6 | S1 | `RemEx.Host/Services/IPC/HostControlServer.cs` | A |
| 11 | RemEx-irl | IPC-4 | S1 | IPC stack (multi-file) | B |
| 12 | RemEx-b3m | IPC-8 | S3 | `Remex.Core/Services/RemExLocalIPC.cs`, `…/IpcPairingPinQueryService.cs` | A |
| 13 | RemEx-lhd | PAIR-2 | S0 | `RemEx.Host/Services/Security/PairingService.cs` | B |
| 14 | RemEx-29e | PAIR-6 | S2 | `…/PairingService.cs` | A |
| 15 | RemEx-dta | PAIR-3 | S0 | `RemEx.Host/Services/Security/CertificateService.cs` | B |
| 16 | RemEx-3n6 | PAIR-1 | S1 | `…/PairedClientRegistry.cs` + handshake (multi-file) | B |
| 17 | RemEx-rc4 | PAIR-4 | S1 | `…/PairedClientRegistry.cs` | A |
| 18 | RemEx-xk9 | PAIR-7 | S3 | `RemEx.Host/Handlers/PairingHandler.cs` | A |
| 19 | RemEx-288 | PROTO-3 | S0 | `Remex.Core/Messages/MessageSerializer.cs`, `RemEx.Host/Handlers/PingPongHandler.cs` | B |
| 20 | RemEx-4uy | PROTO-4 | S1 | `PingPongHandler.cs`, `HostBootstrapper.cs`, Core | B |
| 21 | RemEx-e3z | JNI-1 | S0 | `Remex.Core/Native/JniHelper.cs`, `…/AndroidNativeExports.cs` | B |
| 22 | RemEx-9m1 | JNI-2 | S0 | `…/AndroidNativeExports.cs` | B |
| 23 | RemEx-8ay | JNI-4 | S2 | `…/AndroidNativeExports.cs` | B |
| 24 | RemEx-ymb | JNI-3 | S3 | `…/AndroidNativeExports.cs` | A |
| 25 | RemEx-85i | JNI-5 | S3 | `…/AndroidNativeExports.cs` | A |
| 26 | RemEx-hht | JNI-6 | S3 | `RemEx.Android/app/build.gradle.kts` | A |
| 27 | RemEx-ii3 | RD-1 | S0 | `RemEx.Host/Services/RemoteDesktop/FFmpegH264Encoder.cs` | B |
| 28 | RemEx-fs5 | RD-3 | S0 | `…/FFmpegH264Encoder.cs` | B |
| 29 | RemEx-aa0 | RD-4 | S1 | `…/FFmpegH264Encoder.cs` | A |
| 30 | RemEx-bqc | RD-2 | S0 | `RemEx.Android/.../H264StreamDecoder.kt` | B |
| 31 | RemEx-kx4 | RD-5 | S1 | `…/H264StreamDecoder.kt` + ViewModel | B |
| 32 | RemEx-p0l | RD-6 | S2 | `RemEx.Host/Services/ScreenCapture/DxgiDesktopCapture.cs` | B |
| 33 | RemEx-m3a | RD-7 | S2 | `RemEx.Android/.../RemoteDesktopViewModel.kt` | B |
| 34 | RemEx-q6u | RD-8 | S2 | `RemEx.Host/Handlers/RemoteDesktopHandler.cs` | A |
| 35 | RemEx-a13 | NSD-1 | S0 | `RemEx.Android/.../data/NsdDiscoveryManager.kt` | B |
| 36 | RemEx-4bb | NSD-3 | S3 | `RemEx.Android/.../ConnectionViewModel.kt` | A |
| 37 | RemEx-ngs | NSD-4 | S1 | `RemEx.Host/Services/Network/MdnsAdvertisingService.cs` | B |
| 38 | RemEx-i8x | NSD-5 | S1 | `…/MdnsAdvertisingService.cs` | B |
| 39 | RemEx-00x | NSD-6 | S3 | `Remex.Core/Services/Network/MdnsDiscoveryService.cs` | A |

> **Cross-platform parity rule applies to every order.** Where an order names a Windows API (`PipeSecurity`, `WindowsIdentity`, `FileSecurity`, ACL SIDs), it MUST be guarded by `OperatingSystem.IsWindows()` with a working Linux branch (`UnixFileMode`/`SetUnixFileMode 0600`, owner-only). Validate on Windows **and** CachyOS before closing.

---

# PART A — DROP-IN ORDERS (atomic, single-file, low-risk)

### A1 · PROTO-5 (RemEx-jny, S1) — enable revocation when client-cert auth lands
- **File:** `Remex.Core/Services/Network/RemexNetworkListener.cs:244`
- **Method:** `HandleClientAsync` → `SslStream.AuthenticateAsServerAsync(...)`
- **Current → intended:** `checkCertificateRevocation: false` → `checkCertificateRevocation: true` (only meaningful once PROTO-1 enables `clientCertificateRequired: true`; sequenced after PROTO-2).
- **Edit logic:** flip the flag in the same call site PROTO-1 edits. If PROTO-1's chosen auth is `PairedClientRegistry` token rather than client cert, leave `false` and instead close this bead with a note that revocation is N/A for server-only TLS.
- **Expected states:** revoked host certs rejected at handshake; no behavior change for valid certs.

### A2 · IPC-7 (RemEx-79h, S2) — guard backoff delays on shutdown
- **File:** `RemEx.Host/Services/IPC/LocalIpcServerService.cs:126-138`
- **Method:** `ExecuteAsync(CancellationToken stoppingToken)` catch blocks.
- **Edit logic:** wrap each `await Task.Delay(…, stoppingToken)` in the `catch` arms with `try { await Task.Delay(...); } catch (OperationCanceledException) { break; }` (pattern already used in `HostControlServer.cs:86`).
- **Expected states:** clean `while` exit on shutdown; no faulted/cancelled task surfaced from `ExecuteAsync`.

### A3 · IPC-5 (RemEx-qg2, S1) — drop `CurrentUserOnly`, add explicit ACL
- **File:** `RemEx.Host/Services/IPC/IpcHostServer.cs:45`
- **Method:** pipe creation in `ExecuteAsync`.
- **Current → intended:** `new NamedPipeServerStream(name, …, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly)` → `NamedPipeServerStreamAcl.Create(...)` on Windows with `PipeSecurity` granting `WellKnownSidType.LocalSystemSid` `FullControl` + `WellKnownSidType.InteractiveSid` `ReadWrite`; Linux keeps the plain ctor (Unix perms).
- **Note:** if IPC-4 deletes this server, this order is absorbed into IPC-4; apply whichever lands first and close the other as duplicate.
- **Expected states:** interactive-user UI client connects across Session 0; service accounts/guests excluded.

### A4 · IPC-6 (RemEx-oj8, S1) — explicit ACL on `RemExHostControl` pipe
- **File:** `RemEx.Host/Services/IPC/HostControlServer.cs:53-58`
- **Method:** `RunAsync` pipe creation.
- **Edit logic:** Windows branch via `NamedPipeServerStreamAcl.Create` with `PipeSecurity` = LocalSystem `FullControl` + Interactive `ReadWrite`; Linux plain ctor. First confirm deployment model (if the GUI host also runs LocalSystem this is moot — record that in the bead).
- **Expected states:** `HostControlClient.RequestTakeoverAsync` connects cross-session; port-handoff succeeds instead of racing the bind.

### A5 · IPC-8 (RemEx-b3m, S3) — distinguish ACL denial from "no server"
- **Files:** `Remex.Core/Services/RemExLocalIPC.cs:45`, `RemEx.Host/Services/IPC/IpcPairingPinQueryService.cs:43,71`
- **Edit logic:** add `catch (UnauthorizedAccessException ex)` before the generic catch; return a distinct localized "permission" result and log `ex.GetType().Name`. Do not collapse into `null`/"IPC Error".
- **Expected states:** the PIN screen surfaces an actionable permission error rather than blank.

### A6 · PAIR-6 (RemEx-29e, S2) — compare raw HMAC bytes
- **File:** `RemEx.Host/Services/Security/PairingService.cs:172-179`
- **Method:** `VerifyClientHmacCoreAsync`.
- **Current → intended:** `CryptographicOperations.FixedTimeEquals(UTF8.GetBytes(expectedBase64), UTF8.GetBytes(clientBase64))` → decode client value with a guarded `Convert.FromBase64String`, then `FixedTimeEquals(expectedHmacRawBytes /* 32 */, clientHmacRaw)`. Mirrors the already-correct client side (`PairingClient.cs:104`).
- **Expected states:** on malformed base64 return `false` (no throw); fixed-length constant-time compare.

### A7 · PAIR-7 (RemEx-xk9, S3) — stop echoing crypto exception text to peer
- **File:** `RemEx.Host/Handlers/PairingHandler.cs:85-100`
- **Edit logic:** wrap the ECDH import/derive in try/catch; return `MakeError("invalid pairing key")` — do not interpolate `ex.Message`.
- **Expected states:** generic error to peer; detail logged host-side only.

### A8 · PAIR-4 (RemEx-rc4, S1) — harden `paired_clients.json` permissions *(blocked-by PAIR-1)*
- **File:** `RemEx.Host/Services/Security/PairedClientRegistry.cs:109-126`
- **Method:** `PersistToDisk()`.
- **Edit logic:** after `File.Move(tempPath, _storePath, overwrite:true)`: on Linux `File.SetUnixFileMode(_storePath, UserRead | UserWrite)`; on Windows set a restrictive `FileSecurity` (LocalSystem + Administrators only) via `FileInfo.SetAccessControl`. Apply identically to the per-client secret file introduced by PAIR-1. **NativeAOT note:** this file uses reflection-based `JsonSerializer` (line 88/121) — acceptable because `PairedClientRegistry` lives in `RemEx.Host` (not AOT), but do not move it into `Remex.Core`.
- **Expected states:** store readable/writable only by service identity + admins on both OSes.

### A9 · JNI-3 (RemEx-ymb, S3) — clear pending exception on alloc-fail early return
- **File:** `Remex.Core/Native/AndroidNativeExports.cs:1019-1020, 1086-1087`
- **Edit logic:** on the `jArray/jString == IntPtr.Zero` branches, `if (JniHelper.ExceptionCheck(env)) JniHelper.ExceptionClear(env);` before `return`. (Dispatcher reuses one daemon-thread `env`; a pending `OutOfMemoryError` otherwise poisons the next callback.)
- **Expected states:** no cross-callback exception bleed.

### A10 · JNI-5 (RemEx-85i, S3) — move `ReadJString` inside the `Export` guard
- **File:** `Remex.Core/Native/AndroidNativeExports.cs:455-457, 582`
- **Edit logic:** relocate the `ReadJString(...)` calls for `StartPairingNative`/`SubmitPairingPinNative` inside the `Export(env, () => …)` lambda (matching the inline form at 359-360) so the boundary guard covers marshalling.
- **Expected states:** a managed throw during marshalling is caught, not propagated past `[UnmanagedCallersOnly]`.

### A11 · JNI-6 (RemEx-hht, S3) — constrain `.so` selection to requested config + ELF check
- **File:** `RemEx.Android/app/build.gradle.kts:325-336`
- **Method:** `SyncRemexCoreSoTask.doSync`.
- **Edit logic:** before `maxByOrNull { lastModified }`, `filter` candidates whose path actually contains the requested configuration (normalize casing — `artifactsPivot` lowercases `conf` while `bin/$conf` does not); add an ELF sanity check on the chosen file (`EI_CLASS == 2` 64-bit, `e_machine == 0xB7` AArch64).
- **Expected states:** never packages a stale/cross-config `.so`.

### A12 · RD-4 (RemEx-aa0, S1) — cancel ffmpeg stderr reader *(blocked-by RD-3)*
- **File:** `RemEx.Host/Services/RemoteDesktop/FFmpegH264Encoder.cs:222-234`
- **Edit logic:** give the stderr `Task.Run` loop a `CancellationToken` from a CTS owned by the encoder; cancel+dispose it in `DisposeProcess`. Stop referencing the disposed encoder's `_logger` after cancellation.
- **Expected states:** no orphaned reader tasks accumulate under encoder-rebuild churn (verify with `dotnet-counters` thread count while dragging the quality slider).

### A13 · RD-8 (RemEx-q6u, S2) — validate pointer samples
- **File:** `RemEx.Host/Handlers/RemoteDesktopHandler.cs:1296-1334`
- **Method:** `EnqueuePointerSampleAsInputEvent`.
- **Edit logic:** before the `(int)` casts, clamp `LogicalX/Y` to the active stream pixel bounds and `Dx/Dy` to a sane delta via the shared `Remex.Core/Validation` helpers; reject NaN/Infinity.
- **Expected states:** out-of-range/hostile doubles cannot wrap into arbitrary `MoveMouse` coordinates.

### A14 · NSD-3 (RemEx-4bb, S3) — de-dup discovery launches *(blocked-by NSD-1)*
- **File:** `RemEx.Android/app/src/main/java/com/clindsay94/remex/ui/screens/ConnectionViewModel.kt:139-155`
- **Method:** `discoverHost()`.
- **Edit logic:** guard at top with `if (_isDiscovering.value) return`, or store `discoveryJob` and `discoveryJob?.cancel()` before relaunch (cancel-previous).
- **Expected states:** one in-flight discovery; no stacked multicast-lock cycles.

### A15 · NSD-6 (RemEx-00x, S3) — remove dead `MdnsDiscoveryService` from Core
- **File:** `Remex.Core/Services/Network/MdnsDiscoveryService.cs`
- **Edit logic:** referenced only by legacy `Remex.Client` + its tests; not on the Android/Host path but adds NativeAOT/trim surface in `libRemexCore.so`. Delete with the `Remex.Client` phase-out, OR if retained, validate `srv.Port ∈ 1..65535` and that `resolvedHost` parses as `IPAddress`/RFC-1123 host before building the `ws://` URL.
- **Expected states:** no unused mDNS parser compiled into the Android native lib.

---

# PART B — PRD ORDERS (multi-file / S0 / S1 refactors)

### B1 · PROTO-1 (RemEx-htt, S0) — authenticate the 8338 command channel  ⛔ release blocker
- **File:** `Remex.Core/Services/Network/RemexNetworkListener.cs` (bind `:94`, handshake `:241-244`, dispatch `:305-310`)
- **Methods:** `StartListeningAsync` (bind), `HandleClientAsync` (`clientCertificateRequired:false` → auth), dispatch gate before `ExecuteCommandAsync`.
- **DECISION (user-confirmed 2026-06-22): AUTHENTICATED-REMOTE, not loopback.** The host runs as a Windows Service (LocalSystem, Session 0) specifically so remote commands + telemetry work with **no user logged in** — locking 8338 to loopback would break the product's core purpose. Keep `IPAddress.Any`; add authentication.
- **Intended:** No command may reach `ExecuteCommandAsync` without an authenticated, paired identity. Require a `PairedClientRegistry` token/clientId in the `CommandRequest` and reject before dispatch (parity with the `/ws` default-deny gate at `PingPongHandler.cs:157`). Optionally also `clientCertificateRequired:true` + validate the client cert against the pinned set for defense-in-depth (and to make PROTO-5 revocation meaningful). **Note:** authenticating on clientId alone inherits the PAIR-1 bearer-token weakness — prefer wiring this to PAIR-1's challenge-response once that lands.
- **Edit logic:** add an auth check between deserialize (`:289`) and dispatch (`:309`); on failure return `new CommandResponse(false, "Unauthorized", …)` and close the connection. Remove the misleading "pairing verification deferred" comment at `:307` — this order *is* that deferral coming due.
- **Expected states:** unauthenticated peers receive `Unauthorized` and cannot trigger power actions; authorized callers unchanged.
- **DoD:** an integration test connecting without credentials asserts no `_commandService.*` invocation; green on Windows + Linux.

### B2 · PROTO-2 (RemEx-4ky, S0) — bound 8338 concurrency + timeouts *(blocked-by PROTO-1)*
- **File:** `Remex.Core/Services/Network/RemexNetworkListener.cs:174-205, 230-283`
- **Methods:** `AcceptClientsAsync`, `HandleClientAsync`.
- **Edit logic:** introduce a `SemaphoreSlim(maxConcurrent)` (config-driven, default e.g. 16); `await gate.WaitAsync(token)` before spawning `HandleClientSafeAsync`, release in its `finally`; reject (immediate close) when at capacity. Wrap the TLS handshake (`:241`) and both `ReadExactlyAsync` (`:271,283`) in `CancellationTokenSource.CreateLinkedTokenSource(token)` + `CancelAfter(handshakeTimeout/readTimeout)`.
- **Expected states:** connection floods and slow-loris connections are bounded; `MaxPayloadSize` allocation no longer multiplies by unbounded N.
- **DoD:** load test with N≫cap idle connections shows bounded memory + tasks.

### B3 · PAIR-5 (RemEx-a75, S0) — gate pairing endpoints to loopback/authenticated  ⛔ release blocker
- **File:** `RemEx.Host/HostBootstrapper.cs:301-329`
- **Methods:** the `GET /pairing-pin` and `POST /start-pairing` minimal-API handlers.
- **Edit logic:** at the top of each handler, reject non-loopback callers: `if (!IPAddress.IsLoopback(httpContext.Connection.RemoteIpAddress)) return Results.NotFound();` (404 to avoid advertising the endpoint). The host UI/tray reads the PIN via the local IPC path, not over HTTP. If a genuine remote-provisioning flow is required, require an already-paired client token instead. Delete the `:299-300` comment claiming "no new attack surface."
- **Expected states:** remote callers cannot mint a pairing session or read the PIN; localhost behavior unchanged.
- **DoD:** integration test from a non-loopback address gets 404; loopback still returns the PIN.

### B4 · IPC-1 (RemEx-m1i, S0) — lock down the live IPC pipe ACL + verify caller  ⛔ release blocker
- **File:** `RemEx.Host/Services/IPC/LocalIpcServerService.cs:82-84, 182-256`
- **Methods:** pipe creation in `ExecuteAsync`; `ExecuteCommandAsync`.
- **Edit logic:** (1) Remove the `WorldSid` (`S-1-1-0`) rule; grant `WellKnownSidType.LocalSystemSid` `FullControl` + `WellKnownSidType.InteractiveSid` `ReadWrite` only (keep the current-identity `FullControl` rule for stale-handle recovery). (2) Before dispatching state-changing or secret-returning commands (`SHUTDOWN…`, `GETPAIRINGPIN`, `STARTPAIRING`), verify the connected client via `pipeServer.RunAsClient(...)` / `GetImpersonationUserName()` and confirm it is the interactive console user. Linux: rely on `0600` owner-only pipe semantics (document the divergence).
- **Expected states:** non-interactive/guest local accounts can neither read the pairing PIN nor issue power commands.
- **DoD:** a process running as a different local user is denied connect; interactive user works.

### B5 · IPC-2 (RemEx-n6u, S0) — concurrent, timeout-bounded accept loop *(blocked-by IPC-1)*
- **File:** `RemEx.Host/Services/IPC/LocalIpcServerService.cs:114-150`
- **Edit logic:** transfer stream ownership into `HandleClientAsync` and spawn it fire-and-forget (`_ = HandleClientAsync(server, token)`), re-accepting immediately (mirror `IpcHostServer.cs:79`). Add a per-connection read timeout via a linked CTS `CancelAfter`.
- **Expected states:** a hung/silent client no longer wedges the PIN/power channel.

### B6 · IPC-3 (RemEx-4ic, S1) — framed IPC protocol *(blocked-by IPC-2)*
- **File:** `RemEx.Host/Services/IPC/LocalIpcServerService.cs:146-150` (+ client `Remex.Core/Services/RemExLocalIPC.cs` and the `IpcClient*`/`IpcPairingPinQueryService` callers)
- **Edit logic:** replace the single 8192-byte `ReadAsync` with a 4-byte big-endian length prefix; validate `0 < len ≤ MaxMessage` (reject oversize → no allocation DoS); loop `ReadExactlyAsync` until `len` bytes read. Update all clients to write the same framing. (Coordinate with IPC-4 — if stacks are consolidated, frame once on the surviving pipe.)
- **Expected states:** payloads > 8192 B and chunked writes parse correctly; oversize rejected.

### B7 · IPC-4 (RemEx-irl, S1) — collapse the two IPC stacks *(after IPC-1/2/3, IPC-5)*
- **Files:** `LocalIpcServerService.cs`, `IpcHostServer.cs`, `Remex.Core/Services/RemExLocalIPC.cs`, callers of `RemExLocalIPC.SendCommandAsync` (`Remex.Client/ViewModels/AppLauncherViewModel.cs:151`).
- **Edit logic:** pick `LocalIpcServerService` (the hardened survivor) as the single server; introduce one shared pipe-name constant (resolve the `"RemExLocalIPC"` vs `"RemexIPC"` collision); fold any still-needed `IpcHostServer` action (e.g. LaunchApp) into `ExecuteCommandAsync` as a new case; delete `IpcHostServer` + the static newline client. Remove the duplicate `AddHostedService` (`HostBootstrapper.cs:140`).
- **Expected states:** one pipe, one framing, one ACL; LaunchApp works cross-session.
- **DoD:** tray UI LaunchApp succeeds against the LocalSystem service; no `RemexIPC` references remain.

### B8 · PAIR-2 (RemEx-lhd, S0) — PIN brute-force throttle  ⛔ release blocker
- **File:** `RemEx.Host/Services/Security/PairingService.cs:154-202` (+ session-state fields)
- **Method:** `VerifyClientHmacCoreAsync`.
- **Edit logic:** add a per-session failed-attempt counter; on mismatch (`:187-189`) increment and after N (e.g. 5) call `CancelPairingCore()` (forces a fresh PIN). Reduce `PairingTimeoutSeconds` from 600 to ~120. Add per-IP throttling on `/ws` `pairing_complete` and `/start-pairing` (sliding window). Use `RandomNumberGenerator` for any added jitter.
- **Expected states:** ≤ N guesses per session; 10⁶ space no longer exhaustible within a session window.
- **DoD:** test driving N+1 wrong PINs asserts the session is cancelled.

### B9 · PAIR-3 (RemEx-dta, S0) — protect the host private key  ⛔ release blocker
- **File:** `RemEx.Host/Services/Security/CertificateService.cs:134-144`
- **Edit logic:** write the PFX atomically with restrictive perms *before* it contains data — create temp with `UnixFileMode.UserRead|UserWrite` (Linux) / explicit `FileSecurity` LocalSystem+Administrators (Windows), write, then `File.Move`. Eliminate the TOCTOU window (set perms before/at create, not after). Prefer storing the key in the machine keystore (`X509Store(StoreName.My, StoreLocation.LocalMachine)` / DPAPI machine scope) over a flat PFX; if a PFX must persist, give it a machine-protected password rather than `null`.
- **Expected states:** non-admin local users cannot read the key; SPKI-pin trust model holds.
- **DoD:** verify file mode `0600`/restricted ACL on both OSes after first run.

### B10 · PAIR-1 (RemEx-3n6, S1) — bind reconnect auth to a secret (proof-of-possession)
- **Files:** `RemEx.Host/Services/Security/PairedClientRegistry.cs` (storage), `PairingService.cs`/`PairingHandler.cs` (issue secret at pairing), `PingPongHandler.cs:126` + `HostBootstrapper.cs:571` (reconnect gate), Android `RemexNativeClient`/Core `PairingClient.cs` (client side).
- **Edit logic:** at pairing completion, persist a per-client 32-byte random secret (or reuse the derived `_sessionKey`) keyed by clientId instead of a bare presence flag. On each reconnect, run a challenge-response: server sends a random nonce, client returns `HMAC-SHA256(secret, nonce)`, verified with `CryptographicOperations.FixedTimeEquals`. Reject connections presenting only a clientId. Harden the secret file via PAIR-4.
- **Expected states:** possession of a clientId alone no longer authenticates; the strong PIN-derived material is no longer discarded.
- **DoD:** replaying a captured clientId without the secret fails; legitimate paired device reconnects.
- **Risk note:** wire-format/handshake change — coordinate an Android + Host release; does **not** require a `protocolVersion` bump if added as new optional handshake fields, but document in CHANGELOG.

### B11 · PROTO-3 (RemEx-288, S0) — don't crash the `/ws` loop on oversize, never skip cleanup
- **Files:** `Remex.Core/Messages/MessageSerializer.cs:72-73`, `RemEx.Host/Handlers/PingPongHandler.cs:108-331`
- **Edit logic:** in `ReceiveAsync`, on exceeding the 4 MB cap, drain the remaining frames and return `null` (or a typed oversize error) instead of `throw new InvalidOperationException`. In `HandleAsync`, move the cleanup block (`:310-331`: file-transfer cleanup, pairing cancel, telemetry stream-CTS) into a `finally`, and add `catch (InvalidOperationException)` defensively. **AOT note:** `MessageSerializer` is in `Remex.Core` — keep using the source-gen `RemexJsonSerializerContext`; do not introduce reflection.
- **Expected states:** a hostile oversize message closes one connection cleanly with no orphaned telemetry task or leaked transfer state.
- **DoD:** test sending a > 4 MB frame asserts cleanup ran and the process is unaffected.

### B12 · PROTO-4 (RemEx-4uy, S1) — single protocol-version policy *(blocked-by PROTO-3)*
- **Files:** new `Remex.Core` helper `ProtocolVersionPolicy.IsSupported(int)`; callers `PingPongHandler.cs:137` and `HostBootstrapper.cs:554`; align test `RemoteDesktopAuthTests.cs:117-143`.
- **Edit logic:** replace the two ad-hoc checks (`< 2` vs `!= "2"`) with one shared rule; decide forward-compat (accept-range vs exact) and apply to both `/ws` and `/ws/desktop`. Parse the desktop value once to `int`.
- **Expected states:** both planes agree on the same client; a future v3 bump behaves identically on both.

### B13 · JNI-1 (RemEx-e3z, S0) — clear pending Java exceptions at the export boundary
- **Files:** `Remex.Core/Native/JniHelper.cs:32-59` (`ReadJString`), `Remex.Core/Native/AndroidNativeExports.cs:1130-1141` (`Export`).
- **Edit logic:** in `ReadJString`, after each JNI call that can raise (`GetStringLength`/`GetStringChars`), `if (ExceptionCheck(env)) { ExceptionClear(env); return null; }`. In `Export`'s prologue and before the final `CreateJString`, clear any pending exception. Apply to the `IntPtr` exports (`InitRemex`/`WakePc`/`SendMessage`/`SendCommand`, `:358-376`).
- **Expected states:** no JNI calls occur while a Java exception is pending; no SIGABRT.
- **DoD:** fault-inject a pending exception path; assert no abort.

### B14 · JNI-2 (RemEx-9m1, S0) — make the export catch block non-throwing *(blocked-by JNI-1)*
- **File:** `Remex.Core/Native/AndroidNativeExports.cs:1138-1148`
- **Edit logic:** wrap the catch body: `try { return CreateJString(env, SerializeOperationFailure(...)); } catch { return IntPtr.Zero; }`. Pre-serialize a constant fallback so even `SerializeOperationFailure` can't throw out of the boundary. Apply the same symmetry the void path already has.
- **Expected states:** a failure during error-serialization cannot escape `[UnmanagedCallersOnly]`.

### B15 · JNI-4 (RemEx-8ay, S2) — lock pairing session statics *(blocked-by JNI-2)*
- **File:** `Remex.Core/Native/AndroidNativeExports.cs:548-631`
- **Edit logic:** guard all transitions of `_activePairingClient`/`_pairingWebSocket`/`_activePairingResponse` under the existing `lock (SyncRoot)` (consistent with `RegisterCallback`/frame paths); capture locals before `await`/`GetResult()`; or model the session as an immutable record swapped via `Interlocked.Exchange`.
- **Expected states:** concurrent `StartPairingNative`/`SubmitPairingPinNative` cannot dispose-then-use the `ClientWebSocket`.
- **DoD:** drive both exports concurrently from two Java threads; assert no `ObjectDisposedException`.

### B16 · RD-1 (RemEx-ii3, S0) — non-blocking encoder feed
- **File:** `RemEx.Host/Services/RemoteDesktop/FFmpegH264Encoder.cs:307-335` (`EncodeFrame`), capture call `RemoteDesktopHandler.cs:376`.
- **Edit logic:** stop calling the synchronous blocking `_stdin.Write(...)+Flush()` on the capture thread. Introduce a bounded `Channel<byte[]>` (capacity small, e.g. 2-3) between capture and a dedicated stdin-writer task that uses `Stream.WriteAsync(buffer, ct)`; drop frames when the channel is full instead of blocking; observe `ct` so disconnect terminates the writer.
- **Expected states:** a stalled HW encoder/slow client drops frames rather than freezing the whole stream; disconnect tears the writer down promptly.
- **DoD:** `kill -STOP` the ffmpeg child mid-stream; capture thread does not hang and disconnect completes within the cancellation window.

### B17 · RD-3 (RemEx-fs5, S0) — bound encoder output + accumulator *(blocked-by RD-1)*
- **File:** `RemEx.Host/Services/RemoteDesktop/FFmpegH264Encoder.cs:19, 251-265, 283, 335`
- **Edit logic:** (1) cap the Annex-B `acc` accumulator — if it exceeds a few MB without an AUD start code, reset/abort the stream (malformed input guard). (2) Replace the unbounded `_encodedFrames` `ConcurrentQueue` with a bounded `Channel<byte[]>` (drop-oldest), or have the capture loop fully drain via `TryGetEncodedFrame` each iteration and keep only the newest.
- **Expected states:** no unbounded memory growth or latency pile-up when consume < produce.

### B18 · RD-2 (RemEx-bqc, S0) — stop silent frame drops; recover keyframes
- **Files:** `RemEx.Android/.../H264StreamDecoder.kt:75-92`; host `FFmpegH264Encoder.cs:310-314` (real keyframe support) + a host keyframe-request message.
- **Edit logic:** on `dequeueInputBuffer` returning `< 0`, retry with a bounded loop / larger timeout before dropping; never silently drop. Move to `MediaCodec.setCallback` async with a bounded input queue. On decoder error/desync, send a keyframe-request to the host; implement real on-demand IDR (ffmpeg `force_key_frames`/`-x264-params`) — `forceKeyframe` is currently a no-op.
- **Expected states:** transient backlog no longer causes GOP-length (≈1 s) green corruption; missed IDR recoverable on demand.

### B19 · RD-5 (RemEx-kx4, S1) — track host resolution changes in the decoder *(blocked-by RD-2)*
- **Files:** `RemEx.Android/.../H264StreamDecoder.kt:42-65`, `RemoteDesktopViewModel.kt` (meta handling).
- **Edit logic:** drive decoder (re)creation off the host-reported `PixelWidth/PixelHeight` in frame meta (authoritative after `ClampScaleForH264`'s 4096 cap), not the client scale. Handle `INFO_OUTPUT_FORMAT_CHANGED` and SPS/PPS-on-IDR so MediaCodec reconfigures.
- **Expected states:** mid-stream host resolution changes don't garble output.

### B20 · RD-6 (RemEx-p0l, S2) — eliminate per-frame allocations on DXGI capture
- **File:** `RemEx.Host/Services/ScreenCapture/DxgiDesktopCapture.cs:457, 480-540`
- **Edit logic:** reuse one writable `Bitmap` and a pooled output buffer (`ArrayPool<byte>.Shared` or a reused `byte[]` sized to the fixed `ExpectedInputByteCount`) instead of allocating two `Bitmap`s + `new byte[bytes]` per frame; replace `Marshal.AllocHGlobal(SizeOf<MappedSubresource>())` per frame with `stackalloc`/`Span<byte>`.
- **Measurement (required):** capture `dotnet-counters` `alloc-rate` + Gen0 GC count while streaming H.264 at 120 fps before/after; target a measured drop in Gen0 churn (not a blanket "zero-allocation" claim).

### B21 · RD-7 (RemEx-m3a, S2) — bound MJPEG recomposition
- **File:** `RemEx.Android/.../RemoteDesktopViewModel.kt:92-93, 583-587`
- **Edit logic:** render MJPEG frames to a `Surface`/`AndroidView` (as the H.264 path does) rather than pushing unique-timestamp `Bitmap`s through `_currentFrame` `StateFlow`; if kept Bitmap-based, isolate the read in a leaf composable and use `derivedStateOf`/a `@Stable` frame holder.
- **Measurement (required):** Layout Inspector recomposition counts on `RemoteDesktopScreen` during MJPEG before/after; confirm the screen subtree is no longer recomposing per frame.

### B22 · NSD-1 (RemEx-a13, S0) — serialize/cancel NSD resolve
- **File:** `RemEx.Android/.../data/NsdDiscoveryManager.kt:109-137`
- **Edit logic:** add `cont.invokeOnCancellation { … }` to the Phase-2 resolve coroutine (Phase-1 already has it at `:100`); serialize resolves process-wide with a `Mutex` around `discoverAndResolve` (pre-API-34 allows one resolve/process). On API 34+, migrate to `NsdManager.registerServiceInfoCallback` / `resolveService(Executor, ServiceInfoCallback)` which supports concurrent, cancellable resolves. Fixes the `FAILURE_ALREADY_ACTIVE` race with the self-heal caller (`RemexClientManager.kt:140-141`).
- **Expected states:** overlapping manual + self-heal discovery no longer fails or leaks a pending resolve.

### B23 · NSD-4 (RemEx-ngs, S1) — re-advertise mDNS on network change
- **File:** `RemEx.Host/Services/Network/MdnsAdvertisingService.cs:23-72`
- **Edit logic:** subscribe `NetworkChange.NetworkAddressChanged`; on change, `Unadvertise(profile)`, rebuild addresses, `Advertise` the new set; unsubscribe on `stoppingToken`. Replace the single-shot advertise + `Task.Delay(Timeout.Infinite)`.
- **Expected states:** DHCP renew / NIC switch / VPN up-down keeps the host discoverable.

### B24 · NSD-5 (RemEx-i8x, S1) — advertise all interfaces; Linux-aware filtering *(blocked-by NSD-4)*
- **File:** `RemEx.Host/Services/Network/MdnsAdvertisingService.cs:82-144`
- **Edit logic:** drop the single-address `yield break` fast path; advertise all preferred unicast addresses. Extend the virtual-interface exclusion list with Linux names (`docker`, `virbr`, `tailscale`, `wg`, `veth`) alongside the Windows ones; optionally include link-local-safe IPv6.
- **Expected states:** multi-NIC/Linux hosts are discoverable on the segment the client is actually on.
- **DoD:** on CachyOS with `docker0`/`tailscale0` present, the advertised address matches the client's segment.

---

# PART C — RELEASE VERDICT

## ⛔ NO-SHIP

RemEx 2.0 must not ship in its current state. The audit found **14 S0** and **12 S1** defects across every audited interconnect. Three independent, individually-sufficient ship-blockers were confirmed by direct source read:

1. **PROTO-1 (RemEx-htt)** — the 8338 TCP command channel is bound to `0.0.0.0`, runs as a live hosted service, and executes **SHUTDOWN/RESTART/SLEEP/LOCK** after a server-only TLS handshake with **zero client authentication**. Any device on the network can power-control the PC.
2. **PAIR-5 (RemEx-a75)** + **PAIR-2 (RemEx-lhd)** — `/start-pairing` and `/pairing-pin` disclose the pairing PIN to **unauthenticated remote** callers, and PIN verification has **no brute-force throttle** over a 10-minute session. Together these defeat the out-of-band pairing model entirely (remote takeover without physical access).
3. **IPC-1 (RemEx-m1i)** — the live `RemExLocalIPC` pipe grants **Everyone** read/write and returns the live pairing PIN (and accepts power commands) to **any local user**.

Supporting S0s compound the picture: host private key world-readable (PAIR-3), uncaught oversize-message path leaking per-connection state (PROTO-3), JNI exception paths that abort the JVM/NativeAOT runtime (JNI-1/JNI-2), streaming hangs and unbounded queues (RD-1/RD-2/RD-3), the NSD resolve race (NSD-1), and IPC DoS/flood vectors (IPC-2/PROTO-2).

## Blocking bead IDs (must be closed before ship)

**P0 (all required):** `RemEx-htt` PROTO-1 · `RemEx-a75` PAIR-5 · `RemEx-m1i` IPC-1 · `RemEx-lhd` PAIR-2 · `RemEx-dta` PAIR-3 · `RemEx-n6u` IPC-2 · `RemEx-4ky` PROTO-2 · `RemEx-288` PROTO-3 · `RemEx-e3z` JNI-1 · `RemEx-9m1` JNI-2 · `RemEx-ii3` RD-1 · `RemEx-fs5` RD-3 · `RemEx-bqc` RD-2 · `RemEx-a13` NSD-1

**P1 (required for a quality 2.0; at minimum the security + parity set):** `RemEx-3n6` PAIR-1 · `RemEx-rc4` PAIR-4 · `RemEx-irl` IPC-4 · `RemEx-qg2` IPC-5 · `RemEx-oj8` IPC-6 · `RemEx-4ic` IPC-3 · `RemEx-ngs` NSD-4 · `RemEx-i8x` NSD-5 · `RemEx-kx4` RD-5 · `RemEx-aa0` RD-4 · `RemEx-4uy` PROTO-4 · `RemEx-jny` PROTO-5

## Residual risk that MAY be knowingly shipped (defer with logged reason)

- **P2:** RD-6 (`p0l`), RD-7 (`m3a`), RD-8 (`q6u`), PAIR-6 (`29e`), JNI-4 (`8ay`), IPC-7 (`79h`) — performance/hardening; not exploitable on their own. Defer only with a benchmark/measurement attached per the cited detection method.
- **P3:** JNI-3 (`ymb`), JNI-5 (`85i`), JNI-6 (`hht`), NSD-3 (`4bb`), NSD-6 (`00x`), PAIR-7 (`xk9`), IPC-8 (`b3m`) — localized cleanups/diagnosability.

## Definition of Done (release)

Every **P0 and P1** bead closed via an applied order, with:
1. **Green build** on Windows, Linux (CachyOS via `build-remex.ps1`), and Android (`scripts/android-fresh.ps1`).
2. **Green tests** (`dotnet test Remex.sln`) plus new regression tests named in each order's DoD.
3. **Cross-platform parity verified** for every order touching ACL/file-permission/native code — tested on Windows **and** CachyOS, or a follow-up bead filed for the untested OS.
4. **CHANGELOG.md** updated under `Security`/`Fixed`/`Changed`; `protocolVersion` bump coordinated only if a wire-format break is taken (PAIR-1 can avoid it via additive optional fields).

P2/P3 may defer with a logged reason on the bead.

## Design decisions — RESOLVED 2026-06-22 (user-confirmed)

- **8338 design (PROTO-1, B1): AUTHENTICATED-REMOTE.** Host runs as a Windows Service (LocalSystem, Session 0) precisely to serve remote commands + telemetry with no user logged in. Do **not** bind loopback; require a paired-client identity before dispatch. (See B1, updated.)
- **IPC-6 / Session-0 model: confirmed and unchanged.** Remote commands + telemetry flow through the Session-0 service directly and do **not** traverse the named pipe — the headless "works without login" requirement is unaffected by any pipe fix. The pipe ACL orders (IPC-1/IPC-5/IPC-6) govern only the **local tray/dashboard UI** (interactive user) talking to the service; still required, but they have no bearing on remote-without-login. Confirm GUI-host identity vs the LocalSystem agent during implementation of IPC-6.
- **`Remex.Client`: remove 100% (new bead `RemEx-d8s`).** NOT a clean delete — `RemEx.Host` references the project and uses `Remex.Client.Services` in `Program.cs`, `StartupRegistrationService.cs`, `SessionKeepUnlockedService.cs`, `DesktopIconExtractionService.cs`. The still-used types must be **migrated** into `RemEx.Host`/`Remex.Core` first, then the legacy UI deleted and the project + `Remex.Client.Tests` removed from `Remex.sln`. `RemEx-d8s` subsumes NSD-6 (`RemEx-00x`) and the legacy `PinnedCertStore`/`IpcWakeOnLanService` cleanup. Sequence after the P0/P1 security fixes (it touches some of the same Host files).
