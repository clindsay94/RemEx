# RemEx 2.0 Security Protocol Refactor

This document summarizes the investigation and implementation of the RemEx 2.0 secure pairing and connection refactor between the Android Client and the Linux/Windows Host.

## 1. Initial Issue: WSS Connection Timeout
**Symptom:** The Android client consistently timed out after 20 seconds during the `StartPairing` TLS handshake.

**Investigation & Theory:**
*   Initial theory pointed to the manual `[UnmanagedCallersOnly]` overrides of `verifyRemoteCertificate` in `AndroidNativeExports.cs` blocking the `.NET` `SslStream` handshake.
*   After removing the overrides and re-enabling `JNI_OnLoad` for the NativeAOT Android library, the timeouts persisted, proving the JNI override was not the root cause.
*   Detailed TCP probe logging was implemented in the Native layer to distinguish L4 (TCP) from L6/L7 (TLS) failures. The logs revealed the client was failing the initial TCP connection.

**Resolution:** 
*   The issue was purely environmental. The Android device had Wi-Fi disabled, attempting to route the private IP (`10.0.0.3`) over a cellular network, causing pure TCP timeouts. 
*   The `UnmanagedCallersOnly` overrides were **necessary** to bypass the OS trust manager during the TLS handshake since RemEx handles certificate validation manually via SPKI pinning at the WebSocket layer. The overrides were restored.

## 2. Refactoring "Access Key" to "Pairing PIN"
**Goal:** Migrate the UI and data layer from the legacy shared "Access Key" model to the new 2.0 ephemeral 6-digit "Pairing PIN" model.

**Implementation:**
*   **UI Updates:** Replaced "Access Key" inputs with "Pairing PIN" across the `ConnectionScreen`. Added length validation (max 6 digits) and changed input types to `NumberPassword`.
*   **Data Layer:** Purged all references to `accessKey` from `SettingsManager.kt`, `ConnectionPreferences`, and `DataStore`. The PIN is intentionally transient and never persisted.
*   **ViewModel/Widget Cleanup:** Removed access key injection from `AppLauncherViewModel`, `RemoteControlViewModel`, `TaskManagerViewModel`, and `RemoteControlWidget`. Commands are now authenticated implicitly via the secured TLS tunnel.
*   **Smart Pairing:** Updated `RemexClientManager` to detect when a PIN is manually provided in the `ConnectionScreen`. If provided, the manager automatically orchestrates the `StartPairing` -> `SubmitPairingPin` flow in the background, bypassing the dedicated `PairingScreen` entirely.

## 3. Pairing Payload Missing & Handshake Race Condition
**Symptom:** `PairingClient` aborted the handshake with `Expected PairingResponse, got host_info`.

**Investigation & Theory:**
*   The host `PingPongHandler` greets all new WebSocket connections with a `HostInfo` payload immediately upon connection.
*   The `PairingClient` expected the very first message received to be the `PairingResponse`, failing instantly when it encountered the `HostInfo` greeting.

**Resolution:**
*   Updated `StartPairingAsync` and `CompletePairingAsync` in `PairingClient.cs` to loop and safely ignore non-pairing messages (like background telemetry or host greetings) until the expected pairing response is received.

## 4. PIN Verification Failure & AuthenticationException
**Symptom:** `SubmitPairingPin` failed with "incorrect PIN or session expired" followed by an `AuthenticationException / RemoteCertificateNameMismatch` during the subsequent connection attempt.

**Investigation & Theory:**
*   A severe race condition was identified between the manual connection flow and the background heartbeat loop in `RemexClientManager`.
*   When the user tapped "Connect", the manager began automatic pairing. Milliseconds later, the heartbeat loop fired, saw no active connection, and emitted a `pairingRequired` event.
*   Because `pairingRequired` was a `SharedFlow` with `replay = 1`, it immediately launched the `PairingScreen`, which executed a *second* `StartPairing` command.
*   The second `StartPairing` wiped out the native WebSocket state of the first session, generating a new PIN on the host. The client then submitted the user's typed PIN against the *new* session, resulting in an immediate rejection.
*   The subsequent `AuthenticationException` occurred because the aborted pairing forced the app to fall back on a *stale* SPKI hash saved from a prior run.

**Resolution:**
*   **Replay Buffer:** Changed `_pairingRequired` to `replay = 0` to prevent stale UI triggers.
*   **Heartbeat Isolation:** Added an `isAutoConnect` flag to the `connect()` routine so background heartbeats silently fail on untrusted hosts rather than hijacking the UI.
*   **State Locking:** Added synchronous checks to `toggleConnection` to prevent double-tap race conditions.
*   **Stale Hash Recovery:** Updated the logic to explicitly clear the cached SPKI hash (`spkiHash = null`) if the user manually types a PIN, forcing a clean re-pair and overwriting the stale certificate.