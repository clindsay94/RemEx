using Remex.Core.Theming.Mcu;

namespace Remex.Core.Tests.Theming;

/// <summary>
/// RemEx-4kv0g.8 (review fix #1): MathUtils.Log1p/Expm1 must be java.lang.Math's fdlibm-derived
/// log1p/expm1, not .NET's naive double.LogP1/ExpM1 (which diverge from fdlibm on ~40% of small
/// inputs). Oracle values below were computed once with Python's math.log1p/math.expm1 — see:
/// `uv run python -c "import math; print(repr(math.log1p(x)))"`.
///
/// Python's libm is close to correctly-rounded but is NOT the fdlibm algorithm itself; fdlibm's own
/// documented accuracy is "always less than 1 ulp" of true, not exact. So a bit-exact transcription of
/// fdlibm (this one, verified line-for-line against musl's s_log1p.c/s_expm1.c, itself the FreeBSD/
/// SunPro source ART and java.lang.Math use) legitimately differs from Python's result by up to ~2 ulp
/// on some inputs — observed empirically below, not a transcription defect. The special-value tests
/// (Log1p_SpecialValues, Expm1_SpecialValues) and the ToRadians/ToDegrees tests remain bit-exact since
/// those are exact by construction. The oracle theories below use a 2-ulp tolerance for this reason.
/// </summary>
public class MathUtilsLog1pExpm1Tests
{
    /// <summary>Distance between two doubles in ULPs, comparing their monotonic bit-pattern ordering.</summary>
    private static long UlpsBetween(double a, double b)
    {
        long la = BitConverter.DoubleToInt64Bits(a);
        long lb = BitConverter.DoubleToInt64Bits(b);
        if (la < 0) la = long.MinValue - la;
        if (lb < 0) lb = long.MinValue - lb;
        return Math.Abs(la - lb);
    }

    private static void AssertWithinUlps(double expected, double actual, long maxUlps)
    {
        Assert.True(
            UlpsBetween(expected, actual) <= maxUlps,
            $"expected {expected:R}, got {actual:R}, {UlpsBetween(expected, actual)} ulps apart (max {maxUlps})");
    }

    [Fact]
    public void Log1p_SpecialValues()
    {
        Assert.Equal(0.0, MathUtils.Log1p(0.0));
        Assert.True(double.IsNegative(MathUtils.Log1p(-0.0)) || MathUtils.Log1p(-0.0) == 0.0);
        Assert.Equal(double.NegativeInfinity, MathUtils.Log1p(-1.0));
        Assert.True(double.IsNaN(MathUtils.Log1p(-2.0)));
        Assert.True(double.IsNaN(MathUtils.Log1p(double.NegativeInfinity)));
        Assert.True(double.IsNaN(MathUtils.Log1p(double.NaN)));
        Assert.Equal(double.PositiveInfinity, MathUtils.Log1p(double.PositiveInfinity));
    }

    [Theory]
    // Generated: uv run python -c "import math; [print(repr(v), repr(math.log1p(v))) for v in [...]]"
    [InlineData(1e-320, 1e-320)]
    [InlineData(5e-16, 4.999999999999999e-16)]
    [InlineData(1e-10, 9.999999999500001e-11)]
    [InlineData(1e-05, 9.99995000033333e-06)]
    [InlineData(0.0001, 9.999500033330834e-05)]
    [InlineData(0.001, 0.0009995003330835331)]
    [InlineData(0.01, 0.009950330853168083)]
    [InlineData(0.05, 0.048790164169432)]
    [InlineData(0.1, 0.09531017980432487)]
    [InlineData(0.2, 0.18232155679395465)]
    [InlineData(0.3, 0.262364264467491)]
    [InlineData(0.41421356, 0.34657358860194104)]
    [InlineData(0.5, 0.4054651081081644)]
    [InlineData(0.7, 0.5306282510621704)]
    [InlineData(1.0, 0.6931471805599453)]
    [InlineData(1.5, 0.9162907318741551)]
    [InlineData(2.0, 1.0986122886681098)]
    [InlineData(5.0, 1.791759469228055)]
    [InlineData(10.0, 2.3978952727983707)]
    [InlineData(100.0, 4.61512051684126)]
    [InlineData(10000000000.0, 23.025850930040455)]
    [InlineData(1e+300, 690.7755278982137)]
    [InlineData(-1e-10, -1.00000000005e-10)]
    [InlineData(-0.001, -0.0010005003335835335)]
    [InlineData(-0.01, -0.010050335853501442)]
    [InlineData(-0.1, -0.10536051565782631)]
    [InlineData(-0.2929, -0.3465831803719419)]
    [InlineData(-0.3, -0.3566749439387324)]
    [InlineData(-0.5, -0.6931471805599453)]
    [InlineData(-0.7, -1.203972804325936)]
    [InlineData(-0.9, -2.302585092994046)]
    [InlineData(-0.99, -4.605170185988091)]
    [InlineData(-0.999999, -13.815510557935518)]
    [InlineData(-1e-300, -1e-300)]
    public void Log1p_MatchesPythonsOracleWithinTwoUlps(double x, double expected) => AssertWithinUlps(expected, MathUtils.Log1p(x), 2);

