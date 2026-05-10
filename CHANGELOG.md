# Changelog

All notable changes to RemEx will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

---

## [1.15.0] - 2026-05-15

### Added
- **Stability:** Final polish and stability fixes for the 1.x series before 2.0.

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
| 1.15.0 | Current | May 15, 2026 | May 15, 2027 |
| 1.11.0 | Supported | Apr 15, 2026 | Apr 15, 2027 |
| 1.10.0 | Maintained | Mar 2026 | Mar 2027 |
| 1.9.0 | Unsupported | Feb 2026 | Feb 2027 |
| < 1.9.0 | Unsupported | - | - |

---

## Release Process

Releases follow [Semantic Versioning](https://semver.org/):
- **MAJOR** version for incompatible API changes
- **MINOR** version for new functionality (backwards compatible)
- **PATCH** version for bug fixes

See [RELEASE_V1.11.0.md](RELEASE_V1.11.0.md) for detailed v1.11.0 release notes.
