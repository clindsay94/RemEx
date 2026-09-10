using System.Linq;
using Avalonia.Media;
using FluentAssertions;
using Remex.Desktop.Models;
using Remex.Desktop.Services;
using Xunit;

namespace Remex.Desktop.Tests.Services;

/// <summary>
/// The seven strategies the sheet offers are Android's seven, in Android's order, and the two
/// that have no MaterialColorUtilities 0.3.0 style — Neutral and Monochrome — render as the spec
/// says: Neutral is the library's low-chroma Spritz, Monochrome zeroes every tonal palette.
/// </summary>
public class SchemeStrategyTests
{
    private static readonly Color Seed = Color.Parse("#6C4CFF");

    [Fact]
    public void TheSevenStrategiesAreAndroidsInAndroidsOrder()
    {
        SchemeVariants.All.Should().Equal(
            "TonalSpot", "Expressive", "FruitSalad", "Rainbow", "Vibrant", "Neutral", "Monochrome");
    }

    [Theory]
    [InlineData("Spritz", "Neutral")]
    [InlineData("Content", "TonalSpot")]
    [InlineData("Fidelity", "TonalSpot")]
    [InlineData(null, "TonalSpot")]
    [InlineData("", "TonalSpot")]
    [InlineData("tonalspot", "TonalSpot")]
    [InlineData("Monochrome", "Monochrome")]
    public void NormalizeMapsRetiredAndUnknownNamesOntoTheSeven(string? stored, string expected)
    {
        SchemeVariants.Normalize(stored).Should().Be(expected);
    }

    [Fact]
    public void NeutralIsLowChromaWhereVibrantIsNot()
    {
        double ChromaAt50(string variant) =>
            SeedHct.FromColor(DynamicColorGenerator.GenerateTonalRamps(Seed, variant).Primary.Single(t => t.Tone == 50).Color).Chroma;

        ChromaAt50("Neutral").Should().BeLessThan(16, "Neutral is the library's Spritz style, chroma 12 on primary");
        ChromaAt50("Vibrant").Should().BeGreaterThan(30, "Vibrant pins primary chroma at 48");
    }

    [Fact]
    public void MonochromeHasNoChromaOnAnyTonalPalette()
    {
        var ramps = DynamicColorGenerator.GenerateTonalRamps(Seed, "Monochrome");

        // Exact grey, not "low chroma". CAM16 reads even an R=G=B grey back with chroma 0.8–2.9
        // rising with tone (incomplete chromatic adaptation in the default viewing conditions), so
        // a chroma ceiling either fails true greys or lets a faint tint through. Equal channels
        // cannot be argued with.
        foreach (var ramp in new[] { ramps.Primary, ramps.Secondary, ramps.Tertiary, ramps.Neutral })
        foreach (var (tone, color) in ramp)
            AssertExactGrey(color, $"tone {tone}");
    }

    private static void AssertExactGrey(Color color, string what)
    {
        color.G.Should().Be(color.R, $"{what} must be an exact grey (got #{color.R:X2}{color.G:X2}{color.B:X2})");
        color.B.Should().Be(color.R, $"{what} must be an exact grey (got #{color.R:X2}{color.G:X2}{color.B:X2})");
    }

    [Fact]
    public void MonochromeKeepsSuccessGreenAndWarningAmber()
    {
        var palette = DynamicColorGenerator.Generate(Seed, "Monochrome", isDark: true);

        SeedHct.FromColor(palette.Success).Chroma.Should().BeGreaterThan(20, "success keeps its own seed (Theme.kt:110)");
        SeedHct.FromColor(palette.Warning).Chroma.Should().BeGreaterThan(20);
        AssertExactGrey(palette.Primary, "the user's accent under Monochrome");
        AssertExactGrey(palette.Surface, "the surface under Monochrome");
    }

    [Fact]
    public void AnythingOutsideTheSevenRendersAsTonalSpot()
    {
        DynamicColorGenerator.Generate(Seed, "Fidelity", isDark: true)
            .Should().Be(DynamicColorGenerator.Generate(Seed, "TonalSpot", isDark: true));
        DynamicColorGenerator.Generate(Seed, "Content", isDark: false)
            .Should().Be(DynamicColorGenerator.Generate(Seed, "TonalSpot", isDark: false),
                "Content is no longer a user-facing strategy");
    }

    [Theory]
    [InlineData("Vibrant")]
    [InlineData("Neutral")]
    [InlineData("Expressive")]
    public void SuccessAndWarningArePinnedToTonalSpotRegardlessOfVariant(string variant)
    {
        // Gate decision, RemEx-gw3ad: a seed means the same thing on both ends of the link, so
        // semantic colours must not change meaning with the decorative variant. Android
        // (Theme.kt:110-111) always builds success/warning from SchemeTonalSpot; the PC now
        // matches for every SchemeVariant, not just Monochrome.
        var variantPalette = DynamicColorGenerator.Generate(Seed, variant, isDark: true);
        var tonalSpotPalette = DynamicColorGenerator.Generate(Seed, "TonalSpot", isDark: true);

        variantPalette.Success.Should().Be(tonalSpotPalette.Success, $"success is pinned to TonalSpot under {variant}");
        variantPalette.OnSuccess.Should().Be(tonalSpotPalette.OnSuccess, $"onSuccess is pinned to TonalSpot under {variant}");
        variantPalette.Warning.Should().Be(tonalSpotPalette.Warning, $"warning is pinned to TonalSpot under {variant}");
        variantPalette.OnWarning.Should().Be(tonalSpotPalette.OnWarning, $"onWarning is pinned to TonalSpot under {variant}");

        // Not Primary: at this seed's hue, Vibrant's chroma boost happens to land on the exact same
        // tone-80 value TonalSpot produces, which would make a Primary-equality check pass by
        // coincidence rather than by verifying the variant is actually still applied. Secondary
        // (and PrimaryContainer, Tertiary) differ from TonalSpot for all three variants here.
        variantPalette.Secondary.Should().NotBe(tonalSpotPalette.Secondary,
            $"{variant}'s roles must still follow its own variant, only success/warning are pinned");
    }

    [Fact]
    public void EveryStrategyStillProducesAReadableSurfacePair()
    {
        foreach (var variant in SchemeVariants.All)
        foreach (var isDark in new[] { true, false })
        {
            var palette = DynamicColorGenerator.Generate(Seed, variant, isDark);
            DynamicColorGenerator.ContrastRatio(palette.Surface, palette.OnSurface)
                .Should().BeGreaterOrEqualTo(4.5, $"{variant} {(isDark ? "dark" : "light")}");
        }
    }
}
