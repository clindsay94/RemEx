using Avalonia.Media;
using FluentAssertions;
using Remex.Desktop.Controls;
using Xunit;

namespace Remex.Desktop.Tests.Controls;

/// <summary>
/// RemEx-n2kv0: under SchemeVariants.Monochrome, AccentColor and SecondaryAccentColor (Primary and
/// Tertiary, both rebuilt as chroma-0 palettes on the same hue) collapse to the same grey, so a
/// dual-metric sparkline with no accent of its own draws both series indistinguishably. The gate
/// decision detects this by colour distance in HCT, not by variant name, so a future variant that
/// collapses the same way is covered without a new special case.
/// </summary>
public class SparklineDualMetricDimmingTests
{
    [Fact]
    public void IdenticalColoursAreIndistinguishable()
    {
        var grey = Color.Parse("#808080");

        SparklineControl.SeriesColorsAreIndistinguishable(grey, grey).Should().BeTrue(
            "identical colours can never be told apart regardless of the metric used");
    }

    [Fact]
    public void TwoDifferentChromaZeroGreysAreIndistinguishable()
    {
        // Monochrome's Primary and Tertiary are both exact greys, but not necessarily the exact
        // same TONE - hue is meaningless noise at chroma 0, so tone alone must not read as distinct.
        var lightGrey = Color.Parse("#B0B0B0");
        var darkGrey = Color.Parse("#404040");

        SparklineControl.SeriesColorsAreIndistinguishable(lightGrey, darkGrey).Should().BeTrue(
            "both are chroma-0 - the Monochrome case the gate decision was written for");
    }

    [Fact]
    public void TheDefaultTonalSpotPrimaryTertiaryPairIsNotDimmed()
    {
        // The app's own StyledProperty defaults (AccentColorProperty, SecondaryAccentColorProperty)
        // before the theme Style overrides them - a violet accent against an amber tertiary, the
        // shape every non-Monochrome variant produces (distinct hue, non-trivial chroma on both).
        var accent = Color.Parse("#C0C0FF");
        var secondaryAccent = Color.Parse("#FFB020");

        SparklineControl.SeriesColorsAreIndistinguishable(accent, secondaryAccent).Should().BeFalse(
            "a violet accent and an amber tertiary are nowhere near each other in hue or chroma");
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
}
