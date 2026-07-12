# RemEx on Linux — Setup Guide

This guide is written for everyone — you do not need to be a Linux expert. Copy-paste the
commands as shown and check the ✓ checkpoints as you go.

## How RemEx works on Linux (30 seconds)

RemEx on your PC is **one app**: `Remex.Agent`. It is the window you see **and** the
connection host your phone talks to, in a single program — the same design as on Windows.

- It runs as **you**, inside your normal desktop session. **Nothing runs as root.**
- It **starts automatically when you sign in** (minimized to the tray), so your phone can
  always reach the PC.
- There is **no background service**. If you used an older RemEx that installed a
  `remex-host` service or a separate `remex-client` app, the installer removes those
  automatically — see [Upgrading from an older RemEx](#upgrading-from-an-older-remex).

### Where everything lives

| What | Where |
|---|---|
| The app itself | `~/.local/share/remex-agent/` |
| Pairing data (certificate + paired phones) | `~/.local/share/Remex/` |
| "Start at login" entry | `~/.config/autostart/remex-agent.desktop` |
| App-menu entry | `~/.local/share/applications/remex-agent.desktop` |
| Terminal command | `remex-agent` |

Everything is inside your home folder. Deleting those folders removes RemEx completely.

---

## [A] Installing a release package

### A1. Install the helper packages

RemEx shows your PC's screen on your phone through two standard Linux components:
`xdg-desktop-portal` (the permission system) and PipeWire (the video stream). Install the
one portal "backend" that matches your desktop:

| Package | Who needs it | Arch / CachyOS / Manjaro | Ubuntu / Debian / Pop!_OS | Fedora |
|---|---|---|---|---|
| `xdg-desktop-portal` | Everyone | `sudo pacman -S xdg-desktop-portal` | `sudo apt install xdg-desktop-portal` | `sudo dnf install xdg-desktop-portal` |
| `xdg-desktop-portal-kde` | KDE Plasma | `sudo pacman -S xdg-desktop-portal-kde` | `sudo apt install xdg-desktop-portal-kde` | `sudo dnf install xdg-desktop-portal-kde` |
| `xdg-desktop-portal-gnome` | GNOME | `sudo pacman -S xdg-desktop-portal-gnome` | `sudo apt install xdg-desktop-portal-gnome` | `sudo dnf install xdg-desktop-portal-gnome` |
| `xdg-desktop-portal-wlr` | sway / Hyprland | `sudo pacman -S xdg-desktop-portal-wlr` | `sudo apt install xdg-desktop-portal-wlr` | `sudo dnf install xdg-desktop-portal-wlr` |
| `pipewire` + `wireplumber` | Everyone | `sudo pacman -S pipewire wireplumber` | `sudo apt install pipewire wireplumber` | `sudo dnf install pipewire wireplumber` |
| `libei` | Wayland users (recommended) | `sudo pacman -S libei` | `sudo apt install libei1` | `sudo dnf install libei` |
| `ffmpeg` | Everyone (recommended) | `sudo pacman -S ffmpeg` | `sudo apt install ffmpeg` | `sudo dnf install ffmpeg` |

Not sure whether your setup is healthy? The installer checks for you, and you can always run
the built-in health check afterwards:

```bash
~/.local/share/remex-agent/install.sh doctor
```

### A2. Extract and install

```bash
# Replace the filename with the version you downloaded
tar -xzf remex-agent-v2.1.0-linux-x64.tar.gz
./remex-agent-v2.1.0-linux-x64/install.sh
```

The installer:
1. Removes anything left over from older RemEx versions (old service, old app, old
   certificate location) — it will explain each step and may ask for your password **once**
   if old root-owned files need to be cleaned up.
2. Copies RemEx to `~/.local/share/remex-agent/`.
3. Adds RemEx to your app menu and sets it to start at login (minimized to the tray).

**Checkpoint:** the installer ends with `RemEx installed.` and no red warnings. ✓

### A3. Launch it

- **App menu:** look for **RemEx** (log out and back in if it doesn't appear, or run
  `kbuildsycoca6` on KDE / `update-desktop-database ~/.local/share/applications` on GNOME).
- **Terminal:** `remex-agent`

**Checkpoint:** the RemEx window opens, and the connection host starts. To confirm the host
is listening:

```bash
ss -tln | grep 5005
```

You should see a line with `:5005`. ✓

### A4. Pair your phone

1. Install the RemEx app on your Android phone.
2. Make sure phone and PC are on the same network (same Wi-Fi, or both on Tailscale).
3. In the phone app, add your PC — it is discovered automatically on a home network, or
   enter the PC's IP address.
4. RemEx on the PC shows a **6-digit PIN**. Enter it on the phone.

That's it. The phone remembers this PC permanently (it "pins" the PC's security
certificate). You only re-pair if you delete the pairing data folder.

