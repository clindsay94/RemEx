# RemEx — Linux Installation Guide

This document covers two scenarios:

- **[A] Installing a release package** — you downloaded a `.tar.gz` from the Releases page.
- **[B] Building from source** — you cloned the repository and want to build and install yourself.

If you are an end user, follow **[A]**. If you are a developer or contributor, follow **[B]**.

---

## [A] Installing from a Release Package

### A1. Prerequisites

RemEx needs the PipeWire runtime to capture the screen. It is installed by default on most modern distros, but verify:

```bash
# Check PipeWire is present
pipewire --version
```

If the command is not found, install it:

```bash
# Arch / CachyOS / Manjaro
sudo pacman -S pipewire

# Ubuntu / Debian / Pop!_OS
sudo apt install pipewire

# Fedora
sudo dnf install pipewire
```

**Checkpoint:** `pipewire --version` prints a version number (e.g. `1.x.x`). ✓

---

### A2. Extract the package

```bash
# Replace the filename with the version you downloaded
tar -xzf remex-client-v2.0.0-linux-x64.tar.gz
```

You will get a folder named `remex-client-v2.0.0-linux-x64/`.

**Checkpoint:** `ls remex-client-v2.0.0-linux-x64/` shows `install.sh` and `Remex.Client.Desktop`. ✓

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

**Checkpoint:** Both `grep` commands print a line like `remex-client-.../libremex_linux_bridge.so`. ✓

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
dotnet publish Remex.Client.Desktop -c Release -r linux-x64 --self-contained
./Remex.Client.Desktop/bin/Release/net10.0/linux-x64/publish/Remex.Client.Desktop
```

The MSBuild targets in the `.csproj` automatically copy `libremex_linux_bridge.so` into the publish directory during the publish step, so no manual copying is needed.

---

---

## Troubleshooting

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

The native bridge `.so` is not next to the executable. This usually means:

1. You installed the host package that was built **before** the fix that added the bridge to the client package — rebuild from source using **[B]** above.
2. Or the host was installed manually without running `install.sh` — run `~/.local/share/remex-host/install.sh install` to redo it.

Verify the file exists after install:

```bash
ls ~/.local/share/remex-host/libremex_linux_bridge.so
ls ~/.local/share/remex-client/libremex_linux_bridge.so
```

Check its runtime dependencies are satisfied:

```bash
ldd ~/.local/share/remex-host/libremex_linux_bridge.so
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
