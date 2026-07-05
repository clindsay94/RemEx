<div align="center">

<img src="docs/assets/remex-logo.png" alt="RemEx" width="160" />

# RemEx — Remote Execution

### Your PC, in your pocket. A buttery‑smooth, end‑to‑end‑encrypted remote desktop and control suite that turns any Android phone into a full remote for your Windows or Linux machine.

<p>
  <img alt="Version" src="https://img.shields.io/badge/version-2.0.0-FFB000?style=for-the-badge&labelColor=0d1117" />
  <img alt="Platforms" src="https://img.shields.io/badge/host-Windows%20%7C%20Linux-1f6feb?style=for-the-badge&labelColor=0d1117" />
  <img alt="Client" src="https://img.shields.io/badge/client-Android-3DDC84?style=for-the-badge&logo=android&logoColor=white&labelColor=0d1117" />
  <img alt="License" src="https://img.shields.io/badge/license-MIT-8957e5?style=for-the-badge&labelColor=0d1117" />
</p>

<p>
  <img alt=".NET" src="https://img.shields.io/badge/.NET%2010-512BD4?style=flat-square&logo=dotnet&logoColor=white" />
  <img alt="Avalonia" src="https://img.shields.io/badge/Avalonia%20UI-8B5CF6?style=flat-square" />
  <img alt="Kotlin" src="https://img.shields.io/badge/Kotlin-7F52FF?style=flat-square&logo=kotlin&logoColor=white" />
  <img alt="Compose" src="https://img.shields.io/badge/Jetpack%20Compose-4285F4?style=flat-square&logo=jetpackcompose&logoColor=white" />
  <img alt="NativeAOT" src="https://img.shields.io/badge/NativeAOT%20JNI-00599C?style=flat-square" />
  <img alt="H.264" src="https://img.shields.io/badge/H.264%20%7C%20MJPEG-FF6B35?style=flat-square" />
  <img alt="TLS" src="https://img.shields.io/badge/TLS%201.3%20%E2%80%A2%20WSS-2EA043?style=flat-square&logo=letsencrypt&logoColor=white" />
</p>

<sub>**Android (client) → PC (host).** One encrypted connection. No cloud relay, no account, no telemetry leaving your network.</sub>

</div>

---

## ✨ Why RemEx

Most remote tools make you choose: smooth *or* secure, simple *or* powerful, Windows *or* Linux. RemEx refuses the trade‑off.

It streams your desktop at up to **120 FPS** with a hardware‑accelerated H.264 pipeline, injects pixel‑perfect touch, stylus, and keyboard input — even into elevated admin windows — and does it all over a **TLS 1.3** channel locked to *your* device with cryptographic pairing and certificate pinning. The host runs as a hardened, always‑elevated app **inside your own signed‑in desktop session** on Windows **and** Linux; the client is a gorgeous, glassmorphic Android app that speaks eight languages and adapts to your phone live.

> **Architecture in one line:** the PC runs `remex.agent` (the entire PC side — the always‑elevated in‑session app *and* the dashboard UI, in one process), and your Android phone is the one and only client. The connection is always direct, non‑loopback, and encrypted.

---

## 🧭 Table of Contents

