using FluentAssertions;
using Remex.Branding;
using SkiaSharp;
using Xunit;

namespace Remex.Desktop.Tests.Branding;

public class BrandDataTests
{
    [Fact]
    public void AllBrandPaths_ParseToNonEmptyGeometry()
    {
        var paths = new[]
        {
            SplashBrand.Window, SplashBrand.Dot1, SplashBrand.Dot2, SplashBrand.Dot3,
            SplashBrand.Chevron, SplashBrand.RStem, SplashBrand.RBowl, SplashBrand.RLeg,
            SplashBrand.RHole, SplashBrand.Cursor,
        };
        paths.Should().OnlyContain(p => !p.IsEmpty);
    }

    [Fact]
    public void MarkGeometry_StaysWithinViewport()
    {
        // Every path lives inside the 108-unit box (with a small AA tolerance).
        foreach (var p in new[] { SplashBrand.Window, SplashBrand.Chevron, SplashBrand.Cursor, SplashBrand.RLeg })
        {
            p.Bounds.Left.Should().BeGreaterThanOrEqualTo(-1f);
            p.Bounds.Top.Should().BeGreaterThanOrEqualTo(-1f);
            p.Bounds.Right.Should().BeLessThanOrEqualTo(RemexBrandData.Viewport + 1f);
            p.Bounds.Bottom.Should().BeLessThanOrEqualTo(RemexBrandData.Viewport + 1f);
        }
    }

    [Fact]
    public void Amber_MatchesBrandHex()
    {
        SplashBrand.Amber.Should().Be(new SKColor(0xFF, 0xB6, 0x3D));
    }
}
