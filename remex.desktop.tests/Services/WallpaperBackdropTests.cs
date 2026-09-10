using System;
using System.IO;
using FluentAssertions;
using Remex.Core.Models;
using Remex.Desktop.Services;
using Xunit;

namespace Remex.Desktop.Tests.Services;

public class WallpaperBackdropTests
{
    [Theory]
    [InlineData(0.0, 0.0)]
    [InlineData(0.6, 28.8)]
    [InlineData(1.0, 48.0)]
    [InlineData(-0.5, 0.0)]
    [InlineData(3.0, 48.0)]
    [InlineData(double.NaN, 0.0)]
    public void BlurMapsZeroToOneOntoZeroToFortyEightPixelsAndClamps(double blur, double expected)
    {
        WallpaperBackdrop.BlurRadiusFor(blur).Should().BeApproximately(expected, 0.001);
    }

    [Fact]
    public void DesktopSourceAsksTheDesktopForItsPath()
    {
        var settings = new CustomizationSettings { WallpaperSource = WallpaperSources.Desktop, WallpaperImagePath = @"C:\ignored.png" };

        WallpaperBackdrop.ResolvePath(settings, () => @"C:\wall.jpg").Should().Be(@"C:\wall.jpg");
        WallpaperBackdrop.ResolvePath(settings, () => null).Should().BeNull("no wallpaper set is 'no answer', never a throw");
    }

    [Fact]
    public void ImageSourceUsesTheAppOwnedCopyOnlyWhileItExists()
    {
        var existing = Path.Combine(Path.GetTempPath(), $"remex-wp-{Guid.NewGuid():N}.png");
        File.WriteAllBytes(existing, new byte[] { 1, 2, 3 });
        try
        {
            var settings = new CustomizationSettings { WallpaperSource = WallpaperSources.Image, WallpaperImagePath = existing };
            WallpaperBackdrop.ResolvePath(settings, () => @"C:\wall.jpg").Should().Be(existing);

            var gone = settings with { WallpaperImagePath = existing + ".missing" };
            WallpaperBackdrop.ResolvePath(gone, () => @"C:\wall.jpg").Should().BeNull(
                "a missing copy falls back to Solid for the session (spec section 6), not to the desktop wallpaper");
            WallpaperBackdrop.ResolvePath(settings with { WallpaperImagePath = null }, () => @"C:\wall.jpg").Should().BeNull();
        }
        finally
        {
            File.Delete(existing);
        }
    }
}
