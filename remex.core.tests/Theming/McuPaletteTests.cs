using Remex.Core.Theming.Mcu;

namespace Remex.Core.Tests.Theming;

/// <summary>RemEx-4kv0g.9: palettes, blending, contrast, temperature and dislike reproduce material 1.14.0's own oracles.</summary>
public class McuPaletteTests
{
    private const uint Red = 0xFFFF0000u, Green = 0xFF00FF00u, Blue = 0xFF0000FFu, Yellow = 0xFFFFFF00u, White = 0xFFFFFFFFu, Black = 0xFF000000u;

    [Theory]
    // PalettesTest.tones_ofBlue: TonalPalette.fromHueAndChroma(cam.hue, cam.chroma) of pure blue
    [InlineData(100, 0xFFFFFFFFu)] [InlineData(95, 0xFFF1EFFFu)] [InlineData(90, 0xFFE0E0FFu)] [InlineData(80, 0xFFBEC2FFu)]
    [InlineData(70, 0xFF9DA3FFu)] [InlineData(60, 0xFF7C84FFu)] [InlineData(50, 0xFF5A64FFu)] [InlineData(40, 0xFF343DFFu)]
    [InlineData(30, 0xFF0000EFu)] [InlineData(20, 0xFF0001ACu)] [InlineData(10, 0xFF00006Eu)] [InlineData(0, 0xFF000000u)]
    public void TonalPalette_TonesOfBlue(int tone, uint expected)
    {
        var cam = Cam16.FromInt(Blue);
        var tones = TonalPalette.FromHueAndChroma(cam.Hue, cam.Chroma);
        Assert.Equal(expected, tones.Tone(tone));
    }

    [Theory]
    // BlendTest.harmonize_*
    [InlineData(Red, Blue, 0xFFFB0057u)] [InlineData(Red, Green, 0xFFD85600u)] [InlineData(Red, Yellow, 0xFFD85600u)]
    [InlineData(Blue, Green, 0xFF0047A3u)] [InlineData(Blue, Red, 0xFF5700DCu)] [InlineData(Blue, Yellow, 0xFF0047A3u)]
    [InlineData(Green, Blue, 0xFF00FC94u)] [InlineData(Green, Red, 0xFFB1F000u)]
    public void Blend_Harmonize(uint design, uint source, uint expected) => Assert.Equal(expected, Blend.Harmonize(design, source));

    [Fact]
    public void Contrast_ImpossibleRatiosAndOutOfRangeTonesAnswerMinusOne()
    {
        Assert.Equal(-1.0, Contrast.Lighter(90.0, 10.0), 0.001);
        Assert.Equal(-1.0, Contrast.Lighter(110.0, 2.0), 0.001);
        Assert.Equal(-1.0, Contrast.Lighter(-10.0, 2.0), 0.001);
        Assert.Equal(100.0, Contrast.LighterUnsafe(100.0, 2.0), 0.001);
        Assert.Equal(-1.0, Contrast.Darker(10.0, 20.0), 0.001);
        Assert.Equal(-1.0, Contrast.Darker(110.0, 2.0), 0.001);
        Assert.Equal(-1.0, Contrast.Darker(-10.0, 2.0), 0.001);
        Assert.Equal(0.0, Contrast.DarkerUnsafe(0.0, 2.0), 0.001);
    }

    [Fact]
    public void Contrast_RatioOfTones_IsSymmetricAndSpansOneToTwentyOne()
    {
        Assert.Equal(21.0, Contrast.RatioOfTones(100.0, 0.0), 0.001);
        Assert.Equal(21.0, Contrast.RatioOfTones(0.0, 100.0), 0.001);
        Assert.Equal(1.0, Contrast.RatioOfTones(50.0, 50.0), 0.001);
        // Lighter() intentionally overshoots by up to LuminanceGamutMapTolerance (0.4 L*) so the
        // returned tone's real contrast ratio never falls short of the request; a wider tolerance
        // here reflects that by-design overshoot, not slack in the transcription.
        Assert.Equal(Contrast.Ratio45, Contrast.RatioOfTones(Contrast.Lighter(30.0, Contrast.Ratio45), 30.0), 0.1);
    }

    [Theory]
    // TemperatureCacheTest.testRawTemperature
    [InlineData(Blue, -1.393)] [InlineData(Red, 2.351)] [InlineData(Green, -0.267)] [InlineData(White, -0.5)] [InlineData(Black, -0.5)]
    public void TemperatureCache_RawTemperature(uint argb, double expected) => Assert.Equal(expected, TemperatureCache.RawTemperature(Hct.FromInt(argb)), 0.001);

    [Theory]
    // TemperatureCacheTest.testComplement — the tertiary palette of SchemeFidelity
    [InlineData(Blue, 0xFF9D0002u)] [InlineData(Red, 0xFF007BFCu)] [InlineData(Green, 0xFFFFD2C9u)] [InlineData(White, 0xFFFFFFFFu)] [InlineData(Black, 0xFF000000u)]
    public void TemperatureCache_Complement(uint argb, uint expected) => Assert.Equal(expected, new TemperatureCache(Hct.FromInt(argb)).GetComplement().ToInt());

    [Fact]
    public void TemperatureCache_AnalogousColorsHaveTheRequestedCountAndTheInputInTheMiddle()
    {
        // SchemeContent takes .get(2) of getAnalogousColors(3, 6): the count is 3 and index 1 is the input itself.
        var input = Hct.FromInt(Blue);
        var analogous = new TemperatureCache(input).GetAnalogousColors(3, 6);
        Assert.Equal(3, analogous.Count);
        Assert.Equal(input.ToInt(), analogous[1].ToInt());
        Assert.Equal(5, new TemperatureCache(input).GetAnalogousColors().Count);
    }

    public static TheoryData<uint> MonkSkinTones() => new()
    {
        0xFFF6EDE4u, 0xFFF3E7DBu, 0xFFF7EAD0u, 0xFFEADABAu, 0xFFD7BD96u, 0xFFA07E56u, 0xFF825C43u, 0xFF604134u, 0xFF3A312Au, 0xFF292420u,
    };

    [Theory, MemberData(nameof(MonkSkinTones))]
    public void Dislike_MonkSkinToneScaleIsLiked(uint argb) => Assert.False(DislikeAnalyzer.IsDisliked(Hct.FromInt(argb)));

    public static TheoryData<uint> BileColors() => new() { 0xFF95884Bu, 0xFF716B40u, 0xFFB08E00u, 0xFF4C4308u, 0xFF464521u };

    [Theory, MemberData(nameof(BileColors))]
    public void Dislike_BileColorsAreDislikedAndFixable(uint argb)
    {
        var hct = Hct.FromInt(argb);
        Assert.True(DislikeAnalyzer.IsDisliked(hct));
        Assert.False(DislikeAnalyzer.IsDisliked(DislikeAnalyzer.FixIfDisliked(hct)));
    }

    [Fact]
    public void Dislike_Tone67IsNotDislikedAndIsReturnedUnchanged()
    {
        var hct = Hct.From(100.0, 50.0, 67.0);
        Assert.False(DislikeAnalyzer.IsDisliked(hct));
        Assert.Equal(hct.ToInt(), DislikeAnalyzer.FixIfDisliked(hct).ToInt());
    }
}
