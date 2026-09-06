using Avalonia.Media;
using FluentAssertions;
using Remex.Desktop.ViewModels;
using Xunit;

namespace Remex.Desktop.Tests.ViewModels;

/// <summary>
/// Guards <see cref="SensorViewModel.SecondaryAccentHex"/>'s no-secondary-sensor fallback
/// (RemEx-qljv).
/// </summary>
/// <remarks>
/// CanvasView binds this property directly onto <c>SparklineControl.SecondaryAccentColor</c>, and a
/// Binding always produces a value — so the App.axaml Style that gives SparklineControl its own
/// theme-derived default never gets a chance to run there; a local value always outranks a Style
/// setter. The old hardcoded <c>"#FFB020"</c> literal would therefore have survived the rest of this
/// bead's fix untouched, one layer up. The fallback must go through <c>ThemeResources</c> instead, so
/// a live theme's <c>PaletteTertiary</c> wins here too whenever one is available.
/// </remarks>
public class SensorViewModelSecondaryAccentTests
{
    [Fact]
    public void FallsBackThroughThemeResourcesRatherThanTheOldLiteral()
    {
        var sensor = new SensorViewModel();

        // No Avalonia Application in a unit test, so ThemeResources.Color degrades to its own
        // fallback colour — but the point of this test is that it goes THROUGH that lookup rather
        // than bypassing it with the bare literal. The numeric value is unchanged on purpose.
        Color.Parse(sensor.SecondaryAccentHex).Should().Be(Color.Parse("#FFB020"),
            "the fallback colour itself is unchanged; only the path that produces it must go "
            + "through ThemeResources so a live theme's PaletteTertiary wins when one is available");
    }
}
