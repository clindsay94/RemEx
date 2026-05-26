# RemEx ⚡

## Remote Execution Command Center

[![.NET 10](https://img.shields.io/badge/.NET-10.0-512BD4?logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/)
[![Avalonia UI](https://img.shields.io/badge/Avalonia-11.3-7B2BFC?logo=data:image/svg+xml;base64,&logoColor=white)](https://avaloniaui.net/)
![Android](https://img.shields.io/badge/Android-Compose%20%7C%20Material%203-3DDC84?logo=android&logoColor=white)
![Platform](https://img.shields.io/badge/Platform-Windows%20%7C%20Linux%20%7C%20Android-22C55E)
[![License: MIT](https://img.shields.io/badge/License-MIT-F59E0B)](LICENSE)
![Version](https://img.shields.io/badge/Version-2.0.0-FF6B6B)

A high-performance, cross-platform **command center** for remote PC management.\
Real-time hardware telemetry · Remote desktop · QR Pairing · App launcher · Process manager\
Available as a polished glassmorphic **.NET / Avalonia** desktop app and a full **native Android** app (Kotlin + Jetpack Compose) powered by a .NET NativeAOT JNI core.

> **Production release:** RemEx 2.0.0 is available on Google Play. Once the app is live on the Play Store, direct APK downloads from this repository will be discontinued.

---

## 🚀 Quick Install & Run

### Windows Desktop Client (Installer)

1. Go to the [**Releases**](https://github.com/clindsay94/remex/releases) page and download the latest `RemEx-v2.0.0-Setup.exe`.
2. Run the installer and follow the wizard:
   - Choose **Client only** or **Client + Windows Service** (service starts automatically at boot).
3. Launch **RemEx** from the Start Menu or desktop shortcut.

### Linux Desktop Client & Host

See **[`docs/LINUX_INSTALL.md`](docs/LINUX_INSTALL.md)** for the full installation guide including prerequisites, step-by-step instructions, verification checkpoints, and troubleshooting.

**Short version** (release package):
```bash
tar -xzf remex-client-v2.0.0-linux-x64.tar.gz
./remex-client-v2.0.0-linux-x64/install.sh install
```

### Native Android App

1. **Install from Google Play:** [**Download on the Play Store**](https://play.google.com/store/apps/details?id=com.clindsay94.remex)
2. Use the new **PIN-based pairing** or **QR Scanner** on the connection screen to pair instantly with your PC!

---

## 💎 Key Features (v2.0.0)

### ✨ Refined Android & Desktop UX
- **Haptic Feedback:** Tactile vibration feedback for all Android interactions.
- **Refined Themes:** 4 premium theme variants (CyberNOC, Monolith, SolarFlare, BaseDarkGlass).
- **Improved Android UI:** Completely refactored RemoteDesktopScreen with optimized touch handling.
- **Enhanced Customization:** Better settings and appearance customization across all platforms.

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
- **Live Localization:** Switch between 8 languages (en, es, hi, id, pl, pt-BR, tr, uk) from Settings.
- **Interactive Onboarding:** Desktop and Android both ship with refreshed getting-started walkthroughs for 2.0.

### 📡 Real-Time Telemetry & Desktop
- **HWInfo (Windows) / lmsensors (Linux):** Massive sensor support with smart deduplication.
- **GPU-Accelerated Remote Desktop:** Low-latency DXGI capture (Windows) and optimized Linux capture.
- **Free-form Canvas:** Arrange live sensor cards on a 4,000x4,000 zoomable workspace.

### 🔐 Production Readiness
- **TLS 1.3 Encryption:** All network traffic (WSS/TLS) is encrypted end-to-end.
- **PIN-based Pairing:** Secure cryptographic device pairing replaces plaintext access keys.
- **Strict Validation:** Robust input validation across all network layers.
- **Async & Null Safety:** Hardened codebase following strict architectural guidelines.

---

## 🚀 What's New in 2.0
RemEx 2.0 is a major milestone focusing on security, performance, and flagship features:
- **E2E Encryption:** Every connection is now secured via TLS 1.3 / WSS.
- **Secure Pairing:** ECDH P-256 pairing with a 6-digit PIN out-of-band binding.
- **Remote File Transfer:** Browse, upload, download, and manage files on your remote host.
- **Play Store Ready:** Fully optimized and hardened for production Google Play release.
- **Critical Fixes:** Resolved all 11 high-severity findings identified during the security audit.

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
| WSS `/ws` | Encrypted Telemetry + Power Commands | 5005 |
| WSS `/ws/desktop` | Encrypted Remote Desktop Stream | 5005 |
| TCP (TLS) | External Script Ingress (Encrypted) | 8338 |
| Named Pipe | Local IPC (Client ↔ Service) | `RemExLocalIPC` |

---

## 🛠️ Building for Developers

### Prerequisites
- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- Android SDK (see [`docs/ANDROID_SETUP.md`](docs/ANDROID_SETUP.md))
- Linux packages: `cmake`, `pkg-config`, `pipewire` dev headers (see [`docs/LINUX_INSTALL.md`](docs/LINUX_INSTALL.md))

### Desktop & Host (quick run)
```bash
# Run Host
dotnet run --project Remex.Host

# Run Client
dotnet run --project Remex.Client.Desktop
```

### Linux Packages (full build + install)
```bash
# Build both Client and Host .tar.gz packages (handles dotnet publish + cmake internally)
./installer/build-linux.sh

# Packages land in installer/Output/ — see docs/LINUX_INSTALL.md for install steps
```

### Cross-platform Installer Script
```bash
# Auto-detects environment:
# - Windows / WSL => Windows installer flow
# - Linux         => Linux package flow
pwsh ./installer/build-installer.ps1
```

### Android (Hardened Fresh Build)
We recommend using our fresh pipeline to ensure the NativeAOT `.so` is perfectly synced:
```powershell
.\scripts\android-fresh.ps1 -Configuration Release
```

---

## 🔒 Secure Remote Access (Tailscale)

By default, RemEx runs on your local home network. If you want to securely access your desktop PC from outside your home (e.g., from work, a coffee shop, or cellular data), we highly recommend using **[Tailscale](https://tailscale.com)**.

Tailscale establishes a zero-configuration, secure, end-to-end encrypted virtual private network (overlay) using WireGuard®. It allows your Android app to connect to your host from anywhere in the world without exposing your desktop to the public internet or configuring risky router port forwarding.

### 🐧 On Linux Host
During installation, the `install.sh` script will interactively query if you want to automatically set up Tailscale.
To configure manually:
1. Install Tailscale: `curl -fsSL https://tailscale.com/install.sh | sh`
2. Connect and authenticate: `sudo tailscale up`

### 🪟 On Windows Host
1. Download and run the official [Tailscale for Windows Installer](https://tailscale.com/download/windows).
2. Log in through your browser to add your Windows PC to your private tailnet.

### 📱 On Android Client
1. Download **Tailscale** from the Google Play Store.
2. Sign in with the **same account** used on your host PC.
3. Turn on the Tailscale VPN switch.
4. Copy your host PC's Tailscale private IP address (starts with `100.x.y.z`).
5. Open **RemEx**, enter that `100.x.y.z` IP address, and pair/connect instantly from anywhere!

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
