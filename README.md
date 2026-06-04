# RemEx ⚡

<div align="center">

[![.NET 10](https://img.shields.io/badge/.NET-10.0-512BD4?logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/)
[![Avalonia UI](https://img.shields.io/badge/Avalonia-11.3-7B2BFC?logo=data:image/svg+xml;base64,&logoColor=white)](https://avaloniaui.net/)
![Android](https://img.shields.io/badge/Android-Compose%20%7C%20Material%203-3DDC84?logo=android&logoColor=white)
![Platform](https://img.shields.io/badge/Platform-Windows%20%7C%20Linux%20%7C%20Android-22C55E)
[![License: MIT](https://img.shields.io/badge/License-MIT-F59E0B)](docs/LICENSE)
![Version](https://img.shields.io/badge/Version-2.0.0-FF6B6B)

**A high-performance, cross-platform command center for remote PC management.**

Real-time hardware telemetry · H.264 hardware video · Remote desktop · QR pairing · App launcher · File transfer · Process manager

*Polished glassmorphic **.NET/Avalonia** desktop client · Full **native Android** app (Kotlin + Jetpack Compose) · .NET NativeAOT JNI core*

</div>

---

## 🚀 Install

### Windows
1. Download `RemEx-v2.0.0-Setup.exe` from [**Releases**](https://github.com/clindsay94/remex/releases).
2. Run the installer — choose **Client only** or **Client + Windows Service** (service starts automatically at boot).
3. Launch **RemEx** from the Start Menu.

### Linux
See [`docs/LINUX_INSTALL.md`](docs/LINUX_INSTALL.md) for the full guide.

```bash
tar -xzf remex-client-v2.0.0-linux-x64.tar.gz
./remex-client-v2.0.0-linux-x64/install.sh install
```

### Android
[![Get it on Google Play](https://img.shields.io/badge/Google%20Play-Download-3DDC84?logo=google-play&logoColor=white)](https://play.google.com/store/apps/details?id=com.clindsay94.remex)

Or sideload the APK from [Releases](https://github.com/clindsay94/remex/releases). Pair in seconds with **QR scan** or **6-digit PIN** on the Connection screen.

---

## ✨ What's New in 2.0

> **RemEx 2.0 is a complete overhaul — security-first architecture, hardware-accelerated video, and a polished new identity across every platform.**

| | |
|---|---|
| **H.264 Hardware Streaming** | Host encodes with NVENC / QSV / AMF / VAAPI; Android decodes via `MediaCodec` for zero-copy GPU delivery. Automatic MJPEG fallback if encoders are absent. |
| **TLS 1.3 + PIN Pairing** | Every connection is end-to-end encrypted. ECDH P-256 key exchange with a 6-digit out-of-band PIN replaces plaintext access keys entirely. |
| **Remote File Transfer** | Browse, upload, and download files with SHA-256 integrity verification. Android can also share its own folders back to the host. |
| **Cosmic Zoom Splash** | Cinematic boot animation — radiating hyperdrive starfield, stenciled neon "R", high-voltage flash + shudder, then the gold lightning bolt materialization. Choose your style in Personalization settings. |
| **Premium Launcher Icon** | Cyber-dark grid background with electric-gold lightning bolt foreground. Themed monochrome variant included for Android 13+ themed icons. |
| **ANSI Startup Banner** | Host initialization prints a colorful ASCII banner showing active ports, platform, and startup state. |
| **Clipboard Bitmap Copy** | Canvas snapshot now copies the bitmap directly to the system clipboard. |
| **M3 Expressive Android** | Spring physics throughout — list reorder, pull-to-refresh, app tile press, lazy grid animations, animated gauge rings. |

---

## 💎 Core Features

### 🔐 Security — Production-Grade
- **TLS 1.3 encryption** on every connection (WSS / TLS sockets)
- **ECDH P-256 pairing** — 6-digit PIN out-of-band binding, no plaintext keys ever on the wire
- **SHA-256 SPKI certificate pinning** on client
- Strict input validation across all network layers

### 📡 Real-Time Telemetry
- **Windows:** HWInfo integration with smart sensor deduplication
- **Linux:** lmsensors + native kernel metrics
- **Free-form canvas** — arrange live sensor cards on a 4,000 × 4,000 zoomable workspace
- Undo/redo layout history, command palette, canvas snapshot-to-clipboard

### 🖥️ Remote Desktop
- **H.264 hardware video** — NVENC/QSV/AMF (Windows), VAAPI/libx264 (Linux), `MediaCodec` (Android)
- **MJPEG fallback** — automatic, seamless, zero configuration required
- **DXGI capture** (Windows) · **PipeWire / X11** (Linux)
- Client cursor overlay · two-finger scroll · cursor size slider · in-fullscreen settings panel
- Full Wayland input injection via portal integration

### 📁 Remote File Transfer
- Browse, upload, download with **SHA-256 integrity verification**
- Shared roots model (configurable on host)
- Android-hosted shared folders accessible to the desktop client

### 📱 Android App
- **Material 3 Expressive** — spring physics, animated gauge rings, tactile feedback throughout
- **8 Quick Settings tiles** — Lock, Shutdown, Restart, Restart to UEFI, WoL, Sleep, Hibernate, Monitor Off
- **Home screen widgets** — Hardware Monitor, Remote Control, App Launcher (reconfigurable, resizable)
- **App Launcher** — recent carousel, pinning, pull-to-refresh, animated grid entries
- **Two-stage haptic feedback** — sent vs. acknowledged
- Localized in **8 languages**: English, Spanish, Hindi, Indonesian, Polish, Portuguese (BR), Turkish, Ukrainian

### 🐧 Linux — First-Class Support
- Native PipeWire + X11 frame capture
- Full mouse and keyboard input simulation
- mDNS discovery · systemd user-level service · `pkexec` for privileged task termination
- `install.sh` with optional Tailscale setup

### 💻 Desktop Client (Avalonia)
- **Glassmorphic UI** — layered translucent cards, dynamic gradient theming
- **4 premium themes** — CyberNOC, Monolith, SolarFlare, BaseDarkGlass
- **Live localization** — switch between 8 languages from Settings without restart
- **Cinematic splash** — Cosmic Zoom animation or classic materialization
- Keyboard shortcuts, undo/redo canvas history, command palette

---

## 🔗 Secure Remote Access (Tailscale)

RemEx is built for local networks. For access outside your home network, use **[Tailscale](https://tailscale.com)** — zero-config WireGuard® overlay that keeps your desktop off the public internet.

<details>
<summary>Setup instructions</summary>

**Linux Host:** The `install.sh` installer can configure Tailscale interactively. Manual:
```bash
curl -fsSL https://tailscale.com/install.sh | sh
sudo tailscale up
```

**Windows Host:** Install from [tailscale.com/download/windows](https://tailscale.com/download/windows) and sign in.

**Android:** Install Tailscale from Play Store, sign in with the same account, then use your host's `100.x.y.z` IP in RemEx.

</details>

---

## 🏗️ Architecture

```
Remex.Core/              Shared models, messages, and validation logic
                         ↳ Also compiled as libRemexCore.so (NativeAOT) for Android JNI
Remex.Host/              Headless ASP.NET service (Minimal APIs + WebSocket + mDNS)
Remex.Client/            Shared Avalonia UI (views, viewmodels, controls, services, themes)
Remex.Client.Desktop/    Desktop entry point (Windows / Linux)
RemEx.Android/           Native Android app — Kotlin + Jetpack Compose + JNI → libRemexCore.so
docs/                    Architectural guidelines and API contracts
installer/               Windows (Inno Setup) and Linux (bash) build scripts
scripts/                 Utility scripts (Windows Service, android-fresh pipeline)
```

### Communication Protocols

| Protocol | Purpose | Default Port |
| :--- | :--- | :--- |
| WSS `/ws` | Encrypted telemetry + power commands | 5005 |
| WSS `/ws/desktop` | H.264 / MJPEG remote desktop stream | 5005 |
| TCP (TLS) | External script ingress (encrypted) | 8338 |
| Named Pipe | Local IPC (Client ↔ Service) | `RemExLocalIPC` |

---

## 🛠️ Building

### Prerequisites
- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- Android SDK → [`docs/ANDROID_SETUP.md`](docs/ANDROID_SETUP.md)
- Linux packages: `cmake`, `pkg-config`, `pipewire` dev headers → [`docs/LINUX_INSTALL.md`](docs/LINUX_INSTALL.md)

### Desktop & Host
```bash
dotnet run --project Remex.Host
dotnet run --project Remex.Client.Desktop
```

### Tests
```bash
dotnet test Remex.sln
```

### Linux Packages
```bash
./installer/build-linux.sh
# Output → installer/Output/
```

### Windows Installer
```powershell
pwsh ./installer/build-installer.ps1
```

### Android (Hardened Fresh Build)
```powershell
.\scripts\android-fresh.ps1 -Configuration Release
```

---

## 📖 Docs & Guidelines

| Document | Description |
| :--- | :--- |
| [`docs/ANDROID_SETUP.md`](docs/ANDROID_SETUP.md) | Android SDK setup on Windows, Linux, macOS |
| [`docs/API_CONTRACTS.md`](docs/API_CONTRACTS.md) | WebSocket, H.264 streaming, pairing, file transfer, REST protocols |
| [`docs/ASYNC_GUIDELINES.md`](docs/ASYNC_GUIDELINES.md) | Mandatory async/await patterns |
| [`docs/NULL_SAFETY_GUIDELINES.md`](docs/NULL_SAFETY_GUIDELINES.md) | Null safety rules |
| [`docs/VALIDATION_GUIDELINES.md`](docs/VALIDATION_GUIDELINES.md) | Network-layer input validation |
| [`docs/LINUX_INSTALL.md`](docs/LINUX_INSTALL.md) | Linux installation guide |
| [`docs/CHANGELOG.md`](docs/CHANGELOG.md) | Full release history |
| [`docs/SECURITY.md`](docs/SECURITY.md) | Security policy and vulnerability reporting |
| [`docs/CONTRIBUTING.md`](docs/CONTRIBUTING.md) | Contributing guide |

---

## Contributing
See [`docs/CONTRIBUTING.md`](docs/CONTRIBUTING.md) for detailed conventions.

## License
[MIT](docs/LICENSE) — Copyright © 2026 Connor
