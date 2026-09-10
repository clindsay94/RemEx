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
    public void SplashPaletteDefault_AmberAccent_MatchesBrandHex()
    {
        // SplashBrand.Amber is mutable now (RemEx-alwfa.1: ApplyPalette can recolour it from a live
        // seed), so pinning it directly is fragile against test-run order. SplashPalette.Default is
        // immutable and is what a fresh SplashBrand paints with, so pin that instead.
        var expected = new SKColor(0xFF, 0xB6, 0x3D);
        uint expectedArgb = ((uint)expected.Alpha << 24) | ((uint)expected.Red << 16)
            | ((uint)expected.Green << 8) | expected.Blue;
        SplashPalette.Default.Accent.Should().Be(expectedArgb);
    }

    [Fact]
    public void FreshSplashBrand_DrawsWithDefaultPalette()
    {
        // Guards the one thing the mutable-Amber test above used to pin implicitly: before any
        // ApplyPalette call, SplashBrand's statics equal SplashPalette.Default's fields exactly.
        SplashBrand.Amber.Should().Be(new SKColor(SplashPalette.Default.Accent));
        SplashBrand.MarkStart.Should().Be(new SKColor(SplashPalette.Default.MarkStart));
        SplashBrand.MarkEnd.Should().Be(new SKColor(SplashPalette.Default.MarkEnd));
        SplashBrand.BackdropStart.Should().Be(new SKColor(SplashPalette.Default.BackdropStart));
        SplashBrand.BackdropEnd.Should().Be(new SKColor(SplashPalette.Default.BackdropEnd));
    }
}
