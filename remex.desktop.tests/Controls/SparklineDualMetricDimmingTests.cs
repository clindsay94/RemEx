using Avalonia.Media;
using FluentAssertions;
using Remex.Desktop.Controls;
using Remex.Desktop.Models;
using Remex.Desktop.Services;
using Xunit;

namespace Remex.Desktop.Tests.Controls;

/// <summary>
/// RemEx-n2kv0: under SchemeVariants.Monochrome, AccentColor and SecondaryAccentColor (Primary and
/// Tertiary, both rebuilt as chroma-0 palettes on the same hue) collapse to the same grey, so a
/// dual-metric sparkline with no accent of its own draws both series indistinguishably. The gate
/// decision detects this by colour distance in HCT, not by variant name, so a future variant that
/// collapses the same way is covered without a new special case.
/// </summary>
/// <remarks>
/// <para>
/// DARK MODE ONLY, since the MCU port (RemEx-4kv0g.12): Google's real <c>SchemeMonochrome</c>
/// (<c>MaterialDynamicColors.java</c>) places Primary and Tertiary at tone 100/90 in dark mode —
/// close enough to read as indistinguishable — but at tone 0/25 in LIGHT mode, which is black next
/// to a visibly lighter grey. The old albi005 approximation collapsed both modes identically; the
/// exact port does not, and light mode not collapsing is Google's own behaviour, not a bug here.
/// </para>
/// <para>
/// Driven by <see cref="DynamicColorGenerator.Generate"/>'s real output, not hand-written hex - a
/// prior review (this bead) found four tests here that fed arbitrary hex and never exercised what
/// the palette actually produces. The seed sweep is the same three cells
/// <c>scripts/ui-palette-sweep.ps1 -ListCells</c> and <c>docs/UI-PALETTE-SWEEP.md</c> define for
/// stress-testing generated colour: the shipped default plus the near-white and near-black extremes.
/// </para>
/// </remarks>
public class SparklineDualMetricDimmingTests
{
    private static readonly (string Name, string Hex)[] SweepSeeds =
    {
        ("Default", "#6C4CFF"), // BaseDarkGlass preset, the shipped default (docs/UI-PALETTE-SWEEP.md)
        ("Chalk", "#F5F5F5"),   // near-white
        ("Ink", "#0B0B0F"),     // near-black
    };

    public static IEnumerable<object[]> SeedModeContrastCombinations()
    {
        foreach (var (name, hex) in SweepSeeds)
        foreach (var isDark in new[] { true, false })
        foreach (var contrast in new[] { 0.0, 1.0 })
            yield return new object[] { name, hex, isDark, contrast };
    }

    public static IEnumerable<object[]> DarkSeedContrastCombinations()
    {
        foreach (var (name, hex) in SweepSeeds)
        foreach (var contrast in new[] { 0.0, 1.0 })
            yield return new object[] { name, hex, true, contrast };
    }

    [Theory]
    [MemberData(nameof(DarkSeedContrastCombinations))]
    public void MonochromePrimaryAndTertiaryAreIndistinguishable(string name, string hex, bool isDark, double contrast)
    {
        var seed = Color.Parse(hex);
        var palette = DynamicColorGenerator.Generate(seed, SchemeVariants.Monochrome, isDark, contrast);

        SparklineControl.SeriesColorsAreIndistinguishable(palette.Primary, palette.Tertiary).Should().BeTrue(
            $"Monochrome zeroes chroma on every tonal palette, so seed {name} ({hex}, dark={isDark}, " +
            $"contrast={contrast}) must still collapse Primary and Tertiary to the same grey");
    }

    [Theory]
    [MemberData(nameof(SeedModeContrastCombinations))]
    public void TonalSpotPrimaryAndTertiaryAreDistinguishable(string name, string hex, bool isDark, double contrast)
    {
        var seed = Color.Parse(hex);
        var palette = DynamicColorGenerator.Generate(seed, SchemeVariants.TonalSpot, isDark, contrast);

        SparklineControl.SeriesColorsAreIndistinguishable(palette.Primary, palette.Tertiary).Should().BeFalse(
            $"TonalSpot spreads Primary and Tertiary across distinct hues, so seed {name} ({hex}, " +
            $"dark={isDark}, contrast={contrast}) must not trip the collapse predicate");
    }

    /// <summary>
    /// Neutral (Spritz-like, ~12-16 chroma on both roles) was flagged by review as a case the
    /// predicate might wrongly call indistinguishable. Measured against real
    /// <see cref="DynamicColorGenerator.Generate"/> output across every seed/mode/contrast cell
    /// above, Neutral's Primary and Tertiary land within ~1-3 degrees of hue, ~4 units of chroma and
    /// well under 1 tone of each other (e.g. the Default dark seed produces #C9C4D6 vs #C9C3DC) - a
    /// difference no viewer would read as two separate line colours. The predicate reporting these
    /// as indistinguishable is therefore correct for Neutral's actual palette, not the bug the
    /// review predicted; forcing this assertion to False would fail against real data and would make
    /// the control claim two near-identical greys are "different enough" to skip dimming. See the
    /// Deviations note in this bead's handoff for the measured values.
    /// </summary>
    [Theory]
    [MemberData(nameof(SeedModeContrastCombinations))]
    public void NeutralPrimaryAndTertiaryAreIndistinguishableInPractice(string name, string hex, bool isDark, double contrast)
    {
        var seed = Color.Parse(hex);
        var palette = DynamicColorGenerator.Generate(seed, SchemeVariants.Neutral, isDark, contrast);

        SparklineControl.SeriesColorsAreIndistinguishable(palette.Primary, palette.Tertiary).Should().BeTrue(
            $"Neutral's real Primary/Tertiary pair for seed {name} ({hex}, dark={isDark}, " +
            $"contrast={contrast}) measures under a tone apart and within the hue/chroma match band - " +
            "genuinely near-identical, not a predicate false positive");
    }

