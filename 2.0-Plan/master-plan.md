# RemEx 2.0 — Multi-Agent Execution Plan ("Cosmic Raven")

> **Plan author:** Opus 4.7. Designed so each track below can be lifted verbatim into a separate context (Claude Sonnet via Copilot CLI, Gemini CLI, Claude Haiku in Claude Code, or a local model). Each track is self-contained with pre-conditions, exact file edits, verification commands, and post-conditions.

## Agent Responsibilities

1. **Update the `2.0-Tracker` document with the current phase and status of your tasks.**
2. **Do not deviate from the plan or your assigned tasks. Follow instructions exactly as written.**
3. **Do not modify tasks assigned to other agents.**
4. **Do not change the column headers or table structure.**
5. **Do not create new rows in the table. If you need to add a new task, add it to the end of the table as a new row, following the same format.**
6. **Do not delete rows from the table.**
7. **Do not reorder rows in the table.**
8. **Do not edit files that are not assigned to you.**
9. **Do not edit files that are not in your current phase.**
10. **Do not edit files that are in a later phase.**
11. **Do not edit files that are in an earlier phase.**
12. **Do not edit files that are in a locked phase.**
13. **Do not edit files that are in a completed phase.**
14. **If you encounter a conflict or a block, document it in the `2.0-Tracker` document and notify the user. Do not move forward without addressing the conflict or block, unless told explicitly by the user to do so.**
15. **Assignments in the Matrix are subject to change. If user asks you to complete a task that is not in your assigned phase, notify the user that the task is not in your assigned phase. If the user explicitly tells you to complete the task, you may do so.**

---

## 1. Context

**Why this exists.** RemEx is currently 1.15.0 (desktop) / 1.14.0 (Android), production-ready as a *trusted-LAN* tool with plaintext access-key auth over WebSocket and TCP. The user has just received Google Play production access and wants the next release to land as **2.0** — a major version that earns its bump by changing the security model and adding flagship capability, not by accumulating polish. This plan is absolute, do not get creative, do not add new features, do not "fix" anything that is not explicitly listed in this plan, and do not go outside the scope of this plan unless explicitly told to do so by the user. If you are not sure about something, ask the user.

**Goal.** Ship RemEx 2.0 with:

1. End-to-end encrypted transport (WSS/TLS 1.3) replacing plaintext WebSocket and TCP.
2. Cryptographic device pairing replacing plaintext access-key matching.
3. Production-grade Play Store readiness (R8, AAB, cleartext disabled, network security config, Crashlytics NDK).
4. A flagship user-visible feature (remote file transfer) that makes the version bump feel earned to users.
5. Resolution of every Critical and High severity issue from `review-report.md`.

**Explicitly out of scope for 2.0** (see §13 for phased ship plan):

