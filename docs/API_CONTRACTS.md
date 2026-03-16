# RemEx API Contracts

This document provides the full API documentation for the various communication protocols used in RemEx.

## 1. WebSocket Telemetry & Remote Execution (`/ws`)

The WebSocket endpoint provides real-time bidirectional communication. It is primarily used for streaming hardware telemetry and issuing remote commands between the UI client and the host service.

**Endpoint:** `ws://<host>:<port>/ws` (Default port: 5005)

### Envelope: `RemexMessage`

All messages exchanged over the WebSocket use the `RemexMessage` JSON envelope.

| Property | Type | Description |
| :--- | :--- | :--- |
| `type` | `string` | **Required.** Message type discriminator (e.g., `"ping"`, `"telemetry"`, `"command"`). |
| `timestamp` | `long?` | UTC ticks when the message was created, used for latency measurement. |
| `telemetry` | `TelemetryPayload?` | Optional payload attached for telemetry streaming. |
| `commandAction` | `string?` | Command action name (e.g., `"Shutdown"`, `"Lock"`). |
| `commandParameters` | `Dictionary<string, string>?` | Command parameters (e.g., for WoL MAC address). |
| `commandSuccess` | `bool?` | Whether the command succeeded (for response messages). |
| `commandMessage` | `string?` | Response message from command execution. |
| `launcherEntries` | `List<AppEntry>?` | Launcher sync list. |
| `launcherEntry` | `AppEntry?` | Single launcher entry for add/remove. |

### Message Types

- `ping` / `pong`: Used for keep-alive and latency calculation.
- `telemetry`: Streaming hardware sensor data.
- `command`: Request to execute a remote command.
- `command_response`: Response indicating the success/failure of a command.
- `launcher_sync`: Synchronization of app launcher entries.
- `launcher_add`: Request to add a new launcher entry.
- `launcher_remove`: Request to remove a launcher entry.

---

## 2. TCP Command Ingress (External Network Listener)

The external TCP listener is used to receive one-shot system power commands, such as shutting down or waking up machines over the network.

**Endpoint:** TCP Socket on Port `8338` (Configurable via `Remex:CommandPort`)

⚠️ **Security Warning:** The TCP Command Ingress endpoint allows remote execution of power commands (Shutdown, Restart, Force Restart, Restart to UEFI, Lock, and Wake-on-LAN). Ensure this port is protected by a firewall and only accessible from trusted networks.

### Request Payload: `CommandRequest`

The client must send a UTF-8 encoded JSON string matching the following structure:

```json
{
  "Action": "string",
  "Parameters": {
    "Key": "Value"
  }
}
```

| Property | Type | Description |
| :--- | :--- | :--- |
| `Action` | `string` | The command to execute (Case-insensitive). |
| `Parameters` | `Dictionary<string, string>?` | Optional parameters for specific commands (like Wake-on-LAN). |

**Supported Actions:**
- `SHUTDOWN`: Initiates a system shutdown.
- `RESTART`: Initiates a system restart.
- `FORCERESTART`: Forces a system restart without waiting for applications.
- `RESTARTTOUEFI`: Restarts the system directly into UEFI/BIOS settings.
- `LOCK`: Locks the current user session.
- `WAKEONLAN`: Sends a magic packet to wake a target machine.
  - **Required Parameter:** `"MacAddress"` (e.g., `"00:11:22:33:44:55"`)
  - **Optional Parameter:** `"BroadcastIp"` (Default: `"255.255.255.255"`)
  - **Optional Parameter:** `"Port"` (Default: `"9"`)

### Response Payload: `CommandResponse`

The server responds with a UTF-8 encoded JSON string indicating the result.

```json
{
  "Success": true,
  "Message": "string"
}
```

---

## 3. Local IPC (Named Pipe)

The Named Pipe is exclusively used for local communication between the Avalonia UI client and the background Host Service (e.g., launching local applications).

**Endpoint:** Named Pipe `RemexIPC`

### Request Payload: `CommandRequest` (Local IPC)

The client writes a JSON string to the pipe:

```json
{
  "Action": "string",
  "TargetPath": "string"
}
```

| Property | Type | Description |
| :--- | :--- | :--- |
| `Action` | `string` | The local action to perform. |
| `TargetPath` | `string?` | Optional path or argument for the action. |

### Response Payload: `CommandResponse` (Local IPC)

The background service responds with:

```json
{
  "Success": true,
  "Message": "string"
}
```

---

## 4. REST / Minimal APIs

The RemEx Host exposes a basic HTTP endpoint for health checks and service discovery.

**Endpoint:** `http://<host>:<port>/` (Default port: 5005)

### Health Check

- **Method:** `GET`
- **Path:** `/`
- **Response:**
  ```json
  {
    "service": "Remex.Host",
    "status": "running"
  }
  ```
