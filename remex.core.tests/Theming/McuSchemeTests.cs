using Remex.Core.Theming.Mcu;

namespace Remex.Core.Tests.Theming;

/// <summary>RemEx-4kv0g.10: the scheme layer against material 1.14.0's SchemeTonalSpotTest, the contrast curve's anchor points, and the wire vocabulary.</summary>
public class McuSchemeTests
{
    private static readonly MaterialDynamicColors Colors = new();
    private const uint Blue = 0xFF0000FFu;

    [Fact]
    public void TonalSpot_KeyColors_OfBlue()
    {
        var scheme = new SchemeTonalSpot(Hct.FromInt(Blue), false, 0.0);
        Assert.Equal(0xFF6E72ACu, Colors.PrimaryPaletteKeyColor().GetArgb(scheme));
        Assert.Equal(0xFF75758Bu, Colors.SecondaryPaletteKeyColor().GetArgb(scheme));
        Assert.Equal(0xFF936B84u, Colors.TertiaryPaletteKeyColor().GetArgb(scheme));
        Assert.Equal(0xFF77767Du, Colors.NeutralPaletteKeyColor().GetArgb(scheme));
        Assert.Equal(0xFF777680u, Colors.NeutralVariantPaletteKeyColor().GetArgb(scheme));
    }

    [Theory]
    // SchemeTonalSpotTest.lightTheme_{min,standard,max}Contrast_*
    [InlineData(-1.0, "primary", 0xFF6C70AAu)] [InlineData(0.0, "primary", 0xFF555992u)] [InlineData(1.0, "primary", 0xFF22265Cu)]
    [InlineData(-1.0, "primaryContainer", 0xFFD5D6FFu)] [InlineData(0.0, "primaryContainer", 0xFFE0E0FFu)] [InlineData(1.0, "primaryContainer", 0xFF40447Bu)]
    [InlineData(-1.0, "onPrimaryContainer", 0xFF7175B0u)] [InlineData(0.0, "onPrimaryContainer", 0xFF3E4278u)] [InlineData(1.0, "onPrimaryContainer", 0xFFFFFFFFu)]
    [InlineData(-1.0, "surface", 0xFFFBF8FFu)] [InlineData(0.0, "surface", 0xFFFBF8FFu)] [InlineData(1.0, "surface", 0xFFFBF8FFu)]
    [InlineData(-1.0, "onSurface", 0xFF5F5E65u)] [InlineData(0.0, "onSurface", 0xFF1B1B21u)] [InlineData(1.0, "onSurface", 0xFF000000u)]
    public void TonalSpot_LightTheme_OfBlue(double contrast, string role, uint expected)
        => Assert.Equal(expected, McuScheme.Build(Blue, SchemeVariant.TonalSpot, isDark: false, contrast)[role]);

    [Theory]
    [InlineData(-1.0, 3.0)] [InlineData(-0.5, 3.75)] [InlineData(0.0, 4.5)] [InlineData(0.25, 5.75)] [InlineData(0.5, 7.0)] [InlineData(1.0, 11.0)] [InlineData(2.0, 11.0)] [InlineData(-3.0, 3.0)]
    public void ContrastCurve_InterpolatesBetweenItsFourAnchors(double level, double expected)
        => Assert.Equal(expected, new ContrastCurve(3.0, 4.5, 7.0, 11.0).Get(level), 0.0001);

    [Fact]
    public void DynamicScheme_RotatedHue_UsesTheSingleRotationShortcutAndTheTable()
    {
        // Hct.From(0.0, 40.0, 50.0) is gamut-mapped, so its actual hue is not exactly 0.0 (chroma 40 at
        // tone 50 is not fully achievable at hue 0); the rotation is still exactly +60 from that hue.
        var zeroHueHct = Hct.From(0.0, 40.0, 50.0);
        Assert.Equal(MathUtils.SanitizeDegreesDouble(zeroHueHct.Hue + 60.0),
            DynamicScheme.GetRotatedHue(zeroHueHct, new double[] { 0, 360 }, new double[] { 60 }), 0.0001);
        var hues = new double[] { 0, 41, 61, 101, 131, 181, 251, 301, 360 };            // SchemeVibrant.HUES
        var rotations = new double[] { 18, 15, 10, 12, 15, 18, 15, 12, 12 };           // SchemeVibrant.SECONDARY_ROTATIONS
        var fiftyHueHct = Hct.From(50.0, 40.0, 50.0);
        Assert.Equal(MathUtils.SanitizeDegreesDouble(fiftyHueHct.Hue + 15.0),
            DynamicScheme.GetRotatedHue(fiftyHueHct, hues, rotations), 0.0001);
    }

    [Fact]
    public void Wire_RoundTripsAllNineAndFallsBackToTonalSpot()
    {
        var expected = new[] { "monochrome", "neutral", "tonal_spot", "vibrant", "expressive", "fidelity", "content", "rainbow", "fruit_salad" };
        Assert.Equal(expected, SchemeVariantWire.All.Select(v => v.ToWire()).ToArray());
        foreach (var v in SchemeVariantWire.All)
        {
            Assert.True(SchemeVariantWire.TryFromWire(v.ToWire(), out var back));
            Assert.Equal(v, back);
        }
        Assert.False(SchemeVariantWire.TryFromWire("spritz", out _));
        Assert.False(SchemeVariantWire.TryFromWire("TonalSpot", out _));   // exact wire form only; the desktop's PascalCase table is SchemeVariants (Task 7)
        Assert.Equal(SchemeVariant.TonalSpot, SchemeVariantWire.FromWireOrDefault("spritz"));
        Assert.Equal(SchemeVariant.TonalSpot, SchemeVariantWire.FromWireOrDefault(null));
    }

    [Fact]
    public void EveryVariantIsConstructibleAndErrorPaletteIsAlwaysTheFixedRed()
    {
        foreach (var v in SchemeVariantWire.All)
        {
            var s = McuScheme.Create(0xFF6750A4u, v, true, 0.0);
            Assert.Equal(v, s.Variant);
            Assert.Equal(25.0, s.ErrorPalette.Hue, 0.0001);
            Assert.Equal(84.0, s.ErrorPalette.Chroma, 0.0001);
        }
    }
}
