using System;
using System.Drawing.Imaging;
using Remex.Agent.Services.ScreenCapture;
using Xunit;

namespace Remex.Agent.Tests;

/// <summary>
/// The GDI capture tier's scratch bitmaps (perf audit P4-11): one allocation per size, not per frame.
/// </summary>
public class ReusableBitmapTests
{
    [Fact]
    public void SameSize_ReturnsTheSameBitmap_WithoutAllocating()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var slot = new ReusableBitmap();
        var first = slot.Get(64, 32);
        var second = slot.Get(64, 32);

        Assert.Same(first, second);
        Assert.Equal(1, slot.AllocationCount);
        Assert.Equal(PixelFormat.Format32bppArgb, first.PixelFormat);
    }

    [Fact]
    public void NewSize_ReplacesAndDisposesTheOldBitmap()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var slot = new ReusableBitmap();
        var small = slot.Get(64, 32);
        var large = slot.Get(128, 64);

        Assert.NotSame(small, large);
        Assert.Equal(2, slot.AllocationCount);
        Assert.Equal(128, large.Width);
        Assert.Equal(64, large.Height);
        // A disposed GDI+ image throws on property access.
        Assert.ThrowsAny<ArgumentException>(() => _ = small.Width);
    }
}
