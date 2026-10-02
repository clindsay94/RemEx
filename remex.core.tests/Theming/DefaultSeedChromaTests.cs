using Remex.Core.Models;
using Remex.Core.Theming.Mcu;

namespace Remex.Core.Tests.Theming;

/// <summary>
/// RemEx-pp4cm C8: a fresh PC profile paints RemEx's own colours, the same ones the phone's first run
/// paints. The default chroma must be the default seed's OWN chroma: the custom path rebuilds the seed
/// as <c>Hct.From(hue, chroma, tone)</c>, so any other value paints (and syncs) a different colour.
/// </summary>
public class DefaultSeedChromaTests
{
    private const uint DefaultSeed = 0xFF6C4CFF;

    [Fact]
    public void TheDefaultChromaIsTheDefaultSeedsOwnChromaRounded()
    {
        var seed = Hct.FromInt(DefaultSeed);

        Assert.Equal(CustomizationSettings.DefaultSeedChroma, Math.Round(seed.Chroma, 1));
    }

    [Fact]
    public void TheDefaultChromaRoundTripsToTheDefaultSeed()
    {
        var seed = Hct.FromInt(DefaultSeed);

        var rebuilt = Hct.From(seed.Hue, CustomizationSettings.DefaultSeedChroma, seed.Tone);

        Assert.Equal(DefaultSeed, rebuilt.ToInt());
    }

    [Fact]
    public void AFreshProfileStartsOnTheCustomSourceWithRemExsOwnSeed()
    {
        var fresh = new CustomizationSettings();

        Assert.Equal(ColorSources.Custom, fresh.ColorSource);
        Assert.Equal("#6C4CFF", fresh.AccentColor);
        Assert.Equal(CustomizationSettings.DefaultSeedChroma, fresh.ThemeSeedChroma);
        Assert.Equal(CustomizationSettings.DefaultSeedChroma, fresh.ThemeSeedChromaRequest);
    }
}
