# Remex.Host — Agent Playbook (2.0)

This project is the server-side implementation of the RemEx ecosystem. In 2.0, it transitions from a plaintext trusted-LAN tool to a hardened, encrypted service.

## 2.0 Role: Security & Service Implementation
The Host is responsible for certificate management, protocol enforcement, and fulfilling file transfer requests.

## Assigned 2.0 Tracks
- **Track 1A**: Kestrel TLS 1.3 implementation.
- **Track 1B**: X25519 Pairing handshake logic.
- **Track 2A**: File transfer service implementation (Track 2.0.1).

## Tactical Anchor Nodes (GitNexus)
Use `gitnexus_context` to understand the host lifecycle and security layer:
- `HostBootstrapper`: Configures Kestrel and dependency injection.
- `CertificateService`: Handles self-signed cert generation and SPKI hashing.
- `PairingService`: Manages the 6-digit PIN state machine and paired client persistence.
- `PingPongHandler`: The primary dispatcher for WebSocket messages.

## Verification Checklist
- [ ] `dotnet build Remex.Host/Remex.Host.csproj -c Release` passes.
- [ ] `curl -k https://localhost:5005/ -v` shows TLS 1.3 handshake.
- [ ] Plaintext HTTP connection to port 5005 is refused.
- [ ] `cert.pfx` is generated in the appropriate system data directory with restricted permissions.
