# Contributing to RemEx

Thanks for your interest in contributing! This document covers how to set up the project, build each target, and submit changes.

---

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- For Avalonia Android builds: `dotnet workload install android`
- For the native Android app (`RemEx.Android`): Android Studio (or a standalone JDK 17+ and Android SDK with `ANDROID_HOME` set)
- An IDE that supports .NET — Visual Studio 2022+, Rider, or VS Code with the C# Dev Kit

---

## Project Structure

```text
Remex.sln                    .NET solution
├── Remex.Core/              Shared models, messages, and service interfaces
│                            ↳ Also compiled as libRemexCore.so (NativeAOT) for Android JNI
├── Remex.Host/              ASP.NET headless service (Minimal APIs + WebSocket + mDNS)
├── Remex.Client/            Shared Avalonia UI — views, viewmodels, controls, services, themes
├── Remex.Client.Desktop/    Desktop entry point (Windows / Linux)
├── Remex.Client.Android/    Avalonia Android entry point + M3 theme overrides
├── Remex.Core.Tests/        xUnit tests for Core
├── Remex.Host.Tests/        xUnit tests for Host
├── RemEx.Android/           Native Android app — Kotlin + Jetpack Compose + JNI → libRemexCore.so
└── scripts/                 Utility scripts (Windows Service installer, android-fresh pipeline)
```

---

## Build & Run

### Host Service

```bash
dotnet run --project Remex.Host
```

### Desktop Client

```bash
dotnet run --project Remex.Client.Desktop
```

### Tests

```bash
dotnet test Remex.sln
```

---

## Publish

### Desktop — Self-Contained Single File (Windows x64)

```bash
dotnet publish Remex.Client.Desktop\Remex.Client.Desktop.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
```

### Avalonia Android APK

```bash
dotnet publish Remex.Client.Android\Remex.Client.Android.csproj -c Release -f net10.0-android
```

### Native Android App (`RemEx.Android`) — Hardened Fresh Rebuild

The native Android app requires `libRemexCore.so` to be built from `Remex.Core` before assembling the APK. Use the hardened pipeline to guarantee a verified artifact every time:

```powershell
# From repo root (recommended)
.\scripts\android-fresh.ps1 -Configuration Debug -Install   # build + install to connected device
.\scripts\android-fresh.ps1 -Configuration Debug            # build only
.\scripts\android-fresh.ps1 -Configuration Release          # release build + verification

# Or run Gradle tasks directly from RemEx.Android/
.\gradlew.bat remexFreshInstallDebug --rerun-tasks --no-configuration-cache
.\gradlew.bat remexFreshAssembleDebug --rerun-tasks --no-configuration-cache
.\gradlew.bat remexFreshAssembleRelease --rerun-tasks --no-configuration-cache
```

These tasks:

- Delete every `bin/` and `obj/` directory across the repository.
- Rebuild `Remex.Core` as a NativeAOT Android shared library (`libRemexCore.so`).
- Copy the `.so` into `RemEx.Android/app/src/main/jniLibs/arm64-v8a/` via `SyncRemexCoreSoTask`.
- Verify the APK-embedded library SHA-256 matches the just-built file via `VerifyRemexCoreInApkTask`.
- Fail immediately on any hash mismatch or missing artifact.

---

## Development Notes

### Architecture (Avalonia client)

- **MVVM** — Views in `Remex.Client/Views/`, ViewModels in `Remex.Client/ViewModels/`. Uses `CommunityToolkit.Mvvm` source generators (`[ObservableProperty]`, `[RelayCommand]`).
- **Navigation** — `ShellViewModel` owns the sidebar and child VM lifecycle. Views are resolved via `DataTemplate` in `ShellView.axaml`.
- **Communication** — `ConnectionViewModel` manages the primary WebSocket. `RemoteDesktopService` has its own dedicated socket (`/ws/desktop`). Local IPC uses a named pipe (`RemExLocalIPC`).
- **Telemetry** — `HWiNFO` (Windows) / `lmsensors` (Linux) polled by the host and broadcast over the WebSocket.
- **mDNS** — `MdnsAdvertisingService` (host) and `MdnsDiscoveryService` (client) enable auto-discovery on the LAN.

### Architecture (native Android — `RemEx.Android`)

- **Compose + ViewModel** — Each screen has a `*Screen.kt` Composable and a `*ViewModel.kt` backed by `StateFlow`.
- **JNI Bridge** — `RemexCoreClient` (Kotlin `object`) loads `libRemexCore.so` at startup and exposes all native entry points. Register callbacks via `RemexCoreClient.setCallback()`.
- **`RemexClientManager`** — Singleton that owns connection state and routes JNI callbacks to the active ViewModel.
- **Navigation** — `AppNavigation.kt` with `NavHost`; routes defined in `NavRoutes.kt`. Bottom `NavigationBar` visible on all main screens; hidden during splash/connection.
- **Personalization** — `SettingsManager` (DataStore) persists theme seed color, font family, and card-shape preset. `PersonalizationViewModel` exposes `StateFlow`s consumed by `RemExTheme` and card Composables.
- **Widgets** — Each widget provider reads from `WidgetSettingsManager` (DataStore) and is configured via `WidgetConfigActivity`.

### Versioning

The .NET version is set once in `Directory.Build.props` and applied to all .NET projects automatically.

**Android Native App (`RemEx.Android`):** Version is managed in `app/version.properties` and read automatically by the Gradle build:

```properties
versionCode=3
versionName=1.1.1
```

Two release workflows are available:

| Command | Version Behavior | Outputs |
|:--------|:-----------------|:--------|
| `.\gradlew remexFreshAssembleRelease` | Uses current version as-is | APK + AAB |
| `.\gradlew remexPublishRelease` | Auto-bumps: versionCode+1, minor+1, patch→0 | APK + AAB (ready for Play Console upload) |

For example, if `version.properties` has `versionCode=3` and `versionName=1.1.1`, running `remexPublishRelease` will build with `versionCode=4` and `versionName=1.2.0`, and write the new values back to `version.properties`. Commit the updated file after publishing.

### Themes (Avalonia)

Theme resource dictionaries live in `Remex.Client/Themes/`. The `ThemeService` swaps them at runtime and applies customization overrides (accent color, corner radius, opacity, glow). Android-specific overrides are in `Material3Android.axaml`.

---

## Submitting Changes

1. Fork the repository
2. Create a feature branch: `git checkout -b feature/your-feature`
3. Make changes and ensure tests pass: `dotnet test Remex.sln`
4. Commit with a clear message
5. Open a pull request against `main`

### Guidelines

- Keep PRs focused — one feature or fix per PR
- Follow existing code style and naming conventions
- Add tests for new message types, models, or service logic
- Update `CHANGELOG.md` under an `[Unreleased]` section

---

## License

By contributing, you agree that your contributions will be licensed under the [MIT License](LICENSE).
