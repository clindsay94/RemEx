# Security Policy

## Supported Versions

RemEx is a remote access and command execution tool. We take security seriously and support the latest versions with security updates.

| Version | Supported          |
| ------- | ------------------ |
| 1.12.x  | :white_check_mark: |
| 1.10.x   | :white_check_mark: |
| < 1.8   | :x:                |

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

### Access Key & Pairing

The `AccessKey` used for WebSocket and TCP command authentication is:
- **Transmitted in plaintext** over the WebSocket connection as a query-string parameter (`?key=<value>`).
- **Stored in plaintext** in the client's `client-settings.json` file on disk.
- **Stored in plaintext** in the Android app's DataStore preferences.
- **Shared via QR Code:** The pairing QR code contains the host IP, port, and access key in plaintext.

### Linux Elevation (`pkexec`)

On Linux, the Task Manager uses `pkexec` to attempt process termination if the host service is not running with sufficient privileges. This will trigger a system-native authentication prompt on the host machine.

### Environment Security

RemEx is designed for **trusted LAN environments only**. The access key provides a barrier against unauthorized access on the local network but is not a substitute for network-level encryption (TLS/SSL). 

**Recommendations:**
- If your threat model requires protection against network sniffing, use a VPN or encrypted tunnel (e.g., WireGuard or Tailscale) between client and host.
- Ensure the host machine's firewall is configured to only allow traffic on the RemEx ports (Default: 5005, 8338) from known local devices.
