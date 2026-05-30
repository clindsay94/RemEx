# Changelog

All notable changes to RemEx will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

---

## [2.0.0] — 2026-05-15

### Added
- End-to-end encrypted transport (TLS 1.3 / WSS) for all client-host communication
- Cryptographic device pairing replacing plaintext access keys (ECDH P-256 + 6-digit PIN)
- SHA-256 SPKI certificate pinning on client
- Remote file transfer with SHA-256 integrity verification (browse, upload, download, cancel)
- Android file-transfer hosting (shared folders on device accessible to host)
- 8 Quick Settings tiles on Android (Lock, Shutdown, Restart, Restart to UEFI, Wake on LAN, Sleep, Hibernate, Monitor Off)
- Two-stage haptic feedback on Android (sent vs acknowledged)
- Battery optimization onboarding on Android
- Firebase Crashlytics NDK integration
- Target SDK 37 (Android 17) support with Local Network permission flow
- Linux remote desktop input via Wayland portal integration

### Changed
- Remote desktop streaming pipeline optimized to decouple background frame capture producer from WebSocket send consumer via a non-blocking latest-frame buffer.
- Windows screen capture service now supports dynamic host-side cursor rendering (`drawCursor` config parameter), allowing host cursor drawing to be disabled when client drawing is enabled, completely avoiding DXGI CPU/bandwidth overhead on static screens.
- Linux screen capture service timeout caching implemented to bypass slow fallback shell tools during static PipeWire frames.
- Protocol version field added to `RemexMessage`; 1.x clients fail loudly
- Material3 dependency moved from alpha to stable
- Windows Installer (Inno Setup) updated with new branding and versioning
- Linux build scripts updated with `New-REMEX.png` icon priority
- Remote desktop pointer batches now use flattened JSON structure for efficiency

### Fixed
- Settings view freeze on Linux (UI-thread marshalling)
- SavedStatus continuation off UI thread
- DiscoverHostsAsync HostAddress assignment off UI thread
- async-void crash hazard in `OnShowSetAlertRequested`
- Sensor `AlertTriggered` event subscription leak on reconnect
- Duplicate XAML style block in `CanvasView.axaml`
- `RefreshSensors` running on every Settings open/close
- Hardcoded "Sort by:" string in `TaskManagerScreen`
- Snapshot clipboard copies file path; redesigned as "Copy Path" with accurate label
- Remote desktop input from Android on Linux (Wayland pointer events now injected correctly)
- S-Pen hover event crash on Android
- Linux xrandr parser robustness for exotic display configurations
- Client pairing state now persists across restarts; paired client IDs survive reconnect
- All 11 high-severity security audit findings resolved

### Security
- Plaintext access keys are no longer transmitted on the wire
- DataStore exclusion from Auto Backup verified via `data_extraction_rules.xml`
- Network security config disables cleartext traffic on Android
- ECDH curve switched to NIST P-256 (built-in .NET) for better stability over NSec/X25519
- First-time pairing now requires explicit trust gesture; unknown certificates fail closed
- WebSocket authorization gate enforces pairing on all `/ws/desktop` connections

---

## [1.11.0] - 2026-04-15

### Added
- **Haptic Feedback System:** New vibration feedback for all Android interactions
- **Theme Variants:** CyberNOC, Monolith, SolarFlare premium themes
- **Dynamic Color Generator:** Intelligent color scheme generation for themes
- **LinuxInputSimulationService:** Complete Linux input simulation implementation
- **Enhanced DesktopMeta:** Extended platform-specific metadata support
- **Mouse Overlay Improvements:** Better visibility and responsiveness controls
- **Screen Categories:** Improved organization of remote control screens
- **Expanded Localization:** 1,660+ new string resources (full coverage for 8 languages)

### Changed
- **SettingsManager:** Complete architectural refactor for better persistence
- **RemoteDesktopScreen:** Major UI overhaul with optimized touch handling
- **RemoteControlScreen:** Enhanced responsiveness and visual feedback
- **RemoteMouseScreen:** Performance improvements with reduced re-renders
- **Theme System:** Improved color management and consistency
- **UI Layer:** Better null safety and input validation throughout
- **WindowsInputSimulationService:** Comprehensive refactor for improved robustness

### Improved
- Android touch gesture recognition and accuracy
- Desktop client rendering performance
- Cross-platform localization consistency
- Settings persistence and data integrity
- Remote desktop stream responsiveness
- Input timing and synchronization

### Fixed
- Various UI re-render performance issues
- Settings migration from v1.10.0
- Theme loading consistency
- Null reference handling in ViewModels
- Input validation edge cases

### Technical Details
- **Files Modified:** 56
- **Lines Added:** 6,260
- **Lines Removed:** 3,436
- **Net Change:** +2,824 lines
- **Android versionCode:** 11
- **Commit:** 4fb723b

---

## [1.10.0] - 2026-03-XX

### Added
- Full Linux integration with native capture and telemetry services
- QR Code pairing for instant device configuration
- Glassmorphic dashboard with dark glass design
- 8-language support with live localization switching
- Interactive 9-page OS-adaptive tutorial
- Free-form 4,000x4,000 canvas for sensor card arrangement

### Key Features
- GPU-accelerated remote desktop streaming
- HWInfo (Windows) and lmsensors (Linux) integration
- Optional shared-secret authentication
- Strict input validation across all network layers
- Async/await patterns with null safety

---

## [1.9.0] - 2026-02-XX

### Added
- Production readiness audit and hardening
- Comprehensive validation guidelines
- Enhanced error handling and recovery

---

## Version Support

| Version | Status | Release Date | End of Support |
|---------|--------|--------------|----------------|
| 2.0.0 | Current | May 15, 2026 | May 15, 2027 |
| 1.11.0 | Supported | Apr 15, 2026 | Apr 15, 2027 |
| 1.10.0 | Maintained | Mar 2026 | Mar 2027 |
| < 1.9.0 | Unsupported | - | - |

---

## Release Process

Releases follow [Semantic Versioning](https://semver.org/):
- **MAJOR** version for incompatible API changes
- **MINOR** version for new functionality (backwards compatible)
- **PATCH** version for bug fixes

See the entries above for the latest 2.0 release notes and support status.
