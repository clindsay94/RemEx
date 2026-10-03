<div align="center">

# 🛡️ How RemEx Keeps You Safe

**One honest explanation of RemEx's security — written three times, at three depths.**

</div>

RemEx lets your phone see and control your PC. That is a lot of power, so it is built to be safe by
default. This document explains *how*, and you get to choose how deep you go:

| Level | Who it's for | Start here |
|---|---|---|
| **1 · Plain English** | Anyone. No technical knowledge needed. | [Jump ↓](#level-1--the-plain-english-version) |
| **2 · A bit technical** | You know terms like "encryption" and "certificate." | [Jump ↓](#level-2--the-somewhat-technical-version) |
| **3 · Full technical spec** | Security engineers. No simplifications, real algorithm names. | [Jump ↓](#level-3--full-technical-specification) |

> This document explains the *design*. For **which versions are supported** and **how to report a
> vulnerability**, see the [Security Policy](SECURITY.md). For the exact wire messages, see the
> [API Contracts](API_CONTRACTS.md).

---

## Level 1 — The plain-English version

*No technical knowledge needed. Analogies, not jargon.*

### The short version

RemEx only ever talks to **your phone**, only **after you personally approve it**, and everything it
sends is **scrambled** so nobody else on your network can read it. Nothing goes to the internet or to
any company — it's just your phone and your PC, talking directly.

### Only your phone, and only after you say yes

The very first time your phone connects to your PC, your PC shows a **6-digit code** (a PIN) on its
screen. You type that code into your phone. That's the moment you say *"yes, this phone is allowed."*
Until you do that, your PC ignores the phone completely.

Think of it like the code you enter to connect a new remote to a smart TV — except here the code also
proves the connection is genuinely between *your* two devices and not a stranger pretending to be one
of them.

### Nobody in the middle can listen

Everything between your phone and PC travels through a **locked, scrambled tunnel** (the same kind of
encryption your bank's website uses). Even someone connected to the same Wi-Fi can't peek at your
screen, your keystrokes, or your files. If anything ever tried to sneak in between your phone and PC,
the connection simply **refuses to open** rather than risk it.

### You only do the code once

After that first pairing, your phone quietly **remembers your PC** in a locked vault on the phone. From
then on it reconnects on its own — you won't type the code again unless you deliberately "unpair."

### Guessing the code doesn't work

The 6-digit code is only valid for about **two minutes**, then your PC throws it away and shows a new
one. And if someone tries to guess codes over and over, RemEx **slows them down more and more** with
each wrong try. Between the short timer and the slow-down, guessing your way in isn't realistic.

### Nothing goes to the cloud

There's no RemEx account, no company server in the middle, and no data leaving your home network. Your
phone connects **straight to your PC**. If you want to use RemEx away from home, you add a private,
encrypted network of your own (like **Tailscale** or **WireGuard**) — RemEx never does that silently.

### Routines: RemEx acting on its own

Routines (RemEx 3.0) let RemEx do things without you pressing a button: wake your PC when you get home,
put it to sleep after 30 minutes idle, lock it when you tap an NFC tag. That is a new kind of power,
because something you set up once keeps acting later, possibly while you are not looking. So:

- **You only set them up on your phone.** The PC can switch a routine off, pause them all or block a
  phone, but it can't create or change one. It checks every routine again before it keeps it.
- **Nothing powers your PC off without a warning.** Before any routine shuts down, restarts, signs out,
  sleeps or hibernates the PC, the PC shows a **15-second countdown with a Cancel button**, even if the
  routine came from your phone. The only thing that skips it is you clicking **Run now on the PC itself
  and confirming**, because then you are clearly sitting there.
- **One button stops them.** Pause all, on the phone or on the PC, stops every routine from starting on
  its own until you resume.
- **No new doors.** Routines travel over the same locked connection as everything else. RemEx does not
  open any new network port for them, and does not use the internet.
- **No location tracking.** The phone knows you are home by recognising your home Wi-Fi, not by GPS, and
  RemEx never asks for location permission.

### Your part of the deal

RemEx protects the *connection*. A few things are still up to you:

- **Keep your PC's login password private** — RemEx runs as *you* on your PC.
- **Only pair phones you trust**, and enter the PIN where nobody's peeking over your shoulder.
- **Install RemEx updates** when they're available.
- Turn on the optional "keep session unlocked" feature **only if you understand it** — it keeps your PC
  usable (unlocked) while your phone is connected, and RemEx warns you clearly on screen while it's on.

---

## Level 2 — The somewhat-technical version

*You know what encryption and certificates are; here's how RemEx uses them.*

### Encrypted transport (TLS 1.3)

Every connection — telemetry, power commands, pairing, file transfer, and the remote-desktop video
stream — runs over **TLS 1.3** (or TLS 1.2 if the client does not support 1.3), over secure
WebSockets (`wss://`). Plain, unencrypted connections are **switched off** on the Android side
entirely, so there is no accidental "fall back to cleartext."

### Certificate pinning, sealed by the PIN

The PC generates its own **certificate** the first time it runs and keeps it. When you pair, your phone
records a fingerprint of that certificate (its **SPKI hash**) and *pins* it: from then on, your phone
will only trust a PC that presents **that exact certificate**. If the certificate ever changes
unexpectedly, the phone **refuses to connect** rather than trust a possible impostor.

Because the certificate is self-signed, pinning alone is "trust the first one you see." RemEx closes
that gap with the **6-digit PIN**: the PIN mathematically proves that the phone and PC that pinned each
other are the *same two devices that ran the pairing* — so an attacker can't slip in during that first
handshake. (This is why the PIN matters even though everything is already encrypted.)

### The pairing handshake (ECDH + PIN)

Pairing uses **ECDH (Elliptic-Curve Diffie–Hellman)** — a standard way for two devices to agree on a
shared secret key without ever sending the key itself. The phone and PC each generate a temporary key
pair, exchange the public halves, and independently compute the same shared secret. The 6-digit PIN is
then used to confirm — via a keyed check (**HMAC**) — that both sides really do hold the same secret and
that the human approved it. If the check fails, pairing is rejected.

### Why brute-forcing the PIN fails

Two independent defenses:

1. **Short life:** the PIN is valid for **120 seconds**, then it's discarded and regenerated.
2. **Throttling:** repeated pairing attempts from the same device are rate-limited with an **escalating
   back-off** (each failure makes the next attempt wait longer, with a little randomness added). An
   attacker can't fire off thousands of guesses in the two-minute window.

### Reconnecting safely

When you pair, your PC binds your phone to a **32-byte reconnect secret** (a random-strength key derived
during pairing). Later reconnections must **prove they hold that secret** (again via an HMAC, over a
fresh random challenge each time) — a mere device ID is not enough, so nobody can impersonate your phone
just by copying an identifier they saw on the network.

### Every channel is gated

Pairing isn't just for the control channel — the **remote-desktop video stream is gated too**. In
production there is exactly **one** thing that decides whether a connection is allowed: the host's
paired-client registry. There is no back door or "loopback exception" in normal operation.

### The optional command port (8338)

RemEx also exposes a TCP port (8338) so *external automation scripts* can send power commands
(shutdown, restart, etc.). No RemEx app uses it — it's for your own scripts. It is **default-deny**:
every command must include the ID of a device that has already paired, or it is rejected and the
connection closed **before any action runs**. If the authentication component somehow isn't loaded, the
port **fails closed** (rejects everything) rather than open.

### Where secrets are stored

- **On the PC:** the certificate's private key and the paired-device list are stored in a
  machine-protected location that **only administrators and the system account can read** (and are
  owner-only-readable on Linux). A normal, non-administrator program on the same PC cannot read your
  private key or your paired-device list.
- **On your phone:** pinned certificate fingerprints and reconnect secrets are **encrypted**
  (AES-256-GCM) and the encryption key is held in the **Android Keystore** (hardware-backed on most
  phones), so they can't be lifted off the device.

### Running with admin rights — carefully

On Windows, RemEx runs **elevated** (with administrator rights) inside your own signed-in session. This
is deliberate: it's what lets your phone control administrator windows, and it's what keeps your
paired-device list protected. RemEx is designed to **never** start without those rights — doing so would
lock it out of its own certificate and break every existing pairing, so it refuses that unsafe path.

### Routines (3.0)

Routines add something the rest of RemEx did not have: **persistent, unattended authority**. A routine
you approve once keeps acting later, when nobody is watching. The design keeps that authority no larger
than what a paired phone could already do by hand, and puts a person back in the loop for anything that
takes the PC away from its user.

- **The phone is the only editor.** Your phone sends each PC only the routines that start on that PC,
  over the existing paired `/ws` channel. The PC **revalidates every routine** (shared validator, its own
  launcher allowlist, the power verbs it actually supports) and stores only the ones that pass. A
  routine it rejects is dropped, including any older version of it, so the PC never runs a definition
  your phone no longer holds. Checks run again at the moment a routine runs, so an app removed from the
  allowlist or a phone that was unpaired stops working immediately.
- **The countdown.** Every routine-issued shutdown, force shutdown, restart, force restart, restart to
  UEFI, sign-out, sleep or hibernate counts down **15 seconds** on the PC with a Cancel button, whoever
  started it: an automatic trigger, a phone, an NFC tag, a shortcut. Lock and display-off are not
  destructive and do not count down. When the phone is connected it mirrors the countdown and can
  cancel it too. Only one countdown runs at a time on a PC.
- **Presence is the only bypass.** The one thing that skips the countdown is **Run now on the PC,
  confirmed in a dialog by the person sitting there**. That confirmation is an in-process flag with **no
  wire representation**: no field of any routine message can set it, so no phone, and no attacker
  holding a phone, can skip the countdown. A confirmation dialog that cannot be shown declines.
- **Tests can only do less.** The phone's "Test" flag makes the PC simulate destructive steps (the
  countdown shows, the verb is never issued) and history marks the run as simulated on both sides.
- **Pause all and blocking.** Pause all (either side) stops every automatic trigger. The PC user can also
  switch off one routine, block a phone's routines entirely, or revoke the phone, which deletes its
  routines, history and queued messages and cancels anything it has running.
- **No new ingress.** Routines use only the authenticated `/ws` channel. The optional command port
  **8338 is unchanged**: it still accepts only its fixed list of power verbs, and nothing about
  routines is handled there. PC-run routines execute inside the RemEx process itself.
- **NFC tags and shortcuts.** A tag carries a per-routine random token that you can rotate, and it only
  runs a routine when your phone is **unlocked**. A home-screen shortcut carries a signature the app
  checks before it runs anything. Another app on the phone cannot start a routine: the only routine
  entry point exposed to the system is the NFC one, and only the system NFC service can call it. A
  cloned tag still cannot power the PC off without the countdown.
- **What leaves the phone, and what doesn't.** Routine data on the phone is encrypted at rest and
  **excluded from Android cloud backup and device-to-device transfer**. The export file leaves out NFC
  tokens, keys, your home-network details, PC identities and MAC addresses.
- **The PC store is locked down.** On Windows the routine files live next to the paired-device list and
  get the same treatment: **LocalSystem and Administrators only, inheritance off**, written atomically.
  RemEx runs elevated, so a routine is a way to make an elevated process act; the lock-down is what stops
  an ordinary program on the same PC from planting one. A file owned by anyone else, or writable by
  anyone else, is **never loaded**: it is set aside, no routine runs, and the PC shows a warning.

### Honest limits (what RemEx does *not* do)

- It secures the *connection*; it can't protect a PC that's already infected with malware, or one an
  attacker can physically sit down at while it's unlocked.
- A routine acts with the authority of the phone that made it. A phone that is paired, unlocked and in
  the wrong hands can set up routines, the same as it can already send commands. Revoke a lost phone
  on the PC; that also deletes its routines.
- Pair in private — anyone who watches you enter the PIN during those two minutes could pair their own
  device.
- Over the open internet, use a VPN. TLS protects the data, but exposing services directly to the
  internet is never a good idea.

---

## Level 3 — Full technical specification

*No simplifications. Exact primitives, parameters, and the files that implement them.*

### Cryptographic primitives

| Purpose | Primitive / parameters |
|---|---|
| Transport | TLS 1.3 or TLS 1.2 (server accepts both), secure WebSockets (`wss://`) on `/ws` and `/ws/desktop`; `SslStream` over TCP on `8338`; HTTPS on the REST surface |
| Host identity | Self-signed **RSA-2048** X.509 certificate, **SHA-256** signature (PKCS#1 v1.5), **5-year** validity, generated on first host start |
| Certificate pin | **SHA-256** over the certificate's `SubjectPublicKeyInfo` (SPKI), base64-encoded |
| Key agreement | **ECDH** on **NIST P-256** (`nistP256`), ephemeral host key pair per pairing session |
| Session-key KDF | **HKDF-SHA256** → 32-byte key. IKM = ECDH raw shared secret; **salt = certificate SPKI SHA-256 hash**; **info = `"remex-pair-v1"`** (domain separation) |
| PIN confirmation | **HMAC-SHA256** keyed by the derived session key (see below); constant-time comparison |
| At-rest (Android) | **Tink AES-256-GCM AEAD**; keyset sealed by an **Android Keystore** master key |
| Integrity (file transfer) | **SHA-256** over the full file, verified end-to-end |
| Wire envelope | `RemexMessage` JSON, `protocolVersion: 2`; mismatched majors are rejected (fail-loud) |

### Certificate and pinning

- Generated via `RSA.Create(2048)` and a `CertificateRequest` signed `SHA256` / PKCS#1, `NotAfter =
  UtcNow + 5 years`. Persisted as `cert.pfx`.
- The SPKI hash is computed as `SHA256.HashData(spki)` and exposed as
  `ICertificateService.GetSpkiSha256Base64()`.
- The client pins that base64 SPKI hash at pairing time and validates it on every subsequent TLS
  handshake. Trust model: **trust-on-first-use, authenticated by the pairing PIN.** The self-signed cert
  provides confidentiality + a stable pinned identity; the PIN authenticates the otherwise-anonymous
  ECDH exchange against a first-connection MITM.
- **Never rotate/regenerate silently.** `CertificateService` carries a brick canary: if an existing
  `cert.pfx` is present but unreadable it logs `Critical` and **refuses to regenerate**, because a new
  cert would invalidate every pinned client.

### Pairing protocol (wire)

ECDH P-256 with an out-of-band 6-digit PIN binding. See [API_CONTRACTS.md §2](API_CONTRACTS.md#2-pairing-protocol-handshake).

1. **Client → Host** `pairing_request`: `ClientPublicKeyBase64`, `clientId`.
2. **Host → Client** `pairing_response`: `HostPublicKeyBase64`, `HostId`, `HostName`,
   `CertificateSpkiHashBase64`, `PinHmacBase64`. Host displays the 6-digit PIN on screen.
   - `PinHmac = HMAC-SHA256(sessionKey, PIN)`.
3. **Client → Host** `pairing_complete`: `ClientPinHmacBase64`, same `clientId`.
   - `ClientPinHmac = HMAC-SHA256(sessionKey, "ack:" + PIN)`.
4. **Host → Client** response with `Success=true` iff the client HMAC verifies (constant-time).

### Session-key derivation (exact)

```
sharedSecret = ECDH_P256(host_ephemeral_priv, client_pub)      // DeriveRawSecretAgreement
sessionKey   = HKDF(
                 hash   = SHA-256,
                 ikm    = sharedSecret,
                 length = 32,
                 salt   = SHA-256(certificate SPKI),            // binds the key to the TLS cert
                 info   = UTF8("remex-pair-v1"))                // domain separation
```

Binding the salt to the certificate SPKI ties the pairing session to the exact TLS identity the client
will pin, so a session key derived against a different certificate cannot validate. (`PairingService.DeriveSessionKey`.)

### PIN confirmation

- Host proof: `HMAC-SHA256(sessionKey, PIN)` (`ComputeHostHmac`).
- Client ack: `HMAC-SHA256(sessionKey, "ack:" + PIN)`; the host recomputes the expected value and
  compares with a **constant-time** equality check (`CryptographicOperations.FixedTimeEquals` on raw
  bytes, after base64-decoding the client value). Mismatches increment a bounded attempt counter and
  are logged.

### Anti-brute-force

- PIN TTL: **120 s** (`PairingService.PairingTimeoutSeconds`), then discarded/regenerated.
- A bounded per-session HMAC-mismatch cap of **5 attempts** (`PairingService.MaxFailedHmacAttempts`)
  rejects the session after too many failures.
- Per-remote-IP throttling on `/ws` is not implemented yet; it is tracked as future work
  (RemEx-tr1s).

### Reconnect authentication (`PairedClientRegistry`)

- Each paired client is bound to a **32-byte reconnect secret** = the ECDH/HKDF session key.
- Reconnection is authenticated by **HMAC over a fresh server nonce** proving possession of that secret
  (`TryGetReconnectSecret` + HMAC verification), *not* by presenting a bare `clientId` (a guessable
  identifier). A later socket rebinds to the paired client only on a valid proof.
- `PairedClientRegistry` is the **single production authentication path** (non-loopback), for both `/ws`
  and `/ws/desktop`.

### 8338 command ingress

- `SslStream` (server-only certificate) over TCP `8338` (`Remex:CommandPort`). Server-only TLS cannot
  identify the caller, so authentication is at the application layer.
- **Default-deny:** every `CommandRequest` must carry a `ClientId` present in `PairedClientRegistry`
  (`PairedClientChannelAuthenticator`); a missing/unknown ID returns `Unauthorized` and the socket
  closes **before** any power action executes. When no authenticator is registered the channel
  **fails closed**. No first-party client uses this port. See
  [API_CONTRACTS.md §4](API_CONTRACTS.md#4-tcp-command-ingress-external-network-listener).

### At-rest protection

- **Host — `cert.pfx` and `paired_clients.json`:**
  - Windows: NTFS ACL granting **FullControl to LocalSystem + Administrators only**, with
    `SetAccessRuleProtection(isProtected: true, preserveInheritance: false)` (inheritance disabled). The
    PFX is written **atomically with restrictive permissions set before any key bytes touch disk**,
    closing the TOCTOU window (PAIR-3 / PAIR-4).
  - Linux/macOS: `0600` owner-only.
  - Location: machine-wide `ProgramData` (Windows) / per-user `~/.local/share/Remex/` (Linux).
- **Android — `PinnedHostStore`:** two Jetpack `DataStore` files (`remex_pinned_hosts`,
  `remex_reconnect_secrets`). Every value is Tink **AES-256-GCM AEAD**-encrypted; the Tink keyset is
  sealed by an Android Keystore key (`android-keystore://remex_pinned_host_key`). The deprecated
  `EncryptedSharedPreferences` / `MasterKey` APIs are not used.

### Process and privilege model

- Windows: single process, **always elevated (high integrity)** — `requestedExecutionLevel =
  requireAdministrator` in `app.manifest` — auto-started by a Task Scheduler **logon task** (`RemEx`,
  `LogonType=InteractiveToken`, `RunLevel=Highest`), so it starts elevated at sign-in with no UAC
  prompt. Runs inside the signed-in **interactive session** (not Session 0), which is what permits
  screen capture and lets `SendInput` bypass UIPI into elevated windows via the user's linked full-admin
  token.
- **Elevation is load-bearing.** The elevated token is what retains FullControl over the ACL-restricted
  `cert.pfx` / `paired_clients.json`; a medium-integrity start would get Administrators as *deny-only*,
  fail to read the cert, and brick every SPKI-pinned pairing. This path is intentionally unshippable.
- **Children don't inherit the admin token.** Apps, links and folders RemEx opens for the user start
  with the desktop shell's normal (medium) token via `IUnelevatedLauncher`; only a target that asks for
  administrator rights, or a PC with no shell running, falls back to the elevated launch (RemEx-pp4cm.2,
  see [ARCHITECTURE-HOST.md](ARCHITECTURE-HOST.md)).
- No Windows Service, no Session 0, no cross-process IPC: the former `RemExLocalIPC` /
  `RemExHostControl` pipes and `LocalIpcServerService` were removed; UI↔host is in-process DI
  (`EmbeddedHostServiceLocator`). See [ARCHITECTURE-HOST.md](ARCHITECTURE-HOST.md).

### Transport / wire hardening

- Android `network_security_config.xml`: `<base-config cleartextTrafficPermitted="false">`, and the
  manifest sets `android:usesCleartextTraffic="false"` — cleartext is impossible, not merely
  discouraged.
- `RemexMessage.protocolVersion` must be `2`; legacy 1.x messages (no version field, old access-key
  model) are rejected. 1.x's access keys and plaintext WebSockets were removed in 2.0.

### Routines (3.0)

Full threat table: `docs/specs/2026-09-26-routines-design.md` §9 (T1-T24). Wire contracts:
[API_CONTRACTS.md §8](API_CONTRACTS.md#8-routines-30).

- **Channel and identity.** All nine routine message types ride the authenticated `/ws` channel (TLS +
  SPKI pin + HMAC-over-nonce reconnect). Phone → host handlers are pairing-gated and scoped to the
  connection's **proven** `clientId`; the owner is never a payload field, and loopback (no proven
  identity) is refused. Host state is keyed by `clientId`, so sync, reports, queued messages, cancel and
  ack are all owner-scoped (T17). A `routine_run_request` names one of the sender's stored routine ids;
  the PC runs its own revalidated copy and never a definition from the wire (T24).
- **Validation.** `routines_sync` is size-capped (64 KB), coalesced to one per 2 s per client, and
  ordered by a monotonic per-PC revision (stale and conflicting revisions are refused). Each routine
  goes through `RoutineValidator` plus host checks (`pc.*` trigger, own `HostIdentity`, verbs in
  `routinePowerVerbs`, `appId` in `launchers.json` and not a network path, idle/session source present).
  Rejected routines are not stored and evict their previous version. The run path revalidates: owner
  still in `PairedClientRegistry`, routine still valid, launcher entry still allowed (T6). No routine
  payload has `required` members; malformed elements become per-routine rejections and the handlers
  never throw into the receive loop (T18).
- **Destructive-verb countdown (D1).** `SHUTDOWN`, `FORCESHUTDOWN`, `RESTART`, `FORCERESTART`,
  `RESTARTTOUEFI`, `SIGNOUT`, `SLEEP`, `HIBERNATE` from any routine source wait 15 s behind a cancellable
  countdown window (topmost, independent of `MainWindow`), mirrored to a connected owner as a
  `routine_notify{kind:countdown}` and cancellable with `routine_cancel`. One countdown at a time per
  host (`conflict_countdown_active`). `LOCK` and `MONITOROFF` do not count down.
- **Presence (D3, T21).** `presenceConfirmed` is set only by PC Run now after the Routines page's
  confirmation dialog (`RoutinesViewModel.OnConfirmationRequested`) returns a confirmation; it is not a
  field of any payload. Every request from the phone runs with it
  false. A dialog that cannot show fails closed.
- **Test and dry run (T22, T23).** `testRun` can only suppress a destructive verb (outcome
  `simulated`). `--routines-dry-run` is a command-line switch only (no setting, file or wire field) and
  shows a persistent banner.
- **Rate limits and loops (T9).** 4 concurrent host runs; 30 runs per owner per hour; 60 s minimum
  between automatic runs of one routine; single-flight per routine; 60 step requests per client per
  minute; sensor sustain + hysteresis + 300 s cooldown; session triggers settle 3 s and cap at 20 per
  routine per hour; runs caused by another run are suppressed.
- **No new ingress (T15).** No routine type is handled by `RemexNetworkListener`; `CommandVerbs.ScriptIngress`
  is unchanged; host routines execute in-process. `RoutineIngressIsolationTests` pins this.
- **Host store (T14).** `routines.json`, `routine_runs.json` and `routine_notify_queue.json` in the host
  state directory. Windows: protected DACL, FullControl for LocalSystem + Administrators only, atomic
  writes. On load, a file whose owner is not LocalSystem/Administrators, or that grants write to anyone
  else, is **not trusted**; unreadable or untrusted content is moved aside as
  `<name>.unreadable-<utc>`, the store starts empty, and a PC warning is shown. Partial content is never
  executed. Linux: `0600`, the same trust boundary as `paired_clients.json`.
- **Android.** Routine DataStores and the routine Tink keyset are Tink AES-256-GCM sealed by an Android
  Keystore key and excluded in both `backup_rules.xml` and `data_extraction_rules.xml` (cloud backup and
  device transfer) (T12). NFC: per-routine rotatable random token, unlocked device required, 10 s
  debounce; the only exported routine component is the NFC activity, guarded by
  `android.permission.DISPATCH_NFC_MESSAGE` (T1-T3). Shortcuts carry an HMAC signature verified before
  anything runs. The `.remexroutines` export whitelist excludes tokens, keys, homes, `hostIdentity` and
  MAC (T13). No location permission and no exact alarms (T16).
- **Logs (T11).** No NFC tokens, keys, fingerprint values or notify bodies; client ids through
  `LogRedaction.RedactClientId`; MACs partly masked; routine names truncated.

### Threat model

**In scope (mitigated):**

- Passive eavesdropping / traffic capture on the local network → TLS 1.3.
- First-connection MITM / impostor host → PIN-authenticated ECDH + SPKI pinning, fail-closed on cert
  change.
- Unauthenticated control, including of the video stream and the 8338 power port → paired-registry gate,
  default-deny, fail-closed.
- PIN brute force → 120 s TTL + escalating per-IP throttle + bounded attempts.
- Device-ID spoofing on reconnect → HMAC-over-nonce proof of the reconnect secret.
- Local secret theft by a non-privileged process / off-device extraction → ACL/`0600` at rest on the
  host; Keystore-sealed AES-256-GCM on Android.
- Routines (3.0): a hostile or buggy paired phone, a planted routine file, a cloned NFC tag, or a
  remote attempt to skip the countdown → host revalidation, owner scoping, the 15 s countdown with no
  wire bypass, the store ACL trust check, per-routine NFC tokens (see *Routines (3.0)* above).

**Out of scope (your responsibility / not claimed):**

- A host OS already compromised by malware, or physical access to an unlocked PC.
- Shoulder-surfing the PIN during the ~2-minute pairing window.
- Direct exposure of RemEx ports to the public internet without a VPN (use Tailscale / WireGuard).
- Endpoint compromise of the Android device itself.

### Where each control lives (source map)

| Control | Implementation |
|---|---|
| Certificate + SPKI + ACL + brick canary | `remex.agent/Services/Security/CertificateService.cs` |
| Pairing (ECDH, HKDF, PIN HMAC, TTL) | `remex.agent/Services/Security/PairingService.cs`, `Handlers/PairingHandler.cs` |
| Paired registry + reconnect secrets + ACL | `remex.agent/Services/Security/PairedClientRegistry.cs` |
| 8338 authentication | `remex.agent/Services/Network/PairedClientChannelAuthenticator.cs` |
| Android at-rest storage | `remex.android/app/src/main/java/com/clindsay94/remex/security/PinnedHostStore.kt` |
| Cleartext ban | `remex.android/app/src/main/res/xml/network_security_config.xml`, `AndroidManifest.xml` |
| Elevation manifest / autostart | `remex.agent/app.manifest`, `scripts/autostart-remex.ps1` |
| Routine sync, revalidation, store ACL | `remex.agent/Services/Routines/RoutineSyncHandler.cs`, `RoutineHostValidator.cs`, `RoutineStateFiles.cs` |
| Routine countdown and presence | `remex.agent/Services/Routines/RoutineCountdownCoordinator.cs`, `RoutineHostRunner.cs`, `RoutineStepRequestHandler.cs` |
| Routine backup exclusions | `remex.android/app/src/main/res/xml/backup_rules.xml`, `data_extraction_rules.xml` |

---

<div align="center">
<sub>Found a security issue? Please follow the <a href="SECURITY.md">responsible-disclosure policy</a> — do not open a public issue.</sub>
</div>
