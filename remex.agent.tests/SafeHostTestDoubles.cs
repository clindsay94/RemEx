using Remex.Core.Models;
using Remex.Core.Services.Command;
using Remex.Agent.Services;
using Remex.Agent.Services.RemoteDesktop.Linux;
using Remex.Agent.Services.RemoteDesktop.Windows;
using Remex.Agent.Services.Session;

namespace Remex.Agent.Tests;

/// <summary>
/// An <see cref="IHostCapabilitiesProvider"/> that always reports a ready desktop. The real provider
/// reads the developer's live input desktop, so a /ws/desktop stream test run while the PC is locked
/// (or a UAC prompt is up) got "Winlogon secure desktop" and a closed socket instead of frames
/// (RemEx-jxza0).
/// </summary>
public sealed class DesktopReadyHostCapabilitiesProvider : IHostCapabilitiesProvider
{
    public HostCapabilities GetCurrent() => new()
    {
        Platform = "test",
        SupportsRemoteDesktop = true,
    };

    public LinuxPrerequisiteReport? GetLinuxPrerequisiteReport() => null;

    public WindowsRemoteDesktopDiagnosticReport? GetWindowsRemoteDesktopDiagnosticReport() => null;
}

/// <summary>
/// No-op <see cref="IInteractiveSessionGuard"/> for integration tests. The real
/// <c>WindowsInteractiveSessionGuard</c> runs <c>tscon.exe &lt;session&gt; /dest:console</c> to
/// reconnect/disconnect the Windows console session when the "keep session unlocked" feature is on
/// (flag file <c>ProgramData\RemEx\keep-session-unlocked.flag</c>). If a test host boots with the real
/// guard and a desktop stream engages it, the developer's session is physically locked — screen flash,
/// then the Windows login/PIN screen (RemEx-21g follow-up). Test hosts must never perform that OS call.
/// </summary>
public sealed class NoOpInteractiveSessionGuard : IInteractiveSessionGuard
{
    public void EngageForRemoteControl(string clientId) { }
    public void Disengage(string clientId) { }
}

/// <summary>
/// No-op <see cref="ISystemCommandService"/> for integration tests — defense-in-depth so a test host
/// can never execute a real OS power/session command (lock, sign-out, monitor-off, shutdown, restart).
/// The real Windows implementation actually locks/reboots the machine.
/// </summary>
public sealed class NoOpSystemCommandService : ISystemCommandService
{
    public Task Lock() => Task.CompletedTask;
    public Task Shutdown(int delaySeconds = 0) => Task.CompletedTask;
    public Task ForceShutdown(int delaySeconds = 0) => Task.CompletedTask;
    public Task Restart(int delaySeconds = 0) => Task.CompletedTask;
    public Task ForceRestart(int delaySeconds = 0) => Task.CompletedTask;
    public Task RestartToUefi(int delaySeconds = 0) => Task.CompletedTask;
    public Task Sleep() => Task.CompletedTask;
    public Task Hibernate() => Task.CompletedTask;
    public Task SignOut() => Task.CompletedTask;
    public Task MonitorOff() => Task.CompletedTask;
}
