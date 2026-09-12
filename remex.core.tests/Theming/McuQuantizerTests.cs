using Remex.Core.Theming.Mcu;

namespace Remex.Core.Tests.Theming;

/// <summary>RemEx-4kv0g.11: the quantizer/score pair behind wallpaper seeding, against material 1.14.0's QuantizerMapTest, ScoreTest and java.util.Random.</summary>
public class McuQuantizerTests
{
    private const uint Red = 0xFFFF0000u, Green = 0xFF00FF00u, Blue = 0xFF0000FFu;

    [Fact]
    public void JavaRandom_ReproducesJavaUtilRandom()
    {
        // java.util.Random(0x42688).nextInt(100) × 8 and .nextInt(16) × 5, computed from the JDK's LCG definition
        // (seed = (seed ^ 0x5DEECE66DL) & ((1L << 48) - 1); next = (seed * 0x5DEECE66DL + 0xBL) & mask; nextInt's rejection loop).
        // Cross-check: new Random(42).nextInt(10) × 5 is the well-known 0,3,8,4,0 sequence.
        var r = new JavaRandom(0x42688);
        Assert.Equal(new[] { 61, 18, 60, 65, 41, 51, 70, 69 }, Enumerable.Range(0, 8).Select(_ => r.NextInt(100)).ToArray());
        var r16 = new JavaRandom(0x42688);
        Assert.Equal(new[] { 6, 13, 4, 10, 1 }, Enumerable.Range(0, 5).Select(_ => r16.NextInt(16)).ToArray());
        var r42 = new JavaRandom(42);
        Assert.Equal(new[] { 0, 3, 8, 4, 0 }, Enumerable.Range(0, 5).Select(_ => r42.NextInt(10)).ToArray());
    }

    [Fact]
    public void QuantizerMap_CountsExactColours()
    {
        // QuantizerMapTest.quantize_1R / 1G / 1B / 5B / 2R3G / 1R1G1B (complete)
        Assert.Equal(new Dictionary<uint, int> { [Red] = 1 }, new QuantizerMap().Quantize(new[] { Red }, 128).ColorToCount);
        Assert.Equal(new Dictionary<uint, int> { [Green] = 1 }, new QuantizerMap().Quantize(new[] { Green }, 128).ColorToCount);
        Assert.Equal(new Dictionary<uint, int> { [Blue] = 1 }, new QuantizerMap().Quantize(new[] { Blue }, 128).ColorToCount);
        Assert.Equal(new Dictionary<uint, int> { [Blue] = 5 }, new QuantizerMap().Quantize(new[] { Blue, Blue, Blue, Blue, Blue }, 128).ColorToCount);
        Assert.Equal(new Dictionary<uint, int> { [Red] = 2, [Green] = 3 }, new QuantizerMap().Quantize(new[] { Red, Red, Green, Green, Green }, 128).ColorToCount);
        Assert.Equal(new Dictionary<uint, int> { [Red] = 1, [Green] = 1, [Blue] = 1 }, new QuantizerMap().Quantize(new[] { Red, Green, Blue }, 128).ColorToCount);
    }

    [Fact]
    public void QuantizerCelebi_TwoPureColoursComeBackWithTheirPopulations()
    {
        var pixels = Enumerable.Repeat(Red, 5).Concat(Enumerable.Repeat(Blue, 3)).ToArray();
        var clusters = QuantizerCelebi.Quantize(pixels, 4);
        Assert.InRange(clusters.Count, 1, 2);
        Assert.Equal(8, clusters.Values.Sum());
    }

    [Fact]
    public void Score_PrioritizesChroma()
    {
        var m = new Dictionary<uint, int> { [0xFF000000u] = 1, [0xFFFFFFFFu] = 1, [0xFF0000FFu] = 1 };
        Assert.Equal(new List<uint> { 0xFF0000FFu }, Score.ScoreColors(m, 4));
    }

    [Fact]
    public void Score_PrioritizesChromaWhenProportionsEqual()
    {
        var m = new Dictionary<uint, int> { [0xFFFF0000u] = 1, [0xFF00FF00u] = 1, [0xFF0000FFu] = 1 };
        Assert.Equal(new List<uint> { 0xFFFF0000u, 0xFF00FF00u, 0xFF0000FFu }, Score.ScoreColors(m, 4));
    }

    [Fact]
    public void Score_GeneratesGoogleBlueWhenNoColoursAreAvailable()
    {
        var m = new Dictionary<uint, int> { [0xFF000000u] = 1 };
        Assert.Equal(new List<uint> { 0xFF4285F4u }, Score.ScoreColors(m, 4));
    }

    [Fact]
    public void Score_DedupesNearbyHues()
    {
        var m = new Dictionary<uint, int> { [0xFF008772u] = 1, [0xFF318477u] = 1 };   // H 180 C 42 T 50 / H 184 C 35 T 50
        Assert.Equal(new List<uint> { 0xFF008772u }, Score.ScoreColors(m, 4));
    }

