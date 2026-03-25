# Implementation Plan - RemEx Android Porting

This plan outlines the steps to bring the remaining RemEx functionality (Remote Control, App Launcher, Task Manager, Remote Desktop) to the new Android application using Material 3 and Jetpack Compose.

## Objective
Port core RemEx features from the original client to the Android app, utilizing the existing NativeAOT infrastructure in `Remex.Core`.

## Key Files & Context
- **Native Infrastructure:** `Remex.Core/Native/AndroidNativeExports.cs`, `Remex.Core/Native/JniHelper.cs`
- **Android UI:** `RemEx.Android/app/src/main/java/com/clindsay94/remex/`
- **Shared Logic:** `Remex.Core/Messages/RemexMessage.cs`, `Remex.Core/Models/`

## Proposed Changes

### 1. Native Infrastructure (Remex.Core)
- **WebSocket Client:** Implement a `RemexWebSocketClient` in `Remex.Core` that handles the persistent connection to the host.
- **JNI Callbacks:** Enhance the JNI bridge to support calling back into Java/Kotlin for real-time telemetry and connection state updates.
- **Service Registration:** Update `AndroidNativeExports` to maintain the connection and dispatch commands over the WebSocket instead of local IPC.

### 2. Android Infrastructure
- **Navigation:** Integrate `navigation-compose` for screen transitions.
- **Drawer Layout:** Implement a `ModalNavigationDrawer` for consistent navigation across features.
- **Shared State:** Use a central `AppViewModel` or similar to manage global connection state.

### 3. Screen Implementations (Jetpack Compose + Material 3)
- **ConnectionScreen:** Configure Host IP, Port, and manage connection status.
- **DashboardScreen:** (Improvement) Real-time telemetry display with better visuals.
- **RemoteControlScreen:** Virtual trackpad, mouse buttons, and remote keyboard.
- **AppLauncherScreen:** List apps from the host and launch them.
- **TaskManagerScreen:** View running processes and manage them.
- **RemoteDesktopScreen:** Live screen stream (JPEG-over-WebSocket).

## Implementation Steps

### Phase 1: Native & Infrastructure
1. **[Native] Update JNI Bridge:**
   - Add `RegisterCallback` to `AndroidNativeExports.cs` to store a global reference to the Java callback object.
   - Implement `CallVoidMethod` in `JNIEnv` (JniHelper.cs) to allow calling `onTelemetryUpdate` and `onConnectionStateChanged`.
2. **[Native] Implement WebSocket Client:**
   - Create `Remex.Core/Native/RemexNativeClient.cs` to handle `ClientWebSocket` logic.
   - Redirect `SendCommand` and `InitRemex` to use this client.
3. **[Android] Add Dependencies:**
   - Add `navigation-compose` and `androidx-navigation-compose` to `libs.versions.toml` and `build.gradle.kts`.
4. **[Android] Setup Navigation:**
   - Create `NavRoutes.kt` and `AppNavigation.kt`.
   - Update `MainActivity.kt` with `ModalNavigationDrawer`.

### Phase 2: Core Screens
1. **Connection Screen:**
   - Create `ConnectionViewModel.kt` and `ConnectionScreen.kt`.
   - Persist host settings using `DataStore` or `SharedPreferences`.
2. **Enhanced Dashboard:**
   - Update `DashboardScreen.kt` to integrate with the new navigation and connection state.
3. **Remote Control (Mouse/Keyboard):**
   - Create `RemoteControlViewModel.kt` and `RemoteControlScreen.kt`.
   - Implement a trackpad area using `PointerInput`.

### Phase 3: Advanced Screens
1. **App Launcher:**
   - Fetch `launcher_sync` from native and display list.
2. **Task Manager:**
   - Fetch `process_list_sync` from native and display list.
3. **Remote Desktop:**
   - Implement a binary stream receiver in `RemexCoreClient.kt` (or via callback).
   - Create `RemoteDesktopScreen.kt` to render JPEG frames.

## Verification & Testing
- **Native Connectivity:** Verify WebSocket connection status in logs and UI.
- **Command Dispatch:** Ensure "Lock", "Sleep", "Restart" work over the network.
- **Telemetry:** Verify CPU/GPU/RAM usage updates in real-time without polling from Java.
- **Navigation:** Test switching between all screens via the drawer.
- **Responsiveness:** Ensure Material 3 components adapt to different screen sizes.
