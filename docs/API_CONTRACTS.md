# RemEx API Contracts

Full API documentation for the communication protocols used in RemEx.

---

## 0. Security & Protocol Versioning

### Protocol Version

RemEx 2.0 introduces the `protocolVersion` field. Clients and hosts MUST support `protocolVersion: 2` or higher. Legacy 1.x messages without this field are rejected.

### Encryption (TLS 1.3)

All network communication is encrypted via TLS 1.3. 
- **WebSocket:** Use `wss://`
- **TCP:** Use `SslStream` wrapping the TCP socket
- **HTTP:** Use `https://`

---

## 1. WebSocket Telemetry & Remote Execution (`/ws`)

The WebSocket endpoint provides real-time bidirectional communication. It is primarily used for streaming hardware telemetry and issuing remote commands between the UI client and the host service.

**Endpoint:** `wss://<host>:<port>/ws` (Default port: 5005)

### Envelope: `RemexMessage`

All messages exchanged over the WebSocket use the `RemexMessage` JSON envelope.

| Property | Type | Description |
| :--- | :--- | :--- |
| `type` | `string` | **Required.** Message type discriminator (e.g., `"ping"`, `"telemetry"`, `"command"`). |
| `protocolVersion` | `int` | **Required.** Must be `2` for RemEx 2.0. |
| `clientId` | `string?` | Stable client installation identifier. Required for reconnectable paired sessions so the host can bind a later socket to the client that completed PIN pairing. |
| `timestamp` | `long?` | UTC ticks when the message was created, used for latency measurement. |
| `telemetry` | `TelemetryPayload?` | Optional payload attached for telemetry streaming. |
| `commandAction` | `string?` | Command action name (e.g., `"Shutdown"`, `"Lock"`). |
| `commandParameters` | `Dictionary<string, string>?` | Command parameters (e.g., for WoL MAC address). |
| `commandSuccess` | `bool?` | Whether the command succeeded (for response messages). |
| `commandMessage` | `string?` | Response message from command execution. |
| `pairingRequest` | `PairingRequest?` | Payload for the pairing handshake. |
| `pairingResponse` | `PairingResponse?` | Payload for the pairing handshake. |
| `pairingComplete` | `PairingComplete?` | Payload for the pairing handshake. |
| `fileTransferStart` | `FileTransferStart?` | Payload for file transfer initiation. |
| `fileTransferChunk` | `FileTransferChunk?` | Payload for file transfer data. |
| `fileTransferEnd` | `FileTransferEnd?` | Payload for file transfer completion. |
| `fileBrowseRequest` | `FileBrowseRequest?` | Request to browse remote files. |
| `fileBrowseResponse` | `FileBrowseResponse?` | Response with remote file list. |


### Message Types (New in 2.0)

- `pairing_request`: Initiate ECDH pairing.
- `pairing_response`: Host ephemeral key + PIN HMAC.
- `pairing_complete`: Client PIN HMAC acknowledgement.
- `file_browse_request`: Request a directory listing.
- `file_browse_response`: Directory listing response.
- `file_transfer_start`: Initiate an upload/download.
- `file_transfer_chunk`: Data packet (base64 encoded).
- `file_transfer_end`: Signal transfer completion and verify hash.
- `file_transfer_progress`: Update transfer status.

---

## 2. Pairing Protocol (Handshake)

RemEx 2.0 uses an ECDH (NIST P-256) key exchange with a 6-digit PIN out-of-band binding.

1. **Client → Host:** `pairing_request` with `ClientPublicKeyBase64` and `clientId`.
2. **Host → Client:** `pairing_response` with `HostPublicKeyBase64`, `HostId`, `HostName`, `CertificateSpkiHashBase64`, and `PinHmacBase64`.
   - Host displays 6-digit PIN to the user.
   - `PinHmac` is `HMAC-SHA256(SessionKey, PIN)`.
3. **Client → Host:** `pairing_complete` with `ClientPinHmacBase64` and the same `clientId`.
   - `ClientPinHmac` is `HMAC-SHA256(SessionKey, "ack:" + PIN)`.
4. **Host → Client:** `command_response` with `Success=true` if pairing is accepted.

Once paired, the client pins the `CertificateSpkiHashBase64` and uses it to validate the host in future TLS handshakes. The client must also keep sending the same top-level `clientId` on subsequent WebSocket messages so the host can recognize a fresh session socket as belonging to the paired client.

---

## 3. Remote File Transfer Protocol

### Shared Roots
The host defines "Shared Roots" (e.g., "Downloads", "Documents"). Clients browse and transfer files relative to these roots.

### Message Flow (Download)
1. **Client → Host:** `file_browse_request` to locate the file.
2. **Client → Host:** `file_transfer_start` with `direction="download"`.
3. **Host → Client:** `file_transfer_chunk` messages until the file is exhausted.
4. **Host → Client:** `file_transfer_end` with the full file SHA-256 hash.
5. **Client:** Verifies the received chunks against the hash.

### Message Flow (Upload)
1. **Client → Host:** `file_transfer_start` with `direction="upload"` and total file hash.
2. **Client → Host:** `file_transfer_chunk` messages.
3. **Host:** Updates `file_transfer_progress` periodically.
4. **Client → Host:** `file_transfer_end` signal.
5. **Host:** Verifies the received file hash and responds with success/failure.

