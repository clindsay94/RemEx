# RemEx — Linux Installation Guide

This document covers two scenarios:

- **[A] Installing a release package** — you downloaded a `.tar.gz` from the Releases page.
- **[B] Building from source** — you cloned the repository and want to build and install yourself.

If you are an end user, follow **[A]**. If you are a developer or contributor, follow **[B]**.

---

## [A] Installing from a Release Package

### A1. Prerequisites

RemEx talks to the desktop via `xdg-desktop-portal` (RemoteDesktop + ScreenCast) and streams pixels through PipeWire. The portal-backend package is **desktop-specific** — install the one matching your DE.

| Package | Purpose | Required | Arch / CachyOS / Manjaro | Ubuntu / Debian / Pop!_OS | Fedora |
|---|---|---|---|---|---|
| `xdg-desktop-portal` | Portal frontend (RemoteDesktop / ScreenCast) | Required | `sudo pacman -S xdg-desktop-portal` | `sudo apt install xdg-desktop-portal` | `sudo dnf install xdg-desktop-portal` |
| `xdg-desktop-portal-kde` | Portal backend for KDE Plasma | KDE | `sudo pacman -S xdg-desktop-portal-kde` | `sudo apt install xdg-desktop-portal-kde` | `sudo dnf install xdg-desktop-portal-kde` |
| `xdg-desktop-portal-gnome` | Portal backend for GNOME | GNOME | `sudo pacman -S xdg-desktop-portal-gnome` | `sudo apt install xdg-desktop-portal-gnome` | `sudo dnf install xdg-desktop-portal-gnome` |
| `xdg-desktop-portal-wlr` | Portal backend for sway / Hyprland / other wlroots | Wayland WM | `sudo pacman -S xdg-desktop-portal-wlr` | `sudo apt install xdg-desktop-portal-wlr` | `sudo dnf install xdg-desktop-portal-wlr` |
| `pipewire` + `wireplumber` | Screen capture stream | Required | `sudo pacman -S pipewire wireplumber` | `sudo apt install pipewire wireplumber` | `sudo dnf install pipewire wireplumber` |
| `libei` | Wayland-native input injection | Recommended (Wayland) | `sudo pacman -S libei` | `sudo apt install libei1` | `sudo dnf install libei` |
| `libevdev` | uinput virtual device support | Recommended | `sudo pacman -S libevdev` | `sudo apt install libevdev2` | `sudo dnf install libevdev` |
| `ffmpeg` | H.264 hardware encoder (VAAPI/libx264) + MJPEG fallback | Recommended | `sudo pacman -S ffmpeg` | `sudo apt install ffmpeg` | `sudo dnf install ffmpeg` |

Optional but useful (RemEx probes for these at startup and uses whichever is present):

| Tool | Purpose | Arch | Ubuntu/Debian |
|---|---|---|---|
| `kdotool` | Window / cursor control on KDE | `sudo pacman -S kdotool` (AUR) | not in repos — build from source |
| `xdotool` | X11 input + window control | `sudo pacman -S xdotool` | `sudo apt install xdotool` |
| `ydotool` | Wayland-generic uinput input | `sudo pacman -S ydotool` | `sudo apt install ydotool` |
| `spectacle` | KDE screenshot fallback | `sudo pacman -S spectacle` | `sudo apt install kde-spectacle` |
| `grim` + `slurp` | wlroots screenshot fallback | `sudo pacman -S grim slurp` | `sudo apt install grim slurp` |

**Verify the portal stack is healthy:**

```bash
# Frontend must expose RemoteDesktop and ScreenCast for KDE/GNOME
busctl --user introspect org.freedesktop.portal.Desktop /org/freedesktop/portal/desktop \
  | grep -E 'RemoteDesktop|ScreenCast'

# PipeWire must be running
systemctl --user status pipewire wireplumber

# Or use the bundled doctor (after install)
~/.local/share/remex-host/remex.agent --doctor
```

