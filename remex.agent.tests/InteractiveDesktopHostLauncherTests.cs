using System;
using Remex.Agent.Services;
using Xunit;

namespace Remex.Agent.Tests;

public class InteractiveDesktopHostLauncherTests
{
    [Fact]
    public void HasHostInSession_HostInActiveSession_ReturnsTrue()
        => Assert.True(InteractiveDesktopHostLauncher.HasHostInSession(new uint[] { 1, 3 }, 1));

    [Fact]
    public void HasHostInSession_HostOnlyInOtherSessions_ReturnsFalse()
        => Assert.False(InteractiveDesktopHostLauncher.HasHostInSession(new uint[] { 3 }, 1));

    [Fact]
    public void HasHostInSession_NoHosts_ReturnsFalse()
        => Assert.False(InteractiveDesktopHostLauncher.HasHostInSession(Array.Empty<uint>(), 1));

    [Fact]
    public void HasHostInSession_InvalidActiveSession_ReturnsFalse()
        => Assert.False(InteractiveDesktopHostLauncher.HasHostInSession(new uint[] { 1 }, 0xFFFFFFFF));
}
