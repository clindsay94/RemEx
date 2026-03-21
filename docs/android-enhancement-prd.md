# PRD: RemEx Android Enhancement

## 1. Problem Statement
The current RemEx Android app lacks native visual polish (Material You), has a static and non-configurable widget, missing app icon, broken background effects (Mica/Gradients), and settings that do not persist across re-installs.

## 2. Success Criteria
- [ ] 3 Configurable Widgets: Sensors, Remote Control, and Resource Monitor.
- [ ] Material You Dynamic Colors: Theme matches system accent on Android 12+.
- [ ] "Glass" Background: High-quality translucent background on Android.
- [ ] Persistent Settings: Settings survive uninstallation and re-installation.
- [ ] Adaptive App Icon: Modern Android icon based on RemEx-Icon.png.
- [ ] Runtime Permissions: Proper handling of Notification and Storage permissions.

## 3. Technical Design

### 3.1 Configurable Widgets
- **Providers:** 
    - `SensorWidgetProvider`
    - `RemoteControlWidgetProvider`
    - `ResourceWidgetProvider`
- **Configuration:** Each widget will have a `WidgetConfigActivity` (Native Android) to choose specific sensors or commands.
- **Storage:** `SharedPreferences` per widget instance ID.

### 3.2 Visual Polish
- **Dynamic Color Extractor:** In `MainActivity.cs`, fetch `MaterialColors` and update `ThemeService`.
- **Blur Effect:** Replace Mica with a performant Skia-based blur or a native `RenderEffect` for Android 12+.
- **Icon:** Implement Adaptive Icon (`mipmap-anydpi-v26`).

### 3.3 Persistence
- **Auto Backup:** Update `AndroidManifest.xml` and `backup_rules.xml` to ensure `dashboard_layout.json` is backed up to Google Cloud.

## 4. Implementation Plan

### Step 1: Adaptive Icon & Permissions
- Generate adaptive icon assets from `RemEx-Icon.png`.
- Update `AndroidManifest.xml` with `<application android:icon="@mipmap/ic_launcher">`.
- Add notification and storage permission handling.

### Step 2: Persistence Fix
- Verify and update Android Auto Backup rules.
- Ensure all settings are stored in the correct `Remex/` subfolder.

### Step 3: Material You & Backgrounds
- Implement dynamic color fetching.
- Add "Glass" background implementation for Android.

### Step 5: Configurable Widgets
- Implement `WidgetConfigActivity`.
- Create the 3 widget providers and their respective layouts.