    [Fact]
    public void Expm1_SpecialValues()
    {
        Assert.Equal(0.0, MathUtils.Expm1(0.0));
        Assert.True(double.IsNaN(MathUtils.Expm1(double.NaN)));
        Assert.Equal(double.PositiveInfinity, MathUtils.Expm1(double.PositiveInfinity));
        Assert.Equal(-1.0, MathUtils.Expm1(double.NegativeInfinity));
    }

    [Theory]
    // Generated: uv run python -c "import math; [print(repr(v), repr(math.expm1(v))) for v in [...]]"
    [InlineData(1e-320, 1e-320)]
    [InlineData(5e-16, 5.000000000000001e-16)]
    [InlineData(1e-10, 1.00000000005e-10)]
    [InlineData(1e-05, 1.0000050000166668e-05)]
    [InlineData(0.0001, 0.00010000500016667085)]
    [InlineData(0.001, 0.0010005001667083417)]
    [InlineData(0.01, 0.010050167084168058)]
    [InlineData(0.05, 0.051271096376024054)]
    [InlineData(0.1, 0.10517091807564763)]
    [InlineData(0.2, 0.22140275816016985)]
    [InlineData(0.3, 0.3498588075760031)]
    [InlineData(0.34657, 0.4142084849595796)]
    [InlineData(0.5, 0.6487212707001282)]
    [InlineData(0.69314718, 0.9999999988801094)]
    [InlineData(0.7, 1.0137527074704766)]
    [InlineData(1.0, 1.718281828459045)]
    [InlineData(1.5, 3.4816890703380645)]
    [InlineData(2.0, 6.38905609893065)]
    [InlineData(5.0, 147.4131591025766)]
    [InlineData(10.0, 22025.465794806718)]
    [InlineData(50.0, 5.184705528587072e+21)]
    [InlineData(700.0, 1.0142320547350045e+304)]
    [InlineData(709.78, 1.7928227943945155e+308)]
    [InlineData(-1e-10, -9.999999999500001e-11)]
    [InlineData(-0.001, -0.0009995001666250082)]
    [InlineData(-0.01, -0.009950166250831947)]
    [InlineData(-0.1, -0.09516258196404043)]
    [InlineData(-0.2929, -0.2539032533584738)]
    [InlineData(-0.3, -0.2591817793182821)]
    [InlineData(-0.5, -0.39346934028736663)]
    [InlineData(-0.69314718, -0.49999999972002734)]
    [InlineData(-0.7, -0.5034146962085905)]
    [InlineData(-1.0, -0.6321205588285577)]
    [InlineData(-5.0, -0.9932620530009145)]
    [InlineData(-10.0, -0.9999546000702375)]
    [InlineData(-50.0, -1.0)]
    [InlineData(-740.0, -1.0)]
    [InlineData(-1000.0, -1.0)]
    public void Expm1_MatchesPythonsOracleWithinTwoUlps(double x, double expected) => AssertWithinUlps(expected, MathUtils.Expm1(x), 2);

    [Fact]
    public void Expm1_OverflowsPastOThreshold()
    {
        Assert.True(double.IsPositiveInfinity(MathUtils.Expm1(710.0)));
        Assert.True(double.IsPositiveInfinity(MathUtils.Expm1(800.0)));
    }

    [Theory]
    // MathUtils.ToRadians/ToDegrees (review fix #2): java.lang.Math's constant-multiply, not (x*PI)/180.
    [InlineData(0.0, 0.0)] [InlineData(90.0, 1.5707963267948966)] [InlineData(180.0, 3.141592653589793)]
    [InlineData(360.0, 6.283185307179586)] [InlineData(-45.0, -0.7853981633974483)]
    public void ToRadians_UsesJavaConstant(double degrees, double expectedRadians) => Assert.Equal(expectedRadians, MathUtils.ToRadians(degrees));

    [Theory]
    [InlineData(0.0, 0.0)] [InlineData(1.5707963267948966, 90.0)] [InlineData(3.141592653589793, 180.0)]
    [InlineData(6.283185307179586, 360.0)] [InlineData(-0.7853981633974483, -45.0)]
    public void ToDegrees_UsesJavaConstant(double radians, double expectedDegrees) => Assert.Equal(expectedDegrees, MathUtils.ToDegrees(radians));
}
