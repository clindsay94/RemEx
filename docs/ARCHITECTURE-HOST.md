# Host Architecture: Two Planes (Decision Record)

> **SUPERSEDED (2026-07, RemEx-aep / RemEx-u0oc):** the two planes were merged after all.
> The PC side is now a single `remex.agent` process (UI + embedded host) on both Windows
> (elevated user-session app started by a logon task) and Linux (per-user app started by an
> XDG autostart entry). There is no Windows Service, no systemd unit, and no separate
> `remex.desktop` client. Pre-login power commands were dropped as a non-goal. This record
> is kept for the history of why the split existed.

**Decision (2026-06):** keep both the headless `remex.agent` service **and** the embedded
in-process host inside `remex.desktop`. Do not merge them — Windows makes the two
roles physically non-mergeable:

- A user-session process (desktop client + embedded host) **cannot run before login**.
  Serving "phone can send commands while the PC sits at the login screen" requires a
  session-0 Windows service (or boot scheduled task — same constraint).
- A session-0 service **cannot stream the interactive desktop**: DXGI desktop duplication
  requires an interactive session, and capturing the secure login desktop is privileged
  territory RemEx should not enter.

## The two planes

| Plane | Process | Runs | Provides |
|---|---|---|---|
| Command plane | `remex.agent` as Windows service / systemd unit | from boot, pre-login | power commands, telemetry, WOL, pairing |
| Interactive plane | embedded host inside `remex.desktop` | from user login | remote desktop streaming, input, app launcher |

## How they coexist (ARCH-1)

`remex.desktop/Program.cs` always starts the embedded host. When the `RemexHost`
service is running it owns the default port (5005), so the embedded host takes 5006 (with
one further fallback). The phone disambiguates hosts via `DesktopMeta.HostInstanceId`, and
mDNS instance names are port-qualified off the default port
(`MdnsAdvertisingService`: `"<machine> (5006)"`) so the two `_remex._tcp` responders never
collide.

The discovery chain resolves real ports end-to-end: `HostBootstrapper` writes the actually
bound port into `Host:Port`, `MdnsAdvertisingService` advertises it, and the Android
`NsdDiscoveryManager` takes `service.port` from the NSD resolve callback rather than
assuming 5005.

## Completing the story (ARCH-2)

The service covers pre-login; an autostarted tray client covers everything after login.
`StartupRegistrationService` (registry `Run` key on Windows, `~/.config/autostart` on
Linux) registers the client with `--minimized`, which starts it hidden with the tray icon
active. A Settings toggle ("Launch RemEx when you sign in") and an Inno Setup task
(`launchatlogin`) expose it.

## Service account note (ARCH-3, pending)

For the command plane, `LocalSystem` is sufficient — a named account adds nothing because
session 0 cannot do desktop features regardless of account. The installer/service script
still collects credentials; simplifying that to LocalSystem-by-default remains open.