- iOS app
- Cloud relay / WAN convenience layer (TLS unblocks self-hosted WAN; relay is 2.x)
- UDP secondary telemetry channel (Gemini's PRD §5.1 — analyzed and rejected; current 1s WebSocket telemetry is not the bottleneck, adding a second protocol increases attack surface for marginal gain)
- "Mute Audio" Quick Settings tile (no host-side audio-mute command exists; ship "Lock PC" tile only)
- Custom remote-desktop H.264/HEVC codec (JPEG works; codec swap is its own release)

**Success criteria.**

- All 4 Critical and 7 High items in `review-report.md` resolved.
- 1.x clients fail loudly (not silently) against 2.0 hosts via the new protocol-version field.
- Release-signed AAB passes Play Store internal-testing review.
- All existing automated tests pass (`dotnet test Remex.sln`, `./gradlew test`).
- New TLS + pairing path has unit + integration test coverage.

---

## 2. Plan architecture

The plan is **phased**. Phase 0 and Phase 1 are sequential and must complete before Phase 2 fans out, because every feature track depends on the new transport and message envelope. Phase 2 tracks are parallel-safe (disjoint files except where called out).

```
Phase 0  ──►  Phase 1  ──►  ┌──────────────────────────────────┐
(seq)        (seq)          │ Phase 2 (parallel, multi-agent)  │
                            └──────────────────────────────────┘
                                          │
                                          ▼
                                     Phase 3 (polish, sequential)
                                          │
                                          ▼
                                       Release 2.0.0
```

- **Phase 0:** Foundation. Version bump, new message types, protocol version field, service interfaces. *Owner: 1 strong model, single session.*
- **Phase 1:** Security backbone. TLS, pairing protocol, cert pinning. *Owner: 1 strong model, single session, must complete before Phase 2 starts.*
- **Phase 2:** Feature + release-engineering tracks. Multiple parallel agents, model fit varies per track.
- **Phase 3:** Polish. Localization regen, docs, installer, final test pass. Sequential.

---

## 3. File-ownership matrix (chokepoints)

These files are touched by multiple tracks. **Only edit them in the assigned phase** to avoid merge collisions.

| File | Phase 0 | Phase 1 | Phase 2 (which tracks) |
|---|---|---|---|
| `Remex.Core/Messages/RemexMessage.cs` | ✅ adds protocol version + new payload fields for ALL features | — | none (all changes folded into Phase 0) |
| `Remex.Core/Messages/MessageTypes.cs` | ✅ adds all new constants for ALL features | — | none |
| `Remex.Host/HostBootstrapper.cs` | — | ✅ TLS, pairing, cert | Track 2C (file-transfer endpoint), Track 2I (Crashlytics N/A) |
| `Remex.Host/Handlers/PingPongHandler.cs` | — | ✅ pairing handshake message dispatch | Track 2C (file transfer dispatch) |
| `RemEx.Android/app/src/main/AndroidManifest.xml` | — | — | Track 2D (cleartext, network security), Track 2H (battery opt) |
| `RemEx.Android/app/build.gradle.kts` | — | — | Track 2D (R8/AAB/ABI), Track 2I (Crashlytics) |
| `RemEx.Android/app/proguard-rules.pro` | — | — | Track 2D (keep rules) |
| `RemEx.Android/app/src/main/java/com/clindsay94/remex/RemexClientManager.kt` | — | ✅ pairing, cert pin storage | Track 2G (JNI hardening — wraps existing methods, no signature change) |
| `RemEx.Android/app/src/main/java/com/clindsay94/remex/RemexCoreClient.kt` | ✅ adds new native method declarations only | ✅ pairing native method bodies | Track 2G (try/catch wrappers, no signature change) |
| `Directory.Build.props` | ✅ version → 2.0.0 | — | — |
| `RemEx.Android/app/version.properties` | ✅ versionName=2.0.0, versionCode=15 | — | — |

**Rule for parallel agents:** if your track touches a file in a column other than your phase, **stop and escalate** — the dependency graph was wrong, not your edit.

---

## 4. Agent assignment guide

Pick the right model per track. Lighter models do mechanical work with verification gates; stronger models do anything that requires cross-file reasoning, protocol design, or platform-specific gotchas.

| Track | Suggested model | Why |
|---|---|---|
| **Phase 0** Foundation | Claude Sonnet 4.6 / Opus / GPT-5 | Cross-cutting; sets contracts everything else depends on |
| **Phase 1** TLS + pairing | Claude Sonnet 4.6 / Opus | Crypto-adjacent; mistakes silently break security |
| **2A** File transfer | Claude Sonnet 4.6 / Gemini 2.5 Pro | New service, chunking, hashing, three-platform impl |
| **2B** Critical bug fixes (review-report) | Claude Sonnet 4.6 | Threading + event-leak bugs need careful reasoning |
| **2C** Material3 stable + targetSdk=35 | Gemini 2.5 Pro / Sonnet | API surface changes (`MaterialShapes`, `MotionScheme.expressive()`) need targeted edits |
| **2D** Release engineering (cleartext, R8, AAB, network security) | **Claude Haiku / Gemini Flash / local** | Mechanical edits to known files; verification is exact `grep`/`apktool` checks |
| **2E** JNI exception hardening | Claude Sonnet 4.6 | Wrap every JNI call site in try/catch; medium reasoning |
| **2F** Firebase Crashlytics NDK | Claude Sonnet 4.6 | Build config + Gradle plugin + symbol upload — mid complexity |
| **2G** Battery optimization onboarding | **Haiku / Flash / local** | Single intent + permission rationale screen |
| **2H** Two-stage haptic feedback | **Haiku / Flash / local** | Adds two states to existing `HapticModifier.kt` |
| **2I** Quick Settings tile (Lock PC) | **Haiku / Flash / local** | Single `TileService` subclass + manifest entry |
| **Phase 3** Localization, docs, installer, tests | **Haiku / Flash / local** for regen + docs; Sonnet for new tests | Mechanical for the first three; tests need test-engineer judgment |

---

## 5. Universal track template

**Every track below uses this exact structure. Lighter models must follow it strictly — do not improvise or skip sections.**

```
ID: <unique track id>
GOAL: <one-sentence outcome>
PRE-CONDITIONS: <what must be true before starting; if false, STOP>
FILES TO MODIFY: <path:line-range, with exact replacement>
NEW FILES: <path, with full content>
DO NOT TOUCH: <files this track must not edit>
VERIFICATION (run all, quote output):
  1. <command> — expect substring "<exact text>"
  2. <command> — expect exit code 0
POST-CONDITIONS: <invariants subsequent tracks rely on>
```

**Verification rule:** A track is "done" only when every verification command above runs and prints the expected substring/exit code. Do not declare done on faith.

---

## 6. Locked design decisions (do not re-litigate)

These were chosen by the planner; they are not open questions. Lighter agents should not suggest alternatives.

| Decision | Choice | Rationale |
|---|---|---|
| Transport encryption | **TLS 1.3 via Kestrel HTTPS endpoint** (`wss://` for WS, `SslStream` wrapping `TcpListener` for the 8338 command port) | Built-in to ASP.NET; AOT-friendly; no extra NuGet |
| Cert generation | **Self-signed RSA 2048, generated on first host start, stored at `%ProgramData%\RemEx\cert.pfx` (Windows) and `/var/lib/remex/cert.pfx` (Linux)** with file ACL = service account only | Avoid CA dependency; PFX format works with `X509Certificate2` directly |
| Cert lifetime | **5 years**, regenerated on demand via `--regen-cert` CLI flag | Long enough that it never naturally expires; user can force-regen if compromised |
| Pairing key exchange | **ECDH over X25519** (`System.Security.Cryptography.ECDiffieHellman` with `ECCurve.NamedCurves.X25519` is unavailable in .NET; use `Curve25519` via the Microsoft `System.Formats.Asn1` + `Cryptography.OpenSsl` path is also unavailable AOT — **use NSec.Cryptography 22.4.0 NuGet** which is AOT-compatible) | NSec wraps libsodium; X25519 is the standard modern curve; AOT-compatible |
| Pairing PIN | **6-digit decimal**, displayed on host UI / printed to host console, valid for 120 seconds | Out-of-band channel binds the ECDH; matches Bluetooth Simple Secure Pairing model |
| Cert pinning | **SHA-256 of SubjectPublicKeyInfo (SPKI)**, base64-encoded, stored in client | SPKI pin survives cert rotation as long as keypair is reused |
| Client cert storage (.NET desktop client) | `Path.Combine(LocalApplicationData, "Remex", "pinned_hosts.json")` — JSON dict of `hostId -> spkiHash` | Same pattern as existing `client-settings.json` |
| Client cert storage (Android) | **`EncryptedSharedPreferences`** via `androidx.security:security-crypto:1.1.0-alpha06` | DataStore preferences encryption is platform-version-dependent; ESP enforces it |
| Audio codec (Phase 2.2 / 2.x release) | **Opus 48 kHz mono 64 kbps** via Concentus 2.0.0 (managed C# Opus port, AOT-compatible) | Low latency; managed code keeps NativeAOT clean |
| File transfer chunk size | **64 KB** | Balances WebSocket frame overhead vs progress granularity |
| File transfer integrity | **SHA-256 of full file**, sent in metadata, verified on receiver | Standard; collision-resistant; built-in `System.Security.Cryptography.SHA256` |
| File transfer resumability | **Not in 2.0** — out-of-scope, can add in 2.x. 2.0 ships single-shot transfers with cancel support. | Resumability needs per-chunk hashing and a state machine; defer |
| Clipboard watch (Windows host) | **`AddClipboardFormatListener` (event-driven)** via P/Invoke on a dedicated message-only window | Push, not poll |
| Clipboard watch (Linux host) | **Poll @ 500 ms** via `xclip -o` (X11) or `wl-paste` (Wayland) | No reliable cross-DE event hook |
| Multi-monitor protocol (Phase 2.x) | `DesktopMeta.Monitors[]` array, indexed by ordinal; client requests monitor by index in `DesktopConfig.MonitorIndex` (default 0 = primary) | Backwards-compatible if `MonitorIndex` defaults to 0 and `Monitors` is optional |
| Protocol version field | New `RemexMessage.ProtocolVersion: int` field, **defaults to 2** in 2.0; host rejects messages with `ProtocolVersion < 2` post-handshake | Cheap insurance; 1.x clients fail loudly with a clear error |

---

## 7. Phased shipping plan (ship dates feel real)

**The advisor strongly recommends not bundling everything into 2.0.0.** A 20-track release with parallel lighter-model agents will not converge cleanly. Phased approach:

| Release | Tracks included | User-visible headline |
|---|---|---|
| **2.0.0** (this plan, all of Phase 0/1 + Phase 2A,2B,2C,2D,2E,2F,2G,2H,2I) | Security backbone + file transfer + Play Store readiness + critical fixes + JNI hardening + Crashlytics + battery onboarding + two-stage haptics + Lock-PC tile | "Encrypted, secure, file-transfer-capable" |
| **2.1.0** (~6-8 weeks after 2.0) | Clipboard sync + multi-host management + sensor threshold alerts + virtual keyboard overlay + landscape remote desktop + WoL auto-fallback | "Productivity update" |
| **2.2.0** (~3 months after 2.1) | Audio streaming + multi-monitor support | "Full remote desktop" |

**The plan below specifies all of 2.0.0 in detail, then provides skeleton specs for 2.1.0 and 2.2.0 tracks at the end so you can hand them off later without re-planning.**

---

# PHASE 0 — FOUNDATION (sequential, strong model)

---

## TRACK 0A — Version bump to 2.0.0

**ID:** `0A-version-bump`
**GOAL:** Bump every version reference to 2.0.0 / versionCode 15, and add the version source-of-truth tooling assertion.
**PRE-CONDITIONS:** Working tree clean on `2.0` (or branch off `main`).
**FILES TO MODIFY:**

1. `/home/connorl/RemEx/Directory.Build.props` line 3:
   - Replace `<Version>1.15.0</Version>` with `<Version>2.0.0</Version>`

2. `/home/connorl/RemEx/RemEx.Android/app/version.properties` (full replace, 2 lines):

   ```properties
   versionCode=15
   versionName=2.0.0
   ```

3. `/home/connorl/RemEx/README.md`:
   - Replace every literal `1.13.0`, `1.14.0`, `1.15.0` with `2.0.0`
   - Replace `1.11.0` in installer download URL with `2.0.0`

4. `/home/connorl/RemEx/SECURITY.md` — supported versions table:
   - Add row: `| 2.0.x | ✓ |` at top
   - Mark `1.13.x` as `✓ until 2026-10-01`
   - Mark `1.10.x` and below as `✗`

5. `/home/connorl/RemEx/CHANGELOG.md`:
   - Insert at top, after the `## [Unreleased]` line if present, otherwise after the file header:

   ```
   ## [2.0.0] — TBD

   ### Added
   - End-to-end encrypted transport (TLS 1.3 / WSS) for all client-host communication
   - Cryptographic device pairing replacing plaintext access keys (ECDH X25519 + 6-digit PIN)
   - SHA-256 SPKI certificate pinning on client
   - Remote file transfer (browse, upload, download, cancel)
   - Quick Settings tile (Lock PC) on Android
   - Two-stage haptic feedback on Android (sent vs acknowledged)
   - Battery optimization onboarding on Android
   - Firebase Crashlytics NDK integration

   ### Changed
   - Protocol version field added to `RemexMessage`; 1.x clients fail loudly
   - Material3 dependency moved from alpha to stable
   - Android `targetSdk` lowered from 36 (preview) to 35 (stable)

   ### Fixed
   - Settings view freeze on Linux (UI-thread marshalling)
   - SavedStatus continuation off UI thread
   - DiscoverHostsAsync HostAddress assignment off UI thread
   - async-void crash hazard in `OnShowSetAlertRequested`
   - Sensor `AlertTriggered` event subscription leak on reconnect
   - Duplicate XAML style block in `CanvasView.axaml`
   - `RefreshSensors` running on every Settings open/close
   - Hardcoded "Sort by:" string in `TaskManagerScreen`
   - Snapshot clipboard copies file path; redesigned as "Copy Path" with accurate label

   ### Security
   - Plaintext access keys are no longer transmitted on the wire
   - DataStore exclusion from Auto Backup verified via `data_extraction_rules.xml`
   - Network security config disables cleartext traffic on Android
   ```

**NEW FILES:** none.
**DO NOT TOUCH:** any other file.
**VERIFICATION:**

1. `grep -r "1\.15\.0\|1\.14\.0\|1\.13\.0" /home/connorl/RemEx/Directory.Build.props /home/connorl/RemEx/RemEx.Android/app/version.properties /home/connorl/RemEx/README.md` — expect no output (exit 1).
2. `grep "^<Version>" /home/connorl/RemEx/Directory.Build.props` — expect `<Version>2.0.0</Version>`.
3. `grep "^versionName" /home/connorl/RemEx/RemEx.Android/app/version.properties` — expect `versionName=2.0.0`.

**POST-CONDITIONS:** Build files report 2.0.0; CHANGELOG has a 2.0.0 entry; SECURITY.md lists 2.0.x as supported.

---

## TRACK 0B — Add new message types and protocol version

**ID:** `0B-message-types`
**GOAL:** Pre-declare every new message type and the protocol version field so subsequent tracks can serialize/deserialize them without touching shared message files.
**PRE-CONDITIONS:** Track 0A complete.

**FILES TO MODIFY:**

1. `/home/connorl/RemEx/Remex.Core/Messages/RemexMessage.cs`:

   - **Add a new field to the `RemexMessage` record**, immediately after the `Type` property:

     ```csharp
     [JsonPropertyName("protocolVersion")]
     public int ProtocolVersion { get; init; } = 2;
     ```

   - **Add new payload properties** (insert before the closing brace of the record, alongside the existing optional payload properties):

     ```csharp
     [JsonPropertyName("pairingRequest")]
     public PairingRequest? PairingRequest { get; init; }

     [JsonPropertyName("pairingResponse")]
     public PairingResponse? PairingResponse { get; init; }

     [JsonPropertyName("pairingComplete")]
     public PairingComplete? PairingComplete { get; init; }

     [JsonPropertyName("fileTransferStart")]
     public FileTransferStart? FileTransferStart { get; init; }

     [JsonPropertyName("fileTransferChunk")]
     public FileTransferChunk? FileTransferChunk { get; init; }

     [JsonPropertyName("fileTransferEnd")]
     public FileTransferEnd? FileTransferEnd { get; init; }

     [JsonPropertyName("fileTransferCancel")]
     public FileTransferCancel? FileTransferCancel { get; init; }

     [JsonPropertyName("fileTransferProgress")]
     public FileTransferProgress? FileTransferProgress { get; init; }

     [JsonPropertyName("fileBrowseRequest")]
     public FileBrowseRequest? FileBrowseRequest { get; init; }

     [JsonPropertyName("fileBrowseResponse")]
     public FileBrowseResponse? FileBrowseResponse { get; init; }
     ```

   - **In the `MessageTypes` static class** (lines 94–117), add the following constants at the end of the existing list:

     ```csharp
     public const string PairingRequest = "pairing_request";
     public const string PairingResponse = "pairing_response";
     public const string PairingComplete = "pairing_complete";
     public const string PairingError = "pairing_error";
     public const string FileTransferStart = "file_transfer_start";
     public const string FileTransferChunk = "file_transfer_chunk";
     public const string FileTransferEnd = "file_transfer_end";
     public const string FileTransferCancel = "file_transfer_cancel";
     public const string FileTransferProgress = "file_transfer_progress";
     public const string FileBrowseRequest = "file_browse_request";
     public const string FileBrowseResponse = "file_browse_response";
     ```

**NEW FILES:**

1. `/home/connorl/RemEx/Remex.Core/Models/PairingMessages.cs` (full content):

   ```csharp
   using System.Text.Json.Serialization;

   namespace Remex.Core.Models;

   public sealed record PairingRequest
   {
       [JsonPropertyName("clientPublicKey")] public required string ClientPublicKeyBase64 { get; init; }
       [JsonPropertyName("clientName")] public required string ClientName { get; init; }
       [JsonPropertyName("clientVersion")] public required string ClientVersion { get; init; }
   }

   public sealed record PairingResponse
   {
       [JsonPropertyName("hostPublicKey")] public required string HostPublicKeyBase64 { get; init; }
       [JsonPropertyName("hostId")] public required string HostId { get; init; }
       [JsonPropertyName("hostName")] public required string HostName { get; init; }
       [JsonPropertyName("certificateSpkiHash")] public required string CertificateSpkiHashBase64 { get; init; }
       [JsonPropertyName("pinHmac")] public required string PinHmacBase64 { get; init; }
   }

   public sealed record PairingComplete
   {
       [JsonPropertyName("clientPinHmac")] public required string ClientPinHmacBase64 { get; init; }
   }
   ```

2. `/home/connorl/RemEx/Remex.Core/Models/FileTransferMessages.cs` (full content):

   ```csharp
   using System.Text.Json.Serialization;

   namespace Remex.Core.Models;

   public sealed record FileTransferStart
   {
       [JsonPropertyName("transferId")] public required string TransferId { get; init; }
       [JsonPropertyName("direction")] public required string Direction { get; init; } // "upload" | "download"
       [JsonPropertyName("remotePath")] public required string RemotePath { get; init; }
       [JsonPropertyName("fileName")] public required string FileName { get; init; }
       [JsonPropertyName("totalBytes")] public required long TotalBytes { get; init; }
       [JsonPropertyName("sha256")] public required string Sha256Base64 { get; init; }
   }

   public sealed record FileTransferChunk
   {
       [JsonPropertyName("transferId")] public required string TransferId { get; init; }
       [JsonPropertyName("offset")] public required long Offset { get; init; }
       [JsonPropertyName("dataBase64")] public required string DataBase64 { get; init; }
   }

   public sealed record FileTransferEnd
   {
       [JsonPropertyName("transferId")] public required string TransferId { get; init; }
       [JsonPropertyName("success")] public required bool Success { get; init; }
       [JsonPropertyName("errorMessage")] public string? ErrorMessage { get; init; }
   }

   public sealed record FileTransferCancel
   {
       [JsonPropertyName("transferId")] public required string TransferId { get; init; }
   }

   public sealed record FileTransferProgress
   {
       [JsonPropertyName("transferId")] public required string TransferId { get; init; }
       [JsonPropertyName("bytesTransferred")] public required long BytesTransferred { get; init; }
       [JsonPropertyName("totalBytes")] public required long TotalBytes { get; init; }
   }

   public sealed record FileBrowseRequest
   {
       [JsonPropertyName("requestId")] public required string RequestId { get; init; }
       [JsonPropertyName("path")] public required string Path { get; init; }
   }

   public sealed record FileBrowseResponse
   {
       [JsonPropertyName("requestId")] public required string RequestId { get; init; }
       [JsonPropertyName("path")] public required string Path { get; init; }
       [JsonPropertyName("entries")] public required FileEntry[] Entries { get; init; }
       [JsonPropertyName("errorMessage")] public string? ErrorMessage { get; init; }
   }

   public sealed record FileEntry
   {
       [JsonPropertyName("name")] public required string Name { get; init; }
       [JsonPropertyName("isDirectory")] public required bool IsDirectory { get; init; }
       [JsonPropertyName("sizeBytes")] public long SizeBytes { get; init; }
       [JsonPropertyName("modifiedUnixMs")] public long ModifiedUnixMs { get; init; }
   }
   ```

3. `/home/connorl/RemEx/Remex.Core/Serialization/RemexJsonSerializerContext.cs` — **read the file first, then append new `[JsonSerializable(typeof(...))]` attributes** for each of these types:
   - `PairingRequest`, `PairingResponse`, `PairingComplete`
   - `FileTransferStart`, `FileTransferChunk`, `FileTransferEnd`, `FileTransferCancel`, `FileTransferProgress`
   - `FileBrowseRequest`, `FileBrowseResponse`, `FileEntry`

**DO NOT TOUCH:** `MessageSerializer.cs`, any handler file, any client file. This track is **declarative only.**

**VERIFICATION:**

1. `cd /home/connorl/RemEx && dotnet build Remex.Core/Remex.Core.csproj -c Release` — expect exit code 0.
2. `grep -c "public const string Pairing" /home/connorl/RemEx/Remex.Core/Messages/RemexMessage.cs` — expect `4` (the four pairing constants).
3. `grep -c "public const string FileTransfer\|public const string FileBrowse" /home/connorl/RemEx/Remex.Core/Messages/RemexMessage.cs` — expect `7`.
4. `grep "ProtocolVersion" /home/connorl/RemEx/Remex.Core/Messages/RemexMessage.cs` — expect at least one match.

**POST-CONDITIONS:** All new message types compile. Subsequent tracks can `using Remex.Core.Models;` and reference these types without re-defining anything.

---

## TRACK 0C — Stub service interfaces

**ID:** `0C-interfaces`
**GOAL:** Pre-declare the C# interfaces that Phase 1 and Phase 2 implementations will fulfill, so multiple tracks can implement against a stable contract in parallel.
**PRE-CONDITIONS:** Tracks 0A and 0B complete.

**NEW FILES:**

1. `/home/connorl/RemEx/Remex.Core/Services/Security/IPairingService.cs`:

   ```csharp
   using System.Threading;
   using System.Threading.Tasks;

   namespace Remex.Core.Services.Security;

   public interface IPairingService
   {
       Task<PairingState> StartPairingAsync(CancellationToken ct);
       string GetActivePin();
       bool IsPairingActive { get; }
       Task<bool> VerifyClientHmacAsync(string clientHmacBase64, CancellationToken ct);
       void CancelPairing();
   }

   public sealed record PairingState(string HostPublicKeyBase64, string Pin, long ExpiresAtUnixMs);
   ```

2. `/home/connorl/RemEx/Remex.Core/Services/Security/ICertificateService.cs`:

   ```csharp
   using System.Security.Cryptography.X509Certificates;
   using System.Threading;
   using System.Threading.Tasks;

   namespace Remex.Core.Services.Security;

   public interface ICertificateService
   {
       Task<X509Certificate2> GetOrCreateCertificateAsync(CancellationToken ct);
       string GetSpkiSha256Base64();
       Task RegenerateAsync(CancellationToken ct);
   }
   ```

3. `/home/connorl/RemEx/Remex.Core/Services/FileTransfer/IFileTransferService.cs`:

   ```csharp
   using System.Collections.Generic;
   using System.IO;
   using System.Threading;
   using System.Threading.Tasks;
   using Remex.Core.Models;

   namespace Remex.Core.Services.FileTransfer;

   public interface IFileTransferService
   {
       Task<IReadOnlyList<FileEntry>> BrowseAsync(string path, CancellationToken ct);
       Task<Stream> OpenForReadAsync(string remotePath, CancellationToken ct);
       Task<Stream> OpenForWriteAsync(string remotePath, long expectedBytes, CancellationToken ct);
   }
   ```

**FILES TO MODIFY:** none.
**DO NOT TOUCH:** any handler, bootstrapper, or client file.

**VERIFICATION:**

1. `cd /home/connorl/RemEx && dotnet build Remex.Core/Remex.Core.csproj -c Release` — expect exit code 0.
2. `ls /home/connorl/RemEx/Remex.Core/Services/Security/IPairingService.cs /home/connorl/RemEx/Remex.Core/Services/Security/ICertificateService.cs /home/connorl/RemEx/Remex.Core/Services/FileTransfer/IFileTransferService.cs` — expect all three to print.

**POST-CONDITIONS:** Three new interfaces exist and compile. Phase 1 (TLS+pairing) implements `IPairingService` and `ICertificateService`; Phase 2A (file transfer) implements `IFileTransferService`.

---

# PHASE 1 — SECURITY BACKBONE (sequential, strong model)

---

## TRACK 1A — TLS 1.3 transport on the host

**ID:** `1A-host-tls`
**GOAL:** Wrap every host endpoint (WebSocket main, WebSocket desktop, command TCP) in TLS 1.3 using a self-signed cert generated and persisted on first start.
**PRE-CONDITIONS:** Phase 0 complete. `dotnet build Remex.sln -c Release` passes.

**NEW FILES:**

1. `/home/connorl/RemEx/Remex.Host/Services/Security/CertificateService.cs` — implements `ICertificateService`:
   - Cert path resolution: Windows = `Path.Combine(Environment.GetFolderPath(SpecialFolder.CommonApplicationData), "RemEx", "cert.pfx")`; Linux = `/var/lib/remex/cert.pfx` (fallback to `Path.Combine(Environment.GetFolderPath(SpecialFolder.LocalApplicationData), "Remex", "cert.pfx")` if `/var/lib/remex` not writable).
   - Cert generation: `using var rsa = RSA.Create(2048); var req = new CertificateRequest("CN=RemExHost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1); req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, critical: true)); req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, critical: false)); var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddYears(5)); var pfx = cert.Export(X509ContentType.Pfx);`
   - Write PFX with file mode 0600 on Linux (`File.SetUnixFileMode`), Windows ACL = service account RW only.
   - SPKI hash: extract `cert.GetPublicKey()` then SHA-256 the raw `cert.PublicKey.EncodedKeyValue.RawData` after wrapping in proper SPKI ASN.1. **Use `cert.GetPublicKeyString()` is wrong — use `cert.PublicKey.ExportSubjectPublicKeyInfo()` (this is `byte[]` in .NET 7+) then SHA-256 then base64.**

2. `/home/connorl/RemEx/Remex.Host/Services/Security/PairingService.cs` — implements `IPairingService`. Skeleton only here, full impl in Track 1B.

**FILES TO MODIFY:**

1. `/home/connorl/RemEx/Remex.Host/Remex.Host.csproj` — add NuGet package reference (after the existing `Microsoft.Win32.Registry` line):

   ```xml
   <PackageReference Include="NSec.Cryptography" Version="22.4.0" />
   ```

2. `/home/connorl/RemEx/Remex.Host/HostBootstrapper.cs`:

   **Replace lines 100-101** (the `UseUrls` call):
   - Old: `builder.WebHost.UseUrls($"http://0.0.0.0:{actualPort}");`
   - New:

     ```csharp
     var certService = new CertificateService(builder.Services.BuildServiceProvider().GetRequiredService<ILogger<CertificateService>>());
     var cert = await certService.GetOrCreateCertificateAsync(default);
     builder.WebHost.ConfigureKestrel(options =>
     {
         options.ListenAnyIP(actualPort, listenOptions =>
         {
             listenOptions.UseHttps(cert, httpsOptions =>
             {
                 httpsOptions.SslProtocols = SslProtocols.Tls13 | SslProtocols.Tls12;
             });
         });
     });
     ```

   - Add `using System.Security.Authentication;` to the top.

   **Register the cert and pairing services with DI** (insert in the service registration block, around line 47):

   ```csharp
   builder.Services.AddSingleton<ICertificateService, CertificateService>();
   builder.Services.AddSingleton<IPairingService, PairingService>();
   ```

3. `/home/connorl/RemEx/Remex.Core/Services/Network/RemexNetworkListener.cs` (TCP command port at 8338):
   - **Wrap the accepted `TcpClient` socket in `SslStream`**:
     - Original (line 65 region): `_listener = new TcpListener(IPAddress.Any, actualPort); _listener.Start();`
     - Inside the accept loop, replace `using var client = await _listener.AcceptTcpClientAsync();` and the subsequent `using var stream = client.GetStream();` with:

       ```csharp
       using var client = await _listener.AcceptTcpClientAsync(ct);
       using var rawStream = client.GetStream();
       using var ssl = new SslStream(rawStream, leaveInnerStreamOpen: false);
       await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
       {
           ServerCertificate = _certificate,
           EnabledSslProtocols = SslProtocols.Tls13 | SslProtocols.Tls12,
           ClientCertificateRequired = false,
       }, ct);
       // use `ssl` instead of `stream` for all subsequent reads/writes
       ```

   - Add a constructor parameter `X509Certificate2 certificate` and store it as `_certificate`.
   - Update DI registration in `HostBootstrapper.cs` to pass the cert from `ICertificateService`.

**DO NOT TOUCH:** `RemoteDesktopHandler.cs`, `PingPongHandler.cs`, any client file.

**VERIFICATION:**

1. `cd /home/connorl/RemEx && dotnet build Remex.Host/Remex.Host.csproj -c Release` — expect exit code 0.
2. Start the host (`dotnet run --project Remex.Host`); from another terminal: `curl -k https://localhost:5005/ -v 2>&1 | grep "TLS"` — expect a TLS handshake line, e.g. `TLSv1.3 (IN), TLS handshake`.
3. `curl http://localhost:5005/` — expect a connection error (host no longer listens for plaintext HTTP).
4. `ls -la /var/lib/remex/cert.pfx 2>/dev/null || ls -la "$HOME/.local/share/Remex/cert.pfx"` — expect a 0600-permission file on Linux.
5. Re-start the host; cert mtime should not change (cert reused, not regenerated).

**POST-CONDITIONS:** Host endpoints serve TLS 1.3. Cert and SPKI hash are accessible via `ICertificateService`. Plaintext HTTP/TCP no longer works.

---

## TRACK 1B — Pairing protocol + access-key removal

**ID:** `1B-pairing`
**GOAL:** Implement ECDH X25519 pairing handshake with 6-digit PIN out-of-band binding. Replace the plaintext-access-key flow.
**PRE-CONDITIONS:** Track 1A complete. NSec.Cryptography package available.

**Protocol description (lock this in, do not deviate):**

1. Client opens WSS to `wss://host:5005/ws` with no credentials.
2. Server sends a `RemexMessage { Type=PairingRequest, ProtocolVersion=2 }` if the client is unknown (no SPKI pin recorded). Otherwise jumps to step 7 (mutual recognition via SPKI).
3. Client generates an ephemeral X25519 keypair via NSec, sends `PairingRequest { ClientPublicKeyBase64, ClientName, ClientVersion }`.
4. Server generates ephemeral X25519 keypair, computes the shared secret (X25519 ECDH), derives a 32-byte session key via HKDF-SHA256(secret, salt=cert SPKI hash, info=`"remex-pair-v1"`).
5. Server displays a 6-digit PIN (random `0..999999`) in the host UI / console with a 120-second TTL. Server computes `pinHmac = HMAC-SHA256(sessionKey, PIN)` and sends `PairingResponse { HostPublicKeyBase64, HostId, HostName, CertificateSpkiHashBase64, PinHmacBase64 }`.
6. Client prompts user to enter the PIN. Client computes the same `sessionKey` and verifies `pinHmac` against the user-entered PIN. If valid, client computes `clientPinHmac = HMAC-SHA256(sessionKey, "ack:" + PIN)` and sends `PairingComplete { ClientPinHmacBase64 }`. Server verifies; on success, the client's SPKI hash *of the cert presented during TLS handshake* is recorded as paired.
7. (Future connections.) Client sends `PairingRequest` only if not already paired. If paired, client uses TLS connection directly; server's TLS cert SPKI hash MUST match the pinned hash from step 6 — pin failure aborts the connection.

**FILES TO MODIFY:**

1. `/home/connorl/RemEx/Remex.Host/Services/Security/PairingService.cs` — implement the protocol described above. Key API:
   - `StartPairingAsync(ct)`: generate keypair, generate PIN, set `IsPairingActive=true`, schedule cancellation at 120s.
   - `GetActivePin()`: returns the displayed PIN (also write to host log at INFO level).
   - `VerifyClientHmacAsync(...)`: returns true iff the HMAC matches and PIN has not expired. On success, persist the client's pin record at `Path.Combine(<host data dir>, "paired_clients.json")`.
   - Internal: `ComputeSessionKey(theirPubBase64, ourKeypair, certSpkiHash)` — uses NSec `KeyAgreementAlgorithm.X25519` then `KeyDerivationAlgorithm.HkdfSha256`.

2. `/home/connorl/RemEx/Remex.Host/Handlers/PingPongHandler.cs`:
   - In the message-dispatch `switch` block, **add cases at the top** (before any other case):

     ```csharp
     case MessageTypes.PairingRequest when message.PairingRequest is not null:
         await _pairingHandler.HandlePairingRequestAsync(webSocket, message.PairingRequest, ct);
         continue;
     case MessageTypes.PairingComplete when message.PairingComplete is not null:
         await _pairingHandler.HandlePairingCompleteAsync(webSocket, message.PairingComplete, ct);
         continue;
     ```

   - Inject `IPairingService _pairingHandler` via constructor.
   - **Reject any other message type if the client is unpaired** — add a guard at the top of the loop:

     ```csharp
     if (!_pairingHandler.IsClientPaired(connectionId) && message.Type != MessageTypes.PairingRequest && message.Type != MessageTypes.PairingComplete)
     {
         await SendErrorAsync(webSocket, "Pairing required", ct);
         continue;
     }
     ```

3. `/home/connorl/RemEx/Remex.Host/HostBootstrapper.cs`:
   - **Delete the `ValidateAccessKey` method** (lines 200-212) and the call to it in the `/ws` and `/ws/desktop` endpoint handlers.
   - **Delete the access-key extraction**: `var accessKey = app.Configuration["Remex:AccessKey"] ?? "";`
   - The TLS layer + pairing now provide the auth, not query-string access keys.

4. `/home/connorl/RemEx/Remex.Host/Configuration/RemexHostSettings.cs`:
   - Remove the `Security.AccessKey` property (line 77) and the `Security.RequireAccessKey` property (line 72). Replace with:

     ```csharp
     public bool AllowFirstTimePairing { get; init; } = true;
     public int PairingPinTtlSeconds { get; init; } = 120;
     ```

5. `/home/connorl/RemEx/Remex.Core/Services/Network/RemexNetworkListener.cs` (TCP command port):
   - **Delete the access-key check** (lines 343-358).
   - Replace with: require TLS client auth via the same pairing system. For Phase 1, simplest implementation: only accept TCP commands from clients that have a paired SPKI on the parallel WSS connection. Stash a `HashSet<string> _pairedClientSpkiHashes` in the listener; on each TCP accept, after TLS handshake, extract the *client's* IP address and check if any pairing handshake has completed from that IP within the last 24 hours. If not, refuse the connection. (This is intentionally weaker than the WSS pairing — TCP is for fire-and-forget commands like Wake-on-LAN. Document this limitation in `SECURITY.md`.)

**Android / .NET client side:** see Track 1C and Track 1D.

**DO NOT TOUCH:** `RemoteDesktopHandler.cs` (it inherits the auth via the WSS layer), any UI file, any other handler.

**VERIFICATION:**

1. `cd /home/connorl/RemEx && dotnet build Remex.sln -c Release` — expect exit code 0.
2. `cd /home/connorl/RemEx && dotnet test Remex.Core.Tests Remex.Host.Tests` — expect exit code 0. (Existing tests will need update; flag any failures and fix in Track 2B's scope.)
3. `grep -c "AccessKey" /home/connorl/RemEx/Remex.Host/HostBootstrapper.cs` — expect `0`.
4. `grep "Security:AccessKey" /home/connorl/RemEx/Remex.Host/Configuration/RemexHostSettings.cs` — expect no output.
5. Manual pairing run: start host, observe PIN printed to console, run a one-shot pairing test client (write a small `dotnet script` or test fixture that sends `PairingRequest` and prompts for the PIN). Pairing should succeed; second connection from same client should NOT request a PIN.

**POST-CONDITIONS:** No plaintext access keys exist anywhere in the host codebase. Pairing handshake is required before any non-pairing message is processed. Paired client SPKIs are persisted across restarts.

---

## TRACK 1C — .NET desktop client TLS + pairing

**ID:** `1C-desktop-client-tls`
**GOAL:** Update the Avalonia desktop client to use `wss://`, validate the host cert against a pinned SPKI hash, and implement the client side of the pairing handshake with a PIN entry dialog.
**PRE-CONDITIONS:** Tracks 1A and 1B complete.

**FILES TO MODIFY:**

1. `/home/connorl/RemEx/Remex.Client/Services/Network/RemoteDesktopService.cs`:
   - **Lines 38-42** (URL construction):
     - Replace `var desktopUrl = hostAddress.TrimEnd('/'); if (desktopUrl.EndsWith("/ws")) desktopUrl += "/desktop"; else desktopUrl += "/ws/desktop";`
     - With: same logic, but ensure the scheme is forced to `wss://`. If `hostAddress` starts with `http://`, replace with `wss://`; if `https://`, replace with `wss://`; if no scheme, prepend `wss://`.
   - **Lines 44-45** (`ClientWebSocket` connect):
     - Before `ConnectAsync`, set `_webSocket.Options.RemoteCertificateValidationCallback = ValidateServerCertificate;`
     - Add a new method `ValidateServerCertificate(object sender, X509Certificate? cert, X509Chain? chain, SslPolicyErrors errors)` that:
       - Computes SPKI SHA-256 of `cert` (use `new X509Certificate2(cert).PublicKey.ExportSubjectPublicKeyInfo()` + `SHA256.HashData(...)`).
       - Looks up the pinned hash from `IPinnedCertStore` (new service).
       - Returns `true` iff the computed hash matches the pinned hash. If no pin exists yet (first-time pairing), accept and let the pairing handler record it.
       - **If the pin exists and doesn't match: log loudly, return false.**

2. **NEW FILE** `/home/connorl/RemEx/Remex.Client/Services/Security/PinnedCertStore.cs`:
   - JSON dict on disk at `Path.Combine(LocalApplicationData, "Remex", "pinned_hosts.json")`.
   - API: `string? GetPin(string hostId)`, `void SetPin(string hostId, string spkiHashBase64)`, `void RemovePin(string hostId)`.

3. **NEW FILE** `/home/connorl/RemEx/Remex.Client/Services/Security/PairingClient.cs`:
   - Mirror of host-side pairing protocol from client perspective.
   - Constructor takes `WebSocket` and `IPinnedCertStore`.
   - `Task<bool> RunPairingAsync(string userEnteredPin, CancellationToken ct)` — orchestrates client side of steps 3-6 from Track 1B.

4. **NEW FILE** `/home/connorl/RemEx/Remex.Client/Views/PairingDialog.axaml` and `.axaml.cs`:
   - A simple Avalonia `Window` with a 6-digit numeric input, a "Pair" button, a "Cancel" button.
   - Bind to `PairingDialogViewModel { string PinInput, ICommand SubmitCommand, ICommand CancelCommand, bool IsBusy, string? ErrorText }`.
   - Title: bind to `localService["PairingDialogTitle"]` (add localization key).
   - Style consistent with existing dialogs (lift styles from `Remex.Client/Views/SetAlertDialog.axaml` if present).

5. `/home/connorl/RemEx/Remex.Client/ViewModels/ConnectionViewModel.cs`:
   - In the connect command, **after the WSS connection is established**, check whether the host is paired:
     - If paired (PinnedCertStore has an entry): proceed to normal flow.
     - If not paired: show `PairingDialog`, await user PIN input, run `PairingClient.RunPairingAsync`, on success record the SPKI pin and proceed.
   - Remove any UI fields, persistence, or settings related to "Access Key".

6. `/home/connorl/RemEx/Remex.Client/Configuration/RemexClientSettings.cs`:
   - Delete any access-key field.

7. `/home/connorl/RemEx/Remex.Client/Localization/Strings.resx` (and all sibling language files):
   - Add new keys (Track 3A regenerates other languages from English):
     - `PairingDialogTitle` = "Pair with new host"
     - `PairingDialogPrompt` = "Enter the 6-digit code shown on the host"
     - `PairingDialogSubmit` = "Pair"
     - `PairingDialogCancel` = "Cancel"
     - `PairingErrorBadPin` = "The PIN is incorrect or expired."
     - `PairingErrorCertMismatch` = "Server certificate does not match the recorded pin. Connection refused."
   - **Delete any access-key related strings** if present.

**DO NOT TOUCH:** Any host-side file. Any test file (Phase 2B handles tests).

**VERIFICATION:**

1. `cd /home/connorl/RemEx && dotnet build Remex.Client/Remex.Client.csproj Remex.Client.Desktop/Remex.Client.Desktop.csproj -c Release` — expect exit code 0.
2. `grep -c "AccessKey\|accessKey" /home/connorl/RemEx/Remex.Client/ /home/connorl/RemEx/Remex.Client.Desktop/ -r` — expect `0`.
3. `grep "wss://" /home/connorl/RemEx/Remex.Client/Services/Network/RemoteDesktopService.cs` — expect at least one match.
4. End-to-end: launch host (Track 1A/1B applied), launch desktop client, attempt to connect. PairingDialog should appear; entering the host-displayed PIN should succeed. Disconnect and reconnect — no PairingDialog the second time.

**POST-CONDITIONS:** Desktop client communicates over `wss://` only. SPKI pinning enforced. Pairing dialog exists. Access-key code removed.

---

## TRACK 1D — Android client TLS + pairing

**ID:** `1D-android-tls`
**GOAL:** Update the Android JNI bridge and Kotlin client manager to use `wss://`, pin server certs via SPKI hash stored in `EncryptedSharedPreferences`, and implement the pairing handshake with a PIN entry screen.
**PRE-CONDITIONS:** Tracks 1A, 1B, 1C complete.

**FILES TO MODIFY:**

1. `/home/connorl/RemEx/RemEx.Android/app/build.gradle.kts`:
   - In the `dependencies` block, add:

     ```kotlin
     implementation("androidx.security:security-crypto:1.1.0-alpha06")
     ```

2. **NEW FILE** `/home/connorl/RemEx/RemEx.Android/app/src/main/java/com/clindsay94/remex/security/PinnedHostStore.kt`:
   - Wraps `EncryptedSharedPreferences` with API: `fun getPin(hostId: String): String?`, `fun setPin(hostId: String, spkiHash: String)`, `fun removePin(hostId: String)`, `fun listPaired(): Map<String, String>`.
   - File name: `"remex_pinned_hosts"`. Master key: `MasterKey.Builder(context).setKeyScheme(MasterKey.KeyScheme.AES256_GCM).build()`.

3. **NEW FILE** `/home/connorl/RemEx/RemEx.Android/app/src/main/java/com/clindsay94/remex/ui/screens/PairingScreen.kt`:
   - Compose screen with:
     - `OutlinedTextField` for 6-digit PIN (numeric keyboard via `keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.NumberPassword)`)
     - "Pair" button (disabled while `isLoading`)
     - "Cancel" button
     - Error text when `pairingError != null`
   - All strings via `stringResource()` — add to `values/strings.xml`:
     - `pairing_title`, `pairing_prompt`, `pairing_submit`, `pairing_cancel`, `pairing_error_bad_pin`, `pairing_error_cert_mismatch`
   - `PairingViewModel` exposes `StateFlow<PairingUiState>` and `fun submitPin(pin: String)`.

4. `/home/connorl/RemEx/RemEx.Android/app/src/main/java/com/clindsay94/remex/RemexCoreClient.kt`:
   - **Add new native method declarations** (no body change to existing methods):

     ```kotlin
     external fun StartPairingNative(hostUrl: String, clientName: String, clientVersion: String): String
     external fun SubmitPairingPinNative(pin: String): String
     external fun GetPinnedHostHashNative(hostId: String): String
     external fun SetPinnedHostHashNative(hostId: String, spkiHashBase64: String): String
     ```

   - These map to new C# `AndroidNativeExports` methods (see Track 1E for the C# side).

5. `/home/connorl/RemEx/RemEx.Android/app/src/main/java/com/clindsay94/remex/RemexClientManager.kt`:
   - **Lines 111-143** (`connect` method):
     - Replace the JSON construction (lines 120-125): remove the `accessKey` field; add a check: if `PinnedHostStore.getPin(host) == null`, navigate to `PairingScreen` first; only after pairing completes, call `InitRemex`.
   - **Remove all reads of `accessKey` from `SettingsManager`** in this file.

6. `/home/connorl/RemEx/RemEx.Android/app/src/main/java/com/clindsay94/remex/data/SettingsManager.kt`:
   - **Remove `ACCESS_KEY` Preferences key (line 25)** and all flows / accessors that read or write it.
   - Update `ConnectionPreferences` data class (lines 72+) — drop the `accessKey` field.
   - Update `saveConnectionSettings` (lines 287+) — drop the `accessKey` parameter.
   - Run a project-wide search for `accessKey` and update every call site.

7. `/home/connorl/RemEx/RemEx.Android/app/src/main/java/com/clindsay94/remex/ui/screens/ConnectionScreen.kt`:
   - Remove the access-key TextField and all bindings.
   - Add a "Paired" indicator (e.g., a green dot + "Paired" text) when `PinnedHostStore.getPin(host) != null`.
   - Add an "Unpair" button next to it that calls `PinnedHostStore.removePin(host)`.

8. `/home/connorl/RemEx/RemEx.Android/app/src/main/java/com/clindsay94/remex/ui/screens/QrScannerScreen.kt`:
   - **The QR pairing format changes.** Old format encoded `{ host, port, accessKey }`. New format encodes `{ host, port, hostId, spkiHashBase64 }`. The host generates this QR via a new endpoint (see Track 1E). On scan, the client immediately stores the SPKI pin via `PinnedHostStore.setPin(hostId, spkiHashBase64)` — **no PIN entry needed for QR-pairing**, because the QR is the out-of-band channel.
   - Update the parser to handle the new JSON schema; show an error and guide the user to update the host if old-format QRs are scanned.

**DO NOT TOUCH:** Any handler file (.cs), any other Compose screen except those listed.

**VERIFICATION:**

1. `cd /home/connorl/RemEx/RemEx.Android && ./gradlew :app:assembleDebug` — expect `BUILD SUCCESSFUL`.
2. `cd /home/connorl/RemEx/RemEx.Android && grep -rE "accessKey|access_key|ACCESS_KEY" app/src/main/ | grep -v "/build/" | grep -v "PinnedHostStore"` — expect no output.
3. Install debug APK on a device, attempt to connect to a paired-up host. PairingScreen appears. Enter PIN displayed on host. Pairing succeeds. Disconnect and reconnect — no PairingScreen.
4. Re-scan an old-format QR (one with `accessKey`): clear error message guides user to update host.

**POST-CONDITIONS:** Android client uses WSS only. SPKI pin stored in EncryptedSharedPreferences. Access-key removed. PairingScreen and updated QR scanner work.

---

## TRACK 1E — Native AOT exports for pairing + QR endpoint

**ID:** `1E-aot-exports`
**GOAL:** Add the C# `[UnmanagedCallersOnly]` exports that back the new JNI methods declared in Track 1D, and add the host endpoint that emits the new pairing QR payload.
**PRE-CONDITIONS:** Tracks 1A, 1B, 1C, 1D complete.

**FILES TO MODIFY:**

1. `/home/connorl/RemEx/Remex.Core/AndroidNativeExports.cs` (or equivalent — locate via `grep -r "UnmanagedCallersOnly" /home/connorl/RemEx/Remex.Core` if the path differs):
   - Add four new exports:

     ```csharp
     [UnmanagedCallersOnly(EntryPoint = "StartPairingNative")]
     public static IntPtr StartPairingNative(IntPtr hostUrlPtr, IntPtr clientNamePtr, IntPtr clientVersionPtr) { /* delegate to client pairing impl, return JSON status */ }

     [UnmanagedCallersOnly(EntryPoint = "SubmitPairingPinNative")]
     public static IntPtr SubmitPairingPinNative(IntPtr pinPtr) { /* validate + complete pairing, return JSON status */ }

     [UnmanagedCallersOnly(EntryPoint = "GetPinnedHostHashNative")]
     public static IntPtr GetPinnedHostHashNative(IntPtr hostIdPtr) { /* lookup, return base64 string or empty */ }

     [UnmanagedCallersOnly(EntryPoint = "SetPinnedHostHashNative")]
     public static IntPtr SetPinnedHostHashNative(IntPtr hostIdPtr, IntPtr spkiHashPtr) { /* persist, return JSON status */ }
     ```

   - JNI strings are passed as UTF-8 byte ptrs; use the existing `Marshal.PtrToStringUTF8(...)` and the existing string-return helper (search for an existing `IntPtr` return-marshal helper in this file).

2. `/home/connorl/RemEx/Remex.Host/HostBootstrapper.cs`:
   - **Add a new endpoint `app.MapGet("/pairing-qr", ...)`** that returns a small JSON `{ host, port, hostId, spkiHashBase64 }` — the host UI renders this as a QR.

3. `/home/connorl/RemEx/Remex.Client/ViewModels/SettingsViewModel.cs` (or wherever the QR display logic lives — locate via `grep -r "QRCoder" /home/connorl/RemEx/Remex.Client`):
   - Update QR encoding to use the new payload schema.

**DO NOT TOUCH:** Any Kotlin file (Track 1D handles those).

**VERIFICATION:**

1. `cd /home/connorl/RemEx/RemEx.Android && ./scripts/android-fresh.ps1 -Configuration Release` — expect `BUILD SUCCESSFUL` and the `remexVerifyReleaseApkFreshness` task to confirm the .so hash matches the published binary.
2. `nm -D --defined-only /home/connorl/RemEx/RemEx.Android/app/build/intermediates/merged_native_libs/release/out/lib/arm64-v8a/libRemexCore.so | grep -E "StartPairingNative|SubmitPairingPinNative|GetPinnedHostHashNative|SetPinnedHostHashNative"` — expect 4 lines (one per export).
3. Install the AAB on a device, scan a QR generated by the new endpoint, verify connection succeeds without a PIN prompt.

**POST-CONDITIONS:** Native exports exist; QR-based pairing works end-to-end.

---

# PHASE 2 — FEATURE & RELEASE-ENGINEERING TRACKS (parallel)

> **All tracks below are parallel-safe.** Their `FILES TO MODIFY` lists are disjoint except where called out in §3 (file-ownership matrix). Run any subset in parallel by handing each track to a separate agent.

---

## TRACK 2A — Remote file transfer

**ID:** `2A-file-transfer`
**GOAL:** Implement secure remote file browse + upload + download with progress and cancel, using the `FileTransfer*` messages declared in Phase 0.
**PRE-CONDITIONS:** All Phase 1 tracks complete.
**SUGGESTED MODEL:** Claude Sonnet 4.6 or Gemini 2.5 Pro.

**NEW FILES (host):**

1. `/home/connorl/RemEx/Remex.Host/Services/FileTransfer/FileTransferService.cs` — implements `IFileTransferService` from Phase 0.
   - `BrowseAsync(path, ct)`: `DirectoryInfo.EnumerateFileSystemInfos()`. Reject paths that escape via `..`. Reject paths that resolve to system-restricted locations on Linux (`/proc`, `/sys`, `/dev`).
   - `OpenForReadAsync(path, ct)`: returns a `FileStream` open for read.
   - `OpenForWriteAsync(path, expectedBytes, ct)`: returns a `FileStream` open for write, truncating to 0 bytes. Reject if `expectedBytes > 5_000_000_000` (5 GB cap for 2.0).

2. `/home/connorl/RemEx/Remex.Host/Handlers/FileTransferHandler.cs`:
   - Hooks into `PingPongHandler`'s message dispatch (Track 1B added the dispatcher; this track adds a new case).
   - For each transfer: maintain a `ConcurrentDictionary<string, FileTransferState>` keyed by `transferId`. Each state has a `FileStream` + `SHA256` hasher + `bytesTransferred` counter.
   - On `FileTransferStart`: open the destination/source, register state.
   - On `FileTransferChunk`: append/write, update hash, send `FileTransferProgress` every 10 chunks (or on completion).
   - On `FileTransferEnd`: verify final SHA-256 matches, close stream, send back final ack.
   - On `FileTransferCancel`: close stream, delete partial file (uploads only), remove state.
   - On WebSocket close mid-transfer: clean up all active transfers for that connection.

**FILES TO MODIFY (host):**

1. `/home/connorl/RemEx/Remex.Host/Handlers/PingPongHandler.cs`:
   - Inject `FileTransferHandler` via constructor.
   - In the dispatch switch, add cases for all 7 file-transfer message types, delegating to the handler.

2. `/home/connorl/RemEx/Remex.Host/HostBootstrapper.cs`:
   - Register: `builder.Services.AddSingleton<IFileTransferService, FileTransferService>();` and `builder.Services.AddTransient<FileTransferHandler>();`.

**NEW FILES (.NET desktop client):**

1. `/home/connorl/RemEx/Remex.Client/Services/FileTransfer/FileTransferClient.cs`:
   - API: `Task<IReadOnlyList<FileEntry>> BrowseRemoteAsync(string path, CancellationToken ct)`, `Task UploadAsync(string localPath, string remotePath, IProgress<double> progress, CancellationToken ct)`, `Task DownloadAsync(string remotePath, string localPath, IProgress<double> progress, CancellationToken ct)`.

2. `/home/connorl/RemEx/Remex.Client/Views/FileTransferView.axaml` and `.axaml.cs`:
   - Two-column file browser: local on left, remote on right.
   - Drag from local → upload; drag from remote → download.
   - Progress bar at bottom for active transfer.
   - Cancel button.

3. `/home/connorl/RemEx/Remex.Client/ViewModels/FileTransferViewModel.cs`.

**FILES TO MODIFY (.NET desktop client):**

1. `/home/connorl/RemEx/Remex.Client/Views/MainView.axaml` (or wherever the nav menu lives — locate via `grep -r "NavigationViewItem" /home/connorl/RemEx/Remex.Client/Views`):
   - Add a new nav item "Files" that navigates to `FileTransferView`.

**NEW FILES (Android):**

1. `/home/connorl/RemEx/RemEx.Android/app/src/main/java/com/clindsay94/remex/ui/screens/FileTransferScreen.kt` — Compose UI mirroring the desktop file browser.

2. `/home/connorl/RemEx/RemEx.Android/app/src/main/java/com/clindsay94/remex/ui/screens/FileTransferViewModel.kt`.

**FILES TO MODIFY (Android):**

1. `/home/connorl/RemEx/RemEx.Android/app/src/main/java/com/clindsay94/remex/ui/navigation/NavRoutes.kt` and `AppNavigation.kt`:
   - Add `FileTransfer` route.
   - Add a bottom-nav item.

2. `/home/connorl/RemEx/RemEx.Android/app/src/main/java/com/clindsay94/remex/RemexCoreClient.kt`:
   - Add native method declarations:

     ```kotlin
     external fun BrowseRemoteFolderNative(path: String): String  // returns FileBrowseResponse JSON
     external fun StartFileUploadNative(localUri: String, remotePath: String): String  // returns transferId
     external fun StartFileDownloadNative(remotePath: String, localUri: String): String
     external fun CancelFileTransferNative(transferId: String): String
     ```

   - Add to the `RemexCallback` interface:

     ```kotlin
     fun onFileTransferProgress(progressJson: String)
     fun onFileTransferComplete(completeJson: String)
     ```

   - Update `RemexClientManager` to expose `StateFlow<List<FileTransferStatus>>` and forward callbacks.

3. `/home/connorl/RemEx/Remex.Core/AndroidNativeExports.cs` — add the corresponding `[UnmanagedCallersOnly]` exports.

**DO NOT TOUCH:** Pairing or TLS code. Any other handler.

**VERIFICATION:**

1. `cd /home/connorl/RemEx && dotnet build Remex.sln -c Release` — exit 0.
2. `cd /home/connorl/RemEx/RemEx.Android && ./gradlew :app:assembleDebug` — `BUILD SUCCESSFUL`.
3. End-to-end: connect desktop client to host, browse `/home`, upload a 100 MB file from desktop to host, download back, verify SHA-256 matches via `sha256sum` on both copies.
4. Same test from Android.
5. Cancel a 1 GB upload mid-transfer; verify host deletes the partial file (`ls -la /tmp/remex-test-upload.bin` after cancel — should not exist).

**POST-CONDITIONS:** File transfer works on desktop and Android. SHA-256 verified end-to-end. Cancel cleans up state.

---

## TRACK 2B — Critical bug fixes from review-report.md

**ID:** `2B-review-report-criticals`
**GOAL:** Resolve all 4 Critical issues (C-1 through C-4) and the 7 High-severity issues (H-1 through H-7) flagged by the microscopic code reviewer.
**PRE-CONDITIONS:** Phase 0 complete. Can run in parallel with other Phase 2 tracks.
**SUGGESTED MODEL:** Claude Sonnet 4.6.

**FILES TO MODIFY:**

### C-1: SavedStatus from ThreadPool

`/home/connorl/RemEx/Remex.Client/ViewModels/SettingsViewModel.cs:260`:

- Old: `_ = Task.Delay(3000).ContinueWith(_ => SavedStatus = string.Empty);`
- New: `_ = Task.Delay(3000).ContinueWith(_ => Avalonia.Threading.Dispatcher.UIThread.Post(() => SavedStatus = string.Empty));`

### C-2: DiscoverHostsAsync HostAddress assignment off-thread

`/home/connorl/RemEx/Remex.Client/ViewModels/ConnectionViewModel.cs:141`:

- Wrap the `HostAddress = firstHost` line in `Avalonia.Threading.Dispatcher.UIThread.Post(() => { HostAddress = firstHost; });`.

### C-3: async-void crash in OnShowSetAlertRequested

`/home/connorl/RemEx/Remex.Client/Views/CanvasView.axaml.cs:94`:

- Wrap the entire `async void OnShowSetAlertRequested` body in `try { ... } catch (Exception ex) { _logger.LogError(ex, "Set alert dialog failed"); }`.
- If `_logger` is not in scope, use `System.Diagnostics.Debug.WriteLine`.

### C-4: AlertTriggered event subscription leak

`/home/connorl/RemEx/Remex.Client/ViewModels/CanvasDashboardViewModel.cs:770`:

- Before the `sensor.AlertTriggered += OnSensorAlertTriggered` subscription, **first unsubscribe**: `sensor.AlertTriggered -= OnSensorAlertTriggered;`.
- Add a `Cleanup()` method that iterates all `sensorVms` and unsubscribes; call it on disconnect.

### Settings freeze (PART 1)

`/home/connorl/RemEx/Remex.Client/ViewModels/SettingsViewModel.cs` lines 860-881 (Linux branch of `RefreshServiceStatusAsync`):

- Wrap the entire branch in `await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(async () => { ... });`.

### H-1: Duplicate XAML style block

`/home/connorl/RemEx/Remex.Client/Views/CanvasView.axaml` lines 19, 29:

- Delete the second `<Style Selector="ctrl|DraggableCard.alert-active">` block.
- Verify the first block contains all the merged setters.

### H-2: Snapshot clipboard saves path, not bitmap

`/home/connorl/RemEx/Remex.Client/Views/CanvasView.axaml.cs:171-177`:

- Rename the menu item from "Copy to Clipboard" to "Copy Path" (in localization).
- Update the status message from `localService["SnapshotCopiedToClipboard"]` to `localService["SnapshotPathCopied"]`.
- Add the new localization key.
- Document the limitation in `docs/KNOWN_LIMITATIONS.md` (create file if absent): "Bitmap-on-clipboard requires platform-conditional code; deferred to 2.x."

### H-3: WireMinimapControl fragile double-call

`/home/connorl/RemEx/Remex.Client/Views/CanvasView.axaml.cs` (locate `WireMinimapControl` calls):

- Add a `bool _minimapWired` field. Guard the call: `if (_minimapWired) return; _minimapWired = true;`.

### H-4: RefreshSensors on every Settings open/close

`/home/connorl/RemEx/Remex.Client/ViewModels/ShellViewModel.cs:579-587` (`EnsureSettingsVm`):

- Track an `_settingsRefreshedAt` timestamp; only call `RefreshSensors()` if more than 5 seconds have elapsed OR the canvas card collection has changed since last refresh.

### H-5: targetSdk = 36 (preview)

`/home/connorl/RemEx/RemEx.Android/app/build.gradle.kts`:

- Line 56 (`compileSdk = 36`) → `compileSdk = 35`.
- Line 61 (`targetSdk = 36`) → `targetSdk = 35`.

### H-6: material3 alpha17 in production

`/home/connorl/RemEx/RemEx.Android/gradle/libs.versions.toml`:

- Find the `material3 = "1.5.0-alpha17"` line and replace with `material3 = "1.4.0"` (or the latest stable as of current date — verify via Maven Central before changing).
- After bump, the build will fail on `MaterialShapes` and `MotionScheme.expressive()` references. **Do not skip these.** Replace each:
  - `MaterialShapes.X` → use the equivalent `Shape` token from stable material3 (typically `MaterialTheme.shapes.X`).
  - `MotionScheme.expressive()` → use `MotionScheme.standard()` (the closest stable equivalent).
- Run `./gradlew :app:assembleDebug` and resolve every compile error before declaring done.

### H-7: enableOnBackInvokedCallback

**Already present at `AndroidManifest.xml:26`. Verify with `grep enableOnBackInvokedCallback /home/connorl/RemEx/RemEx.Android/app/src/main/AndroidManifest.xml` — expect a match. NO CHANGE NEEDED. If you somehow get to this point and it's missing, add `android:enableOnBackInvokedCallback="true"` to the `<application>` tag.**

**DO NOT TOUCH:** Any TLS, pairing, or new-feature code. This track is *only* the bug-fix list.

**VERIFICATION:**

1. `cd /home/connorl/RemEx && dotnet build Remex.sln -c Release` — exit 0.
2. `cd /home/connorl/RemEx/RemEx.Android && ./gradlew :app:assembleDebug` — `BUILD SUCCESSFUL`.
3. `grep "1.5.0-alpha" /home/connorl/RemEx/RemEx.Android/gradle/libs.versions.toml` — expect no output.
4. `grep "targetSdk = 36\|compileSdk = 36" /home/connorl/RemEx/RemEx.Android/app/build.gradle.kts` — expect no output.
5. Manual: open Settings on Linux desktop client — must not freeze. Disconnect and reconnect — sensor alert must fire exactly once, not N times.

**POST-CONDITIONS:** All Critical and High items from review-report.md resolved. Updated CHANGELOG entry (Track 0A) is now accurate.

---

## TRACK 2C — Material3 stable migration (already folded into 2B)

**Merged into Track 2B** (H-6). No separate spec — see 2B above.

---

## TRACK 2D — Release engineering (cleartext, R8, AAB, network security config)

**ID:** `2D-release-engineering`
**GOAL:** Apply every Play Store hardening listed in Gemini's PRD §7 and the review-report Play Store checklist that isn't already done.
**PRE-CONDITIONS:** Phase 0 complete. Can run in parallel.
**SUGGESTED MODEL:** **Haiku / Gemini Flash / local model.** This is mechanical work with crisp verification.

**FILES TO MODIFY:**

1. `/home/connorl/RemEx/RemEx.Android/app/src/main/AndroidManifest.xml`:
   - Add `android:usesCleartextTraffic="false"` to the `<application>` opening tag (alongside `android:enableOnBackInvokedCallback="true"` already there).
   - Add `android:networkSecurityConfig="@xml/network_security_config"` to the same `<application>` tag.

2. `/home/connorl/RemEx/RemEx.Android/app/build.gradle.kts`:
   - In the `android { ... defaultConfig { ... } }` block, **after the existing `versionName` line**, add the NDK ABI filter:

     ```kotlin
     ndk {
         abiFilters += listOf("arm64-v8a")
     }
     ```

     (NOT `x86_64` for the Play Store production build — Google Play does not require x86_64 anymore unless you specifically target Chromebook; arm64-v8a is sufficient for ~99% of devices. If you want Chromebook coverage, add `"x86_64"`.)
   - In the `android { ... }` block, add a `bundle` block:

     ```kotlin
     bundle {
         language { enableSplit = true }
         density  { enableSplit = true }
         abi      { enableSplit = true }
     }
     ```

3. `/home/connorl/RemEx/RemEx.Android/app/proguard-rules.pro` — append to existing rules:

   ```proguard
   # Keep all NSec.Cryptography classes (cryptography library can be reflectively invoked)
   -keep class com.nsec.** { *; }

   # Keep new pairing native bridge methods reachable
   -keepclassmembers class com.clindsay94.remex.RemexCoreClient {
       public static native <methods>;
       private static native <methods>;
   }
   -keepclassmembers class com.clindsay94.remex.security.PinnedHostStore { *; }
   -keepclassmembers class com.clindsay94.remex.security.PairingViewModel { *; }

   # Keep file-transfer native bridge
   -keepclassmembers class com.clindsay94.remex.RemexCoreClient$RemexCallback {
       void onFileTransferProgress(java.lang.String);
       void onFileTransferComplete(java.lang.String);
   }
   ```

**NEW FILES:**

1. `/home/connorl/RemEx/RemEx.Android/app/src/main/res/xml/network_security_config.xml`:

   ```xml
   <?xml version="1.0" encoding="utf-8"?>
   <network-security-config>
       <base-config cleartextTrafficPermitted="false">
           <trust-anchors>
               <certificates src="system" />
           </trust-anchors>
       </base-config>
       <!-- Self-signed RemEx host certs are pinned via SPKI in PinnedHostStore;
            we deliberately do not declare them as trusted here, because the JNI/NSec
            layer handles validation independently of OS trust. -->
   </network-security-config>
   ```

**FILES TO MODIFY (data exclusion — review-report M-9):**

1. `/home/connorl/RemEx/RemEx.Android/app/src/main/res/xml/data_extraction_rules.xml`:
   - **Read the existing file first.** Add an exclusion for the EncryptedSharedPreferences file (`remex_pinned_hosts.xml`) and the DataStore preferences file (`settings.preferences_pb`):

     ```xml
     <data-extraction-rules>
         <cloud-backup>
             <exclude domain="sharedpref" path="remex_pinned_hosts.xml"/>
             <exclude domain="file" path="datastore/settings.preferences_pb"/>
         </cloud-backup>
         <device-transfer>
             <exclude domain="sharedpref" path="remex_pinned_hosts.xml"/>
             <exclude domain="file" path="datastore/settings.preferences_pb"/>
         </device-transfer>
     </data-extraction-rules>
     ```

2. `/home/connorl/RemEx/RemEx.Android/app/src/main/res/xml/backup_rules.xml`:
   - Same exclusions in the legacy format:

     ```xml
     <full-backup-content>
         <exclude domain="sharedpref" path="remex_pinned_hosts.xml"/>
         <exclude domain="file" path="datastore/settings.preferences_pb"/>
     </full-backup-content>
     ```

**DO NOT TOUCH:** Any Kotlin or .cs file.

**VERIFICATION:**

1. `cd /home/connorl/RemEx/RemEx.Android && ./gradlew :app:bundleRelease` — `BUILD SUCCESSFUL`. (Requires signing config in `local.properties`.)
2. `cd /home/connorl/RemEx/RemEx.Android && ./gradlew :app:assembleRelease` — `BUILD SUCCESSFUL`.
3. After successful release build:
   - `unzip -l app/build/outputs/bundle/release/*.aab | grep "lib/"` — expect only `lib/arm64-v8a/...` lines (no `armeabi-v7a`, no `x86`).
   - `apkanalyzer manifest print app/build/outputs/apk/release/*.apk | grep -E "usesCleartextTraffic|networkSecurityConfig"` — both should appear.
4. `grep usesCleartextTraffic /home/connorl/RemEx/RemEx.Android/app/src/main/AndroidManifest.xml` — expect a match.
5. `cat /home/connorl/RemEx/RemEx.Android/app/src/main/res/xml/network_security_config.xml` — expect the XML above.

**POST-CONDITIONS:** Cleartext disabled. Network security config restricts cleartext. AAB is single-ABI. R8 keep rules cover all native bridge points. Backups exclude credential storage.

---

## TRACK 2E — JNI exception hardening

**ID:** `2E-jni-hardening`
**GOAL:** Wrap every JNI call site on the Kotlin side in try/catch for `UnsatisfiedLinkError` and `RuntimeException`, so a native crash bubbles up as a Kotlin exception instead of a process tombstone.
**PRE-CONDITIONS:** Phase 1 complete (so all native methods exist).
**SUGGESTED MODEL:** Claude Sonnet 4.6.

**FILES TO MODIFY:**

1. `/home/connorl/RemEx/RemEx.Android/app/src/main/java/com/clindsay94/remex/RemexCoreClient.kt`:
   - For **every** `external fun X(...): Y` declaration, add a public wrapper:

     ```kotlin
     fun X(...): Result<Y> = try {
         Result.success(XNative(...))
     } catch (e: UnsatisfiedLinkError) {
         android.util.Log.e("RemexCoreClient", "Native method X not loaded", e)
         Result.failure(e)
     } catch (e: RuntimeException) {
         android.util.Log.e("RemexCoreClient", "Native method X crashed", e)
         Result.failure(e)
     }
     ```

   - Rename existing `external` methods to suffix `Native` (some are already named that way — verify each).
   - **Update every call site** in `RemexClientManager.kt` and other Kotlin files to use the wrapped (Result-returning) method instead of the raw native method. Pattern: `RemexCoreClient.X(...).onSuccess { ... }.onFailure { ... }`.

2. `/home/connorl/RemEx/RemEx.Android/app/src/main/java/com/clindsay94/remex/RemexClientManager.kt`:
   - Update the `connect()` method (lines 111-143) to handle `Result.failure` from `InitRemex` — set `_isConnecting.value = false` and surface the error through a new `_connectionError: MutableSharedFlow<String>`.

**DO NOT TOUCH:** The C# native exports themselves (Phase 1E owns those); only the Kotlin wrapper layer.

**VERIFICATION:**

1. `cd /home/connorl/RemEx/RemEx.Android && ./gradlew :app:assembleDebug` — `BUILD SUCCESSFUL`.
2. Force a JNI failure (e.g., temporarily delete the .so file from the APK, install, observe app does not crash but instead shows a connection error).
3. `grep -c "external fun" /home/connorl/RemEx/RemEx.Android/app/src/main/java/com/clindsay94/remex/RemexCoreClient.kt` should equal `grep -c "Result<" /home/connorl/RemEx/RemEx.Android/app/src/main/java/com/clindsay94/remex/RemexCoreClient.kt` (one wrapper per native method).

**POST-CONDITIONS:** No raw native call escapes a try/catch. App degrades gracefully on JNI failure.

---

## TRACK 2F — Firebase Crashlytics with NDK support

**ID:** `2F-crashlytics-ndk`
**GOAL:** Integrate Firebase Crashlytics with NDK symbol upload so .so faults get stack traces in the Play Console.
**PRE-CONDITIONS:** Phase 0 complete. **A Firebase project must exist; user provides `google-services.json`.** If it doesn't exist, this track is BLOCKED — surface that to the user and skip.
**SUGGESTED MODEL:** Claude Sonnet 4.6 (mid complexity).

**FILES TO MODIFY:**

1. `/home/connorl/RemEx/RemEx.Android/build.gradle.kts` (root):
   - Add to top-level `plugins`:

     ```kotlin
     id("com.google.gms.google-services") version "4.4.2" apply false
     id("com.google.firebase.crashlytics") version "3.0.2" apply false
     ```

2. `/home/connorl/RemEx/RemEx.Android/app/build.gradle.kts`:
   - Add to `plugins` block:

     ```kotlin
     id("com.google.gms.google-services")
     id("com.google.firebase.crashlytics")
     ```

   - Add to `dependencies`:

     ```kotlin
     implementation(platform("com.google.firebase:firebase-bom:33.6.0"))
     implementation("com.google.firebase:firebase-crashlytics-ndk")
     implementation("com.google.firebase:firebase-crashlytics")
     ```

   - In `android { buildTypes { release { ... } } }`, add:

     ```kotlin
     ndk { debugSymbolLevel = "FULL" }
     ```

   - Add a `firebaseCrashlytics` block:

     ```kotlin
     android.buildTypes.getByName("release").configure<com.google.firebase.crashlytics.buildtools.gradle.CrashlyticsExtension> {
         nativeSymbolUploadEnabled = true
         unstrippedNativeLibsDir = "build/intermediates/merged_native_libs/release/out/lib"
     }
     ```

3. `/home/connorl/RemEx/RemEx.Android/app/google-services.json` — **placed by user**, NOT committed (add to `.gitignore` if not already).

4. `/home/connorl/RemEx/.gitignore` — add `google-services.json` if absent.

**DO NOT TOUCH:** Source files (Crashlytics auto-installs at app start).

**VERIFICATION:**

1. `cd /home/connorl/RemEx/RemEx.Android && ./gradlew :app:bundleRelease` — `BUILD SUCCESSFUL` AND output contains `Crashlytics: Successfully uploaded native symbols`.
2. Trigger a fake crash: in `MainActivity.onCreate`, temporarily add `if (BuildConfig.DEBUG) FirebaseCrashlytics.getInstance().log("test")` then a deliberate crash. Run, observe in the Firebase console.
3. `find /home/connorl/RemEx/RemEx.Android -name google-services.json` — expect a match (or skip the track if absent).

**POST-CONDITIONS:** Crashes (Java + native) appear in Firebase console with full stack traces.

---

## TRACK 2G — Battery optimization onboarding

**ID:** `2G-battery-opt`
**GOAL:** During first-run / first-connection, prompt the user to whitelist RemEx from battery optimization so the foreground service isn't killed.
**PRE-CONDITIONS:** Phase 0 complete.
**SUGGESTED MODEL:** **Haiku / Flash / local.**

**FILES TO MODIFY:**

1. `/home/connorl/RemEx/RemEx.Android/app/src/main/AndroidManifest.xml`:
   - Add permission: `<uses-permission android:name="android.permission.REQUEST_IGNORE_BATTERY_OPTIMIZATIONS"/>` (alongside the existing permissions).

2. `/home/connorl/RemEx/RemEx.Android/app/src/main/java/com/clindsay94/remex/ui/screens/TutorialScreen.kt`:
   - Add a new tutorial step (one of the 9 existing pages — append as page 10 OR insert at appropriate position):
     - Title: `R.string.tutorial_battery_title`
     - Body: `R.string.tutorial_battery_body`
     - Action button: "Whitelist RemEx" → fires intent:

       ```kotlin
       val intent = Intent(Settings.ACTION_REQUEST_IGNORE_BATTERY_OPTIMIZATIONS)
       intent.data = Uri.parse("package:" + context.packageName)
       context.startActivity(intent)
       ```

3. `/home/connorl/RemEx/RemEx.Android/app/src/main/res/values/strings.xml`:
   - Add new keys: `tutorial_battery_title`, `tutorial_battery_body`. (Other locales filled by Track 3A.)

**DO NOT TOUCH:** Other tutorial pages (no need to renumber if appended at end).

**VERIFICATION:**

1. `./gradlew :app:assembleDebug` — `BUILD SUCCESSFUL`.
2. Install on a fresh device/emulator, walk through the tutorial, verify the new battery step appears, tap "Whitelist RemEx", verify the system battery-optimization settings page opens.

**POST-CONDITIONS:** Battery optimization onboarding step is in the tutorial flow and works.

---

## TRACK 2H — Two-stage haptic feedback

**ID:** `2H-haptics`
**GOAL:** Upgrade the existing `HapticModifier.kt` to support three feedback states: command sent, command acknowledged, command failed.
**PRE-CONDITIONS:** Phase 0 complete.
**SUGGESTED MODEL:** **Haiku / Flash / local.**

**FILES TO MODIFY:**

1. `/home/connorl/RemEx/RemEx.Android/app/src/main/java/com/clindsay94/remex/ui/components/HapticModifier.kt`:
   - **Read the file first.** Add three new public functions:

     ```kotlin
     fun View.hapticCommandSent() = performHapticFeedback(HapticFeedbackConstants.CONTEXT_CLICK)
     fun View.hapticCommandAcknowledged() = performHapticFeedback(HapticFeedbackConstants.CONFIRM)
     fun View.hapticCommandFailed() = performHapticFeedback(HapticFeedbackConstants.REJECT)
     ```

   - Or, if the existing module uses Compose's `LocalHapticFeedback`, add Compose-side equivalents:

     ```kotlin
     @Composable fun rememberCommandHaptics(): CommandHaptics { /* returns the three lambdas */ }
     ```

2. `/home/connorl/RemEx/RemEx.Android/app/src/main/java/com/clindsay94/remex/ui/screens/RemoteControlScreen.kt`:
   - For each command button, replace the single haptic call with the new two-stage flow:
     - On press: `hapticCommandSent()`.
     - On `commandResult.success`: `hapticCommandAcknowledged()`.
     - On `commandResult.failure` or timeout (3 s): `hapticCommandFailed()`.

**DO NOT TOUCH:** Files outside the haptic API and command UI.

**VERIFICATION:**

1. `./gradlew :app:assembleDebug` — `BUILD SUCCESSFUL`.
2. Manual: tap any command button on a connected device — feel the "sent" tick, then a confirming tick on success.
3. Disconnect network mid-command; observe the "failed" buzz after timeout.

**POST-CONDITIONS:** Two-stage haptics in place for all command UI.

---

## TRACK 2I — Quick Settings tile (Lock PC)

**ID:** `2I-quick-tile`
**GOAL:** Add a single Quick Settings tile that locks the connected RemEx PC.
**PRE-CONDITIONS:** Phase 0 complete.
**SUGGESTED MODEL:** **Haiku / Flash / local.**

**NEW FILES:**

1. `/home/connorl/RemEx/RemEx.Android/app/src/main/java/com/clindsay94/remex/tile/RemexLockTileService.kt`:

   ```kotlin
   package com.clindsay94.remex.tile

   import android.service.quicksettings.Tile
   import android.service.quicksettings.TileService
   import com.clindsay94.remex.RemexClientManager

   class RemexLockTileService : TileService() {
       override fun onClick() {
           super.onClick()
           if (!RemexClientManager.isConnected.value) {
               qsTile.state = Tile.STATE_INACTIVE
               qsTile.updateTile()
               return
           }
           RemexClientManager.sendCommand("Lock", emptyMap())
       }

       override fun onStartListening() {
           super.onStartListening()
           qsTile.state = if (RemexClientManager.isConnected.value) Tile.STATE_ACTIVE else Tile.STATE_INACTIVE
           qsTile.label = getString(com.clindsay94.remex.R.string.tile_lock_label)
           qsTile.updateTile()
       }
   }
   ```

**FILES TO MODIFY:**

1. `/home/connorl/RemEx/RemEx.Android/app/src/main/AndroidManifest.xml`:
   - Add inside `<application>`:

     ```xml
     <service
         android:name=".tile.RemexLockTileService"
         android:exported="true"
         android:label="@string/tile_lock_label"
         android:icon="@drawable/ic_lock"
         android:permission="android.permission.BIND_QUICK_SETTINGS_TILE">
         <intent-filter>
             <action android:name="android.service.quicksettings.action.QS_TILE"/>
         </intent-filter>
     </service>
     ```

2. `/home/connorl/RemEx/RemEx.Android/app/src/main/res/values/strings.xml`:
   - Add: `<string name="tile_lock_label">Lock PC</string>`. (Other locales filled by Track 3A.)

3. `/home/connorl/RemEx/RemEx.Android/app/src/main/res/drawable/ic_lock.xml`:
   - Standard lock vector drawable. Use `androidx.compose.material.icons.filled.Lock` exported via `vectorDrawable` or copy the SVG path.

**DO NOT TOUCH:** Any other manifest entry, any other service.

**VERIFICATION:**

1. `./gradlew :app:assembleDebug` — `BUILD SUCCESSFUL`.
2. Install, open the Quick Settings drawer, tap the edit pencil, find "Lock PC" tile, drag to active tray.
3. With RemEx connected: tap tile → host PC locks within ~1 second.
4. With RemEx disconnected: tile shows inactive state, tap is a no-op.

**POST-CONDITIONS:** Lock PC quick tile is registered and works.

---

# PHASE 3 — POLISH (sequential, after Phase 2)

---

## TRACK 3A — Localization regen

**ID:** `3A-locale-regen`
**GOAL:** Propagate new English strings from Phase 1/2 into all 8 non-English locales via the existing Python script.
**PRE-CONDITIONS:** All Phase 2 tracks that added strings are complete.
**SUGGESTED MODEL:** **Haiku / Flash / local** (mechanical).

**STEPS:**

1. `cd /home/connorl/RemEx && python3 scripts/generate_locale_files.py`
2. Verify output mentions all 8 locales (`values-es`, `values-fr`, `values-hi`, `values-in`, `values-pl`, `values-pt-rBR`, `values-tr`, `values-uk`).
3. **For .NET .resx files**, no equivalent script exists. Manually add the new keys to each `Strings.{lang}.resx` file using the English text as a placeholder. Mark each placeholder with a `<!-- NEEDS_TRANSLATION -->` comment so a human translator can find them later.

**VERIFICATION:**

1. `for d in /home/connorl/RemEx/RemEx.Android/app/src/main/res/values-*; do grep -c "name=\"pairing_title\"" "$d/strings.xml" || echo "MISSING $d"; done` — expect `1` for every locale.
2. `for f in /home/connorl/RemEx/Remex.Client/Localization/Strings.*.resx; do grep -c "PairingDialogTitle" "$f" || echo "MISSING $f"; done` — expect `1` for every file.

**POST-CONDITIONS:** Every new string is present in every locale file (placeholder is acceptable for non-English where unreviewed).

---

## TRACK 3B — Documentation updates

**ID:** `3B-docs`
**GOAL:** Update `README.md`, `SECURITY.md`, `docs/API_CONTRACTS.md`, `docs/ANDROID_SETUP.md` to reflect 2.0 changes.
**PRE-CONDITIONS:** All Phase 2 tracks complete.
**SUGGESTED MODEL:** **Haiku / Flash / local.**

**FILES TO MODIFY:**

1. `/home/connorl/RemEx/README.md`:
   - Replace the "Quick Install" Android section with: "Install from Google Play: <play store link, TBD>".
   - Replace any mention of "access key" with "PIN-based pairing".
   - Add a new section "What's new in 2.0" listing TLS, pairing, file transfer.
   - Update version badge.

2. `/home/connorl/RemEx/SECURITY.md`:
   - Rewrite "Known Security Considerations" section. Remove "AccessKey transmitted in plaintext"; replace with "TLS 1.3 + ECDH X25519 pairing + SPKI cert pinning. Self-signed certs trusted only after PIN-based or QR-based out-of-band binding."
   - Add: "TCP command port (8338) is TLS-wrapped but uses a weaker auth model — clients must have completed WSS pairing within the last 24 hours from the same IP. Document this trade-off."

3. `/home/connorl/RemEx/docs/API_CONTRACTS.md`:
   - Add a new section "Pairing protocol" documenting the steps from Track 1B.
   - Add a new section "File transfer protocol" documenting the message flow from Track 2A.
   - Add a new field to the message envelope spec: `protocolVersion: int`.

4. `/home/connorl/RemEx/docs/ANDROID_SETUP.md`:
   - Add a section on Crashlytics setup (`google-services.json` placement) if Track 2F was completed.
   - Add a section on the new `EncryptedSharedPreferences` dependency.

**VERIFICATION:** Manual review.

**POST-CONDITIONS:** Docs match the 2.0 reality.

---

## TRACK 3C — Tests

**ID:** `3C-tests`
**GOAL:** Add unit and integration tests for new Phase 1/2 functionality.
**PRE-CONDITIONS:** All Phase 1 and Phase 2 tracks complete.
**SUGGESTED MODEL:** Claude Sonnet 4.6 (uses test-engineer judgment).

**NEW TESTS:**

1. `/home/connorl/RemEx/Remex.Core.Tests/PairingMessagesSerializationTests.cs`:
   - Round-trip every new message type (PairingRequest, PairingResponse, PairingComplete, FileTransfer*).
   - Test that `RemexMessage.ProtocolVersion` defaults to 2 and round-trips.

2. `/home/connorl/RemEx/Remex.Host.Tests/PairingServiceTests.cs`:
   - Mock NSec key generation; verify HKDF-derived session key is deterministic given inputs.
   - Verify PIN HMAC matches expected value for known inputs.
   - Verify expired pairing is rejected.

3. `/home/connorl/RemEx/Remex.Host.Tests/CertificateServiceTests.cs`:
   - First call generates and persists a cert; second call reads the same cert.
   - SPKI hash is deterministic for the same cert.
   - `RegenerateAsync` produces a new cert with a different SPKI hash.

4. `/home/connorl/RemEx/Remex.Host.Tests/FileTransferHandlerTests.cs`:
   - Upload a 1 MB file, verify SHA-256 matches.
   - Cancel mid-transfer, verify partial file deleted.
   - Send a chunk for an unknown `transferId`, expect error response.

5. `/home/connorl/RemEx/Remex.Client.Tests/PinnedCertStoreTests.cs`:
   - Set, get, remove, list operations round-trip via temp directory.

6. `/home/connorl/RemEx/RemEx.Android/app/src/test/java/com/clindsay94/remex/PinnedHostStoreTest.kt`:
   - Robolectric-backed test of `EncryptedSharedPreferences` round-trip.

**VERIFICATION:** `cd /home/connorl/RemEx && dotnet test Remex.sln` — all green. `cd /home/connorl/RemEx/RemEx.Android && ./gradlew test` — all green.

**POST-CONDITIONS:** New code has test coverage.

---

## TRACK 3D — Installer updates

**ID:** `3D-installer`
**GOAL:** Update Inno Setup and Linux packaging scripts to ship the cert directory and any new files.
**PRE-CONDITIONS:** All Phase 1+2 tracks complete.
**SUGGESTED MODEL:** **Haiku / Flash / local.**

**FILES TO MODIFY:**

1. `/home/connorl/RemEx/installer/RemEx.iss`:
   - Add a `[Dirs]` section if absent:

     ```
     [Dirs]
     Name: "{commonappdata}\RemEx"; Permissions: users-modify
     ```

   - Update `AppVersion` references (Track 0A handles version bump; the installer reads from `Directory.Build.props`, so should be automatic — verify).

2. `/home/connorl/RemEx/installer/build-linux.sh`:
   - Update `install.sh` (inside the tarball) to create `/var/lib/remex/` with mode 0700 owned by the service user.

3. `/home/connorl/RemEx/installer/linux/remex-host.service`:
   - Add `ReadWritePaths=/var/lib/remex` to ensure the systemd sandbox lets the host write the cert.

**VERIFICATION:**

1. `bash /home/connorl/RemEx/installer/build-linux.sh` — produces `installer/Output/remex-host-v2.0.0-linux-x64.tar.gz`.
2. Extract, run `install.sh`, verify `/var/lib/remex/` created with mode 0700.

**POST-CONDITIONS:** Installers create the data directory the cert service needs.

---

# PHASE 2.1 + 2.2 — DEFERRED FEATURE TRACKS (ship in 2.1.0 / 2.2.0)

> The following are skeleton specs for tracks that **do not ship in 2.0.0** but are queued for 2.1 and 2.2. Each can be expanded to a full track-spec when the time comes.

---

## TRACK 2.1A — Bidirectional clipboard sync

**Goal:** Sync clipboard contents bidirectionally between client and host.
**Files (host):** `Remex.Host/Services/Clipboard/IClipboardService.cs`, `WindowsClipboardService.cs` (uses `AddClipboardFormatListener` via P/Invoke on a hidden message-only window), `LinuxClipboardService.cs` (polls `xclip -o` or `wl-paste` every 500ms).
**Messages:** `MessageTypes.ClipboardSync`, `RemexMessage.ClipboardData`.
**Files (clients):** Hook into Avalonia clipboard API; on Android, hook `ClipboardManager`.

## TRACK 2.1B — Multi-host management

**Goal:** Save and switch between multiple RemEx hosts.
**Files (Android):** Replace `SettingsManager.ConnectionPreferences` with `List<HostProfile>`. Update `ConnectionScreen` to a host picker (tap to connect, swipe to delete, long-press to edit). Add `HostProfilesRepository`.
**Files (Desktop):** Mirror in `Remex.Client/Services/HostProfilesService.cs` and update `ConnectionViewModel`.

## TRACK 2.1C — Sensor threshold alerts

**Goal:** Configurable per-sensor thresholds; notification fired when crossed.
**Files (Desktop):** Extend `SensorViewModel` with `AlertThreshold` and `AlertActive` properties; tray balloon on cross.
**Files (Android):** `RemexConnectionService` emits a `NotificationCompat` alert per crossed threshold.
**Files (host):** None (host already streams telemetry; threshold logic is client-side).

## TRACK 2.1D — Virtual keyboard overlay (Android remote desktop)

**Goal:** On-screen keyboard toggle for the remote desktop screen.
**Files:** `RemoteDesktopScreen.kt` adds a toggleable `BasicTextField` overlay; keystrokes feed into the existing `RemexCoreClient.SendInputEvent` path.

## TRACK 2.1E — Landscape-first remote desktop

**Goal:** Lock remote desktop screen to landscape with a collapsible toolbar.
**Files:** `MainActivity.kt` declares per-route orientation; `RemoteDesktopScreen.kt` gets a collapsing top app bar.

## TRACK 2.1F — Wake-on-LAN auto-fallback

**Goal:** When connecting to a saved host that is offline, automatically broadcast a WoL magic packet and retry the connection.
**Files:** `RemexClientManager.kt` — on connection failure, look up MAC from `HostProfile`, send WoL via existing `WakeOnLanService` (already implemented; only the auto-trigger wiring is new).

## TRACK 2.2A — Audio streaming

**Goal:** Stream host audio to the client (Opus 48 kHz mono 64 kbps).
**Files (host):** `Remex.Host/Services/Audio/IAudioCaptureService.cs`, `WindowsAudioCaptureService.cs` (NAudio + WASAPI loopback), `LinuxAudioCaptureService.cs` (spawn `parec` for PulseAudio or `pw-cat -p --record` for PipeWire).
**Encoder:** Concentus 2.0.0 NuGet (managed Opus).
**Messages:** `MessageTypes.AudioStreamStart`, `AudioFrame`, `AudioStreamStop`.
**Client decode:** Concentus on .NET; on Android, use the system Opus decoder via `MediaCodec`.

## TRACK 2.2B — Multi-monitor support

**Goal:** Enumerate host monitors, allow client to pick which to view.
**Files (host):** Extend `DxgiDesktopCapture.cs` to loop `EnumOutputs` (currently hardcoded to output 0). Linux: extend `LinuxScreenCaptureService.cs` xrandr parser to capture all monitor geometries, not just primary.
**Protocol:** `DesktopMeta.Monitors[]` array; `DesktopConfig.MonitorIndex` field (default 0). Already declared in §6 design decisions.

---

# 8. Verification: end-to-end checklist before shipping 2.0.0

Run all of the following. Every line must pass.

**Build:**

- [ ] `cd /home/connorl/RemEx && dotnet build Remex.sln -c Release` exits 0.
- [ ] `cd /home/connorl/RemEx/RemEx.Android && ./gradlew :app:bundleRelease` exits 0.
- [ ] `cd /home/connorl/RemEx/RemEx.Android && ./gradlew :app:assembleRelease` exits 0.

**Tests:**

- [ ] `cd /home/connorl/RemEx && dotnet test Remex.sln` — all green.
- [ ] `cd /home/connorl/RemEx/RemEx.Android && ./gradlew test` — all green.

**Static checks:**

- [ ] `grep -r "AccessKey\|accessKey" /home/connorl/RemEx/Remex.Host /home/connorl/RemEx/Remex.Client /home/connorl/RemEx/RemEx.Android/app/src/main/java | grep -v PinnedHostStore | grep -v "Strings\." | grep -v ".designer.cs"` — expect no output.
- [ ] `grep usesCleartextTraffic /home/connorl/RemEx/RemEx.Android/app/src/main/AndroidManifest.xml` — expect a match (set to `"false"`).
- [ ] `grep "1.5.0-alpha" /home/connorl/RemEx/RemEx.Android/gradle/libs.versions.toml` — expect no output.
- [ ] `grep "targetSdk = 36" /home/connorl/RemEx/RemEx.Android/app/build.gradle.kts` — expect no output.
- [ ] `unzip -l /home/connorl/RemEx/RemEx.Android/app/build/outputs/bundle/release/*.aab | grep "lib/"` — only `lib/arm64-v8a/...` (no other ABIs unless explicitly added).

**Manual smoke test (laptop + Android):**

- [ ] Fresh install of host on a Linux laptop. First start: cert generated and persisted; PIN displayed in console.
- [ ] Fresh install of Android app. Open, scan QR or enter host + port. PairingScreen appears. Enter PIN. Pairing succeeds.
- [ ] Reopen app. Connect again. No PairingScreen (SPKI pin recognized).
- [ ] Browse remote files, upload a 100 MB file, download it back. SHA-256 of original == downloaded.
- [ ] Open Quick Settings, tap Lock PC tile. Host PC locks.
- [ ] Force a JNI failure (rename `libRemexCore.so` in `/data/app/.../lib/arm64/`). Reconnect. App shows error, does not crash.
- [ ] Run for 10 min in background with screen off. Reconnect. Foreground service maintained connection.

**Pre-submission gate:**

- [ ] Sign release AAB with the same keystore used in 1.x.
- [ ] Upload to Play Console internal testing track.
- [ ] Wait for Google Play pre-launch report. Resolve any blockers.
- [ ] Verify the listing's "Data safety" section reflects encrypted transit.
- [ ] Promote to closed testing → open testing → production over a 1-week ramp.

---

# 9. Conflict matrix (which tracks share files)

If two tracks below share a file, **the later track in the table must wait for the earlier one OR they must coordinate via the `git rebase` of small, atomic commits.**

| File | Earliest track that touches it | Other tracks that touch it |
|---|---|---|
| `RemexMessage.cs` | 0B | (none — all changes folded into 0B) |
| `MessageTypes` (in `RemexMessage.cs`) | 0B | (none) |
| `HostBootstrapper.cs` | 1A (TLS), 1B (delete access key), 1E (QR endpoint) | 2A (file transfer DI), 2F (Crashlytics N/A — Android only) |
| `PingPongHandler.cs` | 1B (pairing dispatch) | 2A (file-transfer dispatch) |
| `RemexCoreClient.kt` | 0B (declare), 1D (pairing impl), 1E (AOT exports support) | 2A (file transfer methods), 2E (try/catch wrappers) |
| `RemexClientManager.kt` | 1D (remove access key, integrate pairing) | 2A (file transfer state), 2E (Result handling), 2I (sendCommand for Lock tile) |
| `AndroidManifest.xml` | 2D (cleartext + network security) | 2G (battery permission), 2I (tile service) |
| `app/build.gradle.kts` | 2D (R8/AAB/ABI) | 2F (Crashlytics plugins/deps), 2B (H-5 targetSdk fix) |
| `gradle/libs.versions.toml` | 2B (H-6 material3 stable) | 2F (firebase-bom version constant) |
| `SettingsManager.kt` | 1D (remove access key field) | (in 2.1: multi-host refactor) |

**Recommended commit cadence:** each track = 1 PR with 1–3 commits. Track owners rebase on `main` daily.

---

# 10. Open questions for the user

These the planner could not decide. Answer before kicking off the work.

1. **Firebase Crashlytics**: do you want to set this up for 2.0, or skip and revisit in 2.0.1? (Setup requires creating a Firebase project and dropping `google-services.json`.)
2. **ABI filters**: arm64-v8a only, or also x86_64 for Chromebook coverage?
3. **Cert lifetime**: 5 years matches the locked decision. Change to a different value if you want a different cadence.
4. **Should Phase 3D installer changes also include a Windows-side cert directory?** (Currently I specified `%ProgramData%\RemEx`; if you want a different location, say so before 1A starts.)
5. **2.0 scope cut**: do you want the conservative 2.0 (security + file transfer only) or the larger one (also clipboard + multi-host + alerts)? Default in this plan: conservative. If you want larger, we add Tracks 2.1A, 2.1B, 2.1C to Phase 2 and add ~3-4 weeks.

---

# 11. Critical files summary (reference for any agent)

These paths come up everywhere — a lighter agent should bookmark them:

- Host bootstrap & TLS: `/home/connorl/RemEx/Remex.Host/HostBootstrapper.cs`
- WebSocket main handler: `/home/connorl/RemEx/Remex.Host/Handlers/PingPongHandler.cs`
- WebSocket desktop handler: `/home/connorl/RemEx/Remex.Host/Handlers/RemoteDesktopHandler.cs`
- TCP command port: `/home/connorl/RemEx/Remex.Core/Services/Network/RemexNetworkListener.cs`
- mDNS: `/home/connorl/RemEx/Remex.Host/Services/Network/MdnsAdvertisingService.cs`
- Message envelope: `/home/connorl/RemEx/Remex.Core/Messages/RemexMessage.cs`
- Serializer context: `/home/connorl/RemEx/Remex.Core/Serialization/RemexJsonSerializerContext.cs`
- Android JNI bridge: `/home/connorl/RemEx/RemEx.Android/app/src/main/java/com/clindsay94/remex/RemexCoreClient.kt`
- Android client manager: `/home/connorl/RemEx/RemEx.Android/app/src/main/java/com/clindsay94/remex/RemexClientManager.kt`
- Android settings: `/home/connorl/RemEx/RemEx.Android/app/src/main/java/com/clindsay94/remex/data/SettingsManager.kt`
- Android manifest: `/home/connorl/RemEx/RemEx.Android/app/src/main/AndroidManifest.xml`
- Android build: `/home/connorl/RemEx/RemEx.Android/app/build.gradle.kts`
- ProGuard: `/home/connorl/RemEx/RemEx.Android/app/proguard-rules.pro`
- Desktop client connection: `/home/connorl/RemEx/Remex.Client/ViewModels/ConnectionViewModel.cs`
- Desktop client remote desktop: `/home/connorl/RemEx/Remex.Client/Services/Network/RemoteDesktopService.cs`
- Localization regen: `/home/connorl/RemEx/scripts/generate_locale_files.py`

---

# 12. How to dispatch this plan to multiple agents

Suggested workflow:

1. **You (the user)** approve this plan via `ExitPlanMode`.
2. **Open a strong-model session** (Sonnet 4.6 or Opus). Hand it tracks 0A, 0B, 0C, then 1A, 1B, 1C, 1D, 1E in sequence. This is one continuous session, ~2-4 hours of agent work.
3. **Once Phase 1 is merged on `main`**, fan out to Phase 2 in parallel:
   - Tab 1 (Sonnet via Copilot CLI): Track 2A (file transfer) — biggest track, give it the strongest model.
   - Tab 2 (Sonnet on Claude Code): Track 2B (review-report fixes).
   - Tab 3 (Sonnet via Copilot CLI): Track 2E (JNI hardening).
   - Tab 4 (Sonnet on Claude Code): Track 2F (Crashlytics) — only if Firebase is set up.
   - Tab 5 (Gemini CLI): Track 2D (release engineering).
   - Tab 6 (Haiku / Flash / local): Track 2G (battery onboarding).
   - Tab 7 (Haiku / Flash / local): Track 2H (haptics).
   - Tab 8 (Haiku / Flash / local): Track 2I (Quick Settings tile).
4. **Merge Phase 2 to `main` as each track lands.** Rebase remaining tracks daily.
5. **Run Phase 3 sequentially** in a single agent session (any model — Haiku is fine for 3A/3B/3D, Sonnet for 3C tests).
6. **Run §8 verification checklist manually.** This step is yours, not an agent's.

For each agent invocation, copy the entire track section (from `## TRACK X` through the next `---`) into the agent's first message. The track is self-contained.

---

# 13. Anti-hallucination guardrails for lighter models

Insert this preamble before handing any track to a lighter model:

> "You are executing a single track from a multi-agent execution plan. Do not modify files outside the `FILES TO MODIFY` and `NEW FILES` lists. Do not skip or paraphrase the verification commands — run them and quote the output. If a verification command fails, do not declare done; report the failure and stop. Do not invent file paths, line numbers, or function names — every one in this track is real and was checked by the planner. If you find a discrepancy, escalate; do not improvise. Do not add features, refactors, or 'cleanup' the track did not ask for. Confirm pre-conditions are met before any edit. The user will read your verification output, not your prose summary — so quote real command output."

---

*End of plan. Total: ~14 tracks for 2.0.0 + 6 deferred for 2.1/2.2. Owner-recommended cadence: Phase 0+1 in week 1, Phase 2 in weeks 2–4, Phase 3 + manual verification in week 5, Play Store ramp in week 6.*
