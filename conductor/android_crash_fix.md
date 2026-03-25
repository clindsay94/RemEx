# RemEx.Android Startup Crash Fix Plan

## Objective
Fix the fatal `GetClassGRef: class net/dot/android/crypto/DotnetX509KeyManager was not found` crash that occurs when the `RemEx.Android` app loads the `libRemexCore.so` library via JNI.

## Root Cause Analysis
1. **The Core Crash**: The log indicates the crash happens within the .NET runtime's `JNI_OnLoad` during `AndroidCryptoNative_InitLibraryOnLoad`. It attempts to look up `net/dot/android/crypto/DotnetX509KeyManager` via JNI.
2. **Why it happens**: By compiling `Remex.Core` with `<TargetFramework>net10.0-android</TargetFramework>`, the .NET SDK implicitly includes the Xamarin/Android Base Class Library (BCL) wrappers. These wrappers attempt to map .NET cryptography APIs directly to Android's Java Cryptography Architecture (JCA).
3. **The Missing Link**: Because `RemEx.Android` is a native Android Studio (Kotlin/Gradle) app rather than a .NET Android application, it does not include the `.NET for Android` Java classes (such as `mono.android.jar` or `DotnetX509KeyManager.class`). When the native library initializes, the missing Java classes cause a fatal signal `6 (SIGABRT)`.
4. **Duplicate Entry Point**: The previous error during `dotnet publish` (`An item with the same key has already been added. Key: JNI_OnLoad`) happened because the `.NET for Android` runtime automatically injects its own `JNI_OnLoad` method, which collided with our custom implementation.

## Proposed Solution
Since `Remex.Core` is a headless service layer and does not rely on Xamarin Android UI APIs, we can bypass the `.NET for Android` Java bindings entirely. We will build a pure standard C/C++ shared library using the standard `.NET 10` SDK targeted at Android's native environment (`linux-bionic-arm64`).

This forces .NET NativeAOT to use standard BoringSSL/OpenSSL native bindings instead of relying on missing Java Interop files.

## Implementation Steps

### Step 1: Refactor `Remex.Core.csproj`
Modify `Remex.Core.csproj` to remove the Android-specific target framework and rely on pure `.NET 10`.

**Changes:**
*   Change `<TargetFrameworks>net10.0;net10.0-android</TargetFrameworks>` to `<TargetFramework>net10.0</TargetFramework>`.
*   Remove `net10.0-android` conditional ItemGroups and Targets (e.g., `NormalizeAndroidNativeAotTrimmerCustomData`).
*   Simplify the project structure to build a clean shared library.

### Step 2: Clean and Rebuild
Execute a fresh build pipeline targeting the correct Runtime Identifier (`linux-bionic-arm64`).

**Build Command:**
```powershell
dotnet clean -c Debug
dotnet publish .\Remex.Core\Remex.Core.csproj -c Debug -f net10.0 -r linux-bionic-arm64 /p:PublishAot=true
```

### Step 3: Sync and Deploy
1. Copy the resulting `libRemexCore.so` from the `linux-bionic-arm64/publish/` folder into `RemEx.Android/app/src/main/jniLibs/arm64-v8a/`.
2. Run `gradlew clean assembleDebug` to repackage the APK without any stale artifacts.
3. Install the APK via ADB.

## Verification
1. The app should launch and display the `Waiting for Debugger` screen or bypass it completely.
2. The `UnsatisfiedLinkError` and `SIGABRT` crashes related to `DotnetX509KeyManager` will be entirely eliminated.
3. The app should successfully connect to the WebSocket and stream telemetry data.
