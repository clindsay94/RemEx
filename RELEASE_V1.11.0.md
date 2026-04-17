# RemEx v1.11.0 Release Notes

**Release Date:** April 15, 2026  
**Version:** 1.11.0 (Android: versionCode=11, versionName=1.11.0)

---

## 🚀 Post-Release Refinements (Apr 16, 2026)

### UI Component Architecture
- **RemexScreenHeader:** New reusable header component for consistent screen presentation
- Extracted header logic into dedicated composable for better code reuse
- Improved component composition and separation of concerns

### Code Quality Improvements
- Refactored multiple UI screens for better maintainability
- Enhanced code structure and readability improvements
- Optimized import organization across Android UI layer
- Improved null safety in navigation and screen handlers

### Build Configuration
- Updated Directory.Build.props with latest standards
- Enhanced cross-platform build consistency

---

## 📋 Overview

RemEx v1.11.0 is a comprehensive polish release focused on improving user experience across all platforms. This release brings significant enhancements to the Android app's UI/UX, expands desktop theming capabilities, and refactors core services for better stability and maintainability.

**Commit:** `4fb723b`

---

## 🎯 Key Improvements

### 🔥 Android App Enhancements

#### SettingsManager Rewrite
- **Complete architectural refactor** of preference management system
- Improved state persistence and data consistency
- Streamlined API for settings access and modification
- Better error handling and null safety

#### RemoteDesktopScreen Overhaul
- **Optimized touch handling** with improved gesture recognition
- Enhanced rendering performance with reduced re-composition
- Improved screen capture display and responsiveness
- Better handling of orientation changes
- Optimized pinch-to-zoom functionality

#### RemoteControlScreen Enhancements
- Improved button responsiveness and visual feedback
- **New haptic vibration system** for tactile user feedback
- Better touch target sizing for easier interaction
- Enhanced visual hierarchy and UI polish

#### RemoteMouseScreen Improvements
- Performance optimization with reduced re-renders
- **Enhanced mouse overlay** with better visibility
- Improved pointer tracking accuracy
- Better handling of rapid inputs

#### New Features
- **Haptic Vibration Feedback:** Provides tactile feedback for all user interactions
- **Mouse Overlay Enhancements:** Better visibility and responsiveness for remote mouse control
- **Screen Categories:** Improved organization of control screens
- **Updated Tagline & Descriptions:** Better app store presentation

#### Gradle & Dependencies
- Updated Android Gradle Plugin to latest version
- Modernized dependency versions
- Enhanced build configuration for faster compilation

---

### 🎨 Desktop Client (Avalonia) Updates

#### Theme System Enhancements
- Improved theme color management and consistency
- New dynamic color generation system
- Better dark mode support with glassmorphism effects

#### New Theme Variants
- **CyberNOC:** High-contrast cyberpunk-inspired theme
- **Monolith:** Minimalist, clean design theme
- **SolarFlare:** Warm, energetic color scheme
- **BaseDarkGlass:** Refined dark glass aesthetic

#### Localization Expansion
- **1,660+ new string resources** added
- Improved consistency across all UI elements
- Better support for non-English languages
- All 8 supported languages updated:
  - English (en)
  - Spanish (es)
  - Hindi (hi)
  - Indonesian (id)
  - Polish (pl)
  - Portuguese - Brazil (pt-BR)
  - Turkish (tr)
  - Ukrainian (uk)

#### UI/UX Improvements
- **CustomizationView:** Complete redesign for theme and appearance settings
- **SettingsView:** Enhanced layout and organization
- **ShellView:** Optimized XAML structure and rendering
- **TrayFlyoutWindow:** Refined notification and quick-action UI
- **Dashboard Background:** Improved visual effects and performance
- **Canvas View:** Better workspace management and responsiveness

#### Code Quality
- Enhanced input validation throughout UI layer
- Improved null safety with proper guard clauses
- Better separation of concerns in ViewModels
- Optimized data binding patterns

---

### 🔧 Core & Host Services

