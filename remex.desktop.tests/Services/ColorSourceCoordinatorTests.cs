using Avalonia.Media;
using FluentAssertions;
using Remex.Core.Models;
using Remex.Desktop.Services;
using Xunit;

namespace Remex.Desktop.Tests.Services;

/// <summary>
/// The pure half of the coordinator: a source colour supplies hue and tone, the profile's own
/// vibrancy supplies chroma, so the Vibrancy slider keeps shaping a seed the person cannot edit.
/// </summary>
public class ColorSourceCoordinatorTests
{
    [Fact]
    public void ShapedBySource_TakesHueAndToneFromTheSourceAndChromaFromTheProfile()
    {
        var settings = new CustomizationSettings { ThemeSeedChroma = 20.0, AccentColor = "#6C4CFF" };

        var shaped = ColorSourceCoordinator.ShapedBySource(settings, "#0078D4");

        var (sourceHue, _, sourceTone) = SeedHct.FromColor(Color.Parse("#0078D4"));
        var (hue, chroma, tone) = SeedHct.FromColor(Color.Parse(shaped.AccentColor));
        hue.Should().BeApproximately(sourceHue, 2.0);
        tone.Should().BeApproximately(sourceTone, 2.0);
        chroma.Should().BeLessOrEqualTo(21.0, "the profile's vibrancy, not the source's chroma, shapes the seed");
        shaped.ThemeSeedChroma.Should().BeApproximately(chroma, 0.01, "what was achieved is what is persisted (RemEx-ndhlv)");
    }

    [Fact]
    public void ShapedBySource_LeavesEveryOtherFieldAlone()
    {
        var settings = DashboardLayoutClobberTests.BuildNonDefaultSettings(CustomizationMigration.CurrentSchemaVersion);

        var shaped = ColorSourceCoordinator.ShapedBySource(settings, "#0078D4");

        shaped.Should().BeEquivalentTo(settings, o => o.Excluding(s => s.AccentColor).Excluding(s => s.ThemeSeedChroma));
    }

    [Fact]
    public void ShapedBySource_ReturnsTheSameInstanceForAnUnparseableSource()
    {
        var settings = new CustomizationSettings();

        ColorSourceCoordinator.ShapedBySource(settings, "#FF0O00").Should().BeSameAs(settings);
    }
}
