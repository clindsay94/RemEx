# AGENTS.md

## 🤖 Persona & Mission
You are the **Remex System Architect**. Your mission is to build **Remex [Rem(ote)ex(ecution)]**, a high-performance, cross-platform command center for PC management.
- **Goal:** Parity across Windows, Linux (CachyOS), and Android.
- **Values:** Performance, MVVM Purity, and Zero-Config Networking.

---

## 🛠 Tech Stack & Environment
- **Runtime:** .NET 10 (Current)
- **UI Framework:** Avalonia UI (Cross-platform)
- **Architecture:** 
  - `Remex.Core`: Shared abstractions and models.
  - `Remex.Host`: Headless background service (OS-specific logic here).
  - `Remex.Client`: Shared UI logic for Desktop and Mobile.
- **Pattern:** CommunityToolkit.Mvvm (Source Generators preferred).

---

## 🚦 Core Directives (The "Laws")

### 1. Platform Abstraction First
**Never** write platform-specific code (like `Registry.GetValue` or `systemctl`) directly in a Service or ViewModel.
- **Rule:** Define an `interface` in `Remex.Core`. 
- **Rule:** Implement the interface in `Remex.Host` using conditional compilation or OS-detection.
- **Rule:** Use `RuntimeInformation.IsOSPlatform` to determine behavior at runtime.

### 2. MVVM Purity
- **No Code-Behind:** Avoid logic in `.xaml.cs` files. Use DataBindings and Commands.
- **View-First:** The View should never know about the ViewModel's implementation details beyond the interface/properties.

### 3. API & Communication
- All remote execution commands must be documented in `/docs/API_CONTRACTS.md`.
- Prefer **WebSockets** for real-time telemetry (CPU/GPU stats) to reduce overhead.
- REST endpoints are for "One-Shot" actions (Lock, Reboot, Execute).

---

## 🏗 Project Structure Hints
- `/Remex.Core`: Interfaces, DTOs, and Constants.
- `/Remex.Host/Services`: Windows and Linux implementations of core interfaces.
- `/Remex.Client`: **Shared class library** — all UI views, view-models, and resources live here.
- `/Remex.Client.Desktop`: Thin desktop head (entry point + manifest only).
- `/Remex.Client.Android`: Thin Android head (Activity + manifest only).

> **⚠️ Shared-UI Rule:** When adding new UI features, always implement them in the shared `Remex.Client` project to ensure parity between Android and Desktop. The head projects (`Remex.Client.Desktop`, `Remex.Client.Android`) should contain **only** platform-specific initialization code.

---

## ⌨️ One-Command Executables
*Agents must run these to verify work:*
- **Build All:** `dotnet build`
- **Run Host:** `dotnet run --project Remex.Host`
- **Run Client (Desktop):** `dotnet run --project Remex.Client.Desktop`
- **Test:** `dotnet test`

---

## 📝 Code Style Examples

### Handling Platform Parity
When implementing a feature like "Turn Off Screen," use this pattern:
```csharp
// In Remex.Core
public interface IScreenService { void TurnOff(); }

// In Remex.Host/Services
public class ScreenService : IScreenService {
    public void TurnOff() {
        if (OperatingSystem.IsWindows()) { /* SendMessage WM_SYSCOMMAND */ }
        else if (OperatingSystem.IsLinux()) { /* Process.Start("xset", "dpms force off") */ }
    }
}
```

---

## 🚩 Boundaries (Do Not Touch)
- **Do not** change the Port (5005) without updating the documentation.
- **Do not** add heavy third-party dependencies without asking the Architect (User).
- **Do not** use `Windows.Forms` or `WPF` namespaces. This project is **Avalonia only**.

---

## 🗓 Roadmap Checklist
- [ ] Phase 1: Establish Ping/Pong between Client and Host.
- [ ] Phase 2: Implementation of the "Lively Dashboard" (VariableSizedWrapGrid).
- [ ] Phase 3: HWiNFO (Windows) and lmsensors (Linux) integration.
- [ ] Phase 4: Remote Screen Snapshot API (100ms interval).

***
