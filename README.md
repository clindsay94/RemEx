<div align="center">

# RemEx ⚡

**Remote Execution Command Center**

[![.NET 10](https://img.shields.io/badge/.NET-10.0-512BD4?logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/)
[![Avalonia UI](https://img.shields.io/badge/Avalonia-11.3-7B2BFC?logo=data:image/svg+xml;base64,&logoColor=white)](https://avaloniaui.net/)
[![Platform](https://img.shields.io/badge/Platform-Windows%20%7C%20Linux%20%7C%20Android-22C55E)](#)
[![License: MIT](https://img.shields.io/badge/License-MIT-F59E0B)](LICENSE)
[![Version](https://img.shields.io/badge/Version-1.0.0-FF6B6B)](#)

A high-performance, cross-platform **command center** for remote PC management.\
Real-time hardware telemetry · Remote desktop · App launcher · Process manager\
Built with **.NET 10** and **Avalonia UI** — identical on Windows, Linux, and Android.

</div>

---

## Features

### 🖥️ Glassmorphic Dashboard

- **Dark glass design system** — layered translucent cards, fluid hover states, dynamic gradient theming
- **Sidebar navigation** — collapsible `SplitView` with compact icon mode and animated transitions
- **Customizable themes** — accent colors, corner radius, glass opacity, glow strength, and canvas backgrounds
- **Home screen** — NOC-style overview with connection status, quick-action pills, and pinned live sensors

### 📡 Real-Time Telemetry

- **HWiNFO** (Windows) / **lmsensors** (Linux) sensor data streamed over WebSocket
- **Free-form canvas** — drag, resize, and arrange sensor cards on a 4000×4000 workspace
- **Sparkline graphs** — bar, line, area, and gauge visualizations per sensor
- **Pin to Home** — pin any sensor to the landing page for at-a-glance monitoring

### 🖳 Remote Desktop

- Live screen streaming over WebSocket with configurable quality, scale, and FPS
- Touch & stylus input forwarding (mouse, keyboard, S-Pen)
- Fullscreen immersive mode with virtual cursor pad
- Zoom/pan viewport for detail inspection

### ⏻ Remote Execution

- **Power commands** — Shutdown, Restart, Force Restart, Restart to UEFI, Lock
- **Wake-on-LAN** — send magic packets to wake machines on your network
- **TCP command ingress** — accept power commands from external scripts/tools on a configurable port

### ◈ App Launcher

- Define and sync app shortcuts between the client and host
- Launch applications remotely — including from Session 0 (Windows Service) into the interactive desktop
- Persistent local storage with host-sync fallback

### ▤ Task Manager

- Live process list with CPU and memory usage (polled every 2 seconds)
- Search, sort by name/CPU/memory, and kill processes remotely
- Cross-platform: Windows (`Process` API) and Linux (`/proc` filesystem)

---

## Architecture

```text
Remex.Core/              Shared models, messages, and service contracts
Remex.Host/              Headless ASP.NET background service (Minimal APIs + WebSocket)
Remex.Client/            Shared Avalonia UI (Views, ViewModels, Controls, Services)
Remex.Client.Desktop/    Desktop entry point (Windows / Linux)
Remex.Client.Android/    Android entry point (Activity)
Remex.Core.Tests/        xUnit tests — Core models and serialization
Remex.Host.Tests/        xUnit tests — Host endpoints and handlers
```

**Communication Protocols**

| Protocol | Purpose | Default Port |
| :--- | :--- | :--- |
| WebSocket `/ws` | Real-time telemetry + commands | 5005 |
| WebSocket `/ws/desktop` | Remote desktop streaming | 5005 |
| TCP | External command ingress | 8338 |
| Named Pipe | Local IPC (client ↔ service) | `RemExLocalIPC` |
| HTTP `GET /` | Health check | 5005 |

Full API documentation: [`docs/API_CONTRACTS.md`](docs/API_CONTRACTS.md)

---

## Getting Started

### Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- Android workload (for Android builds): `dotnet workload install android`

### Build & Run

**Host service** (telemetry + remote execution backend):

```bash
dotnet run --project Remex.Host
```

**Desktop client**:

```bash
dotnet run --project Remex.Client.Desktop
```

**Run tests**:

```bash
dotnet test Remex.sln
```

### Publish

**Desktop** (self-contained single-file, Windows x64):

```bash
dotnet publish Remex.Client.Desktop\Remex.Client.Desktop.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
```

**Android** (APK):

```bash
dotnet publish Remex.Client.Android\Remex.Client.Android.csproj -c Release -f net10.0-android
```

### Android Fresh Rebuild (Recommended)

When iterating quickly, use the hardened Android pipeline to avoid stale APKs and stale `libRemexCore.so`:

```powershell
# Debug: purge all repo bin/obj, clean, rebuild native lib, build APK, verify hashes/timestamps, install
.\scripts\android-fresh.ps1 -Configuration Debug -Install

# Debug without install
.\scripts\android-fresh.ps1 -Configuration Debug

# Release rebuild + verification
.\scripts\android-fresh.ps1 -Configuration Release
```

Direct Gradle tasks from `RemEx.Android`:

```powershell
.\gradlew.bat remexFreshInstallDebug --rerun-tasks --no-configuration-cache
.\gradlew.bat remexFreshAssembleDebug --rerun-tasks --no-configuration-cache
.\gradlew.bat remexFreshAssembleRelease --rerun-tasks --no-configuration-cache
```

### Install as a Windows Service

The host can run as a Windows Service that starts automatically at boot — no login required.

```powershell
# Install and start
.\scripts\install-service.ps1 -Action Install

# Check status
.\scripts\install-service.ps1 -Action Status

# Remove
.\scripts\install-service.ps1 -Action Uninstall
```

> When the service is running, the desktop client detects the occupied port and connects to the existing host instead of starting its own.

---

## Configuration

The host reads from `appsettings.json`:

```jsonc
{
  "Remex": {
    "CommandPort": 8338     // TCP command ingress port
  }
}
```

The client persists its dashboard layout, sensor positions, theme settings, and host address to a local JSON profile (auto-saved with a 2-second debounce).

---

## Contributing

See [`CONTRIBUTING.md`](CONTRIBUTING.md) for development setup, build commands, and project conventions.

---

## License

[MIT](LICENSE) — Copyright © 2026 Connor
