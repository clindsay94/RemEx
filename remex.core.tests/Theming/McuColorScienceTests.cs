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
    // ColorUtilsTest.argbFromLab, verbatim
    [InlineData(0.0, 0xFF000000u)] [InlineData(0.25, 0xFF010101u)] [InlineData(0.5, 0xFF020202u)] [InlineData(0.75, 0xFF030303u)]
    [InlineData(1.0, 0xFF040404u)] [InlineData(1.5, 0xFF050505u)] [InlineData(2.0, 0xFF070707u)] [InlineData(3.0, 0xFF0B0B0Bu)]
    [InlineData(4.0, 0xFF0E0E0Eu)] [InlineData(5.0, 0xFF111111u)] [InlineData(6.0, 0xFF131313u)] [InlineData(7.0, 0xFF151515u)]
    [InlineData(8.0, 0xFF181818u)] [InlineData(9.0, 0xFF191919u)] [InlineData(10.0, 0xFF1B1B1Bu)] [InlineData(20.0, 0xFF303030u)]
    [InlineData(30.0, 0xFF474747u)] [InlineData(40.0, 0xFF5E5E5Eu)] [InlineData(50.0, 0xFF777777u)] [InlineData(60.0, 0xFF919191u)]
    [InlineData(70.0, 0xFFABABABu)] [InlineData(80.0, 0xFFC6C6C6u)] [InlineData(90.0, 0xFFE2E2E2u)] [InlineData(100.0, 0xFFFFFFFFu)]
    public void ColorUtils_ArgbFromLab_GreyAxis(double l, uint expected) => Assert.Equal(expected, ColorUtils.ArgbFromLab(l, 0.0, 0.0));

    [Theory]
    // ColorUtilsTest.labFromArgb, verbatim — L* channel only ([0])
    [InlineData(0xFF000000u, 0.0)] [InlineData(0xFF010101u, 0.2741748000656514)] [InlineData(0xFF020202u, 0.5483496001313029)]
    [InlineData(0xFF030303u, 0.8225244001969543)] [InlineData(0xFF040404u, 1.0966992002626057)] [InlineData(0xFF050505u, 1.3708740003282571)]
    [InlineData(0xFF060606u, 1.645048800393912)] [InlineData(0xFF070707u, 1.9192236004595635)] [InlineData(0xFF080808u, 2.193398400525215)]
    [InlineData(0xFF0C0C0Cu, 3.3209754491182544)] [InlineData(0xFF101010u, 4.680444846419661)] [InlineData(0xFF181818u, 8.248186036170349)]
    [InlineData(0xFF202020u, 12.250030101522828)] [InlineData(0xFF404040u, 27.093413739449055)] [InlineData(0xFF808080u, 53.585013452169036)]
    [InlineData(0xFFFFFFFFu, 100.0)]
    public void ColorUtils_LabFromArgb_LChannel(uint argb, double expectedL) => Assert.Equal(expectedL, ColorUtils.LabFromArgb(argb)[0], 0.001);

    [Theory]
    // ColorUtilsTest.argbFromLstar, verbatim
    [InlineData(0.0, 0xFF000000u)] [InlineData(0.25, 0xFF010101u)] [InlineData(0.5, 0xFF020202u)] [InlineData(0.75, 0xFF030303u)]
    [InlineData(1.0, 0xFF040404u)] [InlineData(1.5, 0xFF050505u)] [InlineData(2.0, 0xFF070707u)] [InlineData(3.0, 0xFF0B0B0Bu)]
    [InlineData(4.0, 0xFF0E0E0Eu)] [InlineData(5.0, 0xFF111111u)] [InlineData(6.0, 0xFF131313u)] [InlineData(7.0, 0xFF151515u)]
    [InlineData(8.0, 0xFF181818u)] [InlineData(9.0, 0xFF191919u)] [InlineData(10.0, 0xFF1B1B1Bu)] [InlineData(20.0, 0xFF303030u)]
    [InlineData(30.0, 0xFF474747u)] [InlineData(40.0, 0xFF5E5E5Eu)] [InlineData(50.0, 0xFF777777u)] [InlineData(60.0, 0xFF919191u)]
    [InlineData(70.0, 0xFFABABABu)] [InlineData(80.0, 0xFFC6C6C6u)] [InlineData(90.0, 0xFFE2E2E2u)] [InlineData(100.0, 0xFFFFFFFFu)]
    public void ColorUtils_ArgbFromLstar(double lstar, uint expected) => Assert.Equal(expected, ColorUtils.ArgbFromLstar(lstar));

    [Theory]
    // MathUtilsTest.sanitizeDegreesDouble, verbatim (13 cases)
    [InlineData(0.0, 0.0)] [InlineData(30.0, 30.0)] [InlineData(150.0, 150.0)] [InlineData(360.0, 0.0)] [InlineData(450.0, 90.0)]
    [InlineData(1000000.0, 280.0)] [InlineData(-10.0, 350.0)] [InlineData(-90.0, 270.0)] [InlineData(-1000000.0, 80.0)]
    [InlineData(100.375, 100.375)] [InlineData(123456.789, 336.789)] [InlineData(-200.625, 159.375)] [InlineData(-123456.789, 23.211)]
    public void MathUtils_SanitizeDegreesDouble(double degrees, double expected) => Assert.Equal(expected, MathUtils.SanitizeDegreesDouble(degrees), 0.001);

    [Fact]
    public void MathUtils_RotationDirection_MatchesTheOriginalThreeWayImplementation()
    {
        // MathUtilsTest.rotationDirection, verbatim (576 cases): compares the closed-form rotationDirection
        // against the original three-candidate (a, a+360, a-360) implementation it replaced.
        for (double from = 0.0; from < 360.0; from += 15.0)
        {
            for (double to = 7.5; to < 360.0; to += 15.0)
            {
                double expected = OriginalRotationDirection(from, to);
                double actual = MathUtils.RotationDirection(from, to);
                Assert.Equal(expected, actual);
                Assert.Equal(1.0, Math.Abs(actual));
            }
        }

        static double OriginalRotationDirection(double from, double to)
        {
            double a = to - from;
            double b = to - from + 360.0;
            double c = to - from - 360.0;
            double aAbs = Math.Abs(a);
            double bAbs = Math.Abs(b);
            double cAbs = Math.Abs(c);
            if (aAbs <= bAbs && aAbs <= cAbs)
            {
                return a >= 0.0 ? 1.0 : -1.0;
            }
            else if (bAbs <= aAbs && bAbs <= cAbs)
            {
                return b >= 0.0 ? 1.0 : -1.0;
            }
            else
            {
                return c >= 0.0 ? 1.0 : -1.0;
            }
        }
    }

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
    [Trait("Category", "Slow")]
    public void Hct_RoundTripsEverySrgbColour_FullSweep()
    {
        // HctRoundTripTest, unreduced: all 16,777,216 opaque sRGB colours. ~17s.
        for (uint rgb = 0; rgb <= 0xFFFFFFu; rgb++)
        {
            uint argb = 0xFF000000u | rgb;
            var hct = Hct.FromInt(argb);
            Assert.Equal(argb, Hct.From(hct.Hue, hct.Chroma, hct.Tone).ToInt());
        }
    }

    private static bool ColorIsOnBoundary(uint argb) =>
        ColorUtils.RedFromArgb(argb) == 0
        || ColorUtils.RedFromArgb(argb) == 255
        || ColorUtils.GreenFromArgb(argb) == 0
        || ColorUtils.GreenFromArgb(argb) == 255
        || ColorUtils.BlueFromArgb(argb) == 0
        || ColorUtils.BlueFromArgb(argb) == 255;

    [Fact]
    public void Hct_From_Correctness()
    {
        // HctTest.correctness, verbatim (including colorIsOnBoundary at HctTest.java:154-201).
        for (int hue = 15; hue < 360; hue += 30)
        {
            for (int chroma = 0; chroma <= 100; chroma += 10)
            {
                for (int tone = 20; tone <= 80; tone += 10)
                {
                    var hctColor = Hct.From(hue, chroma, tone);

                    if (chroma > 0)
                    {
                        // JUnit assertEquals(hue, hct.getHue(), 4.0) — linear delta, not circular difference.
                        Assert.True(
                            Math.Abs(hue - hctColor.Hue) <= 4.0,
                            $"Incorrect hue for H{hue} C{chroma} T{tone}: got {hctColor.Hue}");
                    }

                    Assert.True(hctColor.Chroma >= 0.0, $"Negative chroma for H{hue} C{chroma} T{tone}");

                    Assert.True(hctColor.Chroma <= chroma + 2.5, $"Chroma too high for H{hue} C{chroma} T{tone}");

                    if (hctColor.Chroma < chroma - 2.5)
                    {
                        Assert.True(
                            ColorIsOnBoundary(hctColor.ToInt()),
                            $"Color not on boundary for non-sRGB color for H{hue} C{chroma} T{tone}");
                    }

                    Assert.Equal(tone, hctColor.Tone, 0.5);
                }
            }
        }
    }
}