- [What's New in 2.0](#-whats-new-in-20)
- [Feature Tour](#-feature-tour)
- [Architecture](#-architecture)
- [Communication Protocols](#-communication-protocols)
- [Security Model](#-security-model)
- [Getting Started](#-getting-started)
- [Building from Source](#-building-from-source)
- [Experience & Personalization](#-experience--personalization)
- [Documentation](#-documentation)
- [Project Layout](#-project-layout)
- [Roadmap](#-roadmap)
- [Contributing](#-contributing)
- [License](#-license)

---

## 🚀 What's New in 2.0

RemEx 2.0 is a ground‑up overhaul of the streaming and input pipeline. The headline: it finally *feels* like you're sitting at the machine.

| 🎬 Remote Desktop | ⚡ Performance | 🔒 Security |
|---|---|---|
| Native **H.264** hardware pipeline (NVENC / QSV / AMF / VAAPI / x264) with automatic **MJPEG** fallback | **120 FPS** default streaming with a hybrid precision frame‑pacer | End‑to‑end **TLS 1.3 / WSS** on every byte |
| **True native cursor** streamed as a live bitmap and drawn at the real hotspot | **Spring‑smoothed** cursor motion (60 Hz position stream, critically‑damped client interpolation) | **ECDH P‑256 + 6‑digit PIN** pairing — no plaintext keys, ever |
| **Zoom pan‑follow** — the view glides to keep the cursor on screen | **Sub‑millisecond** Annex‑B frame slicing via AUD markers | **SHA‑256 SPKI certificate pinning** — unknown certs fail closed |

### Refinements that landed in the 2.0 line

- **120 FPS, honestly.** A naïve `Task.Delay` rounds up to the OS timer (~15.6 ms on Windows) and silently caps you near 60 FPS. RemEx now coarse‑sleeps for the bulk of each frame and **busy‑spins the final few milliseconds** with `Thread.SpinWait` — no global timer hacks — so high‑refresh phones get the real frame rate on Windows *and* Linux.
- **Cursor that glides, not steps.** The host streams cursor position at ~60 Hz; the client animates the overlay toward each point with a **critically‑damped spring**, snapping cleanly on (re)appearance or display switch. The real Windows cursor *shape* is streamed as a BGRA bitmap, so you see the actual I‑beam, resize, and hand cursors — moving smoothly even over a static desktop.
- **Cursor confinement.** While streaming a single monitor, the host pins the pointer to that display with `ClipCursor` (re‑applied every tick) so it can't wander onto a screen you can't see. Released the instant streaming stops.
- **Rotation freedom + letterboxing.** The remote screen rotates with your device instead of being forced landscape, and H.264 video **letterboxes to preserve aspect ratio** — a landscape desktop viewed in portrait looks correct instead of squished.
- **Input that actually lands.** Control now works against **elevated (admin) windows** — the interactive host launches with the user's *linked full‑admin token* (HIGH integrity) so Windows UIPI no longer drops your taps. Stylus/S‑Pen taps on the exact **(0,0) hot corner** are no longer dropped, phantom corner‑clicks are gone, and **secondary monitors** (including those at negative virtual‑desktop origins) map correctly.
- **Type anything.** Emoji and other non‑BMP characters now type through correctly — surrogate pairs are emitted as a single batched key event group instead of being broken apart.
- **Keep‑session‑unlocked (opt‑in, Windows).** A security‑sensitive, *off‑by‑default* mode that keeps your signed‑in session usable for the life of a connection — reconnecting a locked/disconnected session to the console so input keeps working even after a Microsoft "Windows App" RDP client disconnects. Every unlock/re‑lock is audit‑logged.

<sub>Full details in [`docs/CHANGELOG.md`](docs/CHANGELOG.md) and [`docs/release-notes-2.0.md`](docs/release-notes-2.0.md).</sub>

---

## 🧰 Feature Tour

### 🎥 Remote Desktop
- Hardware‑accelerated **H.264** streaming with a hardware‑decoded zero‑copy `TextureView`/`Surface` path on Android, and graceful **MJPEG** degradation when no encoder is available.
- Producer/consumer decoupling with a non‑blocking latest‑frame buffer — capture never blocks the wire and you always see the freshest frame.
- Per‑display streaming, cursor confinement, zoom with pan‑follow, and aspect‑correct letterboxing.

### 🖱️ Control & Input
- Absolute and relative pointer modes, drag, long‑press right‑click, scroll.
- **Stylus / S‑Pen** support with hover, pressure‑aware contact, and accurate edge/corner mapping.
- Full keyboard with Unicode (including emoji) and a special‑keys toolbar.
- Multi‑monitor aware across primary and secondary displays.

### 📊 Live Telemetry
- Real‑time hardware sensors via **HWiNFO** (Windows) and **lmsensors** (Linux).
- A free‑form **4,000 × 4,000 canvas** to arrange sensor cards exactly how you like, with snapshot‑to‑clipboard.

### ⚡ Power & Quick Actions
- Eight Android **Quick Settings tiles**: Lock, Shutdown, Restart, Restart‑to‑UEFI, Wake‑on‑LAN, Sleep, Hibernate, Monitor‑Off.
- **Two‑stage haptics** so you feel the difference between *sent* and *acknowledged*.

### 📁 File Transfer
- Browse, upload, download, and cancel — with **SHA‑256 integrity verification** end to end.
- Android can host shared folders the PC can reach.

### 🔌 Cross‑Platform Host
- One `remex.agent` codebase runs as an **always‑elevated app inside your signed‑in desktop session** — auto‑started with no prompt by a Windows Task Scheduler logon task or a Linux XDG autostart entry. No Windows Service, no daemon, no Session 0.
- Linux remote input via **Wayland portal** integration; `--doctor` flag checks PipeWire/X11/VAAPI prerequisites.

---

## 🏗️ Architecture

Three .NET projects share one wire contract. The Android client and the PC host both build on `Remex.Core` — so the message envelope is defined exactly once and compiled into both sides.

```mermaid
flowchart TB
    subgraph PHONE["📱 remex.android · the only client"]
        direction TB
        UI["Jetpack Compose UI · Kotlin"]
        JNI["JNI bridge"]
        UI --> JNI
    end

    subgraph PC["🖥️ remex.agent · the entire PC side"]
        direction TB
        SVC["Elevated in-session app<br/>(Windows logon task / Linux autostart)<br/>.NET 10 · ASP.NET Minimal API · mDNS discovery"]
        CAP["Screen capture → H.264 / MJPEG encode"]
        INP["Input injection · UIPI-aware · multi-monitor"]
        TEL["Telemetry · HWiNFO / lmsensors"]
        SVC --> CAP & INP & TEL
    end

    CORE["🧩 Remex.Core · shared models · RemexMessage v2 · Guards · Validation<br/>↳ compiled NativeAOT → libRemexCore.so for Android"]

    JNI -. links .-> CORE
    SVC -. references .-> CORE
    PHONE ==>|"WSS · TLS 1.3 · pinned cert · :5005 /ws · /ws/desktop · :8338 TCP"| PC

    classDef phone fill:#0d2818,stroke:#3DDC84,color:#e8fff2;
    classDef pc fill:#0a1929,stroke:#1f6feb,color:#e8f2ff;
    classDef core fill:#1a0e2e,stroke:#8957e5,color:#f3e8ff;
    class PHONE,UI,JNI phone;
    class PC,SVC,CAP,INP,TEL pc;
    class CORE core;
```

**Three projects, one contract:**

| Project | Role |
|---|---|
| **`Remex.Core`** | Shared models, the `RemexMessage` envelope, Guards, validation, source‑generated JSON. Also compiled as a **NativeAOT JNI** library (`libRemexCore.so`) for Android — so client and host share one source of truth for the wire format. |
| **`remex.agent`** | The **entire PC side** — one always‑elevated app running inside your signed‑in session, containing all PC functionality (capture, encode, input, telemetry, file transfer, dashboard UI) in a single process. Android connects *to* this. |
| **`remex.android`** | The **only** network client. Kotlin + Jetpack Compose, calling into `libRemexCore.so` over JNI. |

---

## 📡 Communication Protocols

| Protocol | Endpoint | Port | Purpose |
|---|---|---|---|
| **WSS** | `/ws` | `5005` | Telemetry, power commands, pairing, file transfer |
| **WSS** | `/ws/desktop` | `5005` | H.264 / MJPEG remote‑desktop stream |
| **TCP (TLS)** | — | `8338` | External script command ingress (paired clients only) |

All `/ws` traffic rides the **`RemexMessage` JSON envelope** at `protocolVersion: 2`. Mismatched majors fail loudly rather than silently corrupting state. Hosts are discovered automatically on the LAN via **mDNS**. The dashboard UI and the host run in the **same process**, so there is no local pipe or socket between them to secure — one of several attack surfaces that simply no longer exists in 2.0.

---

## 🔐 Security Model

RemEx is built so that the only way in is the way *you* authorized.

- **Cryptographic pairing.** First connection performs an **ECDH P‑256** key exchange confirmed by a **6‑digit PIN** shown on the host. There are no plaintext access keys anywhere on the wire.
- **Certificate pinning.** At pairing time the Android client pins the host's **SHA‑256 SPKI hash**. If the host certificate ever changes without a deliberate re‑pair, the connection is refused — fail closed, never silently downgrade.
- **Encrypted transport.** Every channel is **TLS 1.3 / WSS**. Cleartext traffic is disabled in the Android network‑security config.
- **Authorization gate.** Pairing is enforced on *all* connections, including `/ws/desktop`. `PairedClientRegistry` is the single authentication path in production.
- **Rate‑limited pairing.** The 6‑digit PIN expires after **120 seconds**, and repeated pairing attempts from a device are throttled with an escalating back‑off — so guessing the PIN by brute force isn't practical.
- **Elevation hardening.** On Windows the host runs **elevated (high integrity) inside your signed‑in session**, auto‑started with no UAC prompt by a Task Scheduler logon task. Security‑sensitive machine‑wide state (`cert.pfx`, `paired_clients.json`) lives in `ProgramData` under an ACL restricted to **LocalSystem + Administrators**, so a normal (non‑elevated) program can't read your private key or your paired‑device list.
- **Auditable, opt‑in escalation.** The keep‑session‑unlocked feature is *off by default*, engages only for an authenticated stream, and logs every unlock/re‑lock.

> **New to this?** Read [**How RemEx keeps you safe**](docs/SECURITY_EXPLAINED.md) — a plain‑English (and, if you want it, deeply technical) walkthrough of every security feature. For supported versions and how to report a vulnerability, see [`docs/SECURITY.md`](docs/SECURITY.md).

---

## 🏁 Getting Started

**You'll need:**

| Side | Requirement |
|---|---|
| 🖥️ PC host | Windows 10/11 (64‑bit) **or** a modern Linux desktop (Wayland or X11; PipeWire for screen capture). Run `--doctor` on Linux to verify prerequisites. |
| 📱 Android client | An Android phone (built against SDK 37 / Android 17). |
| 🌐 Network | Phone and PC on the **same local network**. No internet, cloud account, or relay required. |

### 1. Install the host on your PC

**Windows — the easy way (no coding required):**

1. Download the installer — **`RemEx-v2.0.0-Setup.exe`** — from the project's [**GitHub Releases**](https://github.com/clindsay94/RemEx/releases) page.
2. Double‑click it and follow the wizard. Leave **"Launch RemEx when you sign in"** ticked so RemEx starts by itself every time you log in.
3. RemEx opens its dashboard. The **6‑digit pairing PIN** you'll need in step 3 is shown right there.

> RemEx installs as a normal desktop app that runs with administrator rights inside your own session. There's nothing to configure by hand and no separate background service to manage.

**Linux:** download the release package and run its included `install.sh`, or follow the friendly step‑by‑step in [`docs/LINUX_INSTALL.md`](docs/LINUX_INSTALL.md). To check your system's screen‑capture prerequisites first, run `remex-agent --doctor` (verifies PipeWire / X11 / VAAPI).

<details>
<summary><b>For developers — run from source instead of installing</b></summary>

```bash
dotnet run --project remex.agent -- --doctor   # Linux: verify PipeWire / X11 / VAAPI
dotnet run --project remex.agent               # start the host straight from source
```

A friendly ANSI startup banner prints the active ports, platform, and state. See [`docs/BUILDING.md`](docs/BUILDING.md) for the full build matrix.
</details>

### 2. Install the Android app

Install the RemEx APK on your phone — grab the latest build from the project's **GitHub Releases** page. On first launch you'll be guided through the Local Network permission and battery‑optimization onboarding. See [`docs/ANDROID_SETUP.md`](docs/ANDROID_SETUP.md).

### 3. Pair your phone with your PC

1. Open RemEx on your phone. It automatically finds your PC on your home network and shows it in the list. *(Nothing showing up? Make sure the phone and PC are on the **same Wi‑Fi**, then pull down to refresh — or scan the QR code on the PC's dashboard.)*
2. Tap your PC, then type the **6‑digit PIN** shown on the PC screen. You have about **2 minutes** before that PIN refreshes to a new one.
3. That's it. Your phone securely remembers your PC and reconnects on its own from now on — you won't need to type the PIN again unless you deliberately unpair.

---

## 🛠️ Building from Source

`build-remex.ps1` is the **canonical, cross‑platform** entry point. It runs under `pwsh` on Windows *and* Linux, and syncs versions from `version.properties` automatically.

```powershell
# Everything, release (Windows + Linux + Android)
pwsh ./build-remex.ps1 -c release -t all

# Interactive wizard (no args)
pwsh ./build-remex.ps1

# Android only — hardened fresh build
./scripts/android-fresh.ps1 -Configuration Release

# Linux packages (uses WSL on Windows)
./installer/build-linux.sh

# Run the full test suite
dotnet test Remex.sln
```

**Android prerequisites** (auto‑installed by the build script via `sdkmanager` if missing):
- Android SDK **API Level 37**
- NDK **30.0.14904198** (required for NativeAOT JNI)
- `ANDROID_HOME` set, or `sdk.dir=...` in `remex.android/local.properties`

Build output is consolidated under a single repo‑root `artifacts/` folder. Detailed instructions live in [`docs/BUILDING.md`](docs/BUILDING.md).

---

## 🎨 Experience & Personalization

RemEx is meant to look as good as it performs — for technical and non‑technical users alike.

**Four glassmorphic themes**, each with distinct contrast and background treatments:

| Theme | Vibe |
|---|---|
| **CyberNOC** | Neon network‑operations‑center energy |
| **Monolith** | Quiet, focused, monochrome |
| **SolarFlare** | Warm gold‑orange gradients |
| **BaseDarkGlass** | Clean dark‑glass baseline |

**Eight languages**, switchable live without a restart. Every user‑facing string flows through the localization system — no hardcoded English.

**Cinematic onboarding** — choose your boot intro, including the "Cosmic Zoom" splash: a radiating hyperdrive starfield, a slow‑zooming neon "R", a high‑voltage screen flash with a physical haptic shudder, and the gold‑orange lightning bolt materializing into the full title.

---

## 📚 Documentation

| Topic | Document |
|---|---|
| 🧱 Host architecture (PC side) | [`docs/ARCHITECTURE-HOST.md`](docs/ARCHITECTURE-HOST.md) |
| 🛡️ How RemEx keeps you safe (beginner → deep‑technical) | [`docs/SECURITY_EXPLAINED.md`](docs/SECURITY_EXPLAINED.md) |
| 🔐 Security policy & responsible disclosure | [`docs/SECURITY.md`](docs/SECURITY.md) |
| 🛠️ Building from source | [`docs/BUILDING.md`](docs/BUILDING.md) |
| 📱 Android setup | [`docs/ANDROID_SETUP.md`](docs/ANDROID_SETUP.md) |
| 🐧 Linux install | [`docs/LINUX_INSTALL.md`](docs/LINUX_INSTALL.md) |
| ⚠️ Known limitations | [`docs/KNOWN_LIMITATIONS.md`](docs/KNOWN_LIMITATIONS.md) |
| 🧾 Changelog · 2.0 release notes | [`docs/CHANGELOG.md`](docs/CHANGELOG.md) · [`docs/release-notes-2.0.md`](docs/release-notes-2.0.md) |
| 🤝 Contributing · Code of Conduct | [`docs/CONTRIBUTING.md`](docs/CONTRIBUTING.md) · [`docs/CODE_OF_CONDUCT.md`](docs/CODE_OF_CONDUCT.md) |
| 📐 Engineering guidelines | [Async](docs/ASYNC_GUIDELINES.md) · [Null safety](docs/NULL_SAFETY_GUIDELINES.md) · [Validation](docs/VALIDATION_GUIDELINES.md) |

---

## 📂 Project Layout

```
remex.core/         Shared models, RemexMessage envelope, Guards, validation, source-gen JSON
                    ↳ also compiled as libRemexCore.so (NativeAOT JNI) for Android
remex.agent/         ★ THE PC SIDE — one always-elevated in-session app + all PC functionality
                    ↳ Views/ + ViewModels/ (MVVM) · Services/ · Themes/ · Localization/
remex.android/      ★ THE ONLY CLIENT — Kotlin + Jetpack Compose + JNI → libRemexCore.so
docs/               Architecture, security, build, contributing, changelog, guidelines
installer/          Windows (Inno Setup) + Linux packaging
scripts/            Build & maintenance tooling (cross-platform)
```

> The PC ships as a single product — **`remex.agent`** is the whole PC side, and the Android app is the only network client. There is no separate desktop client that connects to the host.

---

## 🗺️ Roadmap

- In‑app toggle (with security warning) for keep‑session‑unlocked, replacing the current flag file.
- Multi‑display simultaneous streaming.
- Continued input‑pipeline hardening for the RDP‑disconnect lifecycle.

Tracked as issues in the **beads** tracker — see [Contributing](#-contributing).

---

## 🤝 Contributing

RemEx uses **beads (`bd`)** for issue tracking instead of TODO files. The workflow:

```bash
bd ready                 # find unblocked work
bd create --title="..." --type=task|bug|feature --priority=0-4
bd update <id> --claim   # claim it
bd close <id>            # close it when done
```

Read [`docs/CONTRIBUTING.md`](docs/CONTRIBUTING.md) and the relevant sub‑project `AGENTS.md` before changing code. Key conventions:

- **Cross‑platform parity** — every change to the PC side must work on Windows *and* CachyOS/Linux.
- **NativeAOT‑safe `Remex.Core`** — no reflection, no runtime codegen, source‑generated JSON only.
- **No `ConfigureAwait(false)`**, nullable reference types on, validate all network‑facing input.
- **Update the docs and `CHANGELOG.md`** with every change.

Please also review the [Code of Conduct](docs/CODE_OF_CONDUCT.md).

---

## 📄 License

Released under the **MIT License**. See [`docs/LICENSE`](docs/LICENSE).

<div align="center">
<sub>Built with care by Connor Lindsay · © 2026</sub>
</div>
