# Host Architecture (PC side)

> **Status: current (2026-07, RemEx-aep / RemEx-u0oc).** The PC side is **one process** —
> `remex.agent` — that contains both the dashboard UI and the embedded host. It runs **inside the
> signed-in user's interactive desktop session, always elevated**. There is **no Windows Service, no
> systemd unit, no Session 0, and no named-pipe IPC**. The `remex.desktop` folder is a legacy library
> compiled into `remex.agent`, not a separate client. A short history of the previous two-plane design
> is preserved at the end of this document.

## One process, elevated, in your session

`remex.agent` is the entire PC side. It hosts the ASP.NET Minimal-API + WebSocket + mDNS server,
performs screen capture and input injection, gathers telemetry, and renders the Avalonia dashboard —
all in a single process.

| Platform | How it starts | Privilege | Config location |
|---|---|---|---|
| **Windows** | Task Scheduler **logon task** `RemEx` (`LogonType=InteractiveToken`, `RunLevel=Highest`), registered by `scripts/autostart-remex.ps1`. Manifest declares `requestedExecutionLevel=requireAdministrator`. | Elevated (high integrity), no UAC prompt at sign-in | Machine-wide `ProgramData` + `HKLM` for security-sensitive state (`cert.pfx`, `paired_clients.json`, `CaptureBackendPreference`) |
| **Linux** | XDG **autostart** entry `~/.config/autostart/remex-agent.desktop` (`Exec=… --minimized`), installed by `installer/linux/agent-install.sh` to `~/.local/share/remex-agent`. | Per-user session; `pkexec` used only for privileged task-manager actions | Per-user `~/.local/share/Remex/` (`cert.pfx`, `paired_clients.json`) |

### Why one elevated in-session process

Two Windows facts force this shape, and merging the old planes resolved both root causes of the
"host won't stream / cursor unusable in an elevated window" bugs:

- **Capture needs an interactive session.** DXGI Desktop Duplication and Windows.Graphics.Capture
  (WGC) both require a real interactive session — a Session-0 service structurally *cannot* capture or
  inject into the interactive desktop.
- **Input needs to out-rank the target window.** Windows UIPI silently drops input from a
  lower-integrity process aimed at a higher-integrity window. Running elevated (HIGH integrity) via the
  user's linked full-admin token lets `SendInput` reach even "Run as administrator" windows.

Because the UI and host share one process, they talk through **in-process dependency injection**
(`EmbeddedHostServiceLocator`) — there is no `RemExLocalIPC` / `RemExHostControl` pipe or
`LocalIpcServerService` to secure anymore. That whole cross-process attack surface is gone.

### Why elevation is load-bearing (never weaken it)

The machine-wide `cert.pfx` and `paired_clients.json` are protected by an ACL granting **FullControl to
LocalSystem + Administrators only, with inheritance disabled**. An elevated token retains that access,
so **existing pairings survive across restarts with no re-pair**. A medium-integrity (non-elevated)
start would get Administrators as *deny-only*, fail to read `cert.pfx`, and brick every SPKI-pinned
pairing. `CertificateService` includes a brick canary: it logs `Critical` and refuses to regenerate
when an existing `cert.pfx` is present but unreadable.

## Discovery and port binding

`HostBootstrapper` writes the actually-bound port into `Host:Port`; `MdnsAdvertisingService`
advertises that real port over `_remex._tcp`; and the Android `NsdDiscoveryManager` reads
`service.port` from the NSD resolve callback rather than assuming `5005`. This keeps discovery correct
even if the default port is already in use.

---

## History: the former two-plane design (superseded)

> Kept so the *reasons* the split existed are not lost.

**Decision (2026-06, since superseded):** originally RemEx ran a headless `remex.agent` **Windows
service / systemd unit** (the "command plane" — power commands, telemetry, WOL, pairing, available
from boot / pre-login) **plus** an embedded host inside a separate `remex.desktop` client (the
"interactive plane" — remote desktop streaming and input, available after login). The two were kept
separate because a user-session process cannot run before login, while a Session-0 service cannot
stream or inject into the interactive desktop.

**Why it was merged (RemEx-aep):** the split's only unique capability was **pre-login power control**,
which was reclassified as a non-goal on both platforms. Removing it collapsed the two planes into one
elevated in-session process, which simultaneously fixed the capture-in-Session-0 and UIPI-input
failures above and deleted an entire class of cross-session IPC bugs. The former Session-0 service, its
`RemExLocalIPC` / `RemExHostControl` pipes, `AgentCoordinator`, `HostControlServer/Client`,
`InteractiveDesktopHostLauncher`, `SessionBridgingCommandService`, and `WindowsActiveSession` were all
removed. On upgrade, the installer uninstalls any leftover `RemexHost` service / `remex-host.service`
and migrates the certificate into the new store SPKI-intact.
