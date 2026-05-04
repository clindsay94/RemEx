# Security Policy

## Supported Versions

RemEx is a remote access and command execution tool. We take security seriously and support the latest versions with security updates.

| Version | Supported          |
| ------- | ------------------ |
| 2.0.x   | :white_check_mark: |
| 1.13.x  | :white_check_mark: (until 2026-10-01) |
| 1.10.x  | :x:                |
| < 1.10  | :x:                |

## Reporting a Vulnerability

If you discover a security vulnerability in RemEx, please **do not** open a public GitHub issue. Instead, use GitHub's private vulnerability reporting:

1. Go to the [Security Advisory](https://github.com/clindsay94/RemEx/security/advisories) page
2. Click "Report a vulnerability"
3. Provide a detailed description, steps to reproduce, and potential impact

**What to expect:**
- **Acknowledgment:** Within 48-72 hours
- **Investigation:** We'll assess the severity and scope
- **Timeline:** Critical vulnerabilities will be patched within 30 days; high-severity issues within 60 days
- **Disclosure:** We follow responsible disclosure—vulnerabilities will not be publicly discussed until a patch is available

## Known Security Considerations

### 2.0+ Security Model: TLS + ECDH Pairing

RemEx 2.0 uses **TLS 1.3 with certificate pinning** and **ECDH X25519 key exchange** for secure device pairing:

- **Transport Encryption:** All WebSocket connections use `wss://` (TLS 1.3) with self-signed RSA 2048 certificates generated on first host start
- **Certificate Pinning:** Clients pin the SHA-256 hash of the host's certificate SPKI (SubjectPublicKeyInfo), preventing man-in-the-middle attacks
- **Pairing Protocol:** First-time connection requires ECDH X25519 key exchange with a 6-digit PIN displayed on the host (120-second TTL)
- **Session Key Derivation:** HKDF-SHA256 derives a 32-byte session key from the shared secret, using the certificate SPKI hash as salt
- **Paired Client Storage:**
  - **.NET Desktop:** `LocalApplicationData/Remex/pinned_hosts.json` (JSON dictionary of hostId → SPKI hash)
  - **Android:** `EncryptedSharedPreferences` via androidx.security:security-crypto

**TCP Command Port Limitation (Port 8338):**

The TCP command port (used for fire-and-forget commands like Wake-on-LAN) does **not** enforce TLS or pairing verification in 2.0. It accepts plaintext TCP connections and processes commands without authentication. This is intentional for Phase 1 simplicity, as these commands are low-risk (WoL, basic system commands).

**Mitigation:**
- Bind the TCP port to localhost only by setting `RemexHost:Security:LocalhostOnly = true` in `appsettings.json`
- Use firewall rules to restrict access to port 8338 to known local devices
- Track 2.x will add TLS + paired-client verification to the TCP port

### 1.x Security Model (Legacy, EOL after 2026-10-01)

**⚠️ 1.x clients will be rejected by 2.0+ hosts.** The old access-key system has been removed:
- ~~Access keys~~ (removed in 2.0)
- ~~Plaintext WebSocket connections~~ (removed in 2.0)

If you're still running 1.x, upgrade to 2.0 immediately.

### Linux Elevation (`pkexec`)

On Linux, the Task Manager uses `pkexec` to attempt process termination if the host service is not running with sufficient privileges. This will trigger a system-native authentication prompt on the host machine.

### Environment Security

RemEx is designed for **trusted LAN environments only**. The access key provides a barrier against unauthorized access on the local network but is not a substitute for network-level encryption (TLS/SSL). 

**Recommendations:**
- If your threat model requires protection against network sniffing, use a VPN or encrypted tunnel (e.g., WireGuard or Tailscale) between client and host.
- Ensure the host machine's firewall is configured to only allow traffic on the RemEx ports (Default: 5005, 8338) from known local devices.
