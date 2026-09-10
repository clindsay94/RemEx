using System.Runtime.Versioning;
using Remex.Agent.Services.ScreenCapture;

namespace Remex.Agent.Tests;

[SupportedOSPlatform("linux")]
public class LinuxScreenCaptureServiceTests
{
    [Fact]
    public void TryGetVirtualDesktopBounds_UsesScreenCurrentDimensionsAndNegativeOffsets()
    {
        var lines = new[]
        {
            "Screen 0: minimum 8 x 8, current 3200 x 1080, maximum 32767 x 32767",
            "HDMI-0 connected primary 1920x1080+0+0",
            "DP-1 connected 1280x1024-1280+56"
        };

        var (parsed, width, height, left, top) = InvokeTryGetVirtualDesktopBounds(lines);

        Assert.True(parsed);
        Assert.Equal(3200, width);
        Assert.Equal(1080, height);
        Assert.Equal(-1280, left);
        Assert.Equal(0, top);
    }

    [Fact]
    public void TryGetVirtualDesktopBounds_FallsBackToConnectedOutputBounds()
    {
        var lines = new[]
        {
            "DP-1 connected primary 2560x1440+0+0",
            "HDMI-0 connected 1920x1080+2560+180"
        };

        var (parsed, width, height, left, top) = InvokeTryGetVirtualDesktopBounds(lines);

        Assert.True(parsed);
        Assert.Equal(4480, width);
        Assert.Equal(1440, height);
        Assert.Equal(0, left);
        Assert.Equal(0, top);
    }

    [Fact]
    public void ParseXrandrDisplays_ReturnsPerMonitorDescriptors()
    {
        var lines = new[]
        {
            "Screen 0: minimum 8 x 8, current 3200 x 1080, maximum 32767 x 32767",
            "HDMI-0 connected primary 1920x1080+0+0",
            "DP-1 connected 1280x1024-1280+56"
        };

        var displays = LinuxScreenCaptureService.ParseXrandrDisplays(lines);

        Assert.Equal(2, displays.Count);
        Assert.Equal("HDMI-0", displays[0].DisplayId);
        Assert.True(displays[0].IsPrimary);
        Assert.Equal(1920, displays[0].Width);
        Assert.Equal(0, displays[0].Left);
        Assert.Equal("DP-1", displays[1].DisplayId);
        Assert.False(displays[1].IsPrimary);
        Assert.Equal(-1280, displays[1].Left);
        Assert.Equal(56, displays[1].Top);
    }

    [Fact]
    public void ParseXrandrDisplays_KeepsTheConnectorNameAsThePersistentKey()
    {
        // PINS THE DECISION, not an implementation detail. RemEx-i50k removed the Windows fallback
        // that set this key to a value byte-identical to DisplayId, and the obvious parity move was to
        // mirror that here — where the key is ALSO equal to DisplayId. That would have been wrong.
        // A Windows adapter name is an ENUMERATION index: unplug another monitor and the survivor
        // renumbers, so a stored key resolves to a different screen. A DRM connector name is a PORT:
        // DP-1 stays DP-1 whatever else is plugged in. Deleting it would have cost Linux users the
        // remembered-monitor feature to prevent a failure that cannot happen here (RemEx-kiy1).
        var lines = new[]
        {
            "Screen 0: minimum 8 x 8, current 3200 x 1080, maximum 32767 x 32767",
            "DP-1 connected primary 1920x1080+0+0 (normal left inverted right x axis y axis) 527mm x 296mm",
            "HDMI-A-1 connected 1280x1024+1920+0 (normal left inverted right x axis y axis) 380mm x 300mm",
        };

        var displays = LinuxScreenCaptureService.ParseXrandrDisplays(lines);

        Assert.Equal(2, displays.Count);
        Assert.All(displays, d => Assert.False(string.IsNullOrWhiteSpace(d.PersistentDisplayKey)));
        Assert.Equal("DP-1", displays[0].PersistentDisplayKey);
        Assert.Equal("HDMI-A-1", displays[1].PersistentDisplayKey);
    }

    [Fact]
    public void CreateFallbackDisplay_ReportsNoPersistentKey()
    {
        // The other half of the decision. This branch is reached when the host could not enumerate its
        // outputs at all, so it has no stable identity and must not invent one — "default" looked like
        // an identity and was not. Empty is the documented way to say so, and costs nothing: there is
        // one display, and a client that remembers nothing selects the primary, which is that display.
        var display = LinuxScreenCaptureService.CreateFallbackDisplay(left: 0, top: 0, width: 1920, height: 1080);

        Assert.Equal(string.Empty, display.PersistentDisplayKey);

        // DisplayId must stay populated: selection uses it, and GetDisplayCatalog keys off this exact
        // sentinel to decide the host has no real displays to advertise.
        Assert.Equal("default", display.DisplayId);
        Assert.True(display.IsPrimary);
    }

    private static (bool Parsed, int Width, int Height, int Left, int Top) InvokeTryGetVirtualDesktopBounds(string[] lines)
    {
        var parsed = LinuxScreenCaptureService.TryGetVirtualDesktopBounds(lines, out var width, out var height, out var left, out var top);
        return (parsed, width, height, left, top);
    }
}
