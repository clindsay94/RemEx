using System.Runtime.Versioning;
using Remex.Host.Services.RemoteDesktop.Linux;
using Xunit;

namespace Remex.Host.Tests;

/// <summary>
/// Unit tests for the Linux prerequisite tier determination logic.
/// These tests exercise <see cref="LinuxRemoteDesktopTier"/> decisions using
/// pre-built <see cref="LinuxPrerequisiteReport"/> values — no subprocess calls.
/// </summary>
[SupportedOSPlatform("linux")]
public class LinuxRemoteDesktopPrerequisitesTests
{
    // ── Tier: Unsupported ──────────────────────────────────────────────

    [Fact]
    public void Tier_IsUnsupported_WhenNoDisplayServer()
    {
        var report = new LinuxPrerequisiteReport
        {
            IsWaylandSession = false,
            IsX11Session = false,
            SessionBusAvailable = true,
        };
        Assert.Equal(LinuxRemoteDesktopTier.Unsupported, DetermineTier(report));
    }

    [Fact]
    public void Tier_IsUnsupported_WhenNoSessionBus()
    {
        var report = new LinuxPrerequisiteReport
        {
            IsWaylandSession = true,
            SessionBusAvailable = false,
        };
        Assert.Equal(LinuxRemoteDesktopTier.Unsupported, DetermineTier(report));
    }

    // ── Tier: X11Degraded ─────────────────────────────────────────────

    [Fact]
    public void Tier_IsX11Degraded_WhenX11OnlySession()
    {
        var report = new LinuxPrerequisiteReport
        {
            IsWaylandSession = false,
            IsX11Session = true,
            SessionBusAvailable = true,
            PortalRemoteDesktopAvailable = false,
        };
        Assert.Equal(LinuxRemoteDesktopTier.X11Degraded, DetermineTier(report));
    }

    [Fact]
    public void Tier_IsX11Degraded_WhenWaylandButNoPortal()
    {
        var report = new LinuxPrerequisiteReport
        {
            IsWaylandSession = true,
            IsX11Session = true, // XWayland fallback available
            SessionBusAvailable = true,
            PortalRemoteDesktopAvailable = false,
            PortalScreenCastAvailable = false,
        };
        Assert.Equal(LinuxRemoteDesktopTier.X11Degraded, DetermineTier(report));
    }

    // ── Tier: PortalNoPen ─────────────────────────────────────────────

    [Fact]
    public void Tier_IsPortalNoPen_WhenPortalAndPipeWireButNoLibei()
    {
        var report = new LinuxPrerequisiteReport
        {
            IsWaylandSession = true,
            SessionBusAvailable = true,
            PortalRemoteDesktopAvailable = true,
            PortalScreenCastAvailable = true,
            PipeWireRunning = true,
            PipeWireLibraryAvailable = true,
            LibeiAvailable = false,
            UinputNodeExists = false,
            UinputWritable = false,
        };
        Assert.Equal(LinuxRemoteDesktopTier.PortalNoPen, DetermineTier(report));
    }

    // ── Tier: WaylandNative ───────────────────────────────────────────

    [Fact]
    public void Tier_IsWaylandNative_WhenAllDependenciesPresent()
    {
        var report = new LinuxPrerequisiteReport
        {
            IsWaylandSession = true,
            SessionBusAvailable = true,
            PortalRemoteDesktopAvailable = true,
            PortalScreenCastAvailable = true,
            PipeWireRunning = true,
            PipeWireLibraryAvailable = true,
            LibeiAvailable = true,
            UinputNodeExists = true,
            UinputWritable = true,
        };
        Assert.Equal(LinuxRemoteDesktopTier.WaylandNative, DetermineTier(report));
    }

    [Fact]
    public void Tier_IsWaylandNative_WhenLibeiAvailableEvenWithoutUinput()
    {
        var report = new LinuxPrerequisiteReport
        {
            IsWaylandSession = true,
            SessionBusAvailable = true,
            PortalRemoteDesktopAvailable = true,
            PortalScreenCastAvailable = true,
            PipeWireRunning = true,
            PipeWireLibraryAvailable = true,
            LibeiAvailable = true,
            UinputNodeExists = false,
            UinputWritable = false,
        };
        Assert.Equal(LinuxRemoteDesktopTier.WaylandNative, DetermineTier(report));
    }

    // ── CanStream / HasPortalCapture ──────────────────────────────────

    [Fact]
    public void CanStream_IsFalse_WhenUnsupported()
    {
        var report = new LinuxPrerequisiteReport
        {
            SelectedTier = LinuxRemoteDesktopTier.Unsupported,
        };
        Assert.False(report.CanStream);
    }

    [Fact]
    public void CanStream_IsTrue_WhenX11Degraded()
    {
        var report = new LinuxPrerequisiteReport
        {
            SelectedTier = LinuxRemoteDesktopTier.X11Degraded,
        };
        Assert.True(report.CanStream);
    }

    [Fact]
    public void HasPortalCapture_IsTrue_WhenPortalNoPen()
    {
        var report = new LinuxPrerequisiteReport
        {
            SelectedTier = LinuxRemoteDesktopTier.PortalNoPen,
        };
        Assert.True(report.HasPortalCapture);
    }

    // ── Helper ────────────────────────────────────────────────────────

    /// <summary>
    /// Applies the same tier determination logic as <see cref="LinuxRemoteDesktopPrerequisites.DetermineTier"/>
    /// but without spawning subprocesses — used purely to validate the rules.
    /// </summary>
    private static LinuxRemoteDesktopTier DetermineTier(LinuxPrerequisiteReport r)
    {
        if (!r.IsWaylandSession && !r.IsX11Session) return LinuxRemoteDesktopTier.Unsupported;
        if (!r.SessionBusAvailable) return LinuxRemoteDesktopTier.Unsupported;

        bool hasPortal = r.PortalRemoteDesktopAvailable && r.PortalScreenCastAvailable;

        if (!r.IsWaylandSession)
            return LinuxRemoteDesktopTier.X11Degraded;

        if (!hasPortal || !r.PipeWireRunning || !r.PipeWireLibraryAvailable)
            return r.IsX11Session ? LinuxRemoteDesktopTier.X11Degraded : LinuxRemoteDesktopTier.Unsupported;

        bool hasNativeInput = r.LibeiAvailable || (r.UinputNodeExists && r.UinputWritable);

        return hasNativeInput
            ? LinuxRemoteDesktopTier.WaylandNative
            : LinuxRemoteDesktopTier.PortalNoPen;
    }
}
