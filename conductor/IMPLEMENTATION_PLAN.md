# RemEx ⚡ High-Level Implementation Plan

This plan outlines the phased rollout of fixes and new features for RemEx, based on the `EXAMINATION_REPORT.md` findings.

---

## 🛠️ Phase 1: Critical Fixes & System Stability
**Goal:** Restore broken functionality and optimize resource usage.

### 1.1 Remote Process Management (Repair)
- **Host Implementation:** Add `KillProcessElevated` handler in `PingPongHandler.cs`.
- **Permission Handling:** Update `WindowsProcessMonitorService` to handle `Win32Exception` (Access Denied) when reading protected process metadata.
- **Client Sync:** Ensure `TaskManagerViewModel` correctly handles the request-response lifecycle for elevated kills.

### 1.2 Telemetry Broadcaster (Efficiency)
- **Refactor:** Create a `TelemetryBackgroundService` in `Remex.Host` to poll `HWiNFO`/WMI once per second.
- **Broadcast Pattern:** Modify `PingPongHandler` to read from a shared `volatile` telemetry state instead of triggering fresh scans per client.

### 1.3 TCP Listener Robustness (Safety)
- **Framing:** Implement a basic message framing protocol (e.g., length-prefixed) in `RemexNetworkListener.cs` to handle fragmented or large JSON payloads.
- **Reliability:** Add a retry policy and timeout handling for inbound TCP socket operations.

---

## 📡 Phase 2: Core Connectivity & Remote Operations
**Goal:** Enhance discovery and extend remote command capabilities.

### 2.1 Zero-Configuration Networking (mDNS)
- **Integration:** Add `Makaretu.Dns.Multicast` to `Remex.Host` and `Remex.Client`.
- **Advertising:** Configure the Host to advertise the `_remex._tcp` service.
- **Discovery:** Add a "Discover Hosts" feature in `ConnectionViewModel` to automatically populate the host address.

### 2.2 Wake-On-LAN Relay Node
- **Relay Logic:** Update `WakeOnLanService` to allow the Host to act as a bridge, relaying Magic Packets from remote clients (via WAN/VPN) to the local physical network.
- **NIC Targeting:** Add logic to broadcast on all available physical network interfaces (NICs) to ensure delivery.

---

## 🎨 Phase 3: UI/UX & Platform Enhancement
**Goal:** Elevate the aesthetics and provide quick access on mobile.

### 3.1 NOC-Style "Bento Grid"
- **Layout Refactor:** Update `HomeView` and `DashboardView` to use a dynamic Bento-style grid (variable tile sizes) for higher information density.
- **Visual Glow:** Add electric-violet border glow effects for `glass-card:pointerover` in `App.axaml`.

### 3.2 Android Quick Settings & Widgets
- **Quick Settings Tile:** Implement `RemexTileService` in `Remex.Client.Android` for "Lock" and "Shutdown" shortcuts.
- **Home Screen Widget:** Create a `RemoteViews`-based widget showing real-time CPU/GPU/RAM telemetry using the existing connection state.
- **Android Consistency:** Add a fallback dark-violet gradient background for Android to mimic the Windows `Mica` effect.

---

## 🔒 Phase 4: Security Hardening
**Goal:** Protect remote management endpoints from unauthorized access.

### 4.1 Access Key Authentication
- **Configuration:** Add a `Remex:AccessKey` setting in `appsettings.json`.
- **Validation:** Implement a simple handshake in WebSocket/TCP handlers that requires the client to provide the matching key before executing commands.

### 4.2 Stream Encryption
- **Remote Desktop:** Investigate wrapping the binary desktop stream in a basic encryption layer or implementing a secure token-based access mechanism.

---

## 🧪 Verification & Testing
- **Unit Testing:** Add xUnit tests for the new `TelemetryBackgroundService` and `KillProcessElevated` handler.
- **Integration Testing:** Verify mDNS discovery across different subnets/environments.
- **UI Validation:** Audit the new Bento layout on both Windows (Mica) and Android (Fallback) for visual parity.

---

**Next Step:** Request approval to exit Plan Mode and begin Phase 1.