    [Fact]
    public void IdenticalColoursAreIndistinguishable()
    {
        var grey = Color.Parse("#808080");

        SparklineControl.SeriesColorsAreIndistinguishable(grey, grey).Should().BeTrue(
            "identical colours can never be told apart regardless of the metric used");
    }

    [Fact]
    public void TwoWidelySeparatedChromaZeroGreysAreDistinguishable()
    {
        // Monochrome's Primary and Tertiary are both exact greys, but a large enough TONE gap still
        // has to read as two visible lines - a near-black line and a near-white line of "the same
        // grey" are not the false positive the chroma-near-zero check exists for. The tone-delta
        // escape now runs BEFORE the grey short-circuit, so a ~40+ tone gap between two chroma-0
        // colours correctly escapes to "distinguishable" instead of being swallowed by chroma alone.
        var lightGrey = Color.Parse("#B0B0B0");
        var darkGrey = Color.Parse("#404040");

        SparklineControl.SeriesColorsAreIndistinguishable(lightGrey, darkGrey).Should().BeFalse(
            "both are chroma-0, but the tone gap is far above IndistinguishableToneDelta and must " +
            "escape the grey short-circuit, not be swallowed by it");
    }

    [Fact]
    public void TwoCloseChromaZeroGreysAreIndistinguishable()
    {
        // Same chroma-0 Monochrome case, but with the two greys only a few tones apart - well under
        // IndistinguishableToneDelta, so the tone-delta escape does not fire and the chroma-near-zero
        // short-circuit still correctly calls these indistinguishable.
        var lightGrey = SeedHct.ToColor(hue: 0, chroma: 0, tone: 55);
        var darkGrey = SeedHct.ToColor(hue: 0, chroma: 0, tone: 50);

        SparklineControl.SeriesColorsAreIndistinguishable(lightGrey, darkGrey).Should().BeTrue(
            "both are chroma-0 and only a few tones apart - well under IndistinguishableToneDelta - " +
            "so the grey short-circuit must still call these indistinguishable");
    }

    [Fact]
    public void ASmallHueNudgeAtHighChromaStaysDistinguishable()
    {
        // Guards against a threshold so loose that ordinary chroma variety keeps getting called
        // "indistinguishable" just because two saturated colours share the same rough hue family.
        var a = Color.Parse("#FF0000");
        var b = Color.Parse("#FF5010");

        SparklineControl.SeriesColorsAreIndistinguishable(a, b).Should().BeFalse(
            "both are vividly saturated and visibly different reds/oranges, not a Monochrome collapse");
    }

    [Fact]
    public void ALargeToneGapEscapesTheMatchEvenAtCloseHueAndChroma()
    {
        // Same hue, same chroma, ~50 tones apart - a light line and a dark line of "the same
        // colour" that must still read as two distinguishable lines (the escape this bead adds).
        var light = SeedHct.ToColor(hue: 260, chroma: 30, tone: 85);
        var dark = SeedHct.ToColor(hue: 260, chroma: 30, tone: 35);

        SparklineControl.SeriesColorsAreIndistinguishable(light, dark).Should().BeFalse(
            "a ~50 tone gap is far above IndistinguishableToneDelta and must escape the hue/chroma " +
            "match regardless of how close hue and chroma are");
    }

    [Fact]
    public void SecondaryDimOpacityFactorIsTheContrast0FloorAtContrastZero()
    {
        SparklineControl.SecondaryDimOpacityFactor(0.0).Should().Be(0.55,
            "contrast 0.0 must keep the original dim step exactly");
    }

    [Fact]
    public void SecondaryDimOpacityFactorStaysAtOrAboveThreeQuartersAtContrastOne()
    {
        double factor = SparklineControl.SecondaryDimOpacityFactor(1.0);

        factor.Should().BeGreaterThanOrEqualTo(0.75,
            "at contrast 1.0 DynamicColorGenerator.Contrasted has already spent its budget getting " +
            "the pair to AAA - a flat 0.55 multiplier on top would cut a 1px line back under AA, so " +
            "the floor must not drop below 0.75 here");
    }

    [Fact]
    public void SecondaryDimOpacityFactorClampsNegativeContrastToTheContrast0Floor()
    {
        // Reduced-contrast mode never had extra ratio budget to protect, so it gets the same floor
        // as contrast 0.0 rather than an extrapolated-below-0.55 value.
        SparklineControl.SecondaryDimOpacityFactor(-1.0).Should().Be(0.55,
            "negative contrast must clamp to the contrast-0 floor, not extrapolate past it");
    }
}