**Checkpoint:** the phone shows your PC as connected and live stats appear. ✓

### A5. Uninstall

```bash
~/.local/share/remex-agent/install.sh uninstall
```

Your pairing data is deliberately kept (so a reinstall keeps your phones paired). The
uninstaller prints the one command to wipe that too, if you want a truly clean slate.

---

## Upgrading from an older RemEx

Older versions installed **two** things on Linux: a `remex-client` app and a `remex-host`
root service. That design is gone — one app does everything now. Just run the new
`install.sh`; it automatically:

- stops and removes the old `remex-host` system service (asks for your password once),
- removes the old `remex-client` install, launcher, and autostart entries,
- **moves your pairing certificate** from the old root-owned location (`/var/lib/remex`)
  into your user folder — the certificate is not changed, so **already-paired phones keep
  working**.

Nothing else to do. If the installer could not get root access, it prints the exact
commands to finish the cleanup yourself.

---

## Checking what's going on

| Question | Command |
|---|---|
| Is RemEx running? | `pgrep -af Remex.Agent` |
| Is the host listening for the phone? | `ss -tln \| grep 5005` |
| Is my screen-sharing stack healthy? | `~/.local/share/remex-agent/install.sh doctor` |
| Watch RemEx's log output | run `remex-agent` from a terminal |

---

## Troubleshooting

### "RemEx could not read its certificate" / phone cannot connect

**Symptom (log):**

```
crit: Remex.Agent.Services.Security.CertificateService[0]
      RemEx could not read its certificate at /var/lib/remex/cert.pfx ...
[Remex] Could not start embedded host on port 5005 ...
```

**Cause.** An older RemEx version ran as root and left a root-owned certificate behind.
Your normal user cannot read it, and RemEx refuses to create a new one because that would
break every phone you already paired.

**Fix.** Re-run the installer — it repairs this automatically:

```bash
./remex-agent-v2.1.0-linux-x64/install.sh
```

Or do it by hand:

```bash
mkdir -p ~/.local/share/Remex
sudo mv /var/lib/remex/cert.pfx ~/.local/share/Remex/cert.pfx
sudo chown $USER: ~/.local/share/Remex/cert.pfx
chmod 600 ~/.local/share/Remex/cert.pfx
sudo rmdir /var/lib/remex
```

Then start RemEx again. Your paired phones keep working — the certificate itself never
changed. (Never paired a phone? Then you can simply `sudo rm /var/lib/remex/cert.pfx`
instead and RemEx will make a fresh one.)

### Remote desktop shows no picture (portal error in the log)

**Symptom (log):**

```
warn: ...LinuxPortalRemoteDesktopSessionService[0]
      Portal org.freedesktop.portal.RemoteDesktop.CreateSession returned a D-Bus error ...
      No such interface "org.freedesktop.portal.RemoteDesktop"
```

**Cause.** The desktop portal started before your desktop environment finished setting up
its environment variables, so it never loaded the right backend.

**Fix (one-liner):**

```bash
systemctl --user import-environment \
  XDG_CURRENT_DESKTOP XDG_SESSION_TYPE WAYLAND_DISPLAY \
  DISPLAY DBUS_SESSION_BUS_ADDRESS XDG_DATA_DIRS
systemctl --user restart xdg-desktop-portal.service
```