### Folder Manifest Paging (`file_manifest_request` / `file_manifest_response`)
A folder download first lists the folder's subtree in pages. Each entry's `relativePath` is
**root-relative** (it includes the requested folder's own path, e.g. `Photos/2026/a.jpg` for a request
on `Photos`), so it can be fed straight into a download request. Order is ordinal by name, pre-order,
with directories emitted in their own right.

`nextCursor` is opaque to the requester, which echoes it back unchanged with the same root and folder.
Both hosts (the PC's `FileTransferService` and the phone's `FileHostHandler`) format and read it the
same way, so the meaning is fixed:
- Format: `{entriesEmittedSoFar}|{lastPath}`, with the count in invariant-culture digits.
- `lastPath` is the **root-relative** path of the last entry on the page, base prefix included (the
  same string as that entry's `relativePath`). The host strips `{folder}/` before resuming the walk.
- A cursor that is malformed, or whose path does not lie under the requested folder, restarts the
  listing from the first page (with totals) rather than failing.
- `nextCursor` is absent on the last page and when the listing was truncated at the total-entry cap.

### Live preview: range reads (`file_read_range_request` / `file_read_range_response`, 2026-10-08)
Up to 1 MiB (`FileTransferLimits.ReadRangeMaxBytes`) of one file, answered on the control channel in base64 so
the reply stays well under the 4 MB message limit. Capability-flagged by `fileCapabilities.readRange`.

```jsonc
// requester → host
{ "requestId": "…", "rootId": "…", "relativePath": "Logs/app.log",
  "offset": 0, "length": 1048576,   // 1 … 1 MiB
  "fromEnd": false }                // true: the LAST `length` bytes (live tail)
// host → requester
{ "requestId": "…", "offset": 0, "dataBase64": "…", "fileSize": 52344,
  "modifiedUtc": 1759900000000, "eof": true, "errorMessage": null }
```

- Resolved exactly as a download's source is (PC: `ResolvePath` / a consented volume; phone: `facade.resolve`).
  A directory, a missing file, an unshared root, a negative offset or a length outside 1 … 1 MiB is answered with
  an `errorMessage`, never silence.
- An offset at or past the end returns empty `dataBase64` with `eof: true`, not an error: a tail that polls after a
  truncation sees "nothing new", and `fileSize` going down tells it to start over.
- The PC opens with `FileShare.ReadWrite | Delete`, so a log another program is still writing can be tailed.
- The phone answers off its control-message collector and seeks with the provider's file descriptor when it can.
  A provider that reports size 0 gets `fileSize` = at least what was read.
- Preview limits shared by both clients: text 2 MiB from the start, or the last 256 KiB when tailing (polled every
  2 s while visible); images up to 40 MiB, fetched in 1 MiB reads.

`file_hash_request` (existing) is now also answered by the phone (`fileCapabilities.hash`). The wire stays Base64;
screens show lowercase hex (`HashFormat`, C# and Kotlin twins sharing one set of test vectors).

### The PC browsing a paired phone (relay, RemEx-xt0af)
The PC's own File Transfer screen can browse a paired phone. **The connection direction does not
change**: the phone dials `/ws` and `/ws/files` as always, and the PC opens no socket to it. What is
new is that the host (`PhoneFileRelay`) sends a fixed set of **existing** request types DOWN a paired
phone's live, authenticated `/ws` session, and the phone's file host (`FileHostHandler.kt`) answers
them exactly as it would answer any peer. No message type was added.

**Relayed requests (host → phone), and nothing else:** `file_roots_request`, `file_browse_request`,
`file_volumes_request`, `file_search_request`, `file_manifest_request`, `file_metadata_request`,
`file_thumbnail_request`, (2026-10-08) `file_read_range_request` and `file_hash_request`, and (RemEx-fgmne)
`file_manage_request`. Root management (`file_root_manage_request`: which folders the phone shares is only ever
the phone's decision) and the legacy v2 transfer are refused by the relay before they reach the wire.

Rules the host enforces, each pinned by `PhoneFileRelayTests`:
- The target must be **paired** (`PairedClientRegistry`) **and connected** on a session that proved its
  identity; otherwise the PC side gets `PhoneNotConnectedException`.
- A relayed request carries **no `clientId`** (it is nulled on the way out).
- A reply (`file_*_response` of the ten types above) is accepted only from a session with
  `identityProven && !isLoopback`, only for a request this host sent to **that same client id**, and is
  delivered only to the relay connection that asked. Unrequested replies are dropped; nothing is
  broadcast. Loopback can never answer for a phone (RemEx-4215).
- A phone that disconnects, or does not answer within the relay timeout, gets every pending request
  completed with a failure reply (the reply type's `errorMessage`), so nothing hangs.

**Transfers.** A PC-started download from the phone is an ordinary `file_transfer_offer` with
`mode: "download"` sent to the phone (`destRoot` = the phone's root id, `destRelativePath` = the
source FOLDER, `fileName` = the file); the phone answers `file_transfer_ready`, streams data frames on
its own `/ws/files`, drains, and sends `file_transfer_complete`; the host verifies, lands the file in
the folder the person picked, and answers `file_transfer_result`. A PC-started upload is a
`file_transfer_offer` with `mode: "upload"` into a folder the phone shares for writing (the share is the
consent, so the phone raises no prompt); the host streams, drains, sends `file_transfer_complete`, and
waits for the phone's `file_transfer_result`. Verdicts are accepted only from the phone the transfer was
started with. PC-started pulls are not resumable and are not written to `transfer_queue.json`.

**Consent** is the phone's own **Access from your PC** settings: the shared folders, plus whole-device
browsing only while that switch is on. The phone resolves a root id only if it is one of those right now
(`SharedPathPolicy`), and refuses `.`/`..` path names.

**Changing files on the phone (RemEx-fgmne).** `file_manage_request` (`delete`, `rename`, `move`, `copy`,
`mkdir`) is relayed only under two further conditions, both about the person's second switch on the phone,
**Let your PC change files** (off by default, under Access from your PC):
- *The capability.* The phone reports the switch in its roots reply: `fileCapabilities.pcChanges` (boolean,
  additive, default false). A phone that predates the switch never sends it, which reads as "no". The PC's
  relay remembers it per connection from the phone's own proven roots reply (a loopback or unproven reply
  never updates it) and refuses `file_manage_request` with `PhoneChangesNotAllowedException` until it is true.
  The roots reply also zeroes `canRename`/`canMove`/`canDelete` on every root while the switch is off, so the
  buttons stay hidden. The PC re-reads it on every roots reload (the screen's Reload shared folders button).
- *The phone says no again.* The phone re-reads the switch on EVERY request, before it resolves anything, and
  answers with `success: false` and a plain `errorMessage` when it is off. A read-only share, a root that is not
  shared, a path with `..`, a backslash, a NUL or an empty segment, a name over 255 characters or containing a
  separator, and any attempt to delete, rename, move or copy the shared folder itself are refused the same way
  (the shared folder can still be given a new sub-folder). Manage requests are refused on the whole-device
  (full-browse) volume: only folders the person shared by name are writable from the PC. A copy or move onto
  the source itself, or onto a folder, is refused; a replace copies to a temporary sibling and swaps it in
  only after it fully landed, and copies run in the background so they never hold up other requests. The
  phone's refusal text is fixed plain English and never carries a path or provider error.
- *The PC checks first too.* Before the wire the relay validates the operation (one of the five), the root id,
  every new name (`FilePathValidation.IsValidRemoteName`: at most 255 UTF-8 bytes, no separator, no control
  character, no Unicode format character such as U+202E or U+200B) and every path
  (`IsValidRemoteRelativePath`): the same rules the phone applies. Copy and move are not subject to the 90-second request timeout: they stream the
  whole file on the phone before it answers, so they wait up to 30 minutes.

**Reading a phone's files: live preview and hashing (2026-10-08 redesign).** `file_read_range_request` and
`file_hash_request` are relayed READ-ONLY. They need nothing from *Let your PC change files*; the phone resolves
them through the same `facade.resolve` (and so the same `SharedPathPolicy` check against the folders shared right
now) as a browse, on every request. Before the wire the relay checks the root id, the path
(`IsValidRemoteRelativePath`, and never the shared folder itself) and, for a range read, the numbers
(`FileReadRangeValidation`). A hash waits up to 30 minutes (the phone hashes the whole file first); a range read
keeps the 90-second backstop. The phone advertises both with `fileCapabilities.readRange` / `hash` (additive,
default false); a PC hides preview and Compute SHA-256 for a phone that does not.

**Folder upload to a phone** is not a new request either: the PC makes each folder with `mkdir`
(`relativePath` = the parent, `newName` = the folder, shallowest first, a folder that already exists is fine),
then uploads each file as an ordinary `file_transfer_offer` with `mode: "upload"` into that folder. It is offered
only while the phone allows changes.

---

## 4. TCP Command Ingress (External Network Listener)

The external TCP listener is encrypted via TLS 1.3 (server-only certificate) and is **default-deny**: it
authenticates every command against the paired-client registry (PROTO-1 / RemEx-htt).

**Endpoint:** TCP Socket on Port `8338` (Configurable via `Remex:CommandPort`)

⚠️ **Security Warning:** Because the 8338 channel uses server-only TLS, the transport cannot identify the
caller. Authentication is therefore performed at the application layer: every `CommandRequest` **must**
carry a `ClientId` that has completed pairing over `/ws` and is present in the host's paired-client
registry. A request with a missing or unrecognized `ClientId` is rejected with an `Unauthorized`
`CommandResponse` and the connection is closed — **no power action is executed**. External automation
scripts that drove 8338 before 2.0 must be updated to pair first and then include their paired `ClientId`
on every command; an unauthenticated sender no longer works.

### Request Payload: `CommandRequest`

**The request may not exceed 64 KB.** The host reads the length prefix, refuses anything larger, and
closes the connection *without sending a `CommandResponse`* — so an over-framed request looks like a
bare disconnect rather than an error, and that is worth knowing before debugging one. The limit exists
because the buffer is allocated from the declared length before the sender has been authenticated
(RemEx-ga503). It is far above anything this port dispatches: the widest command here is `WAKEONLAN`
with its MAC, broadcast address, port and delay.

The client must send a UTF-8 encoded JSON string matching the following structure:

```json
{
  "Action": "string",
  "Parameters": {
    "Key": "Value"
  },
  "ClientId": "<paired-client-id>"
}
```

> **Note:** No first-party RemEx client uses the 8338 channel — the Android app connects over `/ws`
> (port 5005) and the dashboard UI runs *in the same process* as the host (so it uses no network
> channel at all). Port 8338 exists solely for third-party/external script ingress, which is why the
> `ClientId` requirement is a documentation and integration concern rather than a coordinated client
> release.

---

## 5. Local IPC — removed in 2.0

RemEx 2.0 merged the dashboard UI and the host into a **single process**. The former
`RemExLocalIPC` / `RemExHostControl` named pipes and the `LocalIpcServerService` are **gone** — the
UI now resolves host services in-process through dependency injection (`EmbeddedHostServiceLocator`).
There is no local IPC socket or pipe left to document, secure, or connect to. (See CHANGELOG entry
for RemEx-aep.)

---

## 6. WebSocket Remote Desktop (`/ws/desktop`)

**Endpoint:** `wss://<host>:<port>/ws/desktop` (Default port: 5005)

Requires TLS 1.3 and a completed pairing session.

### Video Streaming Pipeline (v2.0+)

The remote desktop channel auto-negotiates between two streaming modes:

| Mode | Description | Fallback condition |
| :--- | :--- | :--- |
| **H.264 (hardware)** | Host encodes frames with NVENC / QSV / AMF (Windows) or VAAPI / libx264 (Linux). Client decodes via `MediaCodec` hardware decoder. Zero-copy surface delivery via `TextureView`. | Default when FFmpeg + hardware encoders are available |
| **MJPEG** | High-performance JPEG frame stream. Lower CPU than H.264 software encode. | FFmpeg absent, no hardware encoder, or H.264 negotiation fails |

#### Frame Packet Format (H.264 mode)

Frames are sent as raw Annex B NAL units, sliced using Access Unit Delimiter (`0x00 0x00 0x00 0x01 0x09`) markers for sub-millisecond delivery. Each WebSocket binary message contains one complete access unit.

#### Frame Packet Format (MJPEG mode)

Each WebSocket binary message is a complete JPEG frame. The host appends a lightweight header:

```
[4 bytes: frame width (big-endian int32)]
[4 bytes: frame height (big-endian int32)]
[remaining bytes: JPEG data]
```

#### Pointer / Input Events

Input events (mouse move, click, scroll, keyboard) are sent from client → host as JSON messages over the same `/ws/desktop` connection using the flattened structure introduced in 2.0:

```json
{
  "type": "pointer_batch",
  "events": [
    { "kind": "move", "x": 0.42, "y": 0.61 },
    { "kind": "click", "button": 1, "x": 0.42, "y": 0.61 }
  ]
}
```

---

## 7. REST / Minimal APIs

**Endpoint:** `https://<host>:<port>/` (Default port: 5005)

### Health Check (`/`)
- **Method:** `GET`
- **Response:**
  ```json
  {
    "service": "Remex.Agent",
    "status": "running"
  }
  ```

### Pairing QR (`/pairing-qr`)
- **Method:** `GET`
- **Description:** Returns a JSON payload for the Android app to scan.
- **Response:**
  ```json
  {
    "host": "string",
    "port": 5005,
    "hostId": "string",
    "spkiHashBase64": "string (base64)"
  }
  ```

---

## 8. Routines (3.0)

A routine is one trigger followed by ordered steps. The phone is the only editor. Routines whose trigger
happens on the PC (`pc.idle`, `pc.session`, `pc.sensor`) are synced to that PC and run there; routines
whose trigger happens on the phone run on the phone and ask the PC to carry out the steps that need it.
Design: `docs/specs/2026-09-26-routines-design.md` §7. Payload records: `remex.core/Messages/Routines/RoutinePayloads.cs`.

**Transport.** Every routine message is a `RemexMessage` on the existing authenticated `/ws` channel.
`protocolVersion` stays `2`; the feature is additive and advertised through capabilities. **Nothing about
routines is handled on the TCP 8338 listener** (section 4), which stays power-verbs-only.

**Gating.** Each phone-to-host type is pairing-gated like `theme_sync` and scoped to the identity the
connection *proved*: the owner of a sync, run, cancel or ack is the connection's own `clientId`, never a
payload field. A connection with no proven identity (loopback) is ignored and logged. The handlers run
detached from the socket reader and never throw into it; a bad payload is answered or ignored, never a
closed socket.

**Capabilities.**

| Record | Field | Type | Meaning |
| :--- | :--- | :--- | :--- |
| `HostCapabilities` | `supportsRoutines` | `bool` | The PC understands the messages below. Absent: the phone sends none, and a phone-run step that needs the PC fails `pc_too_old` (it never falls back to `command`, which would skip the countdown) |
| | `routineSchemaVersion` | `int` | Highest routine schema the PC validates. `0` = none |
| | `routinePowerVerbs` | `string[]` | Power verbs this PC can run. Never `WAKEONLAN` |
| `ClientCapabilities` | `supportsRoutines` | `bool` | The PC never sends a `routine_*` message to, or queues one for, a client without it. An older phone's router would drop them silently |
| | `routineSchemaVersion` | `int` | Phone's schema. `0` = none |

**Envelope slots.** One optional property per type, all camelCase:

| `type` | Direction | Envelope property | Payload record |
| :--- | :--- | :--- | :--- |
| `routines_sync` | phone → host | `routinesSync` | `RoutinesSyncPayload` |
| `routine_sync_result` | host → phone | `routineSyncResult` | `RoutineSyncResultPayload` |
| `routine_step_request` | phone → host | `routineStepRequest` | `RoutineStepRequestPayload` |
| `routine_step_result` | host → phone | `routineStepResult` | `RoutineStepResultPayload` |
| `routine_notify` | host → phone | `routineNotify` | `RoutineNotifyPayload` |
| `routine_notify_ack` | phone → host | `routineNotifyAck` | `RoutineNotifyAckPayload` |
| `routine_run_report` | host → phone | `routineRunReport` | `RoutineRunReportPayload` |
| `routine_cancel` | phone → host | `routineCancel` | `RoutineCancelPayload` |
| `routine_run_request` | phone → host | `routineRunRequest` | `RoutineRunRequestPayload` |

Every host → phone type starts with `routine_`. The Android native router forwards that whole prefix to
Kotlin in one place (`REGRESSION-GUARDS.md`, "Wire protocol and native message routing"); a new host →
phone routine type must keep the prefix or it is dropped without a trace.

Every payload field is optional at the JSON layer. Lists skip null elements, and a routine or step with
an unknown `type` is kept as a malformed entry that gets its own rejection instead of failing the whole
message.

### `routines_sync` (phone → host)

The phone's **full** set of PC-run routines for this PC, not a delta. Sent after the connection is
authenticated and `host_info` shows `supportsRoutines`, after every edit that changes this PC's set or
the pause flag, and as the forget flush when the phone forgets this PC.

| Property | Type | Description |
| :--- | :--- | :--- |
| `schemaVersion` | `int` | Phone's routine schema version |
| `revision` | `long` | Phone's monotonic revision for this PC, starting at 1 |
| `paused` | `bool` | Phone-side Pause all for this PC. The PC shows "Paused from <phone>" |
| `routines` | `Routine[]` | The full PC-run set for this PC, 0 to 16 routines (spec §6.6 for the shape) |
| `runCursor` | `long` | Highest host run `seq` the phone has stored. `0` = none |
| `forget` | `bool` | `true` only in the forget flush: the PC deletes this owner's routines, history and queued messages |
| `sentAtUnixMs` | `long` | Display only |

Host processing, in this order:

1. **Size.** Over 64 KB serialized: `payload_too_large`, answered at once.
2. **Start-up.** A sync that arrives before the PC has loaded its store and probed its idle and session
   sources waits up to 10 s, then is answered `rate_limited` with nothing applied. The phone retries.
3. **Coalescing.** At most one sync per 2 s per phone is processed; a newer sync from the same phone
   replaces a waiting one, and the replaced one gets no reply.
4. **Block.** The PC user blocked this phone: `blocked_by_pc`.
5. **Schema.** `schemaVersion` newer than the PC's: `schema_too_new`.
6. **Forget.** Deletes the owner's state and answers `ok` with `storedRevision = 0` and no results.
7. **Revision.** Lower than stored: `stale_revision` (with `storedRevision`, so the phone can jump past it
   and resend). Equal with the same content hash: the stored result again. Equal with different
   content: `revision_conflict`.
8. **Validation.** Each routine is revalidated with the shared validator plus PC checks: the trigger is
   `pc.*`, `hostIdentity` is this PC, verbs are in `routinePowerVerbs`, apps are in the launcher
   allowlist, and the idle or session source exists. A rejected routine is **not stored, and any earlier
   version of it is removed**, so the PC never runs a definition the phone no longer holds.
9. **Save.** Atomic, before the reply. A failed save answers `internal_error` and keeps the old set.
10. **Reply**, then, after an `ok` or `partial`, the unseen history as `routine_run_report` pages
    (from `runCursor`), then any queued `routine_notify` messages.

### `routine_sync_result` (host → phone)

| Property | Type | Description |
| :--- | :--- | :--- |
| `revision` | `long` | Echo of the request's revision (equals `storedRevision` when unsolicited) |
| `storedRevision` | `long` | Revision the PC now holds for this owner |
| `status` | `string` | `ok`, `partial`, `stale_revision`, `revision_conflict`, `schema_too_new`, `payload_too_large`, `blocked_by_pc`, `rate_limited`, `internal_error` |
| `results` | `{routineId, accepted, reasonCode, detail?}[]` | One per routine in the request, for `ok` and `partial` only |
| `hostPaused` | `bool` | PC-side Pause all (all owners) |
| `ownerPaused` | `bool` | This phone's `paused`, as the PC applied it |
| `unsolicited` | `bool` | Sent without a request because PC-side state changed: a routine switched off or on at the PC, the phone blocked or unblocked, PC Pause all |
| `pcDisabled` | `string[]` | Routine ids the PC user switched off. The phone never clears these; only the PC can |
| `ownerSuspended` | `string?` | `owner_absent` when the set is suspended because the phone has not connected for 30 days |
| `idleSource` | `string?` | Idle source in use (for example `win32.lastinput`), or null when idle triggers are unavailable |
| `sessionSource` | `string?` | Lock/unlock source in use (for example `win32.wts`), or null |
| `sensorTrigger` | `bool` | Sensor triggers are available on this PC |

### `routine_step_request` (phone → host)

One PC step of a **phone-run** routine.

| Property | Type | Description |
| :--- | :--- | :--- |
| `runId` | `string` | UUID of the phone's run |
| `routineId` | `string` | Routine id |
| `routineName` | `string` | Up to 40 characters, for the countdown and messages on the PC |
| `triggerType` | `string` | Trigger of the run |
| `stepIndex` | `int` | 0 to 11 |
| `step` | `RoutineStep` | `power`, `launchApp`, `media`, or `notify` with `target = pc` |
| `testRun` | `bool` | In-app Test. A destructive verb is never issued: the countdown runs and the result is `simulated` |
| `source` | `string` | Run source, for history only (for example `manual.app`, `nfc.tap`, `home.arrive`) |

The PC revalidates the step against the current validator, launcher allowlist and verb table. Every
destructive verb (`SHUTDOWN`, `FORCESHUTDOWN`, `RESTART`, `FORCERESTART`, `RESTARTTOUEFI`, `SIGNOUT`,
`SLEEP`, `HIBERNATE`) counts down 15 s on the PC first; `LOCK` and `MONITOROFF` never do. **No field of
this message can skip the countdown.** Requests are idempotent on `(clientId, runId, stepIndex)` for
10 minutes: a resend gets the cached result, or `in_progress` while the first is still running. New
requests are limited to 60 per minute per phone (`rate_limited`). The step runs on no connection
token, so a phone that drops during a countdown has not cancelled it; it learns the outcome by
resending.

### `routine_step_result` (host → phone)

| Property | Type | Description |
| :--- | :--- | :--- |
| `runId`, `stepIndex` | `string`, `int` | Correlation |
| `outcome` | `string` | `succeeded`, `failed`, `cancelled`, `simulated`, `in_progress` |
| `reasonCode` | `string` | Reason code (spec §10.1); `ok` on success |
| `countdownShown` | `bool` | The PC countdown was actually on screen |
| `cancelledBy` | `string?` | `pc`, `phone` or `pause` |
| `detail` | `string?` | Up to 120 characters, English, already redacted. The phone shows its own localized reason |

For a destructive verb the PC sends `succeeded` **immediately before** issuing it, because the socket
dies with the machine. `succeeded` means "issued after the countdown".

### `routine_notify` (host → phone) and `routine_notify_ack` (phone → host)

| Property (`routine_notify`) | Type | Description |
| :--- | :--- | :--- |
| `notifyId` | `string` | UUID. The phone ignores ids it has already shown |
| `kind` | `string` | `step` (a `notify` step aimed at the phone) or `countdown` (heads-up that a PC-run routine is counting down) |
| `routineId`, `routineName`, `runId` | `string` | Correlation and display |
| `title`, `body` | `string` | Text the phone authored, echoed back. Up to 40 and 160 characters |
| `countdownEndsAtUnixMs` | `long?` | `countdown` only. The phone offers Cancel, which sends `routine_cancel` |
| `queuedAtUnixMs`, `expiresAtUnixMs` | `long` | Expiry is one hour after queueing |

`routine_notify_ack` is `{ "notifyIds": ["..."] }`. Delivery is at least once: live when the owner is
connected, otherwise held on the PC (at most 20 per phone, oldest dropped, each expiring after an
hour). A held message is removed only by an ack from its owner or by expiry, and an ack for another
phone's id matches nothing. A `countdown` notify is never queued. Every notify is also shown on the PC.

### `routine_run_report` (host → phone)

| Property | Type | Description |
| :--- | :--- | :--- |
| `runs` | `RoutineRun[]` | This owner's PC runs with `seq > runCursor`, oldest first, at most 50 per message |
| `more` | `bool` | More pages follow |
| `live` | `bool` | An in-progress update of one running PC run (step started or finished, countdown started or cancelled). The phone upserts by `runId` and does not advance its cursor |

Sent after a successful sync, when a PC run ends while its owner is connected, and live at every step
transition. `seq` is the record's last-modified sequence, so a record that changes after it ended is
sent again with a new `seq`.

### `routine_cancel` (phone → host)

`{ "runId": "...", "reason": "user" | "pause" }`. Acts only on runs owned by the sender; anything else is
ignored and logged. The PC first looks for a running PC-run routine with that `runId` (stopped before
its next step, countdown included); otherwise it cancels the phone run's step countdown
(`cancelled_on_phone`).

### `routine_run_request` (phone → host)

Runs or tests one of the sender's **stored** PC-run routines now.

| Property | Type | Description |
| :--- | :--- | :--- |
| `runId` | `string` | UUID chosen by the phone. A request without a valid UUID or `routineId` is ignored |
| `routineId` | `string` | One of the sender's stored PC-run routine ids |
| `testRun` | `bool` | Non-destructive steps run for real, destructive ones are simulated |
| `source` | `string` | Informational. The PC records every run request as `manual.app` |

The PC runs its own stored, revalidated copy; this message cannot carry a definition. A `runId` that
belongs to another phone is refused; a repeat of one of the sender's own ids within 10 minutes gets the
existing run's record instead of a second run. The answer is a live `routine_run_report` of the new run,
or of a skipped record with a reason (`routine_not_found`, `pc_not_paired`, `blocked_by_pc`,
`disabled_on_pc`, `invalid_field`, `already_running`, `rate_limited`; `rate_limited` is also the
not-stored answer to a request that arrived before start-up finished). Pause all, the phone's own
switch-off and owner-absent suspension do not refuse it: those stop automatic starts only, and a run
request is person-initiated.
Destructive steps count down, because a phone request is never presence at the PC; only Run now
confirmed on the PC itself skips the countdown, and that flag has no wire representation.


---

## 9. Home pinned sensors

The phone's Home shows the same pinned sensors as the PC Home, and either side can change them. The PC
is the single owner: the list is `DashboardProfile.PinnedSensorIds`, and only the list of sensor names
travels, never cards, positions or themes. Payload records: `remex.core/Models/HomePinnedSensors.cs`;
validation: `remex.core/Validation/HomePinsValidation.cs`.

**Transport and gating.** `RemexMessage` on the authenticated `/ws` channel, `protocolVersion` stays `2`.
The host never sends these to a loopback session and accepts `home_pins_change` only from an
authenticated, non-loopback session. An invalid change is logged and dropped with no reply.

| Record | Field | Type | Meaning |
| :--- | :--- | :--- | :--- |
| `HostCapabilities` | `supportsHomePinsSync` | `bool` | The PC sends `home_pins_sync` and accepts `home_pins_change`. Absent: the phone never sends a change and keeps a phone-local list |

| `type` | Direction | Envelope property | Payload record |
| :--- | :--- | :--- | :--- |
| `home_pins_sync` | host → phone | `homePins` | `HomePinnedSensors { sensorNames, pinnableSensorNames, revision, updatedUtc }` |
| `home_pins_change` | phone → host | `homePinChange` | `HomePinChange { sensorName, pinned }` |

Every home-pins type starts with `home_pins_`. The Android native router forwards that whole prefix to
`RemexCallback.onHomePinsMessage` in one place; a new host → phone type must keep the prefix or it is
dropped without a trace. Both slots are lenient: a wrong-typed field nulls the slot, not the envelope.

- **Names, not ids.** The key is `SensorReading.Name`, compared case-insensitively. A name is non-blank,
  at most 200 characters, with no control characters; a list holds at most 100 names, duplicates removed
  case-insensitively keeping the first.
- **Sync.** Sent once the session has authenticated (straight after `pairing_complete` or
  `reconnect_result`) and after every change; nothing is sent before the PC has published once
  (revision 0), so a phone never mistakes "not loaded yet" for "no pins". `pinnableSensorNames` lists the sensors with a placed
  card on the PC canvas; only those can be pinned durably. `revision` rises per host process; the phone
  ignores an older sync on the same connection and forgets the counter on reconnect.
- **Change.** One sensor per message, never a whole list. The host applies changes in arrival order, so
  changes to different sensors both survive and the later of two changes to one sensor wins. The answer
  is the next `home_pins_sync`; a change the PC cannot honour is answered by resending the unchanged list.


---

## 10. PC logs and diagnostics on the phone

A paired phone can read the PC's captured log and its status rows, read-only. Nothing here writes, clears or
reconfigures anything on the PC. Payload records: `remex.core/Models/DiagnosticsMessages.cs`; validation:
`remex.core/Validation/DiagnosticsValidation.cs`; host: `remex.agent/Services/Diagnostics/PhoneDiagnosticsService.cs`.

**Transport and gating.** `RemexMessage` on the authenticated `/ws` channel, `protocolVersion` stays `2`.
Both requests need a paired session (`RequiresPairing`) and the host additionally answers `refused` to a loopback
session or one that has not proven its identity. At most one request of each kind per second per session
(`rate_limited`). Every string the PC sends is redacted first (secrets, client ids, IP and MAC addresses, file
paths reduced to the file name, e-mail addresses, long key-like strings), and an exception is reduced to its type
and first line. A refusal is still a reply, echoing `correlationId`, so the phone shows a reason rather than waiting.

| `type` | Direction | Envelope property | Payload record |
| :--- | :--- | :--- | :--- |
| `diagnostic_logs_get` | phone → host | `diagnosticLogsRequest` | `DiagnosticLogsRequest { afterSeq, minLevel, max }` |
| `diagnostic_logs_result` | host → phone | `diagnosticLogsResponse` | `DiagnosticLogsResponse { entries[{seq, timeUtc, level, category, message}], lastSeq, truncated, error }` |
| `diagnostic_summary_get` | phone → host | none | none |
| `diagnostic_summary_result` | host → phone | `diagnosticSummaryResponse` | `DiagnosticSummaryResponse { items[{key, state, detail}], error }` |

Every diagnostics type starts with `diagnostic_`; the Android native router forwards that prefix to
`RemexCallback.onDiagnosticMessage` in one place. All slots are lenient: a wrong-typed field nulls the slot, not the
envelope, and the host answers `invalid_request`.

- **Logs request.** `minLevel` is `trace`, `debug`, `information`, `warning`, `error` or `critical` (lower case);
  `max` is 1 to 500; `afterSeq` is 0 or more. `afterSeq` 0 returns the newest page; any other value returns the
  entries after it, oldest first, so a polling phone never skips or repeats a line. An `afterSeq` beyond the PC's
  newest entry (the PC restarted) is served as 0.
- **Logs response.** `entries` are oldest first. `lastSeq` is the value to send as `afterSeq` next: the last entry
  sent when `truncated` is true on an incremental read, otherwise the newest sequence number the PC has, even if
  the filter hid it. `truncated` is true when more matching entries exist than were returned (page cap, or the
  response size cap of about 256 KB, which drops the oldest lines of a newest page).
- **Summary rows.** `key` is one of `listener`, `certificate`, `firewall`, `elevation` (Windows only),
  `autostart`, `capture`, `encoder`, `version`, `uptime`; `state` is `ok`, `warn` or `error`; `detail` is a short
  redacted value. The phone translates the key and keeps an unknown key as plain text.
- **Errors.** `error` is null on success, otherwise `refused`, `rate_limited`, `invalid_request` or `unavailable`.
---

## 11. Phone telemetry alerts

The phone shows and edits the same sensor alert rules as the PC, and the PC's alerts reach the phone's
notification shade while the phone is connected. The PC is the single owner: the rules are
`DashboardProfile.SensorAlerts` (`SensorAlertStore`), and they are evaluated only by the PC's
`SensorAlertTracker`, so the phone never compares a reading with a threshold. Payload records:
`remex.core/Models/SensorAlertWire.cs`; validation: `remex.core/Validation/SensorAlertValidation.cs`.

**Transport and gating.** `RemexMessage` on the authenticated `/ws` channel, `protocolVersion` stays `2`.
The host sends these only to paired phones that hold an open session which proved its identity, never to
a loopback session, and accepts the three requests only from an authenticated, non-loopback session. A
phone that is not connected is simply not told: there is no queue, and the phone asks for the rules again
each time it connects.

| Record | Field | Type | Meaning |
| :--- | :--- | :--- | :--- |
| `HostCapabilities` | `supportsSensorAlerts` | `bool` | The PC sends the alert messages below and accepts the three requests. Absent: the phone hides its alert controls |

| `type` | Direction | Envelope property | Payload record |
| :--- | :--- | :--- | :--- |
| `sensor_alert_fired` | host → phone | `sensorAlertFired` | `SensorAlertFiredEvent { sensorName, displayName, value, unit, threshold, direction, severity, firedAtUtc }` |
| `sensor_alerts_get` | phone → host | none | none. The answer is `sensor_alert_rules` |
| `sensor_alert_set` | phone → host | `sensorAlertChange` | `SensorAlertChange { sensorName, threshold, direction, severity }`. Adds the rule, or replaces the sensor's existing one |
| `sensor_alert_remove` | phone → host | `sensorAlertRemoval` | `SensorAlertRemoval { sensorName }` |

`sensor_alert_rules` (host → phone, envelope property `sensorAlertRules`, payload
`SensorAlertRules { rules, revision, updatedUtc }`, each rule `SensorAlertRule { sensorName, displayName,
unit, currentValue, threshold, direction, severity }`) is how the PC answers `sensor_alerts_get`,
`sensor_alert_set` and `sensor_alert_remove`, but it is a broadcast, not a reply to the asker: the host
sends it to every paired, identity-proven phone that is connected (never loopback), each send under a
5 second limit and all of them at once, so one stalled phone does not delay the rest. It is also sent
whenever the PC's rules change by any other route, and once when the PC's UI first starts answering
(a `sensor_alerts_get` that arrived before then is not lost). It carries the whole list every time, and
close-together requests may be answered by one publish. If the PC's UI is not running its alert bridge,
a request is dropped and nothing is sent, so a phone should treat a missing answer as "no change".
A session may make at most 10 alert requests in any 10 seconds; past that a request is not applied, and
the phone is sent the unchanged rules instead, so its optimistic edit is put back.

Every alert type starts with `sensor_alert_`. The Android native router forwards that whole prefix to
`RemexCallback.onSensorAlertMessage` in one place; a new host → phone type must keep the prefix or it is
dropped without a trace. All four slots are lenient: a wrong-typed field nulls the slot, not the envelope.

- **Direction and severity are names.** `"Above"` / `"Below"` and `"Warning"` / `"Critical"`, verbatim.
  Any other name, or a number that is not one of them, makes the request malformed.
- **Firing.** `sensor_alert_fired` is sent exactly when the PC raises its own alert for the rule, so the
  tracker's 60 second per-sensor cooldown applies to phones as well; there is no second evaluator.
  `value` is the reading that crossed `threshold`, in `unit`.
- **Rules.** `currentValue` is the sensor's latest reading when the PC has one (absent otherwise). `revision`
  rises per host process; the phone ignores an older list on the same connection and forgets the counter on
  reconnect. A list holds at most 100 rules.
- **Requests.** The PC applies a request on its UI thread through `SensorAlertStore` (so its own Alerts
  list refreshes and the profile is saved) and answers by sending `sensor_alert_rules`, accepted or not. A
  refused request is answered with the unchanged list, which puts the phone's optimistic edit back. A set
  is refused when: the sensor name is blank, over 200 characters or has control characters; the threshold
  is not a finite number within ±1e12; the direction or severity is unknown; the PC does not know a sensor
  by that name (unless it already has a rule for it, which can always be edited or removed); or adding the
  rule would make more than 100.
