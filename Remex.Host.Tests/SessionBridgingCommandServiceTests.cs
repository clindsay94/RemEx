using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Remex.Core.Services.Command;
using Remex.Host.Services.Command;

namespace Remex.Host.Tests;

/// <summary>
/// Verifies the Session-0 command bridge. Power commands always pass through to the inner service;
/// the desktop-bound commands (lock/monitor-off/sign-out) pass through unchanged when NOT in Session 0
/// (the interactive GUI-host case). The Session-0 bridging path itself relies on Win32 WTS APIs and a
/// live console session, so it is exercised manually rather than unit-tested here.
/// </summary>
public class SessionBridgingCommandServiceTests
{
    private static bool IsSession0 => Process.GetCurrentProcess().SessionId == 0;

    private static (SessionBridgingCommandService svc, Mock<ISystemCommandService> inner) Create()
    {
        if (!OperatingSystem.IsWindows())
        {
            // SessionBridgingCommandService is [SupportedOSPlatform("windows")]; callers guard on this.
            throw new PlatformNotSupportedException();
        }

        var inner = new Mock<ISystemCommandService>();
        inner.Setup(s => s.Shutdown(It.IsAny<int>())).Returns(Task.CompletedTask);
        inner.Setup(s => s.ForceShutdown(It.IsAny<int>())).Returns(Task.CompletedTask);
        inner.Setup(s => s.Restart(It.IsAny<int>())).Returns(Task.CompletedTask);
        inner.Setup(s => s.ForceRestart(It.IsAny<int>())).Returns(Task.CompletedTask);
        inner.Setup(s => s.RestartToUefi(It.IsAny<int>())).Returns(Task.CompletedTask);
        inner.Setup(s => s.Sleep()).Returns(Task.CompletedTask);
        inner.Setup(s => s.Hibernate()).Returns(Task.CompletedTask);
        inner.Setup(s => s.SignOut()).Returns(Task.CompletedTask);
        inner.Setup(s => s.Lock()).Returns(Task.CompletedTask);
        inner.Setup(s => s.MonitorOff()).Returns(Task.CompletedTask);

        var svc = new SessionBridgingCommandService(NullLogger<SessionBridgingCommandService>.Instance, inner.Object);
        return (svc, inner);
    }

    [Fact]
    public async Task PowerCommands_AlwaysPassThroughToInner()
    {
        if (!OperatingSystem.IsWindows()) return;

        var (svc, inner) = Create();

        await svc.Shutdown(5);
        await svc.ForceShutdown(0);
        await svc.Restart(3);
        await svc.ForceRestart(0);
        await svc.RestartToUefi(0);
        await svc.Sleep();
        await svc.Hibernate();

        inner.Verify(s => s.Shutdown(5), Times.Once);
        inner.Verify(s => s.ForceShutdown(0), Times.Once);
        inner.Verify(s => s.Restart(3), Times.Once);
        inner.Verify(s => s.ForceRestart(0), Times.Once);
        inner.Verify(s => s.RestartToUefi(0), Times.Once);
        inner.Verify(s => s.Sleep(), Times.Once);
        inner.Verify(s => s.Hibernate(), Times.Once);
    }

    [Fact]
    public async Task DesktopCommands_PassThrough_WhenNotInSession0()
    {
        if (!OperatingSystem.IsWindows()) return;
        // Outside Session 0 (interactive/test host) the direct APIs work, so no bridging occurs.
        // In the rare case the test host runs as a Session-0 service, skip — bridging is expected there.
        if (IsSession0) return;

        var (svc, inner) = Create();

        await svc.Lock();
        await svc.MonitorOff();
        await svc.SignOut();

        inner.Verify(s => s.Lock(), Times.Once);
        inner.Verify(s => s.MonitorOff(), Times.Once);
        inner.Verify(s => s.SignOut(), Times.Once);
    }
}
