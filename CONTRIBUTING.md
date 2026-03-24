# Contributing to RemEx

Thanks for your interest in contributing! This document covers how to set up the project, build each target, and submit changes.

---

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- For Android builds: `dotnet workload install android`
- An IDE that supports .NET — Visual Studio 2022+, Rider, or VS Code with the C# Dev Kit

---

## Project Structure

```text
Remex.sln                    Solution root
├── Remex.Core/              Shared models, messages, and service interfaces
├── Remex.Host/              ASP.NET headless service (Minimal APIs + WebSocket)
├── Remex.Client/            Shared Avalonia UI — views, view-models, controls, services
├── Remex.Client.Desktop/    Desktop entry point (Windows / Linux)
├── Remex.Client.Android/    Android entry point
├── Remex.Core.Tests/        xUnit tests for Core
├── Remex.Host.Tests/        xUnit tests for Host
└── scripts/                 Utility scripts (Windows Service installer)
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

### Android — APK

```bash
dotnet publish Remex.Client.Android\Remex.Client.Android.csproj -c Release -f net10.0-android
```

### Android — Hardened Fresh Rebuild Workflow

Use the Gradle tasks below when you need guaranteed fresh APK output with native library verification:

```powershell
# From repo root (recommended wrapper)
.\scripts\android-fresh.ps1 -Configuration Debug -Install
.\scripts\android-fresh.ps1 -Configuration Debug
.\scripts\android-fresh.ps1 -Configuration Release

# Or from RemEx.Android directly
.\gradlew.bat remexFreshInstallDebug --rerun-tasks --no-configuration-cache
.\gradlew.bat remexFreshAssembleDebug --rerun-tasks --no-configuration-cache
.\gradlew.bat remexFreshAssembleRelease --rerun-tasks --no-configuration-cache
```

These tasks:

- Delete every `bin/` and `obj/` directory across the repository.
- Rebuild `Remex.Core` Android NativeAOT output.
- Ensure APK-embedded `libRemexCore.so` hash matches the just-published file.
- Validate timestamps so stale artifacts fail the build immediately.

---

## Development Notes

### Architecture

- **MVVM** — Views in `Remex.Client/Views/`, ViewModels in `Remex.Client/ViewModels/`. Uses `CommunityToolkit.Mvvm` source generators (`[ObservableProperty]`, `[RelayCommand]`).
- **Navigation** — `ShellViewModel` owns the sidebar and child VM lifecycle. Views are resolved via `DataTemplate` in `ShellView.axaml`.
- **Communication** — `ConnectionViewModel` manages the primary WebSocket. `RemoteDesktopService` has its own dedicated socket (`/ws/desktop`). Local IPC uses a named pipe (`RemExLocalIPC`).
- **Telemetry** — `HWiNFO` (Windows) / `lmsensors` (Linux) polled by the host and broadcast over the WebSocket.

### Versioning

The version is set once in `Directory.Build.props` and applied to all projects automatically.

### Themes

Theme resource dictionaries live in `Remex.Client/Themes/`. The `ThemeService` swaps them at runtime and applies customization overrides (accent color, corner radius, opacity, glow).

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
