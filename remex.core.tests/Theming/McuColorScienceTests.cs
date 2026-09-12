using Remex.Core.Theming.Mcu;

namespace Remex.Core.Tests.Theming;

/// <summary>RemEx-4kv0g.8: the colour-science core reproduces material 1.14.0's own unit-test oracles (REFTEST HctTest, ColorUtilsTest, MathUtilsTest).</summary>
public class McuColorScienceTests
{
    private const uint Red = 0xFFFF0000u, Green = 0xFF00FF00u, Blue = 0xFF0000FFu, White = 0xFFFFFFFFu, Black = 0xFF000000u, MidGray = 0xFF777777u;

    [Theory]
    [InlineData(1.5, 1)] [InlineData(0.0, 0)] [InlineData(-3.0, -1)]
    public void MathUtils_Signum(double input, int expected) => Assert.Equal(expected, MathUtils.Signum(input));

    [Theory]
    [InlineData(0, 0)] [InlineData(30, 30)] [InlineData(150, 150)] [InlineData(360, 0)] [InlineData(450, 90)]
    [InlineData(1000000, 280)] [InlineData(-10, 350)] [InlineData(-90, 270)] [InlineData(-1000000, 80)]
    public void MathUtils_SanitizeDegreesInt(int degrees, int expected) => Assert.Equal(expected, MathUtils.SanitizeDegreesInt(degrees));

    [Theory]
    [InlineData(0.0, 0xFF000000u)] [InlineData(0.25, 0xFF010101u)] [InlineData(0.5, 0xFF020202u)] [InlineData(0.75, 0xFF030303u)]
    [InlineData(1.0, 0xFF040404u)] [InlineData(1.5, 0xFF050505u)] [InlineData(2.0, 0xFF070707u)] [InlineData(3.0, 0xFF0B0B0Bu)]
    [InlineData(4.0, 0xFF0E0E0Eu)] [InlineData(5.0, 0xFF111111u)] [InlineData(6.0, 0xFF131313u)] [InlineData(7.0, 0xFF151515u)]
    [InlineData(8.0, 0xFF181818u)] [InlineData(9.0, 0xFF191919u)] [InlineData(10.0, 0xFF1B1B1Bu)] [InlineData(20.0, 0xFF303030u)]
    [InlineData(30.0, 0xFF474747u)] [InlineData(40.0, 0xFF5E5E5Eu)] [InlineData(50.0, 0xFF777777u)] [InlineData(60.0, 0xFF919191u)]
    [InlineData(70.0, 0xFFABABABu)] [InlineData(80.0, 0xFFC6C6C6u)] [InlineData(90.0, 0xFFE2E2E2u)]
    public void ColorUtils_ArgbFromLab_GreyAxis(double l, uint expected) => Assert.Equal(expected, ColorUtils.ArgbFromLab(l, 0.0, 0.0));

    public static TheoryData<uint, double, double, double, double, double, double> CamOracles() => new()
    {
        // argb, hue, chroma, j, m, s, q — HctTest.camFromArgb_*
        { Red,   27.408, 113.357, 46.445, 89.494, 91.889, 105.988 },
        { Green, 142.139, 108.410, 79.331, 85.587, 78.604, 138.520 },
        { Blue,  282.788, 87.230, 25.465, 68.867, 93.674, 78.481 },
        { White, 209.492, 2.869, 100.0, 2.265, 12.068, 155.521 },
        { Black, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0 },
    };

    [Theory, MemberData(nameof(CamOracles))]
    public void Cam16_FromInt_MatchesTheReference(uint argb, double hue, double chroma, double j, double m, double s, double q)
    {
        var cam = Cam16.FromInt(argb);
        Assert.Equal(hue, cam.Hue, 0.001);
        Assert.Equal(chroma, cam.Chroma, 0.001);
        Assert.Equal(j, cam.J, 0.001);
        Assert.Equal(m, cam.M, 0.001);
        Assert.Equal(s, cam.S, 0.001);
        Assert.Equal(q, cam.Q, 0.001);
    }

    [Theory] [InlineData(Red)] [InlineData(Green)] [InlineData(Blue)]
    public void Cam16_RoundTripsThePrimaries(uint argb) => Assert.Equal(argb, Cam16.FromInt(argb).ToInt());

    [Fact]
    public void ViewingConditions_Default()
    {
        var vc = ViewingConditions.Default;
        Assert.Equal(0.184, vc.N, 0.001);
        Assert.Equal(29.981, vc.Aw, 0.001);
        Assert.Equal(1.016, vc.Nbb, 0.001);
        Assert.Equal(1.021, vc.RgbD[0], 0.001);
        Assert.Equal(0.986, vc.RgbD[1], 0.001);
        Assert.Equal(0.933, vc.RgbD[2], 0.001);
        Assert.Equal(0.789, vc.FlRoot, 0.001);
    }

    [Theory]
    // HctTest.relativity_*: colour, background L*, expected — the whole reason Hct has viewing conditions
    [InlineData(Red, 0.0, 0xFF9F5C51u)] [InlineData(Red, 100.0, 0xFFFF5D48u)]
    [InlineData(Green, 0.0, 0xFFACD69Du)] [InlineData(Green, 100.0, 0xFF8EFF77u)]
    [InlineData(Blue, 0.0, 0xFF343654u)] [InlineData(Blue, 100.0, 0xFF3F49FFu)]
    [InlineData(White, 0.0, 0xFFFFFFFFu)] [InlineData(White, 100.0, 0xFFFFFFFFu)]
    [InlineData(MidGray, 0.0, 0xFF605F5Fu)] [InlineData(MidGray, 100.0, 0xFF8E8E8Eu)]
    [InlineData(Black, 0.0, 0xFF000000u)] [InlineData(Black, 100.0, 0xFF000000u)]
    public void Hct_InViewingConditions_Relativity(uint argb, double backgroundLstar, uint expected)
        => Assert.Equal(expected, Hct.FromInt(argb).InViewingConditions(ViewingConditions.DefaultWithBackgroundLstar(backgroundLstar)).ToInt());

    [Fact]
    public void Hct_RoundTripsEveryStridedSrgbColour()
    {
        // HctRoundTripTest walks all 16.7M colours; a stride of 997 (prime, so every channel value is visited) keeps this under a second.
        for (uint rgb = 0; rgb <= 0xFFFFFFu; rgb += 997)
        {
            uint argb = 0xFF000000u | rgb;
            var hct = Hct.FromInt(argb);
            Assert.Equal(argb, Hct.From(hct.Hue, hct.Chroma, hct.Tone).ToInt());
        }
    }

    [Fact]
    public void Hct_From_LandsOnTheRequestedToneAndNeverOvershootsChroma()
    {
        // HctTest.correctness, reduced: tone is exact to 0.5, chroma never exceeds the request by more than 2.5, hue is kept when there is chroma to carry it.
        for (int hue = 15; hue < 360; hue += 30)
        for (int chroma = 0; chroma <= 100; chroma += 10)
        for (int tone = 20; tone <= 80; tone += 10)
        {
            var hct = Hct.From(hue, chroma, tone);
            Assert.True(Math.Abs(hct.Tone - tone) <= 0.5, $"h{hue} c{chroma} t{tone}: tone {hct.Tone}");
            Assert.True(hct.Chroma <= chroma + 2.5, $"h{hue} c{chroma} t{tone}: chroma {hct.Chroma}");
            if (hct.Chroma > 5.0)
                Assert.True(MathUtils.DifferenceDegrees(hue, hct.Hue) <= 4.0, $"h{hue} c{chroma} t{tone}: hue {hct.Hue}");
        }
    }
}
