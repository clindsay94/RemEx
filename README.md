# Remex ⚡ Command Center

[![.NET 10](https://img.shields.io/badge/.NET-10.0-512BD4?logo=dotnet)](https://dotnet.microsoft.com/)
[![Avalonia UI](https://img.shields.io/badge/Avalonia-UI-purple)](https://avaloniaui.net/)
[![Platform](https://img.shields.io/badge/Platform-Windows%20%7C%20Linux%20%7C%20Android-green)](#)

A high-performance, cross-platform **command center** for remote PC management. Built from the ground up with **.NET 10** and **Avalonia UI**, Remex delivers real-time hardware telemetry and remote execution in a sleek, NOC-style dashboard — identical on Windows, Linux, and Android.

---

## ✨ Features

- **Sleek Glassmorphic UI** — Edge-to-edge transparent layout using OS-level Mica/Acrylic blur, dropping native chrome for a custom interactive titlebar.
- **Dark Glass Design System** — Layered translucent cards (`.glass-card`), vibrant fluid hover states, and dynamic gradient variables across all views.
- **Command Center Dashboard** — Three-view "on-glass" navigation with smooth horizontal `PageSlide` transitions:
  - **Home** — NOC-style overview with pinned sensor cards and connection status.
  - **Sensor Workspace** — Free-form Canvas with draggable, resizable sensor cards and a collapsible staging drawer.
  - **Settings** — Snap-to-grid tuning, grid size configuration, and persisted remote connection address.
- **Real-time Telemetry** — HWiNFO (Windows) / lmsensors (Linux) streamed over WebSocket.
- **Customizable Sensor Cards** — Resizable, themed, with sparkline graphs (Bar, Line, Area, Gauge).
- **Remote Execution** — Lock, Reboot, Shutdown, and custom commands via REST endpoints.
- **Cross-Platform Parity** — Shared Avalonia UI across Desktop and Android environments.

---

## 🏗️ Architecture

```text
Remex.Core/              Shared abstractions, models, and message contracts
Remex.Host/              Headless ASP.NET background service (Minimal APIs, WebSocket hub)
Remex.Client/            Shared Avalonia UI (Views, ViewModels, Controls, Services)
Remex.Client.Desktop/    Thin desktop head (entry point only)
Remex.Client.Android/    Thin Android head (Activity only)
Remex.Core.Tests/        xUnit unit tests for Core models and serialization
Remex.Host.Tests/        xUnit integration tests for Host endpoints
```

**Dashboard Structure**
```text
MainWindow
  └─ ShellView (TransitioningContentControl + CrossFade 250ms)
       ├─ HomeView        — Pinned sensors in UniformGrid, connection status
       ├─ CanvasView      — Free-form Canvas, DraggableCard items, staging drawer
       └─ SettingsView    — Connection, snap-to-grid, grid size config
```
Navigation is fully "on-glass" — buttons are embedded within each view's content area, keeping the layout clean and scaling seamlessly across Desktop and Android.

---

## 🚀 Getting Started

### Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download)

### Build & Run

**Start the Host Service** (telemetry + remote execution):
```bash
dotnet run --project Remex.Host
```

**Start the Desktop Client**:
```bash
dotnet run --project Remex.Client.Desktop
```

**Run Tests**:
```bash
dotnet test Remex.sln
```

### Install as a Windows Service

The Host can run as a Windows Service that starts automatically at boot — no login required.

```powershell
# Open PowerShell as Administrator:

# Install and start the service
.\scripts\install-service.ps1 -Action Install

# Check service status
.\scripts\install-service.ps1 -Action Status

# Remove the service
.\scripts\install-service.ps1 -Action Uninstall
```

> **Note:** When the service is running, the Desktop client will detect the occupied port and connect to the existing host instead of starting its own.

---

## 📡 API Reference

| Protocol | Purpose | Port |
| --- | --- | --- |
| **WebSocket** (`/ws`) | Real-time sensor telemetry stream | 5005 |
| **REST** | One-shot actions (Lock, Reboot, Execute) | 5005 |
| **TCP** | Remote Command Ingress (Shutdown, Restart, WoL, Lock) | 8338 (configurable) |
| **Named Pipe** | Local IPC (UI to Background Service) | `RemExLocalIPC` |

*Full API documentation is available in [`/docs/API_CONTRACTS.md`](docs/API_CONTRACTS.md).*

⚠️ **Security Warning:** The TCP Command Ingress endpoint (default port 8338) allows remote execution of power commands (Shutdown, Restart, Force Restart, Restart to UEFI, Lock, and Wake-on-LAN). Ensure this port is protected by a firewall and only accessible from trusted networks.

---

## 📄 License

*Private project.*
