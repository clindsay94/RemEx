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
    public void PrefersTheMonitorInterfacePathOverTheAdapterName()
    {
        // THE BUG. Before this fix the adapter output name was the answer unconditionally, which is
        // an enumeration artefact. If this ever returns the adapter name again while a real panel
        // identity is available, the persistent key has silently gone back to being session-scoped.
        var key = WindowsScreenCaptureService.ChoosePersistentDisplayKey(
            InterfacePath, @"\\.\DISPLAY1", ordinal: 1);

        Assert.Equal(InterfacePath, key);
        Assert.DoesNotContain(@"\\.\DISPLAY", key);
    }

    [Fact]
    public void TheKeyDoesNotDependOnWhichAdapterOutputTheMonitorIsEnumeratedOn()
    {
        // This is the persistence property in miniature: the same physical panel must produce the
        // same key regardless of where it lands in the enumeration. Adding a monitor renumbers the
        // adapter outputs, and that renumbering is exactly what used to change the stored key.
        var first = WindowsScreenCaptureService.ChoosePersistentDisplayKey(
            InterfacePath, @"\\.\DISPLAY1", ordinal: 1);
        var afterAMonitorWasAdded = WindowsScreenCaptureService.ChoosePersistentDisplayKey(
            InterfacePath, @"\\.\DISPLAY3", ordinal: 3);

        Assert.Equal(first, afterAMonitorWasAdded);
    }

    [Fact]
    public void TwoPanelsNeverShareAKey()
    {
        var left = WindowsScreenCaptureService.ChoosePersistentDisplayKey(
            InterfacePath, @"\\.\DISPLAY1", ordinal: 1);
        var right = WindowsScreenCaptureService.ChoosePersistentDisplayKey(
            @"\\?\DISPLAY#DEL41A8#5&9f8e7d6c&0&UID4354#{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}",
            @"\\.\DISPLAY2",
            ordinal: 2);

        Assert.NotEqual(left, right);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void FallsBackToTheAdapterNameWhenWindowsOffersNoPanelIdentity(string? noPath)
    {
        // Degraded but usable: this is the old behaviour, now reached only when the stable source is
        // genuinely unavailable rather than on every enumeration. A blank path must count as absent —
        // returning "" as a key would collide across every display at once.
        var key = WindowsScreenCaptureService.ChoosePersistentDisplayKey(noPath, @"\\.\DISPLAY2", ordinal: 2);

        Assert.Equal(@"\\.\DISPLAY2", key);
    }

    [Fact]
    public void FallsBackToAnOrdinalOnlyWhenThereIsNothingElseAtAll()
    {
        var key = WindowsScreenCaptureService.ChoosePersistentDisplayKey(null, null, ordinal: 3);

        Assert.Equal("monitor-3", key);
    }

    [Fact]
    public void NeverReturnsBlank()
    {
        // Every rung must yield something. A blank key would make every display look like the same
        // display to a client that maps by key.
        Assert.False(string.IsNullOrWhiteSpace(
            WindowsScreenCaptureService.ChoosePersistentDisplayKey("  ", "  ", ordinal: 1)));
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
        // any adapter whose first monitor slot is dormant — a clone-mode or docked setup — and the
        // key would silently drop to the unstable adapter-name rung with nothing to indicate why.
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
        // An active child can still report a blank path when the interface name is unavailable.
        // Returning "" would give every such display the same key.
        Assert.Equal(InterfacePath, Walk(Active(null), Active("   "), Active(InterfacePath)));
    }

    [Fact]
    public void TrimsWhatWindowsReturns()
    {
        // The interface path arrives from a fixed-size marshalled buffer, so trailing whitespace is a
        // real possibility and an untrimmed key would fail to match a trimmed one stored earlier.
        var key = WindowsScreenCaptureService.ChoosePersistentDisplayKey(
            "  " + InterfacePath + "  ", @"\\.\DISPLAY1", ordinal: 1);

        Assert.Equal(InterfacePath, key);
    }
}
