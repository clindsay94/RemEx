## Code-Review-Report.md
---
type: Bugfix
severity: Critical
breaking_changes: False
target_files:
  - RemEx.Android/app/src/main/java/com/clindsay94/remex/RemexClientManager.kt
  - RemEx.Android/app/src/main/java/com/clindsay94/remex/service/RemexConnectionService.kt
  - RemEx.Android/app/src/main/java/com/clindsay94/remex/ui/screens/PairingScreen.kt
  - Remex.Core/Native/RemexNativeClient.cs
  - Remex.Host/Handlers/PingPongHandler.cs
  - Remex.Host/Handlers/PairingHandler.cs
  - Remex.Host/HostBootstrapper.cs
  - Remex.Host/Services/Security/PairedClientRegistry.cs
  - Remex.Host/Services/Security/PairingService.cs
---

## Issue Summary
The reconnection model is not state-safe. Transport connectivity, pairing trust, foreground-service lifetime, and host identity are tracked independently and inconsistently, so disconnects frequently leave one side believing the connection is healthy while the other side requires a fresh pairing or a manual reset.

This explains the exact failure class you described: desktop restarts forget previously paired Android clients, Android can get stuck in a perpetual "connecting" state after asynchronous failures, interrupted pairings stay wedged on the host, and QR/PIN flows do not agree on who the host is.

## Root Cause Analysis

### Finding 1 — Host pairing trust is process-local and is lost every time the desktop host restarts
**Severity:** Critical

**Evidence**
- `Remex.Host/Services/Security/PairedClientRegistry.cs:12-14` explicitly keeps paired clients in an in-memory `ConcurrentDictionary`.
- `Remex.Host/Handlers/PairingHandler.cs:104-107` only registers the client ID in that in-memory registry after successful `PairingComplete`.
- `Remex.Host/Handlers/PingPongHandler.cs:122-130` relies on that registry on every reconnect to decide whether a client is already trusted.

**Why it breaks**
- Android persists its `clientId` (`SettingsManager.getOrCreateClientId()`), but the host does not persist the corresponding trust decision.
- After the desktop host restarts, the Android client still has a valid pinned certificate and the same client ID, but the host forgets that the client was previously paired.
- The next connection is therefore transport-valid but auth-invalid.

**Observed result**
- A desktop restart turns a previously paired Android device into an effectively new client.
- Users are forced back into a handshake path or into manual cleanup because the host has lost the only state it uses to recognize paired clients.

**Proposed Solution**
- Persist paired-client trust on the host, keyed by a stable tuple such as `(host certificate identity, clientId)`.
- Load that store during host startup before accepting WebSocket connections.
- Treat pairing as a durable trust record, not a per-process flag.

### Finding 2 — Android reconnection can deadlock itself in `isConnecting = true`
**Severity:** Critical

**Evidence**
- `RemEx.Android/app/src/main/java/com/clindsay94/remex/RemexClientManager.kt:73-84` suppresses heartbeat reconnect attempts whenever `isConnecting.value` is true.
- `RemEx.Android/app/src/main/java/com/clindsay94/remex/RemexClientManager.kt:117-121` and `144-151` set `_isConnecting` before starting a connection attempt.
- `RemEx.Android/app/src/main/java/com/clindsay94/remex/RemexClientManager.kt:281-284` clears `_isConnecting` only when `onConnectionStateChanged(true)` arrives.
- `RemEx.Android/app/src/main/java/com/clindsay94/remex/RemexClientManager.kt:325-326` forwards connection errors but does not clear `_isConnecting`.
- `Remex.Core/Native/RemexNativeClient.cs:129-145` and `272-275` report failed or dropped connections with `ConnectionStateChanged(false)`.

**Why it breaks**
- Failed asynchronous connects and dropped sockets emit `false`, not `true`.
- The Android manager never clears `_isConnecting` on those failure callbacks.
- Once that happens, the heartbeat loop stops retrying because it sees the client as permanently "in progress".

