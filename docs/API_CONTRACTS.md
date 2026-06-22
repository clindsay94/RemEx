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
> (port 5005) and the local tray/dashboard UI uses the named pipe. Port 8338 exists solely for
> third-party/external script ingress, which is why the `ClientId` requirement is a documentation and
> integration concern rather than a coordinated client release.

---

## 5. Local IPC (Named Pipe)

(No changes in 2.0)

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
    "service": "Remex.Host",
    "status": "running",
    "version": "2.0.0"
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
    "spkiHash": "string (base64)"
  }
  ```

