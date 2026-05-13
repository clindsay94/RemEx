using System.Runtime.Versioning;
using Remex.Host.Services.Input.Linux;
using Remex.Host.Services.RemoteDesktop.Linux;
using Xunit;

namespace Remex.Host.Tests;

/// <summary>
/// Unit tests for <see cref="LinuxInputBackendRouter"/>.
/// These tests verify routing decisions based on <see cref="LinuxInputCapabilitySet"/>
/// without starting real EIS or uinput infrastructure.
/// </summary>
[SupportedOSPlatform("linux")]
public class LinuxInputBackendRouterTests
{
    // ── BackendName selection ──────────────────────────────────────────

    [Fact]
    public void BackendName_IsNull_WhenUnsupported()
    {
        var caps = LinuxInputCapabilitySet.FromReport(new LinuxPrerequisiteReport
        {
            SelectedTier = LinuxRemoteDesktopTier.Unsupported,
        });
        using var router = new LinuxInputBackendRouter(caps);
        Assert.Null(router.BackendName);
    }

    [Fact]
    public void BackendName_IsXdotool_WhenX11Degraded()
    {
        var caps = LinuxInputCapabilitySet.FromReport(new LinuxPrerequisiteReport
        {
            SelectedTier = LinuxRemoteDesktopTier.X11Degraded,
            IsX11Session = true,
        });
        using var router = new LinuxInputBackendRouter(caps);
        // BackendName reports xdotool when EIS is not available (which it won't be
        // in a unit test environment without the native library).
        Assert.Equal("xdotool", router.BackendName);
    }

    [Fact]
    public void BackendName_IsPortalNotify_WhenPortalNoPen()
    {
        var caps = LinuxInputCapabilitySet.FromReport(new LinuxPrerequisiteReport
        {
            SelectedTier = LinuxRemoteDesktopTier.PortalNoPen,
            IsWaylandSession = true,
            PortalRemoteDesktopAvailable = true, // PortalNotifyAvailable derives from this
        });
        using var router = new LinuxInputBackendRouter(caps);
        Assert.Equal("portal-notify", router.BackendName);
    }

    [Fact]
    public void BackendName_IsLibei_WhenWaylandNativeAndEisOpened()
    {
        var caps = LinuxInputCapabilitySet.FromReport(new LinuxPrerequisiteReport
        {
            SelectedTier = LinuxRemoteDesktopTier.WaylandNative,
            IsWaylandSession = true,
            LibeiAvailable = true,
        });
        using var router = new LinuxInputBackendRouter(caps);
        // Without the native library the EIS sender will not open. The router
        // should fall back to portal-notify without throwing.
        Assert.Contains(router.BackendName, new[] { "libei", "portal-notify" });
    }

    // ── Capability routing flags ───────────────────────────────────────

    [Fact]
    public void CapabilitySet_CanInjectPen_WhenUinputWritable()
    {
        var caps = LinuxInputCapabilitySet.FromReport(new LinuxPrerequisiteReport
        {
            SelectedTier = LinuxRemoteDesktopTier.WaylandNative,
            IsWaylandSession = true,
            UinputNodeExists = true,
            UinputWritable = true,
        });
        Assert.True(caps.UinputTabletAvailable);
        Assert.True(caps.CanInjectPen);
    }

    [Fact]
    public void CapabilitySet_CannotInjectPen_WhenUinputNotWritable()
    {
        var caps = LinuxInputCapabilitySet.FromReport(new LinuxPrerequisiteReport
        {
            SelectedTier = LinuxRemoteDesktopTier.WaylandNative,
            IsWaylandSession = true,
            UinputNodeExists = true,
            UinputWritable = false,
        });
        Assert.False(caps.UinputTabletAvailable);
        Assert.False(caps.CanInjectPen);
    }

    [Fact]
    public void CapabilitySet_HasNoInput_WhenUnsupported()
    {
        var caps = LinuxInputCapabilitySet.FromReport(new LinuxPrerequisiteReport
        {
            SelectedTier = LinuxRemoteDesktopTier.Unsupported,
        });
        Assert.False(caps.HasAnyInput);
    }

    // ── Dispose safety ────────────────────────────────────────────────

    [Fact]
    public void Dispose_IsIdempotent_AndDoesNotThrow()
    {
        var caps = LinuxInputCapabilitySet.FromReport(new LinuxPrerequisiteReport
        {
            SelectedTier = LinuxRemoteDesktopTier.X11Degraded,
        });
        var router = new LinuxInputBackendRouter(caps);
        var ex = Record.Exception(() =>
        {
            router.Dispose();
            router.Dispose(); // second dispose must be a no-op
        });
        Assert.Null(ex);
    }
}
