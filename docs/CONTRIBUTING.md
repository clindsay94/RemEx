# Contributing to RemEx

Thanks for your interest in contributing! This document covers how to set up the project, build each target, and submit changes.

---

## Prerequisites
- [Android Studio](https://developer.android.com/studio/) or a standalone JDK 17+ and Android SDK with `ANDROID_HOME` set
  - **New to Android development?** See our comprehensive [Android Setup Guide](docs/ANDROID_SETUP.md) for step-by-step instructions
- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- An IDE that supports .NET — Visual Studio 2022+, Rider, or VS Code with the C# Dev Kit

---

## Project Structure

```text
Remex.sln                    .NET solution
├── Remex.Core/              Shared models, messages, and validation logic
│                            ↳ Also compiled as libRemexCore.so (NativeAOT) for Android JNI
├── Remex.Host/              ASP.NET headless service (Minimal APIs + WebSocket + mDNS)
├── Remex.Client/            Shared Avalonia UI — views, viewmodels, controls, services, themes
├── Remex.Client.Desktop/    Desktop entry point (Windows / Linux)
├── RemEx.Android/           Native Android app — Kotlin + Jetpack Compose + JNI → libRemexCore.so
├── docs/                    Architectural guidelines (Async, Null Safety, Validation)
├── scripts/                 Utility scripts (Windows Service installer, android-fresh pipeline)
└── installer/               Build scripts for Windows (Inno Setup) and Linux (bash)
```

---

## Build & Run

### Host Service & Desktop Client
```bash
# Run Host
dotnet run --project Remex.Host

# Run Client
dotnet run --project Remex.Client.Desktop
```

### Tests
```bash
dotnet test Remex.sln
```

---

## Publish

### Linux Packages
The Linux build produces `.tar.gz` archives for both the client and host, including automated `install.sh` scripts.
```bash
# From repo root
./installer/build-linux.sh
```

### Windows Installer (Inno Setup)
Requires [Inno Setup 6+](https://jrsoftware.org/isinfo.php). The script publishes the desktop binary, then compiles the installer:
```powershell
# From repo root
pwsh ./installer/build-installer.ps1

# Force a specific flow if needed
pwsh ./installer/build-installer.ps1 -Target Windows
pwsh ./installer/build-installer.ps1 -Target Linux
```

### Native Android App (`RemEx.Android`)
The native Android app requires `libRemexCore.so` to be built from `Remex.Core` before assembling the APK.
```powershell
# Build + Verify + Install (recommended)
.\scripts\android-fresh.ps1 -Configuration Release -Install
```

---

## Development Guidelines
We have established strict architectural patterns to ensure "Production Readiness." All contributions must adhere to these guidelines:
- [**Async/Await Patterns**](docs/ASYNC_GUIDELINES.md) — Mandatory for all async code.
- [**Null Safety**](docs/NULL_SAFETY_GUIDELINES.md) — Comprehensive rules for handling nullable types.
- [**Validation**](docs/VALIDATION_GUIDELINES.md) — Unified validation logic for all network-facing services.

---

## Versioning

### .NET Projects
Managed centrally in `Directory.Build.props`.

### Android Native App
Managed in `RemEx.Android/app/version.properties`:
```properties
versionCode=12
versionName=1.10.0
```

Use `.\gradlew remexPublishRelease` from the `RemEx.Android/` directory to auto-increment these values and prepare a release build.

---

## Submitting Changes

1. Fork the repository and create a feature branch.
2. Ensure all tests pass: `dotnet test Remex.sln`.
3. Follow existing code style and naming conventions.
4. **Important:** All new features or bug fixes must include corresponding tests and adhere to our [Architectural Guidelines](docs/).
5. Open a pull request against `main`.

---

## License
By contributing, you agree that your contributions will be licensed under the [MIT License](LICENSE).
