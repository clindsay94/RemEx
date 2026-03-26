<div align="center">

# RemEx ⚡

**Remote Execution Command Center**

[![.NET 10](https://img.shields.io/badge/.NET-10.0-512BD4?logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/)
[![Avalonia UI](https://img.shields.io/badge/Avalonia-11.3-7B2BFC?logo=data:image/svg+xml;base64,&logoColor=white)](https://avaloniaui.net/)
[![Android](https://img.shields.io/badge/Android-Compose%20%7C%20Material%203-3DDC84?logo=android&logoColor=white)](#)
[![Platform](https://img.shields.io/badge/Platform-Windows%20%7C%20Linux%20%7C%20Android-22C55E)](#)
[![License: MIT](https://img.shields.io/badge/License-MIT-F59E0B)](LICENSE)
[![Version](https://img.shields.io/badge/Version-1.1.0-FF6B6B)](#)

A high-performance, cross-platform **command center** for remote PC management.\
Real-time hardware telemetry · Remote desktop · App launcher · Process manager\
Available as a polished glassmorphic **.NET / Avalonia** desktop app and a full **native Android** app (Kotlin + Jetpack Compose) powered by a .NET NativeAOT JNI core.

</div>

---

## Clients

RemEx ships two distinct client experiences sharing the same host backend:

| Client | Stack | Target |
| :--- | :--- | :--- |
| **Desktop / Avalonia Android** | .NET 10 · Avalonia 11 · CommunityToolkit.Mvvm | Windows · Linux · Android (Avalonia) |
| **Native Android** (`RemEx.Android`) | Kotlin · Jetpack Compose · Material 3 · JNI → .NET NativeAOT | Android (arm64-v8a) |

The native Android app bundles `libRemexCore.so` — a NativeAOT-compiled version of `Remex.Core` — and calls it through a Kotlin JNI bridge (`RemexCoreClient`). This gives all WebSocket, mDNS, and command logic identical behavior on Android without a separate runtime.

---

## Features

### 🖥️ Glassmorphic Dashboard (Avalonia)

- **Dark glass design system** — layered translucent cards, fluid hover states, dynamic gradient theming
- **Sidebar navigation** — collapsible `SplitView` with compact icon mode (64 px) and expanded label mode (220 px)
- **Theme engine** — swap between BaseDarkGlass, CyberNOC, Monolith, and SolarFlare at runtime; override accent color, corner radius, glass opacity, and glow strength
- **DashboardBackground** — choose from solid, gradient, or animated canvas backgrounds
- **Home screen** — NOC-style overview with connection status, quick-action pills, and pinned live sensor cards

### 📱 Native Android App (Kotlin / Compose)

- **Material 3** design with dynamic color seeding (Material You), spring animations, and card-shape presets
- **Bottom NavigationBar + FAB** for one-handed navigation; settings opened as a modal flow
- **Personalization screen** — choose font family (system, Roboto, Montserrat, Nunito), theme seed color, and card shape preset (Rounded, Cut, Mixed)
- **Dashboard** — drag-and-drop card placement with animated previews and per-type shape support
- **Home-screen widgets** — Sensor, Resource Monitor, Remote Control, and general Remex widgets configurable from a dedicated `WidgetConfigActivity`
- **Splash screen** with smooth transition into the connection flow

### 📡 Real-Time Telemetry

- **HWiNFO** (Windows) / **lmsensors** (Linux) sensor data streamed over WebSocket
- **Free-form canvas** — drag, resize, and arrange sensor cards on a 4 000 × 4 000 workspace
- **Sparkline graphs** — bar, line, area, and gauge visualizations per sensor (with brush/pen caching for performance)
- **Pin to Home** — pin any sensor to the landing page for at-a-glance monitoring
- **mDNS service discovery** — the host advertises itself via mDNS so clients can auto-detect it on the local network without entering an IP

### 🖳 Remote Desktop

- Live screen streaming over a dedicated WebSocket (`/ws/desktop`) with configurable JPEG quality, downscale factor, and target FPS
- Touch & stylus input forwarding (mouse clicks, keyboard, S-Pen drag)
- Fullscreen immersive mode with virtual cursor pad
- Zoom/pan viewport for detail inspection
- JNI frame callback path for zero-copy delivery to the native Android renderer

### ⏻ Remote Execution

- **Power commands** — Shutdown, Restart, Force Restart, Restart to UEFI, Lock
- **Wake-on-LAN** — broadcast magic packets across all active physical NICs
- **TCP command ingress** — accept power commands from external scripts or tools on a configurable port

### 🔐 Access Key Security


| Layer | Files Modified | What was added |
|-------|---------------|----------------|
| **Host** | HostBootstrapper.cs | `Remex:AccessKey` config read, constant-time validation on both `/ws` and `/ws/desktop` endpoints, returns 401 on mismatch |
| **Host config** | appsettings.json | `Remex.AccessKey` section (empty = disabled) |
| **Core models** | DashboardProfile.cs | `AccessKey` property for desktop profile persistence |
| **Core clients** | RemexNativeClient.cs, RemexDesktopClient.cs | `accessKey` parameter + `BuildUri()` helper appending `?key=` |
| **JNI bridge** | AndroidNativeExports.cs | `AccessKey` in init request, threaded through all desktop endpoints |
| **Avalonia UI** | SettingsView.axaml, SettingsViewModel.cs, ConnectionViewModel.cs | Password field in Settings, persisted to profile, applied on connect/reconnect |
| **Android** | SettingsManager.kt, ConnectionViewModel.kt, ConnectionScreen.kt | DataStore persistence, Key icon text field, passed through JNI init JSON |

**Key design decisions:**
- Empty access key = authentication disabled (backward compatible)
- Key sent as `?key=<value>` query parameter on WebSocket URI
- Server uses `CryptographicOperations.FixedTimeEquals` to prevent timing attacks
- Configurable from all UIs (no JSON file editing required) 


### ◈ App Launcher

- Define app shortcuts on the host, sync them to the client, and launch remotely
- Supports Session 0 → interactive-desktop launching on Windows Services
- Persistent local storage with host-sync fallback; view-model factory injection for safe JSON parsing

### ▤ Task Manager

- Live process list with CPU and memory usage (polled every 2 seconds)
- **Search** by process name; **sort** by name, CPU, or memory — ascending or descending
- Kill processes remotely with automatic elevation fallback (`pkexec` on Linux, admin prompt on Windows)
- Cross-platform: Windows `Process` API and Linux `/proc` filesystem

---

## Architecture

```text
Remex.Core/              Shared models, messages, and service contracts
                         ↳ Compiled as libRemexCore.so (NativeAOT) for Android JNI
Remex.Host/              Headless ASP.NET background service (Minimal APIs + WebSocket + mDNS)
Remex.Client/            Shared Avalonia UI (Views, ViewModels, Controls, Services, Themes)
Remex.Client.Desktop/    Desktop entry point — Windows / Linux
Remex.Client.Android/    Avalonia Android entry point (Activity + M3 theme overrides)
RemEx.Android/           Native Android app — Kotlin + Jetpack Compose + JNI → libRemexCore.so
Remex.Core.Tests/        xUnit tests — Core models and serialization
Remex.Host.Tests/        xUnit tests — Host endpoints and handlers
scripts/                 Utility scripts — Windows Service installer, android-fresh pipeline
```

**Communication Protocols**

| Protocol | Purpose | Default Port |
| :--- | :--- | :--- |
| WebSocket `/ws` | Real-time telemetry + commands | 5005 |
| WebSocket `/ws/desktop` | Remote desktop streaming | 5005 |
| TCP | External command ingress | 8338 |
| Named Pipe | Local IPC (desktop client ↔ Windows Service) | `RemExLocalIPC` |
| mDNS | Host auto-discovery on LAN | — |
| HTTP `GET /` | Health check | 5005 |

Full API documentation: [`docs/API_CONTRACTS.md`](docs/API_CONTRACTS.md)

---

## Getting Started

### Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- Android workload (for Avalonia Android builds): `dotnet workload install android`
- Android Studio or a JDK + Android SDK (for the native `RemEx.Android` Gradle build)

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

**Avalonia Android APK**:

```bash
dotnet publish Remex.Client.Android\Remex.Client.Android.csproj -c Release -f net10.0-android
```

### Native Android App — Hardened Fresh Build (Recommended)

The native `RemEx.Android` Gradle project embeds `libRemexCore.so` built from `Remex.Core`. Use the hardened pipeline to guarantee a fresh native library and verified APK every time:

```powershell
# From repo root — purge bin/obj, rebuild libRemexCore.so, assemble APK, verify SHA-256, install
.\scripts\android-fresh.ps1 -Configuration Debug -Install

# Build only (no install)
.\scripts\android-fresh.ps1 -Configuration Debug
.\scripts\android-fresh.ps1 -Configuration Release
```

Or run Gradle tasks directly from `RemEx.Android/`:

```powershell
.\gradlew.bat remexFreshInstallDebug --rerun-tasks --no-configuration-cache
.\gradlew.bat remexFreshAssembleDebug --rerun-tasks --no-configuration-cache
.\gradlew.bat remexFreshAssembleRelease --rerun-tasks --no-configuration-cache
```

The custom `SyncRemexCoreSoTask` and `VerifyRemexCoreInApkTask` Gradle tasks enforce SHA-256 hash matching between the published `.so` and the APK-embedded copy, failing fast on any stale artifact.

### Install as a Windows Service

The host can run as a Windows Service that starts automatically at boot — no login required.

```powershell
.\scripts\install-service.ps1 -Action Install    # Install and start
.\scripts\install-service.ps1 -Action Status     # Check status
.\scripts\install-service.ps1 -Action Uninstall  # Remove
```

> When the service is running, the desktop client detects the occupied port and connects to the existing host instead of starting its own. The service manager UI is accessible from the Settings panel.

---

## Configuration

The host reads from `appsettings.json`:

```jsonc
{
  "Remex": {
    "CommandPort": 8338,  // TCP command ingress port
    "AccessKey": ""       // Shared key for WebSocket auth (leave empty to disable)
  }
}
```

The Avalonia client persists dashboard layout, sensor positions, theme settings, and host address to a local JSON profile (auto-saved with a 2-second debounce). The native Android client uses `SettingsManager` (Kotlin `DataStore`) for all preferences, including theme seed color, font family, and card-shape preset.

---

## Contributing

See [`CONTRIBUTING.md`](CONTRIBUTING.md) for development setup, build commands, and project conventions.

---

## License

[MIT](LICENSE) — Copyright © 2026 Connor
