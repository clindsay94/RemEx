# Remex.Core — Agent Playbook (2.0)

This project contains the shared message contracts, models, and security interfaces that form the foundation of RemEx 2.0.

## 2.0 Role: Shared Primitives
Remex.Core defines the "Language" and "Trust" of the system. In 2.0, this project is the source of truth for the new encrypted message envelope and pairing handshake models.

## Assigned 2.0 Tracks
- **Track 0B**: Add new message types and protocol version.
- **Track 0C**: Stub service interfaces (`IPairingService`, `ICertificateService`, `IFileTransferService`).

## Tactical Anchor Nodes (GitNexus)
Use `gitnexus_context` on these symbols before modifying message structures:
- `RemexMessage`: The root polymorphic message container.
- `MessageTypes`: Constants for message routing.
- `PairingRequest` / `PairingResponse`: The ECDH handshake models.

## Verification Checklist
- [ ] `dotnet build Remex.Core/Remex.Core.csproj -c Release` passes with exit code 0.
- [ ] New message types are registered in `RemexJsonSerializerContext.cs`.
- [ ] `ProtocolVersion` in `RemexMessage` defaults to `2`.
