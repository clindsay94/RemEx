using Avalonia.Media;
using FluentAssertions;
using Remex.Desktop.Controls;
using Remex.Desktop.Models;
using Remex.Desktop.Services;
using Remex.Desktop.Tests.Services;
using Xunit;

namespace Remex.Desktop.Tests.Controls;

/// <summary>
/// The logo's R must stay visible on the live primary -> tertiary window fill (RemEx-pp4cm.23).
/// Ink-Dark-C1 drew a white R on a white tile because the R was a fixed off-white.
/// </summary>
public class BrandMarkGlyphContrastTests
{
    private static IEnumerable<string> Seeds()
    {
        var seeds = new List<string> { "#6C4CFF", "#F5F5F5", "#0B0B0F", "#00FF00", "#FFFFFF", "#000000" };
        seeds.AddRange(ThemeDictionary.SelectThemeCases().Where(c => c.IsSeeded).Select(c => c.Seed));
        return seeds.Distinct(StringComparer.OrdinalIgnoreCase);
    }

    public static IEnumerable<object[]> Cells() =>
        from seed in Seeds()
        from variant in SchemeVariants.All
        from isDark in new[] { false, true }
        from contrast in new[] { 0.0, 1.0 }
        select new object[] { seed, variant, isDark, contrast };

    [Theory]
    [MemberData(nameof(Cells))]
    public void TheRClearsThreeToOneOnBothEndsOfTheWindowFill(string seed, string variant, bool isDark, double contrast)
    {
        Color.TryParse(seed, out var c).Should().BeTrue();
        var p = DynamicColorGenerator.Generate(c, variant, isDark, contrast);

        var glyph = BrandMarkGlyph.GlyphColor(p.Primary, p.Tertiary, p.OnPrimary);

        BrandMarkGlyph.ContrastRatio(glyph, p.Primary).Should().BeGreaterThanOrEqualTo(BrandMarkGlyph.GlyphMinimumContrast, "R on primary");
        BrandMarkGlyph.ContrastRatio(glyph, p.Tertiary).Should().BeGreaterThanOrEqualTo(BrandMarkGlyph.GlyphMinimumContrast, "R on tertiary");
    }

    [Fact]
    public void TheBrandOffWhiteStaysWhereverItReads()
    {
        var p = DynamicColorGenerator.Generate(Color.Parse("#6C4CFF"), "TonalSpot", isDark: false, contrast: 0);
        BrandMarkGlyph.GlyphColor(p.Primary, p.Tertiary, p.OnPrimary)
            .Should().Be(Color.FromUInt32(Remex.Branding.RemexBrandData.OffWhiteArgb));
    }
}
