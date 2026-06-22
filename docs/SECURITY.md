# Security Policy

## Supported Versions

RemEx is a remote access and command execution tool. We take security seriously and support the latest versions with security updates.

| Version | Supported          |
| ------- | ------------------ |
| 2.0.x   | :white_check_mark: |
| < 2.0   | :x:                |

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

RemEx 2.0+ uses **TLS 1.3 with certificate pinning** and **ECDH NIST P-256 key exchange** for secure device pairing:

- **Transport Encryption:** All WebSocket connections use `wss://` (TLS 1.3) with self-signed RSA 2048 certificates generated on first host start
- **Certificate Pinning:** Clients pin the SHA-256 hash of the host's certificate SPKI (SubjectPublicKeyInfo), preventing man-in-the-middle attacks
- **Pairing Protocol:** First-time connection requires ECDH NIST P-256 key exchange with a 6-digit PIN displayed on the host (120-second TTL)
- **Session Key Derivation:** HKDF-SHA256 derives a 32-byte session key from the shared secret, using the certificate SPKI hash as salt
- **Paired Client Storage:**
  - **.NET Desktop:** `LocalApplicationData/Remex/pinned_hosts.json` (JSON dictionary of hostId → SPKI hash)
  - **Android:** `EncryptedSharedPreferences` via androidx.security:security-crypto

**TCP Command Port (Port 8338):**

The TCP command port is TLS 1.3 encrypted (server-only certificate) and **default-deny** (PROTO-1 / RemEx-htt). Because server-only TLS cannot identify the caller, authentication happens at the application layer: every `CommandRequest` must carry a `ClientId` that is registered in the host's paired-client registry (the same registry used by the `/ws` channel). A request with a missing or unknown `ClientId` is rejected with `Unauthorized` and the connection is closed before any power action runs. No first-party client uses 8338 (Android uses `/ws`; the local UI uses the named pipe); external automation scripts must pair first and include their paired `ClientId` on every command. See `docs/API_CONTRACTS.md` §4 for the payload.

### 1.x Security Model (Legacy, End-of-Life)

**⚠️ 1.x clients will be rejected by 2.0+ hosts.** The old access-key system has been removed:
- ~~Access keys~~ (removed in 2.0)
- ~~Plaintext WebSocket connections~~ (removed in 2.0)

If you're still running 1.x, upgrade to 2.0 immediately.

### Linux Elevation (`pkexec`)

On Linux, the Task Manager uses `pkexec` to attempt process termination if the host service is not running with sufficient privileges. This will trigger a system-native authentication prompt on the host machine.

### Environment Security

RemEx 2.0 provides **network-level encryption** out-of-the-box. All communication between the client and host is secured via TLS 1.3 / WSS, protecting against network sniffing on local networks.

**Recommendations:**
- Even with TLS, we recommend using a VPN (e.g., WireGuard or Tailscale) if accessing your host over the public internet.
- Ensure the host machine's firewall is configured to only allow traffic on the RemEx ports (Default: 5005, 8338) from known local devices.
