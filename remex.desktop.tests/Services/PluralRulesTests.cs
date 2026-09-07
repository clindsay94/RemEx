using FluentAssertions;
using Remex.Desktop.Services.FileTransfer;
using Xunit;

namespace Remex.Desktop.Tests.Services;

/// <summary>
/// <see cref="PluralRules"/> against the CLDR cardinal-plural rules for the nine locales RemEx ships
/// (RemEx-4lcq).
/// </summary>
/// <remarks>
/// Polish and Ukrainian are the two that need genuine coverage: their category is a function of the
/// last one and two digits, not a fixed range, and the standard CLDR sample set
/// (1; 2-4; 5-21; 22-24; 25 for Polish) exists specifically because a naive "n %% 10" implementation
/// gets the boundary around 11-14 wrong in both languages.
/// </remarks>
public class PluralRulesTests
{
    // ── Polish: one / few / many / other, cycling on the last one and two digits ──

    [Theory]
    [InlineData(1, PluralCategory.One)]
    [InlineData(2, PluralCategory.Few)]
    [InlineData(3, PluralCategory.Few)]
    [InlineData(4, PluralCategory.Few)]
    [InlineData(5, PluralCategory.Many)]
    [InlineData(9, PluralCategory.Many)]
    [InlineData(10, PluralCategory.Many)]
    [InlineData(11, PluralCategory.Many)]
    [InlineData(12, PluralCategory.Many)]
    [InlineData(13, PluralCategory.Many)]
    [InlineData(14, PluralCategory.Many)]
    [InlineData(15, PluralCategory.Many)]
    [InlineData(20, PluralCategory.Many)]
    [InlineData(21, PluralCategory.Many)]
    [InlineData(22, PluralCategory.Few)]
    [InlineData(23, PluralCategory.Few)]
    [InlineData(24, PluralCategory.Few)]
    [InlineData(25, PluralCategory.Many)]
    [InlineData(32, PluralCategory.Few)]
    [InlineData(35, PluralCategory.Many)]
    [InlineData(100, PluralCategory.Many)]
    [InlineData(112, PluralCategory.Many)]
    public void Polish_MatchesTheClDrSampleSet(int n, PluralCategory expected) =>
        PluralRules.Category("pl", n).Should().Be(expected);

    // ── Ukrainian: same shape as Polish, but "n % 100 == 11" is carved out of "one" into "many" ──

    [Theory]
    [InlineData(1, PluralCategory.One)]
    [InlineData(21, PluralCategory.One)]
    [InlineData(31, PluralCategory.One)]
    [InlineData(2, PluralCategory.Few)]
    [InlineData(3, PluralCategory.Few)]
    [InlineData(4, PluralCategory.Few)]
    [InlineData(22, PluralCategory.Few)]
    [InlineData(24, PluralCategory.Few)]
    [InlineData(5, PluralCategory.Many)]
    [InlineData(9, PluralCategory.Many)]
    [InlineData(10, PluralCategory.Many)]
    [InlineData(11, PluralCategory.Many)]
    [InlineData(12, PluralCategory.Many)]
    [InlineData(13, PluralCategory.Many)]
    [InlineData(14, PluralCategory.Many)]
    [InlineData(20, PluralCategory.Many)]
    [InlineData(25, PluralCategory.Many)]
    public void Ukrainian_CarvesElevenOutOfOneIntoMany(int n, PluralCategory expected) =>
        PluralRules.Category("uk", n).Should().Be(expected);

    // ── The rest: singular-at-one, zero-or-one, or always "other" ──

    [Theory]
    [InlineData("en", 1, PluralCategory.One)]
    [InlineData("en", 2, PluralCategory.Other)]
    [InlineData("en", 21, PluralCategory.Other)] // NOT "one" - a trap for an n % 10 implementation
    [InlineData("es", 1, PluralCategory.One)]
    [InlineData("es", 5, PluralCategory.Other)]
    [InlineData("pt-BR", 1, PluralCategory.One)]
    [InlineData("pt-BR", 3, PluralCategory.Other)]
    public void SingularAtExactlyOne_ForEnglishSpanishAndBrazilianPortuguese(string culture, int n, PluralCategory expected) =>
        PluralRules.Category(culture, n).Should().Be(expected);

    [Theory]
    [InlineData("fr", 0, PluralCategory.One)]
    [InlineData("fr", 1, PluralCategory.One)]
    [InlineData("fr", 2, PluralCategory.Other)]
    [InlineData("hi", 0, PluralCategory.One)]
    [InlineData("hi", 1, PluralCategory.One)]
    [InlineData("hi", 2, PluralCategory.Other)]
    public void ZeroAndOneShareTheOneCategory_ForFrenchAndHindi(string culture, int n, PluralCategory expected) =>
        PluralRules.Category(culture, n).Should().Be(expected);

    /// <summary>
    /// Turkish and Indonesian never resolve to "one", even at n = 1 (RemEx-4lcq): Turkish does not
    /// inflect a noun after a numeral (Android ships byte-identical "one" and "other" text for it),
    /// and Indonesian has no cardinal plural distinction in CLDR at all.
    /// </summary>
    [Theory]
    [InlineData("tr", 0)]
    [InlineData("tr", 1)]
    [InlineData("tr", 2)]
    [InlineData("tr", 21)]
    [InlineData("id", 0)]
    [InlineData("id", 1)]
    [InlineData("id", 2)]
    public void TurkishAndIndonesian_AreAlwaysOther(string culture, int n) =>
        PluralRules.Category(culture, n).Should().Be(PluralCategory.Other);

    [Fact]
    public void UnrecognisedCulture_FallsBackToTheEnglishRule() =>
        PluralRules.Category("xx-XX", 1).Should().Be(PluralCategory.One);

    [Fact]
    public void MatchingIsCaseInsensitive() =>
        PluralRules.Category("PL", 22).Should().Be(PluralCategory.Few);

    [Fact]
    public void NegativeCount_Throws() =>
        FluentActions.Invoking(() => PluralRules.Category("en", -1)).Should().Throw<ArgumentOutOfRangeException>();
}
