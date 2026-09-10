using System.Linq;
using Avalonia.Media;
using FluentAssertions;
using Remex.Desktop.Services;
using Xunit;

namespace Remex.Desktop.Tests.Services;

/// <summary>
/// Aurora's two colour sets come straight off the tonal ramp (spec section 6): the primary,
/// secondary and tertiary palettes at tone 30 on a dark surface, tone 90 on a light one — the
/// tones Material uses for its containers.
/// </summary>
public class AuroraColorsTests
{
    private static readonly Color Seed = Color.Parse("#6C4CFF");

    [Fact]
    public void TheDarkSetIsTheThreeRampsAtToneThirty()
    {
        var ramps = DynamicColorGenerator.GenerateTonalRamps(Seed, "TonalSpot");
        var set = DynamicColorGenerator.AuroraColors(Seed, "TonalSpot", isLight: false);

        set.Primary.Should().Be(ramps.Primary.Single(t => t.Tone == 30).Color);
        set.Secondary.Should().Be(ramps.Secondary.Single(t => t.Tone == 30).Color);
        set.Tertiary.Should().Be(ramps.Tertiary.Single(t => t.Tone == 30).Color);
    }

    [Fact]
    public void TheLightSetIsTheThreeRampsAtToneNinety()
    {
        var ramps = DynamicColorGenerator.GenerateTonalRamps(Seed, "Vibrant");
        var set = DynamicColorGenerator.AuroraColors(Seed, "Vibrant", isLight: true);

        set.Primary.Should().Be(ramps.Primary.Single(t => t.Tone == 90).Color);
        set.Secondary.Should().Be(ramps.Secondary.Single(t => t.Tone == 90).Color);
        set.Tertiary.Should().Be(ramps.Tertiary.Single(t => t.Tone == 90).Color);
    }

    [Fact]
    public void TheTwoSetsDifferSoSystemModeVisiblyFlipsWithTheOs()
    {
        DynamicColorGenerator.AuroraColors(Seed, "TonalSpot", isLight: false)
            .Should().NotBe(DynamicColorGenerator.AuroraColors(Seed, "TonalSpot", isLight: true));
    }

    [Fact]
    public void MonochromeAuroraIsGrey()
    {
        var set = DynamicColorGenerator.AuroraColors(Seed, "Monochrome", isLight: false);
        SeedHct.FromColor(set.Primary).Chroma.Should().BeLessThan(1.5);
    }
}
