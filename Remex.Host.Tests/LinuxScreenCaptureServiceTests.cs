using System.Runtime.Versioning;
using Remex.Host.Services.ScreenCapture;

namespace Remex.Host.Tests;

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

    private static (bool Parsed, int Width, int Height, int Left, int Top) InvokeTryGetVirtualDesktopBounds(string[] lines)
    {
        var parsed = LinuxScreenCaptureService.TryGetVirtualDesktopBounds(lines, out var width, out var height, out var left, out var top);
        return (parsed, width, height, left, top);
    }
}
