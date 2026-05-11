using System.Reflection;
using Remex.Host.Services.ScreenCapture;

namespace Remex.Host.Tests;

public class LinuxScreenCaptureServiceTests
{
    private static readonly MethodInfo TryGetVirtualDesktopBoundsMethod =
        typeof(LinuxScreenCaptureService).GetMethod(
            "TryGetVirtualDesktopBounds",
            BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("TryGetVirtualDesktopBounds was not found.");

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

    private static (bool Parsed, int Width, int Height, int Left, int Top) InvokeTryGetVirtualDesktopBounds(string[] lines)
    {
        object[] parameters = [lines, 0, 0, 0, 0];
        var parsed = (bool)(TryGetVirtualDesktopBoundsMethod.Invoke(null, parameters) ?? false);
        return (parsed, (int)parameters[1], (int)parameters[2], (int)parameters[3], (int)parameters[4]);
    }
}
