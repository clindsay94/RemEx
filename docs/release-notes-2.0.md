# 🚀 RemEx 2.0 Release Notes: The Next Generation! ⚡

We are officially graduating from closed beta! **RemEx 2.0** represents a ground-up redesign of our core streaming engine, security architecture, and user experience. Whether you are controlling your workstation from across the room or across the globe, RemEx 2.0 delivers unprecedented performance, ironclad security, and a beautiful new visual identity.

Welcome to the open beta and public release of **RemEx 2.0**! Here is a thorough breakdown of all the massive upgrades and fixes waiting for you.

---

## ⚡ Ultimate Performance: Zero-Latency Hardware-Accelerated H.264 Streaming
We have completely rewritten our remote desktop streaming pipeline from the ground up to utilize state-of-the-art video streaming technology.
*   **Hardware-Accelerated H.264 Encoding:** Say goodbye to high CPU usage! The desktop host now harnesses the power of your GPU (including **NVIDIA NVENC, Intel Quick Sync (QSV), AMD AMF, Linux VAAPI, and libx264**) to encode remote desktop frames in real-time.
*   **Android Hardware Decoding:** The Android app now utilizes native `MediaCodec` hardware decoders combined with direct zero-copy surface composition. By integrating Jetpack Compose `AndroidView` with a native `TextureView` and hardware `Surface`, frames are decoded directly on the GPU for maximum efficiency and battery life.
*   **Sub-Millisecond Frame Slicing:** A zero-latency Annex B packet slicer utilizes Access Unit Delimiter (AUD `0x00 0x00 0x00 0x01 0x09`) markers, cutting frame processing latency down to sub-millisecond ranges.
*   **Decoupled Capture & Send Pipelines:** Remote desktop capture is decoupled from the network transmission loop using a non-blocking latest-frame buffer. Network fluctuations will no longer stall your background frame capture.
*   **Universal MJPEG Fallback:** If hardware-accelerated H.264 encoding is unavailable on a host, the system automatically degrades to a highly optimized, high-performance MJPEG streaming pipeline to guarantee a working display.

---

## 🔒 Ironclad Security: Cryptographic Pairing & Encryption
Security is no longer an afterthought. We have replaced legacy security measures with a modern, cryptographic trust model.
*   **ECDH Device Pairing:** Plaintext access keys are gone. Devices now establish mutual trust using an Elliptic Curve Diffie-Hellman (ECDH P-256) handshake paired with a secure 6-digit PIN flow.
*   **End-to-End Encryption:** All command channels and desktop streaming channels are fully encrypted using **TLS 1.3 / WSS** (WebSockets over SSL).
*   **SHA-256 Certificate Pinning:** The Android client pins the host's certificate identity using SHA-256 SPKI fingerprinting, blocking middle-man attacks.
*   **WebSocket Authorization Gates:** Access to all streaming endpoints (e.g. `/ws/desktop`) is now strictly gated by authentication checks. Unpaired clients are shut out immediately.
*   **Auto-Backup Exclusions:** Security tokens and keys are explicitly excluded from Android Auto Backups via customized `data_extraction_rules.xml` to prevent credentials from leaving your physical device.

---

## 🐧 Linux & Wayland Native Integration
Linux is now a first-class citizen with deep native system integration.
*   **PipeWire Screen Capturing:** Built-in support for Wayland desktop environments (like KDE Plasma 6) via `xdg-desktop-portal` and our native bridge `libremex_linux_bridge.so`, boosting streaming speeds on Wayland to parity with Windows.
*   **Correct Wayland Pointer Event Injection:** RemEx now injects pointer and keyboard inputs directly using the Wayland portal APIs, resolving issues where Android gestures failed to register on Wayland.
*   **Robust Fallback & Caching:** If PipeWire is unavailable, the host smoothly falls back to legacy shell tools. Additionally, screen capture timeouts are now cached to prevent slowdowns on static screens.

---

## 🔋 Resilient Reconnection & Background Reliability
We've squashed bugs and hardened connection lifecycles to make RemEx incredibly reliable.
*   **Persistent Pairing & Trust:** Paired client IDs and host trust keys are now persisted across host restarts, ending the frustrating loop of re-pairing devices every time you reboot your PC.
*   **Heartbeat Deadlock Fixes:** Fixed a deadlock where unexpected connection drops left Android in a perpetual, unrecoverable "connecting" state.
*   **Hardened Foreground Service:** The Android foreground service no longer self-terminates on transient network disconnects. It stays alive to manage the reconnect window, and resumes cleanly on relaunch.
*   **Idempotent Takeovers:** Reconnecting now cancels any orphaned socket threads and cleans up previous pairing states, eliminating server-side loop accumulation.

---

## 🎨 Redesigned Visual Identity & Cinematic Splashes
RemEx 2.0 looks and feels premium, inside and out.
*   **Themed Vector Icon:** A brand-new cyber-dark grid background and an electric-gold lightning bolt vector drawable form our new Android launcher icon (with adaptive monochrome support).
*   **Cinematic "Cosmic Zoom" Splash Screen:** Features a high-speed radiating hyperdrive starfield, a slow-zooming stenciled neon "R" logo, a high-voltage white/cyan screen flash with a physical haptic vibration, and a smooth orange-gold lightning gradient fade-in.
*   **Startup ASCII Art Banner:** A gorgeous ANSI color ASCII banner prints details like active ports, host platform info, and initialization states directly into the host console upon startup.
*   **Personalization Settings:** Select your preferred boot intro and splash animations from the newly introduced splash screen library.

---

## 🛠️ Feature Polish & Bug Squashing
A massive suite of quality-of-life additions and bug fixes.
*   **Android Quick Settings Tiles:** Control your PC directly from your notification tray with 8 new tiles: Lock, Shutdown, Restart, Restart to UEFI, Wake on LAN, Sleep, Hibernate, and Monitor Off.
*   **Two-Stage Haptic Feedback:** Distinct vibration patterns differentiate sent commands from host-acknowledged commands.
*   **Battery Optimization Onboarding:** Guides users through disabling Android battery limitations to keep background connections active.
*   **Wayland Pointer Batches:** Pointer updates are batched into a flattened JSON format for lower bandwidth overhead.
*   **Native Clipboard Integration:** Copy dashboard snapshot bitmaps directly onto the desktop system clipboard.
*   **Target SDK 37:** Full support for Android 17, featuring native Local Network permission flows.
*   **Crashlytics NDK Integration:** Native crash reporting via Firebase Crashlytics on Android ensures we can catch and resolve C++ runtime exceptions.
