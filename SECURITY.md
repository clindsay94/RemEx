# Security Policy

## Supported Versions

RemEx is a remote access and command execution tool. We take security seriously and support the latest version with security updates.

| Version | Supported          |
| ------- | ------------------ |
| 1.8.x   | :white_check_mark: |
| 1.7.x   | :white_check_mark: |
| < 1.7   | :x:                |

## Reporting a Vulnerability

If you discover a security vulnerability in RemEx, please **do not** open a public GitHub issue. Instead, use GitHub's private vulnerability reporting:

1. Go to the [Security Advisory](https://github.com/clindsay94/RemEx/security/advisories) page
2. Click "Report a vulnerability"
3. Provide a detailed description, steps to reproduce, and potential impact

**What to expect:**
- **Acknowledgment:** Within 48-72 hours
- **Investigation:** We'll assess the severity and scope
- **Timeline:** Critical vulnerabilities will be patched within 30 days; high-severity issues within 60 days
- **Credit:** You'll be credited in the security advisory (unless you prefer anonymity)
- **Disclosure:** We follow responsible disclosure—vulnerabilities will not be publicly discussed until a patch is available

Thank you for helping keep RemEx secure.

## Known Security Considerations

### Access Key Transmission

The `AccessKey` used for WebSocket and TCP command authentication is:

- **Transmitted in plaintext** over the WebSocket connection as a query-string parameter (`?key=<value>`).
- **Stored in plaintext** in the client's `DashboardProfile.json` file on disk.
- **Stored in plaintext** in the Android app's DataStore preferences.

RemEx is designed for **trusted LAN environments only**. The access key provides a barrier against casual unauthorized access on the local network but is not a substitute for network-level encryption. If your threat model requires protection against network sniffing, use a VPN or tunnel (e.g., WireGuard) between client and host.