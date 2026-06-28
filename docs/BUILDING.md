# Building RemEx

This document outlines the commands used to build, package, and test various components of the RemEx application.

## Unified Build Script

The primary entry point for packaging is the `build-remex.ps1` script located at the repository root.

| Target Platform / Alias | Command | Description |
|-------------------------|---------|-------------|
| All targets (Release)   | `pwsh ./build-remex.ps1 -c release -t all` | Builds all platforms (Windows, Linux, Android) in Release mode. |
| Windows (Unified)       | `pwsh ./build-remex.ps1 -c release -t windows` | Publishes Windows binaries and builds the Inno Setup installer. |
| Windows Client Only     | `pwsh ./build-remex.ps1 -c release -t windows-client` | Publishes the client (`dotnet publish` step only) and skips the installer. |
| Windows Installer Only  | `pwsh ./build-remex.ps1 -c release -t installer` | Runs the Inno Setup compiler only (skips `publish` if already built). |
| Android (Unified)       | `pwsh ./build-remex.ps1 -c release -t android` | Compiles the Android APK via the unified build pipeline. |
| Android APK (Alias)     | `pwsh ./build-remex.ps1 -c release -t apk` | Alias target that compiles the Android APK. |
| Linux Package           | `pwsh ./build-remex.ps1 -c release -t linux` | Compiles and packages the host and client binaries for Linux. |

## Development Commands

Use the standard .NET CLI to run or test local code during development.

| Task | Command |
|---|---|
| Run host in dev mode | `dotnet run --project remex.agent` |
| Run client in dev mode | `dotnet run --project remex.desktop` |
| Run entire test suite | `dotnet test Remex.sln` |
| Run Linux host Doctor | `dotnet run --project remex.agent -- --doctor` |

## Android Development

For clean, standalone Android builds, use the provided helper script:

```powershell
# Compile release APK
.\scripts\android-fresh.ps1 -Configuration Release

# Compile debug APK
.\scripts\android-fresh.ps1 -Configuration Debug
```

## Service Management (Windows)

To set up or remove RemEx auto-start at sign-in (an elevated Task Scheduler logon task — **not** a Windows Service):

```powershell
# Set up auto-start at login (Run as Administrator)
.\scripts\autostart-remex.ps1 -Action Install

# Remove auto-start at login (Run as Administrator)
.\scripts\autostart-remex.ps1 -Action Uninstall
```
