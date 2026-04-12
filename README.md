# RemEx ⚡

## Remote Execution Command Center

[![.NET 10](https://img.shields.io/badge/.NET-10.0-512BD4?logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/)
[![Avalonia UI](https://img.shields.io/badge/Avalonia-11.3-7B2BFC?logo=data:image/svg+xml;base64,&logoColor=white)](https://avaloniaui.net/)
![Android](https://img.shields.io/badge/Android-Compose%20%7C%20Material%203-3DDC84?logo=android&logoColor=white)
![Platform](https://img.shields.io/badge/Platform-Windows%20%7C%20Linux%20%7C%20Android-22C55E)
[![License: MIT](https://img.shields.io/badge/License-MIT-F59E0B)](LICENSE)
![Version](https://img.shields.io/badge/Version-1.10.0-FF6B6B)

A high-performance, cross-platform **command center** for remote PC management.\
Real-time hardware telemetry · Remote desktop · QR Pairing · App launcher · Process manager\
Available as a polished glassmorphic **.NET / Avalonia** desktop app and a full **native Android** app (Kotlin + Jetpack Compose) powered by a .NET NativeAOT JNI core.

---

## 🚀 Quick Install & Run

### Windows Desktop Client (Installer)

1. Go to the [**Releases**](https://github.com/clindsay94/remex/releases) page and download the latest `RemEx-v1.10.0-Setup.exe`.
2. Run the installer and follow the wizard:
   - Choose **Client only** or **Client + Windows Service** (service starts automatically at boot).
3. Launch **RemEx** from the Start Menu or desktop shortcut.

### Linux Desktop Client & Host (Automated)

1. Go to the [**Releases**](https://github.com/clindsay94/remex/releases) page and download:
   - `remex-client-v1.10.0-linux-x64.tar.gz` (for the Desktop UI)
   - `remex-host-v1.10.0-linux-x64.tar.gz` (for the background service)
2. **Extract** the archives:
   ```bash
   tar -xzf remex-client-v1.10.0-linux-x64.tar.gz
   tar -xzf remex-host-v1.10.0-linux-x64.tar.gz
   ```
3. **Install** via the provided scripts (installs to `~/.local/share/` and sets up `systemd` user services):
   ```bash
   # Install Client (adds to Applications menu)
   ./remex-client-v1.10.0-linux-x64/install.sh install

   # Install Host (starts background service)
   ./remex-host-v1.10.0-linux-x64/install.sh install
   ```

### Native Android App

1. Go to the **Releases** page and download the latest `RemEx-v1.10.0.apk`.
2. Open the downloaded `.apk` file to install.
3. Use the new **QR Scanner** on the connection screen to pair instantly with your PC!

---

## 💎 Key Features (v1.10.0)

### 🐧 Full Linux Integration
RemEx is now a first-class citizen on Linux. We've implemented native capture and telemetry services for a seamless experience on Ubuntu, Fedora, Arch, and more.
- **Native Screen Capture:** High-performance Linux frame capture.
- **Input Simulation:** Full mouse/keyboard control for Linux hosts.
- **Systemd Support:** Automated user-level service management.

### 📱 QR Code Pairing
No more typing IP addresses. The desktop client now generates a secure QR code that the Android app scans to auto-configure and connect instantly.
- **Found in:** Home Screen -> "Pair New Device" (Desktop) / Connection Screen -> "Scan QR" (Android).

### 🖥️ Glassmorphic Dashboard (Avalonia)
- **Cinematic Boot:** 5-second animated materialization effect on startup.
- **Dark Glass Design:** Layered translucent cards with dynamic gradient theming.
- **Live Localization:** Switch between 8 languages (en, es, hi, id, pl, pt-BR, tr, uk) instantly without restart.
- **Interactive Onboarding:** 9-page tutorial that intelligently adapts to your operating system.

### 📡 Real-Time Telemetry & Desktop
- **HWInfo (Windows) / lmsensors (Linux):** Massive sensor support with smart deduplication.
- **GPU-Accelerated Remote Desktop:** Low-latency DXGI capture (Windows) and optimized Linux capture.
- **Free-form Canvas:** Arrange live sensor cards on a 4,000x4,000 zoomable workspace.

### 🔐 Production Readiness
- **Strict Validation:** Robust input validation across all network layers.
- **Async & Null Safety:** Hardened codebase following strict architectural guidelines.
- **Access Key Security:** Optional shared-secret authentication for all communication.

---

## 🏗️ Architecture

```text
Remex.Core/              Shared models, messages, and validation logic
                         ↳ Compiled as libRemexCore.so (NativeAOT) for Android JNI
Remex.Host/              Headless ASP.NET service (Minimal APIs + WebSocket + mDNS)
Remex.Client/            Shared Avalonia UI (Views, ViewModels, Themes)
RemEx.Android/           Native Android app (Kotlin + Jetpack Compose + JNI)
docs/                    Architectural guidelines (Async, Null Safety, Validation)
installer/               Build scripts for Windows (Inno Setup) and Linux (bash)
```

### Communication Protocols

| Protocol | Purpose | Default Port |
| :--- | :--- | :--- |
| WebSocket `/ws` | Telemetry + Power Commands | 5005 |
| WebSocket `/ws/desktop` | Remote Desktop Stream | 5005 |
| TCP | External Script Ingress | 8338 |
| Named Pipe | Local IPC (Client ↔ Service) | `RemExLocalIPC` |

---

## 🛠️ Building for Developers

### Prerequisites
- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- Android SDK (see [`docs/ANDROID_SETUP.md`](docs/ANDROID_SETUP.md))

### Desktop & Host
```bash
# Run Host
dotnet run --project Remex.Host

# Run Client
dotnet run --project Remex.Client.Desktop
```

### Linux Packages
```bash
# Build both Client and Host .tar.gz packages
./installer/build-linux.sh
```

### Android (Hardened Fresh Build)
We recommend using our fresh pipeline to ensure the NativeAOT `.so` is perfectly synced:
```powershell
.\scripts\android-fresh.ps1 -Configuration Release
```

---

## 📖 Guidelines & Docs
We've introduced strict development guidelines to maintain the "Production Ready" standard:
- [**Android Setup Guide**](docs/ANDROID_SETUP.md)
- [**Async Guidelines**](docs/ASYNC_GUIDELINES.md)
- [**Null Safety Guidelines**](docs/NULL_SAFETY_GUIDELINES.md)
- [**Validation Guidelines**](docs/VALIDATION_GUIDELINES.md)

---

## Contributing
See [`CONTRIBUTING.md`](CONTRIBUTING.md) for detailed conventions.

## License
[MIT](LICENSE) — Copyright © 2026 Connor