**Checkpoint:** `busctl introspect` shows both `org.freedesktop.portal.RemoteDesktop` and `org.freedesktop.portal.ScreenCast`. PipeWire and WirePlumber are `Active: active (running)`. ✓

If `RemoteDesktop` is missing even though the backend is installed, jump to the troubleshooting entry **"RemoteDesktop interface unavailable even though xdg-desktop-portal-kde is installed"** below.

---

### A2. Extract the package

```bash
# Replace the filename with the version you downloaded
tar -xzf remex-client-v2.0.0-linux-x64.tar.gz
```

You will get a folder named `remex-client-v2.0.0-linux-x64/`.

**Checkpoint:** `ls remex-client-v2.0.0-linux-x64/` shows `install.sh` and `remex.desktop`. ✓

---

### A3. Run the installer

```bash
./remex-client-v2.0.0-linux-x64/install.sh install
```

This copies the application to `~/.local/share/remex-client/`, creates a launcher symlink at `~/.local/bin/remex-client`, and registers the app in your application menu.

**Checkpoint:** The installer prints `RemEx Client installed.` with no errors. ✓

---

### A4. Verify the install

```bash
# Verify the binary is launchable
~/.local/bin/remex-client --version 2>/dev/null || \
  echo "binary is present — GUI app, no --version flag"

# Verify the .desktop entry was created
ls ~/.local/share/applications/remex-client.desktop
```

**Checkpoint:** Both commands succeed without "not found" errors. ✓

---

### A5. Launch

- **Application menu:** Look for **RemEx** in your app launcher. If it does not appear immediately, log out and back in (or run `kbuildsycoca6` on KDE / `update-desktop-database ~/.local/share/applications` on GNOME).
- **Terminal:** `remex-client`

---

### A6. Install the Host service (optional — only needed on the machine being controlled)

The **host** is a background service that runs on the PC you want to remotely access. It is not required on the machine running only the client.

```bash
tar -xzf remex-host-v2.0.0-linux-x64.tar.gz
./remex-host-v2.0.0-linux-x64/install.sh install
```

**Checkpoint:** Service is running:

```bash
systemctl --user status remex-host
```

The output should show `Active: active (running)`. ✓

View live logs:

```bash
journalctl --user -u remex-host -f
```

---

### A7. Uninstall

```bash
# Uninstall client
~/.local/share/remex-client/install.sh uninstall

# Uninstall host service
~/.local/share/remex-host/install.sh uninstall
```

### A8. Secure Remote Access via Tailscale

By default, RemEx works on your local home network. If you need to access your PC securely from outside your home network (e.g., over cellular data or from a work Wi-Fi):

1. **Automated Setup:** During host installation, the installer will interactively prompt you:
   ```text
   Would you like to configure Tailscale for secure remote access from outside your home network? [y/N]
   ```
   Answering `y` will check for Tailscale, install it if missing, and offer to start and authenticate it.

2. **Manual Installation:**
   ```bash
   # Install Tailscale
   curl -fsSL https://tailscale.com/install.sh | sh
   
   # Start the service and log in
   sudo tailscale up
   ```

3. **Get Your Static IP:**
   - On your Android phone, download the **Tailscale** app and log into the same account.
   - Copy the private IP (starts with `100.x.y.z`) listed for your Linux host.
   - Enter this IP in the RemEx Android app to connect securely from anywhere!

---

---

## [B] Building from Source

### B1. Prerequisites

You need the following tools installed before running the build script. **All** of them are required — the build will fail partway through if any are missing.

