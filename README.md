# Remex [Rem(ote)ex(ecution)]

A high-performance, cross-platform **command center** for remote PC management. Built with .NET 10 and Avalonia UI, Remex delivers real-time hardware telemetry and remote execution in a sleek, NOC-style dashboard — identical on Windows, Linux, and Android.

---

## ✨ Features

- **Sleek Glassmorphic UI** — Edge-to-edge transparent layout using OS-level Mica/Acrylic blur, dropping native chrome for a custom interactive titlebar.
- **Dark Glass Design System** — Layered translucent cards (`.glass-card`), vibrant fluid hover states, and dynamic gradient variables across all views.
- **Command Center Dashboard** — Three-view "on-glass" navigation with smooth horizontal `PageSlide` transitions:
  - **Home** — NOC-style overview with pinned sensor cards and connection status
  - **Sensor Workspace** — Free-form Canvas with draggable, resizable sensor cards and a collapsible staging drawer
  - **Settings** — Snap-to-grid tuning, grid size configuration, and persisted remote connection address
- **Real-time Telemetry** — HWiNFO (Windows) / lmsensors (Linux) streamed over WebSocket
- **Customizable Sensor Cards** — Resizable, themed, with sparkline graphs (Bar, Line, Area, Gauge)
- **Remote Execution** — Lock, Reboot, Shutdown, and custom commands via REST endpoints
- **Cross-Platform Parity** — Shared Avalonia UI across Desktop and Android environments

---

## 🏗 Project Structure

```text
Remex.Core/              Shared abstractions, models, message contracts
Remex.Host/              Headless ASP.NET background service (Minimal APIs, WebSocket hub)
Remex.Client/            Shared Avalonia UI (Views, ViewModels, Controls, Services)
Remex.Client.Desktop/    Thin desktop head (entry point only)
Remex.Client.Android/    Thin Android head (Activity only)
Remex.Core.Tests/        xUnit unit tests for Core models and serialization
Remex.Host.Tests/        xUnit integration tests for Host endpoints
```

---

## 🚀 Getting Started

### Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download)

### Build

```bash
dotnet build Remex.sln
```

### Run

```bash
# Start the host service (telemetry + remote execution)
dotnet run --project Remex.Host

# Start the desktop client
dotnet run --project Remex.Client.Desktop
```

### Install as Windows Service

The Host can run as a Windows Service that starts automatically at boot — no login required.

```powershell
# Open PowerShell as Administrator, then:

# Install and start the service
.\scripts\install-service.ps1 -Action Install

# Check service status
.\scripts\install-service.ps1 -Action Status

# Remove the service
.\scripts\install-service.ps1 -Action Uninstall
```

> **Note:** When the service is running, the Desktop client will detect the occupied port and connect to the existing host instead of starting its own.

### Test

```bash
dotnet test Remex.sln
```

---

## 📡 API

| Protocol | Purpose | Port |
| --- | --- | --- |
| WebSocket (`/ws`) | Real-time sensor telemetry stream | 5005 |
| REST | One-shot actions (Lock, Reboot, Execute) | 5005 |

Full API documentation: [`/docs/API_CONTRACTS.md`](docs/API_CONTRACTS.md)

---

## 🎛 Dashboard Architecture

```text
MainWindow
  └─ ShellView (TransitioningContentControl + CrossFade 250ms)
       ├─ HomeView        — Pinned sensors in UniformGrid, connection status
       ├─ CanvasView      — Free-form Canvas, DraggableCard items, staging drawer
       └─ SettingsView    — Connection, snap-to-grid, grid size config
```

Navigation is fully "on-glass" — buttons are embedded within each view's content area. No external tab strips. This model scales identically across Desktop and Android.

---

## 🤖 Agent Team

Development is coordinated by a team of specialized AI agents. See:

- [`AGENTS.md`](AGENTS.md) — Core directives and tech stack
- [`AGENT_COMMS.md`](AGENT_COMMS.md) — Inter-agent communication log
- [`.agent/personas/`](.agent/personas/) — Individual agent role definitions

---

## 📄 License

Private project.
