<!-- gitnexus:start -->
# GitNexus — Code Intelligence (RemEx 2.0 "Cosmic Raven")

This project is indexed by GitNexus as **RemEx**. Use the GitNexus MCP tools to understand the 2.0 protocol flows (Pairing, TLS Handshake, File Transfer).

> [!IMPORTANT]
> **GitNexus MCP Connectivity:** If the MCP tools are not responding or the index seems stale, you MUST run:
> 1. `npx gitnexus analyze` (to refresh the graph)
> 2. `gitnexus mcp` (to start/restart the MCP server)

## 2.0 Upgrade Mission Control

RemEx 2.0 is a major security and feature upgrade. Every agent MUST adhere to the [2.0 Master Plan](file:///home/connorl/RemEx/2.0-Plan/master-plan.md) and update the [2.0 Tracker](file:///home/connorl/RemEx/2.0-Plan/2.0-Tracker).

### The Phase Gate Protocol
Execution is strictly sequential through Phases 0 and 1. Phase 2 allows parallel execution of independent tracks.

1.  **Phase 0 (Foundation)**: Versioning, Message Contracts, Base Interfaces.
2.  **Phase 1 (Security Backbone)**: Kestrel TLS 1.3, X25519 Pairing Protocol.
3.  **Phase 2 (Parallel Tracks)**: File Transfer, Material 3, Release Engineering.
4.  **Phase 3 (Polish)**: Localization, Docs, Installers.

### Chokepoint Matrix (HIGH CONFLICT)
DO NOT edit these files in parallel. Only edit them during their assigned phase.

| File | Phase | Track |
|---|---|---|
| `Remex.Core/Messages/RemexMessage.cs` | Phase 0 | 0B-message-types |
| `Remex.Host/HostBootstrapper.cs` | Phase 1 | 1A-host-tls |
| `RemEx.Android/app/src/main/AndroidManifest.xml` | Phase 2 | 2D-release-eng |

## Always Do

- **MUST run impact analysis before editing any symbol.** Run `gitnexus_impact({target: "symbolName", direction: "upstream"})` and report the blast radius.
- **MUST run `gitnexus_detect_changes()` before committing** to verify affected scope.
- **MUST follow the PRAR workflow** (Perceive, Reason, Act, Refine) as defined in `GEMINI.md`.

## Resources

| Resource | Use for |
|----------|---------|
| `gitnexus://repo/RemEx/context` | Codebase overview |
| `gitnexus://repo/RemEx/processes` | 2.0 Execution flows (Pairing, Transfer) |
| `2.0-Plan/master-plan.md` | Detailed implementation specs |

---
*Individual projects (Core, Host, Client, Android) have their own AGENTS.md with tactical track details.*
<!-- gitnexus:end -->