| Tool | Purpose | Install (Arch/CachyOS) | Install (Ubuntu/Debian) |
|------|---------|----------------------|------------------------|
| .NET 10 SDK | Compile the .NET projects | `sudo pacman -S dotnet-sdk` | See [dotnet.microsoft.com](https://dotnet.microsoft.com/download) |
| cmake | Build the native PipeWire bridge | `sudo pacman -S cmake` | `sudo apt install cmake` |
| pkg-config | Locate PipeWire headers | `sudo pacman -S pkgconf` | `sudo apt install pkg-config` |
| PipeWire dev headers | Native bridge compilation | `sudo pacman -S pipewire` | `sudo apt install libpipewire-0.3-dev` |
| gcc / clang | C compiler for the bridge | `sudo pacman -S base-devel` | `sudo apt install build-essential` |

**Checkpoint:** Verify everything is present before continuing:

```bash
dotnet --version      # should print 10.x.x
cmake --version       # should print cmake version 3.x
pkg-config --version  # should print a version number
gcc --version         # or: clang --version
```

All four commands must succeed. ✓

---

### B2. Clone the repository (if you haven't already)

```bash
git clone https://github.com/clindsay94/remex.git
cd remex
```

---

### B3. Run the build script

This single command does everything: compiles .NET projects, builds the native bridge, and packages everything into `.tar.gz` archives.

```bash
./installer/build-linux.sh
```

> **Do not run `dotnet publish` manually before this.** The script handles publishing internally. Running it yourself beforehand is harmless but wasteful.

You can build only one component if needed:

```bash
./installer/build-linux.sh --skip-host    # client package only
./installer/build-linux.sh --skip-client  # host package only
```

The build typically takes 2–5 minutes on first run (NuGet restore + cmake configure). Subsequent builds are faster.

---

### B4. Verify the build output

```bash
ls installer/Output/
```

**Checkpoint:** You should see files like:

```
remex-client-v2.0.0-linux-x64.tar.gz
remex-host-v2.0.0-linux-x64.tar.gz
```

Both `.tar.gz` files must be present. ✓

Verify the native bridge made it into each package:

```bash
# Client package contains the native bridge
tar -tzf installer/Output/remex-client-v2.0.0-linux-x64.tar.gz | grep libremex_linux_bridge

# Host package contains it too
tar -tzf installer/Output/remex-host-v2.0.0-linux-x64.tar.gz | grep libremex_linux_bridge
```

**Checkpoint:** Both `grep` commands print a line like `remex-client-.../runtimes/linux-x64/native/libremex_linux_bridge.so`. ✓

If either grep returns nothing, the native bridge did not get packaged. Check that cmake built successfully — the build script prints `Native bridge → ...` when it succeeds.

---

### B5. Install from the built packages

Follow steps **A2 → A7** above, pointing at the files in `installer/Output/` instead of downloaded files.

```bash
cd installer/Output

# Install the client
tar -xzf remex-client-v2.0.0-linux-x64.tar.gz
./remex-client-v2.0.0-linux-x64/install.sh install

# Install the host service (optional, only on the controlled machine)
tar -xzf remex-host-v2.0.0-linux-x64.tar.gz
./remex-host-v2.0.0-linux-x64/install.sh install
```

---

### B6. Running directly from the publish directory (dev shortcut)

If you want to run without going through the full package/install cycle — for example, after making a code change — use:

```bash
dotnet publish remex.desktop -c Release -r linux-x64 --self-contained
./remex.desktop/bin/Release/net10.0/linux-x64/publish/remex.desktop
```

The MSBuild targets in the `.csproj` automatically copy `libremex_linux_bridge.so` into `publish/runtimes/linux-x64/native/` during the publish step, so no manual copying is needed.

---

---

## Troubleshooting

### RemoteDesktop interface unavailable even though xdg-desktop-portal-kde is installed

**Symptom (host log):**

```
warn: ...LinuxPortalRemoteDesktopSessionService[0]
      Portal org.freedesktop.portal.RemoteDesktop.CreateSession returned a D-Bus error.
      DBusException: org.freedesktop.DBus.Error.UnknownMethod:
      No such interface "org.freedesktop.portal.RemoteDesktop" on object at path
      /org/freedesktop/portal/desktop
fail: ...LinuxCaptureSessionLifetime[0]
      LinuxCaptureSessionLifetime: portal session creation failed; PipeWire capture unavailable.
```

**Cause.** The `xdg-desktop-portal` frontend (D-Bus name `org.freedesktop.portal.Desktop`) was started by `systemd --user` before Plasma/GNOME pushed `XDG_CURRENT_DESKTOP`, `WAYLAND_DISPLAY`, etc. into the user manager. Without those vars, the frontend's portal-file matcher (`UseIn=KDE` / `UseIn=GNOME`) can't pick the correct backend, so it never exposes the `RemoteDesktop` interface — even after the backend process is later activated. The frontend's exposed-interface table is frozen at startup.

**One-line fix (try this first):**

```bash
systemctl --user import-environment \
  XDG_CURRENT_DESKTOP XDG_SESSION_TYPE WAYLAND_DISPLAY \
  DISPLAY DBUS_SESSION_BUS_ADDRESS XDG_DATA_DIRS
systemctl --user restart xdg-desktop-portal.service
```

Verify:

```bash
busctl --user introspect org.freedesktop.portal.Desktop /org/freedesktop/portal/desktop \
  | grep RemoteDesktop
# Expected: org.freedesktop.portal.RemoteDesktop  interface
```

Then restart the host:

```bash
systemctl --user restart remex-host
```

**RemEx ships an automated check.** The host detects this state and self-repairs on the first failed `CreateSession` call per process. The installer's `install.sh install` runs the same `import-environment + restart` pre-emptively, and `~/.local/share/remex-host/remex.agent --doctor` prints a detailed report plus an option to apply safe repairs.

If `--doctor` reports that the backend package is missing entirely (rather than "frontend stale"), install it via the dependency table in section A1.

---

### App does not appear in the application launcher after install

The `.desktop` entry is written to `~/.local/share/applications/`. Some desktop environments cache this directory and need a nudge:

```bash
# KDE Plasma
kbuildsycoca6

# GNOME / Cinnamon / XFCE
update-desktop-database ~/.local/share/applications
```

If it still does not appear, log out and back in.

---

### "PipeWire native library not available" in host logs

The native bridge `.so` is missing from the .NET runtime probing path. This usually means:

1. You installed the host package that was built **before** the fix that added the bridge to the client package — rebuild from source using **[B]** above.
2. Or the host was installed manually without running `install.sh` — run `~/.local/share/remex-host/install.sh install` to redo it.

Verify the file exists after install:

```bash
ls ~/.local/share/remex-host/runtimes/linux-x64/native/libremex_linux_bridge.so
ls ~/.local/share/remex-client/runtimes/linux-x64/native/libremex_linux_bridge.so
```

Check its runtime dependencies are satisfied:

```bash
ldd ~/.local/share/remex-host/runtimes/linux-x64/native/libremex_linux_bridge.so
```

Any line containing `not found` indicates a missing system library. Install the package that provides it (usually `pipewire`).

---

### Host service fails to start

```bash
# Check status
systemctl --user status remex-host

# Read the full log
journalctl --user -u remex-host --no-pager | tail -50
```

Common causes:
- **Port 5005 already in use:** Another process is on that port. Check `ss -tlnp | grep 5005`.
- **Missing PipeWire:** Install `pipewire` and ensure the PipeWire session is running (`systemctl --user status pipewire`).

---

### cmake fails during build

```bash
# Confirm cmake can find PipeWire
pkg-config --exists libpipewire-0.3 && echo "found" || echo "NOT FOUND"
```

If `NOT FOUND`, install the PipeWire development package for your distro (see the prerequisites table in B1).

---

### `dotnet` command not found

Install .NET 10 SDK from [dotnet.microsoft.com](https://dotnet.microsoft.com/download). On Arch/CachyOS:

```bash
sudo pacman -S dotnet-sdk
```

Verify with `dotnet --version` after install.