    [Fact]
    public void Score_MaximizesHueDistance()
    {
        var m = new Dictionary<uint, int> { [0xFF008772u] = 1, [0xFF008587u] = 1, [0xFF007EBCu] = 1 };
        Assert.Equal(new List<uint> { 0xFF007EBCu, 0xFF008772u }, Score.ScoreColors(m, 2));
    }

    [Fact]
    public void Score_GeneratedScenarioOne()
    {
        var m = new Dictionary<uint, int> { [0xFF7EA16Du] = 67, [0xFFD8CCAEu] = 67, [0xFF835C0Du] = 49 };
        Assert.Equal(new List<uint> { 0xFF7EA16Du, 0xFFD8CCAEu, 0xFF835C0Du }, Score.ScoreColors(m, 3, 0xFF8D3819u, false));
    }

    [Fact]
    public void Score_GeneratedScenarioTwo()
    {
        var m = new Dictionary<uint, int> { [0xFFD33881u] = 14, [0xFF3205CCu] = 77, [0xFF0B48CFu] = 36, [0xFFA08F5Du] = 81 };
        Assert.Equal(new List<uint> { 0xFF3205CCu, 0xFFA08F5Du, 0xFFD33881u }, Score.ScoreColors(m, 4, 0xFF7D772Bu, true));
    }

    [Fact]
    public void Score_GeneratedScenarioThree()
    {
        var m = new Dictionary<uint, int> { [0xFFBE94A6u] = 23, [0xFFC33FD7u] = 42, [0xFF899F36u] = 90, [0xFF94C574u] = 82 };
        Assert.Equal(new List<uint> { 0xFF94C574u, 0xFFC33FD7u, 0xFFBE94A6u }, Score.ScoreColors(m, 3, 0xFFAA79A4u, true));
    }

    [Fact]
    public void Score_GeneratedScenarioFour()
    {
        var m = new Dictionary<uint, int> { [0xFFDF241Cu] = 85, [0xFF685859u] = 44, [0xFFD06D5Fu] = 34, [0xFF561C54u] = 27, [0xFF713090u] = 88 };
        Assert.Equal(new List<uint> { 0xFFDF241Cu, 0xFF561C54u }, Score.ScoreColors(m, 5, 0xFF58C19Cu, false));
    }

    [Fact]
    public void Score_GeneratedScenarioFive()
    {
        var m = new Dictionary<uint, int> { [0xFFBE66F8u] = 41, [0xFF4BBDA9u] = 88, [0xFF80F6F9u] = 44, [0xFFAB8017u] = 43, [0xFFE89307u] = 65 };
        Assert.Equal(new List<uint> { 0xFFAB8017u, 0xFF4BBDA9u, 0xFFBE66F8u }, Score.ScoreColors(m, 3, 0xFF916691u, false));
    }

    [Fact]
    public void Score_GeneratedScenarioSix()
    {
        var m = new Dictionary<uint, int> { [0xFF18EA8Fu] = 93, [0xFF327593u] = 18, [0xFF066A18u] = 53, [0xFFFA8A23u] = 74, [0xFF04CA1Fu] = 62 };
        Assert.Equal(new List<uint> { 0xFF18EA8Fu, 0xFFFA8A23u }, Score.ScoreColors(m, 2, 0xFF4C377Au, false));
    }

    [Fact]
    public void Score_GeneratedScenarioSeven()
    {
        var m = new Dictionary<uint, int> { [0xFF2E05EDu] = 23, [0xFF153E55u] = 90, [0xFF9AB220u] = 23, [0xFF153379u] = 66, [0xFF68BCC3u] = 81 };
        Assert.Equal(new List<uint> { 0xFF2E05EDu, 0xFF9AB220u }, Score.ScoreColors(m, 2, 0xFFF588DCu, true));
    }

    [Fact]
    public void Score_GeneratedScenarioEight()
    {
        var m = new Dictionary<uint, int> { [0xFF816EC5u] = 24, [0xFF6DCB94u] = 19, [0xFF3CAE91u] = 98, [0xFF5B542Fu] = 25 };
        Assert.Equal(new List<uint> { 0xFF3CAE91u }, Score.ScoreColors(m, 1, 0xFF84B0FDu, false));
    }

    [Fact]
    public void Score_GeneratedScenarioNine()
    {
        var m = new Dictionary<uint, int> { [0xFF206F86u] = 52, [0xFF4A620Du] = 96, [0xFFF51401u] = 85, [0xFF2B8EBFu] = 3, [0xFF277766u] = 59 };
        Assert.Equal(new List<uint> { 0xFFF51401u, 0xFF4A620Du, 0xFF2B8EBFu }, Score.ScoreColors(m, 3, 0xFF02B415u, true));
    }

    [Fact]
    public void Score_GeneratedScenarioTen()
    {
        var m = new Dictionary<uint, int> { [0xFF8B1D99u] = 54, [0xFF27EFFEu] = 43, [0xFF6F558Du] = 2, [0xFF77FDF2u] = 78 };
        Assert.Equal(new List<uint> { 0xFF27EFFEu, 0xFF8B1D99u, 0xFF6F558Du }, Score.ScoreColors(m, 4, 0xFF5E7A10u, true));
    }
}
