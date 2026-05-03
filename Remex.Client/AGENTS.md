# Remex.Client — Agent Playbook (2.0)

This project contains the shared Avalonia UI, ViewModels, and client-side services for both the Desktop and (conceptually) other .NET client targets.

## 2.0 Role: Secure Client UI & Logic
The Client manages the user-facing side of pairing and enforces certificate pinning to prevent Man-in-the-Middle (MitM) attacks.

## Assigned 2.0 Tracks
- **Track 1C**: Desktop client TLS support and Pairing Dialog.
- **Track 2B**: Critical Bug Fixes (UI thread freezes, memory leaks).

## Tactical Anchor Nodes (GitNexus)
Focus on these nodes for 2.0 connectivity and stability:
- `RemoteDesktopService`: Manages the `wss://` connection lifecycle and cert validation.
- `PinnedCertStore`: Persists host SPKI hashes.
- `ConnectionViewModel`: Orchestrates the transition from discovery to pairing to active connection.
- `SettingsViewModel`: Critical area for Part 1/Part 2 UI-thread fixes.

## Verification Checklist
- [ ] `dotnet build Remex.Client/Remex.Client.csproj -c Release` passes.
- [ ] No `[ObservableProperty]` updates are performed off the UI thread (Part 1 fix).
- [ ] `PairingDialog` appears correctly when connecting to an unpinned host.
- [ ] `pinned_hosts.json` is correctly updated in `LocalApplicationData`.