**Observed result**
- After a severed connection or a host restart, Android can remain stuck in a pseudo-connecting state.
- Auto-reconnect stops, UI state becomes misleading, and the only practical recovery becomes manual intervention.

**Proposed Solution**
- Make `onConnectionStateChanged(false)` and `onConnectionError(...)` clear `_isConnecting`.
- Separate transport phases explicitly: `Disconnected`, `Connecting`, `Connected`, `AuthRequired`, `Authenticated`, `Reconnecting`, `Failed`.
- Gate the heartbeat on those explicit states instead of a single boolean.

### Finding 3 — The Android foreground service tears itself down on transient disconnects, and force-stop cannot be made transparent
**Severity:** High

**Evidence**
- `RemEx.Android/app/src/main/java/com/clindsay94/remex/service/RemexConnectionService.kt:67-78` stops the service as soon as `isConnected` becomes false.
- `RemEx.Android/app/src/main/java/com/clindsay94/remex/ui/screens/ConnectionViewModel.kt:115-120` only starts that service during a manual connect action.

**Why it breaks**
- The service is being used as the app's "stay alive and reconnect" mechanism, but it explicitly calls `stopSelf()` on the first disconnect.
- That means a routine transport blip kills the very component intended to survive routine transport blips.
- Separately, Android force-stop is an OS-level kill switch. Per Android's `Service.onStartCommand` / foreground-service behavior, `START_STICKY` can recover from system kills, but not from an explicit user force-stop. That behavior is platform-defined, not app-defined. External validation: Android Developers service lifecycle documentation.

**Observed result**
- Background recovery is fragile even for normal disconnects.
- After a true force-stop, the app cannot reconnect until the user launches it again; the code should therefore optimize for clean resume on next launch, not pretend force-stop can be bypassed.

**Proposed Solution**
- Do not self-stop the foreground service on the first disconnect; let it remain alive through the reconnect window.
- Reconnect policy should live in one place and should survive short host outages.
- On next launch after a force-stop, automatically restore the persisted host/client identity and resume the reconnect flow without requiring unpair/reinstall steps.

### Finding 4 — Interrupted pairings are never canceled, so the next pairing attempt can be wedged for up to two minutes
**Severity:** High

**Evidence**
- `Remex.Host/Services/Security/PairingService.cs:26-43` stores a single global pairing session.
- `Remex.Host/Services/Security/PairingService.cs:41-42` sets a 120-second timeout.
- `Remex.Host/Services/Security/PairingService.cs:58-78` rejects any new pairing attempt while the current session is active.
- `Remex.Host/Services/Security/PairingService.cs:161-183` contains `CancelPairing()`, but repo usage is effectively success-path only (`VerifyClientHmacAsync`).
- `Remex.Host/Handlers/PairingHandler.cs` and `Remex.Host/Handlers/PingPongHandler.cs` do not cancel the pairing session when the WebSocket dies mid-handshake.

**Why it breaks**
- If the Android app is killed, the desktop closes, or the network drops in the middle of the PIN flow, the host retains a live singleton pairing session.
- The next retry hits "pairing session already active" instead of starting cleanly.

**Observed result**
- Users have to wait for expiry or manually reset the host-side state.
- This aligns with the "half a step away from reinstalling" failure mode: the retry path is not idempotent.

**Proposed Solution**
- Bind pairing state to the connection/session that started it.
- Cancel the active pairing immediately when that WebSocket closes or when the connect attempt is superseded.
- Prefer per-client or per-connection pairing sessions instead of a single global singleton session.

### Finding 5 — The codebase uses two different host identities for the same desktop
**Severity:** High

**Evidence**
- `Remex.Host/HostBootstrapper.cs:192-200` returns `hostId = HostBootstrapper.InstanceId` from the QR bootstrap endpoint.
- `Remex.Host/Handlers/PairingHandler.cs:64-69` returns `HostId = Environment.MachineName` from the PIN pairing flow.
- `HostBootstrapper.InstanceId` is process-generated (`Guid.NewGuid()`), so it changes every desktop launch.

