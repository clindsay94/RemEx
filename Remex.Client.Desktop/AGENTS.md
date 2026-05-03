# Remex.Client.Desktop — Agent Playbook (2.0)

This is the platform-specific entry point for Windows and Linux desktop versions of RemEx.

## 2.0 Role: Desktop Distribution & Entry
Ensures that the 2.0 upgrade is packaged and launched correctly on desktop platforms.

## Assigned 2.0 Tracks
- **Track 1C**: Desktop-specific bootstrapper updates for TLS.
- **Track 2.1.0** (Upcoming): Clipboard sync and tray management.

## Verification Checklist
- [ ] `dotnet run --project Remex.Client.Desktop` launches the UI successfully.
- [ ] Application name and version correctly report `2.0.0`.
- [ ] System tray icon functionality is preserved post-upgrade.