Then restart RemEx. The installer runs this pre-emptively, RemEx self-repairs once per
run, and `install.sh doctor` diagnoses it in detail. If `doctor` says the backend package
is missing entirely, install it from the table in A1.

### RemEx doesn't appear in the app menu

```bash
# KDE Plasma
kbuildsycoca6

# GNOME / Cinnamon / XFCE
update-desktop-database ~/.local/share/applications
```

If it still doesn't appear, log out and back in.

### "PipeWire native library not available" in the log

The native capture library is missing from the install. Re-run `install.sh` (it verifies
the file and warns if a system library is missing). To check manually:

```bash
ls ~/.local/share/remex-agent/runtimes/linux-x64/native/libremex_linux_bridge.so
ldd ~/.local/share/remex-agent/runtimes/linux-x64/native/libremex_linux_bridge.so
```

Any `not found` line means a system package is missing (usually `pipewire`).

### Port 5005 already in use

Another program — often a stale RemEx from an old install — is holding the port:

```bash
ss -tlnp | grep 5005
```

Note the process name/PID it prints, stop that program (`kill <PID>`), and start RemEx
again. Running the new `install.sh` also prevents old RemEx copies from auto-starting at
login again.

### Two RemEx windows / RemEx starts twice at login

Old and new autostart entries are both firing. Fix:

```bash
rm -f ~/.config/autostart/remex-client.desktop
ls ~/.config/autostart | grep -i remex   # should list ONLY remex-agent.desktop
```

(New installs do this cleanup automatically.)

---

## Remote access from outside your home (Tailscale)

Out of the box, phone and PC must be on the same network. For secure access from anywhere:

```bash
# On the PC
curl -fsSL https://tailscale.com/install.sh | sh
sudo tailscale up
```

Then install the **Tailscale** app on your phone, sign in to the same account, and use the
PC's Tailscale IP (starts with `100.`) in the RemEx phone app.

---

## [B] Building from source (developers)

### B1. Prerequisites

| Tool | Arch/CachyOS | Ubuntu/Debian |
|------|--------------|---------------|
| .NET 10 SDK | `sudo pacman -S dotnet-sdk` | see [dotnet.microsoft.com](https://dotnet.microsoft.com/download) |
| cmake | `sudo pacman -S cmake` | `sudo apt install cmake` |
| pkg-config | `sudo pacman -S pkgconf` | `sudo apt install pkg-config` |
| PipeWire dev headers | `sudo pacman -S pipewire` | `sudo apt install libpipewire-0.3-dev` |
| gcc / clang | `sudo pacman -S base-devel` | `sudo apt install build-essential` |

**Checkpoint:** `dotnet --version`, `cmake --version`, `pkg-config --version`, and
`gcc --version` all print a version. ✓

### B2. Build

```bash
./installer/build-linux.sh
```

One command builds everything: the .NET app, the native PipeWire bridge, and the package.

**Checkpoint:**

```bash
ls installer/Output/
# → remex-agent-v2.1.0-linux-x64.tar.gz
```

Verify the native bridge made it into the package:

```bash
tar -tzf installer/Output/remex-agent-v*.tar.gz | grep libremex_linux_bridge
```

**Checkpoint:** prints a `runtimes/linux-x64/native/libremex_linux_bridge.so` line. ✓

### B3. Install your build

```bash
cd installer/Output
tar -xzf remex-agent-v2.1.0-linux-x64.tar.gz
./remex-agent-v2.1.0-linux-x64/install.sh
```

### B4. Dev shortcut (run without packaging)

```bash
dotnet run --project remex.agent
```

The publish/packaging steps are only needed for a real install; `dotnet run` is fine for
iterating. The health check works here too:

```bash
dotnet run --project remex.agent -- --doctor
```

### Build problems

- **cmake can't find PipeWire:** `pkg-config --exists libpipewire-0.3 && echo found` — if
  not found, install the PipeWire dev package (table in B1).
- **`dotnet` not found:** install the .NET 10 SDK (table in B1) and re-open the terminal.