#### Models
- **DashboardProfile:** Enhanced with additional metadata fields
- **DesktopMeta:** Expanded with platform-specific information
  - Better CPU/GPU detection
  - Improved system information accuracy
  - Extended telemetry metadata

#### Service Improvements
- **IInputSimulationService:** Extended interface for better platform abstraction
- **LinuxInputSimulationService:** 
  - Complete service implementation with 62 new lines of functionality
  - Native X11/Wayland input simulation
  - Improved keyboard and mouse event handling
  - Better error recovery and state management
  
- **WindowsInputSimulationService:** 
  - Major refactor for improved robustness
  - Enhanced error handling
  - Better clipboard integration
  - Improved input timing and synchronization

#### Telemetry Services
- Windows Telemetry: Better sensor aggregation and deduplication
- Linux Telemetry: Improved lmsensors integration
- Cross-platform consistency improvements

#### Remote Desktop Handler
- Optimized frame capture and encoding
- Better stream management
- Improved error recovery
- Enhanced performance monitoring

---

## 📊 Statistics

- **Files Modified:** 56
- **Lines Added:** 6,260
- **Lines Removed:** 3,436
- **Net Change:** +2,824 lines
- **Commits in Release:** 1 (consolidated)

---

## ✅ Testing Recommendations

### Android Testing
- [ ] Test all haptic feedback on various devices
- [ ] Verify SettingsManager persistence across app restarts
- [ ] Test RemoteDesktopScreen with various resolutions
- [ ] Verify touch gestures (pinch, swipe, tap)
- [ ] Test on multiple Android versions (API 26+)
- [ ] Performance testing on mid-range devices

### Desktop Testing
- [ ] Verify all theme variants load correctly
- [ ] Test theme switching without app restart
- [ ] Verify localization strings are complete
- [ ] Test CustomizationView settings persistence
- [ ] Test on both Windows and Linux
- [ ] Verify tray functionality on both platforms

### Cross-Platform Testing
- [ ] Pair Android to Windows host
- [ ] Pair Android to Linux host
- [ ] Test remote desktop streaming
- [ ] Verify mouse/keyboard control
- [ ] Test haptic feedback integration

---

## 🔐 Security Notes

- All input validation has been reviewed and hardened
- No security-relevant changes in this release
- Existing security measures maintained
- Compliance with prior audit findings maintained

---

## 📥 Installation & Upgrade

### Windows Desktop
```bash
Download: RemEx-v1.11.0-Setup.exe
Run installer and follow prompts
Existing settings will be preserved
```

### Linux Desktop
```bash
Download: remex-client-v1.11.0-linux-x64.tar.gz
Extract and run: ./install.sh install
Existing settings will be preserved
```

### Android App
```
Download: RemEx-v1.11.0.apk
Install on device
App will automatically migrate settings from v1.10.0
```

---

## 📝 Known Issues

- **Linux:** Remote desktop cursor display intermittently fails to render on phone screen (Windows behavior unconfirmed)
  - Cursor input still registers correctly
  - Workaround: Rapidly move cursor to expand overlay—expanded version renders temporarily
  - Investigating root cause in cursor overlay rendering/scaling logic

---

## 🙏 Acknowledgments

This release includes refinements based on community feedback and internal testing across Windows, Linux, and Android platforms.

---

## 📞 Support

For issues or questions:
- GitHub Issues: https://github.com/clindsay94/remex/issues
- Discussions: https://github.com/clindsay94/remex/discussions

---

## 🔄 Version History

| Version | Date | Focus |
|---------|------|-------|
| 1.11.0 | Apr 15, 2026 | UI Polish & Cross-Platform Enhancements |
| 1.10.0 | Earlier | Linux Integration & QR Pairing |
| 1.9.0 | Earlier | Production Readiness |

---

**Build Information:**
- Android versionCode: 11
- Target SDK: Latest
- .NET Version: 10.0
- Avalonia UI: 11.3
- Kotlin: Latest
