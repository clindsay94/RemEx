using System.Runtime.Versioning;
using Remex.Agent.Services.ScreenCapture;
using Xunit;

namespace Remex.Agent.Tests;

/// <summary>
/// Covers the Windows source of <c>DesktopDisplayInfo.PersistentDisplayKey</c> (RemEx-zftu).
/// </summary>
/// <remarks>
/// <para>
/// The model documents two identifiers with deliberately different contracts: <c>DisplayId</c> is
/// session-scoped and renumbers when monitors change, and <c>PersistentDisplayKey</c> is the one a
/// client may store to reselect "the monitor I was watching last time". Windows assigned them the
/// SAME string — <c>\\.\DISPLAY1</c>, or a <c>monitor-N</c> ordinal — so the persistent key was
/// exactly as session-scoped as the id it exists to outlive.
/// </para>
/// <para>
/// The failure that made it worth fixing is that it is SILENT. A stored key still resolves after a
/// replug; it just resolves to a different physical screen, and the user gets someone else's monitor
/// with no error anywhere. Nothing about that is visible in a log.
/// </para>
/// <para>
/// The ladder is a pure function precisely so it can be pinned here. The Win32 half —
/// <c>EnumDisplayDevices</c> with <c>EDD_GET_DEVICE_INTERFACE_NAME</c> — needs a real monitor and is
/// not testable in CI; what IS testable, and what actually regressed, is which source gets chosen.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public class PersistentDisplayKeyTests
{
    /// <summary>A real monitor device interface path, as Windows returns it.</summary>
    private const string InterfacePath =
        @"\\?\DISPLAY#GSM5B09#5&1a2b3c4d&0&UID4353#{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}";

    [Fact]
    public void ReturnsTheMonitorInterfacePath()
    {
        Assert.Equal(InterfacePath, WindowsScreenCaptureService.ChoosePersistentDisplayKey(InterfacePath));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ReturnsEmptyWhenWindowsOffersNoPanelIdentity(string? noPath)
    {
        // THE POINT OF RemEx-i50k, and it replaced a fallback ladder that looked helpful. This used
        // to degrade to the adapter output name — which is exactly what DisplayId already is, so the
        // "persistent" key came back BYTE-IDENTICAL to the session-scoped id it exists to outlive.
        // Nothing on the wire tells a client how much to trust the value, so a degraded key looked
        // like a good one, got stored as though it were stable, and resurrected the wrong-monitor bug
        // the key was added to fix.
        //
        // Empty is the honest answer and the more useful one: the client treats a missing key as "do
        // not remember this display", turning a silent wrong answer into a visible lost preference.
        Assert.Equal(string.Empty, WindowsScreenCaptureService.ChoosePersistentDisplayKey(noPath));
    }

    [Fact]
    public void NeverReturnsAnythingResemblingADisplayId()
    {
        // A regression guard with teeth: if anyone reinstates a fallback, the value it would most
        // plausibly reach for is the adapter output name. That must never come back from here.
        foreach (var noPath in new string?[] { null, "", "   " })
        {
            var key = WindowsScreenCaptureService.ChoosePersistentDisplayKey(noPath);

            Assert.DoesNotContain(@"\.\DISPLAY", key);
            Assert.DoesNotContain("monitor-", key);
        }
    }

    [Fact]
    public void TwoPanelsNeverShareAKey()
    {
        var left = WindowsScreenCaptureService.ChoosePersistentDisplayKey(InterfacePath);
        var right = WindowsScreenCaptureService.ChoosePersistentDisplayKey(
            @"\?\DISPLAY#DEL41A8#5&9f8e7d6c&0&UID4354#{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}");

        Assert.NotEqual(left, right);
    }

    [Fact]
    public void TrimsWhatWindowsReturns()
    {
        // The path arrives from a fixed-size marshalled buffer, so trailing whitespace is real and an
        // untrimmed key would fail to match a trimmed one stored earlier.
        Assert.Equal(
            InterfacePath,
            WindowsScreenCaptureService.ChoosePersistentDisplayKey("  " + InterfacePath + "  "));
    }

    // ── The enumeration walk ────────────────────────────────────────────────────────────────
    // Separated from the P/Invoke so it can be exercised without a monitor. The marshalling still
    // needs hardware and is not pretended otherwise; these pin the loop's own failure modes.

    private static WindowsScreenCaptureService.MonitorDeviceEntry Active(string? id) =>
        new(Enumerated: true, StateFlags: 1, DeviceId: id);

    private static WindowsScreenCaptureService.MonitorDeviceEntry Inactive(string? id) =>
        new(Enumerated: true, StateFlags: 0, DeviceId: id);

    private static readonly WindowsScreenCaptureService.MonitorDeviceEntry NoMoreMonitors =
        new(Enumerated: false, StateFlags: 0, DeviceId: null);

    private static string? Walk(params WindowsScreenCaptureService.MonitorDeviceEntry[] entries) =>
        WindowsScreenCaptureService.ResolveMonitorInterfacePath(
            i => i < entries.Length ? entries[(int)i] : NoMoreMonitors);

    [Fact]
    public void WalkTakesTheFirstActiveMonitor()
    {
        Assert.Equal(InterfacePath, Walk(Active(InterfacePath), Active("second-panel")));
    }

    [Fact]
    public void WalkSkipsInactiveEntriesRatherThanStoppingAtThem()
    {
        // The break/continue distinction. Stopping at the first inactive child would return null for
        // any adapter whose first monitor slot is dormant — a clone-mode or docked setup — and since
        // RemEx-i50k removed the fallback, the display would then report NO key at all and the user
        // would lose the ability to remember it. Visible rather than silently wrong, but still a
        // capability lost for no reason.
        Assert.Equal(InterfacePath, Walk(Inactive("dormant"), Active(InterfacePath)));
    }

    [Fact]
    public void WalkStopsAtTheFirstFailedEnumeration()
    {
        // EnumDisplayDevices indices are consecutive and the call fails once there are no more
        // monitors, so continuing past a false return would read an uninitialised struct. Anything
        // after NoMoreMonitors must be unreachable.
        Assert.Null(Walk(NoMoreMonitors, Active(InterfacePath)));
    }

    [Fact]
    public void WalkIgnoresActiveEntriesWithNoDeviceId()
    {
        // An active child can still report a blank path when the interface name is unavailable, and
        // the walk must keep looking rather than stop at it — otherwise a display that DOES have an
        // identity further down the list is reported as having none.
        Assert.Equal(InterfacePath, Walk(Active(null), Active("   "), Active(InterfacePath)));
    }
}
