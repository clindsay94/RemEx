using System.IO;
using System.Runtime.CompilerServices;
using FluentAssertions;
using Xunit;

namespace Remex.Desktop.Tests.Views;

/// <summary>
/// Guards the fix for RemEx-1ufoa.2, carried forward into Spec B's family styling
/// (RemEx-4kv0g.3.2). The dual-metric legend plate on the Sensors canvas card is, for an
/// overridden card, the per-sensor <c>Sensor.Theme.CardBackground</c> (dark by default) at
/// translucent alpha — so an overridden card's legend/value/title text must still bind through
/// <c>Sensor.Theme.*</c>, not a generic palette brush that could be dark-on-dark on a light seed.
/// </summary>
/// <remarks>
/// <para>
/// THE BINDINGS THIS USED TO SCAN MOVED. Before Spec B every sensor card was hex-themed and these
/// bindings lived inline on CanvasView.axaml TextBlocks; RemEx-4kv0g.3.2 moved every per-theme
/// colour off that file entirely (see <c>SensorCardBindingsTests</c>, which now guards the
/// opposite direction — that CanvasView.axaml carries NONE of them) and into
/// <c>Styles/SensorCardFamilies.axaml</c>'s ".family-custom" rules, scoped by host
/// (canvas bindings go through "Sensor.", Home/tray bind the sensor directly). So this file scans
/// SensorCardFamilies.axaml instead.
/// </para>
/// <para>
/// A THEMED (family-following) CARD IS NOT THE SAME RISK. Its plate is now
/// <c>CardPlate{F}Brush</c> and its text <c>CardInkDim{F}Brush</c> — both DynamicResources, but
/// Material's "on&lt;F&gt;Container" role is paired for contrast with "&lt;F&gt;Container" BY
/// CONSTRUCTION, which is exactly the guarantee RemEx-1ufoa.2's own hex-based fix had to build by
/// hand. So this file only re-asserts the override (family-custom) path, which is still hex-based
/// and still needs the original guard.
/// </para>
/// </remarks>
public class CanvasLegendContrastTests
{
    [Fact]
    public void OverriddenCardLegendNames_BindForegroundFromSensorThemeUnitColor()
    {
        var styles = ReadSensorCardFamilies();

        styles.Should().Contain(
            @"Property=""Foreground"" Value=""{Binding Sensor.Theme.UnitColor, Converter={x:Static conv:HexToBrushConverter.Instance}}""",
            "an overridden card's legend names (and unit text) must bind Foreground through " +
            "Sensor.Theme.UnitColor + HexToBrushConverter, not a palette DynamicResource text brush");
    }

    [Fact]
    public void OverriddenCardTitleAndValueText_BindFromSensorThemeNotThePalette()
    {
        var styles = ReadSensorCardFamilies();

        styles.Should().Contain(
            @"Property=""Foreground"" Value=""{Binding Sensor.Theme.LabelColor, Converter={x:Static conv:HexToBrushConverter.Instance}}""",
            "an overridden card's title must bind Foreground through Sensor.Theme.LabelColor, never a palette brush");
        styles.Should().Contain(
            @"Property=""Foreground"" Value=""{Binding Sensor.Theme.ValueColor, Converter={x:Static conv:HexToBrushConverter.Instance}}""",
            "an overridden card's value text must bind Foreground through Sensor.Theme.ValueColor, never a palette brush");
    }

    [Fact]
    public void OverriddenCardLegendPlate_StaysOnSensorThemeCardBackground()
    {
        var styles = ReadSensorCardFamilies();

        styles.Should().Contain("Sensor.Theme.CardBackground, Converter={x:Static conv:HexToTranslucentBrushConverter.Instance}, ConverterParameter=0.78",
            "the legend/value plate on an overridden card is still the per-sensor CardBackground hex at translucent alpha");
    }

    private static string ReadSensorCardFamilies()
        => File.ReadAllText(Path.Combine(RepoRoot(), "remex.desktop", "Styles", "SensorCardFamilies.axaml"));

    private static string RepoRoot([CallerFilePath] string thisSourceFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisSourceFile)!, "..", ".."));
}
