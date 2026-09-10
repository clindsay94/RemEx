using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using FluentAssertions;
using Xunit;

namespace Remex.Desktop.Tests.Views;

/// <summary>
/// Guards the fix for RemEx-1ufoa.2: the dual-metric legend plate on the Sensors canvas card is
/// the per-sensor <c>Sensor.Theme.CardBackground</c> (dark by default) at translucent alpha, so
/// its text must follow the sensor theme (<c>Sensor.Theme.UnitColor</c>) rather than a palette
/// text brush — a <c>DynamicResource</c> text brush is dark-on-dark on a light seed.
/// </summary>
/// <remarks>
/// No headless render exists for this suite, so these are source-scanning guards: a palette text
/// brush on a themed plate compiles and renders something, silently, with no test failure
/// pointing back at the cause unless the source itself is asserted on.
/// </remarks>
public class CanvasLegendContrastTests
{
    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled);

    private static readonly Regex UnitColorForeground = new(
        @"^\{Binding\s+Sensor\.Theme\.UnitColor,\s*Converter=\{x:Static\s+conv:HexToBrushConverter\.Instance\}\}$",
        RegexOptions.Compiled);

    // "Sensor.*" here deliberately admits both Sensor.Theme.* (ValueColor/UnitColor) and the
    // sibling-sensor accent path (Sensor.SecondaryAccentHex) the secondary value badge already
    // used before this fix — both are per-sensor colours, not the app palette. What this must
    // reject is a DynamicResource palette text brush.
    private static readonly Regex ThemeForeground = new(
        @"^\{Binding\s+Sensor\.[A-Za-z.]+,\s*Converter=\{x:Static\s+conv:Hex(?:To)?[A-Za-z]*(?:Brush|Color)Converter\.Instance\}\}$",
        RegexOptions.Compiled);

    private static readonly Regex CardBackgroundPlate = new(
        @"^\{Binding\s+Sensor\.Theme\.CardBackground,\s*Converter=\{x:Static\s+conv:HexToTranslucentBrushConverter\.Instance\}",
        RegexOptions.Compiled);

    [Fact]
    public void DualMetricLegendTextBlocksBindForegroundFromSensorThemeUnitColor()
    {
        var doc = XDocument.Parse(ReadCanvasView());
        var ns = doc.Root!.GetDefaultNamespace();

        var legendBorders = doc.Descendants(ns + "Border")
            .Where(b => Normalize((string?)b.Attribute("IsVisible")) == "{Binding Sensor.IsDualMetric}")
            .ToArray();

        legendBorders.Should().ContainSingle(
            "exactly one dual-metric legend Border should exist on the Sensors card");

        var legend = legendBorders[0];
        var textBlocks = legend.Descendants(ns + "TextBlock").ToArray();

        textBlocks.Should().HaveCountGreaterOrEqualTo(2,
            "the legend must contain at least the primary and secondary name labels (anti-vacuity)");

        var offenders = textBlocks
            .Select(tb => Normalize((string?)tb.Attribute("Foreground")))
            .Where(fg => !UnitColorForeground.IsMatch(fg))
            .ToArray();

        offenders.Should().BeEmpty(
            $"every legend TextBlock must bind Foreground through Sensor.Theme.UnitColor + HexToBrushConverter, " +
            $"not a palette DynamicResource text brush. Offending Foreground values: {string.Join(" | ", offenders)}");
    }

    [Fact]
    public void EveryThemedPlateTextBlockBindsForegroundFromSensorThemeNotThePalette()
    {
        // Covers the value badges (:297, :316) and the legend (:332) together: any TextBlock whose
        // ancestor Border paints its Background from Sensor.Theme.CardBackground via
        // HexToTranslucentBrushConverter must colour its own text from Sensor.Theme.*, never a
        // palette DynamicResource brush.
        var text = ReadCanvasView();
        var doc = XDocument.Parse(text);
        var ns = doc.Root!.GetDefaultNamespace();

        var themedPlateTextBlocks = doc.Descendants(ns + "Border")
            .Where(b => CardBackgroundPlate.IsMatch(Normalize((string?)b.Attribute("Background"))))
            .SelectMany(b => b.Descendants(ns + "TextBlock"))
            .ToArray();

        themedPlateTextBlocks.Should().NotBeEmpty(
            "at least the value badges and legend TextBlocks should sit on a Sensor.Theme.CardBackground plate");

        var offenders = themedPlateTextBlocks
            .Select(tb => Normalize((string?)tb.Attribute("Foreground")))
            .Where(fg => !ThemeForeground.IsMatch(fg))
            .ToArray();

        offenders.Should().BeEmpty(
            $"every TextBlock on a Sensor.Theme.CardBackground plate must colour its text from Sensor.Theme.*, " +
            $"never a palette DynamicResource brush. Offending Foreground values: {string.Join(" | ", offenders)}");
    }

    private static string Normalize(string? value)
        => Whitespace.Replace(value ?? string.Empty, " ").Trim();

    private static string ReadCanvasView()
        => File.ReadAllText(Path.Combine(RepoRoot(), "remex.desktop", "Views", "CanvasView.axaml"));

    private static string RepoRoot([CallerFilePath] string thisSourceFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisSourceFile)!, "..", ".."));
}