**Why it breaks**
- QR bootstrapping and PIN pairing are naming the same host with different identifiers.
- One identifier is stable (`Environment.MachineName`), the other is ephemeral (`InstanceId`).
- Any pinning, trust, or reconnect logic that keys off `hostId` can therefore see the same desktop as a different host after restart.

**Observed result**
- QR-established trust and PIN-established trust do not live in the same identity namespace.
- The system feels like it is "treating the host as new" because, in one flow, it literally is.

**Proposed Solution**
- Pick one stable host identity and use it everywhere: QR bootstrap, PIN pairing, discovery metadata, and reconnect bookkeeping.
- Reserve per-process instance IDs for stream/session diagnostics only, not for trust identity.

### Finding 6 — The main control channel enforces pairing, but the dedicated desktop stream channel does not
**Severity:** High

**Evidence**
- `Remex.Host/Handlers/PingPongHandler.cs:154-170` blocks most messages until pairing is complete.
- `Remex.Host/HostBootstrapper.cs:244-259` maps `/ws/desktop` directly to `RemoteDesktopHandler`.
- `Remex.Host/Handlers/RemoteDesktopHandler.cs:71-183` handles the stream with no pairing or paired-client check.

**Why it breaks**
- The app has two channels with different authorization rules.
- A client can therefore be "unpaired" on the command channel but still interact with the desktop stream channel, or vice versa.

**Observed result**
- Reconnect behavior becomes inconsistent and hard to reason about.
- Users can see partial functionality after reconnects instead of a single clean state transition, which makes failures look random.

**Proposed Solution**
- Apply the same trust/auth model to `/ws` and `/ws/desktop`.
- Reuse the same stable client identity and same persisted pairing record for both channels.
- Expose one authoritative "ready" state only after both transport and authorization are satisfied.

## Proposed Solution
The fix needs to be architectural, not a one-line retry tweak.

1. **Persist trust on both sides**
   - Host: persist paired clients instead of using an in-memory registry.
   - Client: persist a stable client identity and reuse it across reconnects.

2. **Promote authentication to a first-class connection state**
   - Do not equate `WebSocket open` with `ready`.
   - The client should not report "connected" until the host has either recognized the persisted pairing or the pairing handshake has completed.

3. **Make reconnect idempotent**
   - Starting a new connect attempt must cancel any in-flight connect, pairing, and stream state from the previous attempt.
   - Any interrupted pairing must be cleaned up immediately on socket close.

4. **Unify host identity**
   - Use one stable host identifier across QR, PIN, discovery, and pin storage.
   - Keep per-process instance IDs only for stream/session diagnostics.

5. **Stop treating the foreground service as disposable**
   - Keep it alive across transient disconnects.
   - Accept that force-stop cannot be bypassed, but make next-launch recovery automatic and clean.

6. **Align channel authorization**
   - `/ws` and `/ws/desktop` should share the same trust contract and same persisted pairing record.

## Testing Gaps
- `Remex.Host.Tests/PairingServiceTests.cs:27-212` validates cryptographic pairing mechanics, but not disconnect cleanup, host restart persistence, or retry-after-abort behavior.
- `Remex.Client.Tests/ViewModels/ConnectionViewModelTests.cs:222-359` acknowledges missing reconnect/timeout abstractions; the most relevant reconnection cases are currently untested.
- No Android tests were found for:
  - reconnect after async connection failure
  - reconnect after desktop restart
  - interrupted pairing retry
  - foreground-service behavior during transient disconnects
  - clean resume after app relaunch

## Recommended Fix Order
1. Persist `PairedClientRegistry` and unify host identity.
2. Fix Android state handling so failed async connects clear `_isConnecting`.
3. Cancel pairing sessions on connection loss.
4. Keep the foreground service alive during reconnect windows.
5. Apply pairing/auth checks consistently to `/ws` and `/ws/desktop`.
