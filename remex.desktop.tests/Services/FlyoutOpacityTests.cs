using System;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace Remex.Desktop.Tests.Services;

/// <summary>
/// Flyout toolbar drop 1 (RemEx-4kv0g.18.5), spec §3 / §6: <c>FlyoutOpacity</c> drives the tray
/// flyout popup's own glass (<c>FlyoutGlassBrush</c>) and nothing else — the cards keep the app's
/// own Card Opacity (<c>GlassOpacity</c>), unchanged at every popup opacity.
/// </summary>
/// <remarks>
/// SAME SEAM AS <c>SensorCardOpacityTests</c> / <c>ThemeKeyCoverageTests</c>:
/// <c>ThemeService.ApplyCustomization</c> needs a live Avalonia <c>Application</c> to run, and there
/// is none in a unit test, so the arithmetic is pinned by reading the literal clamp/formula text out
/// of the source and re-running it here, rather than by constructing a <c>ThemeService</c> and
/// reading a resource back.
/// </remarks>
public class FlyoutOpacityTests
{
    [Fact]
    public void FlyoutOpacityChangesOnlyTheFlyoutGlass()
    {
        var source = File.ReadAllText(Path.Combine(RepoRoot(), "remex.desktop", "Services", "ThemeService.cs"));

        var flyoutAlphaMatch = Regex.Match(source,
            @"byte flyoutAlpha = \(byte\)Math\.Round\(Math\.Clamp\(settings\.FlyoutOpacity,\s*([\d.]+),\s*([\d.]+)\)\s*\*\s*255\)");
        flyoutAlphaMatch.Success.Should().BeTrue("FlyoutGlassBrush's alpha computation must exist");
        double.Parse(flyoutAlphaMatch.Groups[1].Value, CultureInfo.InvariantCulture).Should().Be(0.0,
            "the popup glass must be able to reach fully clear, unlike the 5% card-background floor");
        double.Parse(flyoutAlphaMatch.Groups[2].Value, CultureInfo.InvariantCulture).Should().Be(1.0);

        source.Should().Contain("SetResourceOverrideInternal(\"FlyoutGlassBrush\"",
            "FlyoutGlassBrush must actually be published from flyoutAlpha");

        // Clamping: 0.3 is a representative in-range value, 1.5/-0.2 prove the clamp bites at both
        // ends (the settings' own clamp contract, mirrored wherever a setter reads FlyoutOpacity).
        foreach (var (requested, expectedFraction) in new (double Requested, double ExpectedFraction)[]
        {
            (0.3, 0.3),
            (1.5, 1.0),
            (-0.2, 0.0),
        })
        {
            var expectedAlpha = (byte)Math.Round(Math.Clamp(expectedFraction, 0.0, 1.0) * 255);
            var actualAlpha = (byte)Math.Round(Math.Clamp(requested, 0.0, 1.0) * 255);
            actualAlpha.Should().Be(expectedAlpha,
                $"FlyoutOpacity={requested} must clamp to {expectedFraction} before becoming alpha");
        }

        // THE ISOLATION ITSELF (docs/REGRESSION-GUARDS.md, "Tray flyout" entry): the card formulas
        // must key off GlassOpacity ONLY, never FlyoutOpacity, so CardBody*/CardBackgroundBrush stay
        // bit-identical at every popup opacity (SensorCardOpacityTests pins these same two formulas'
        // clamp bounds; this pins which setting feeds them).
        var cardAlphaMatch = Regex.Match(source,
            @"byte cardAlpha = \(byte\)Math\.Round\(Math\.Clamp\(settings\.(\w+),");
        cardAlphaMatch.Success.Should().BeTrue("CardBackgroundBrush's alpha computation must exist");
        cardAlphaMatch.Groups[1].Value.Should().Be("GlassOpacity",
            "CardBackgroundBrush's alpha must never read FlyoutOpacity");

        var cardBodyAlphaMatch = Regex.Match(source,
            @"var cardBodyAlpha = \(byte\)Math\.Round\(Math\.Clamp\(settings\.(\w+),");
        cardBodyAlphaMatch.Success.Should().BeTrue("the per-family sensor-card body alpha computation must exist");
        cardBodyAlphaMatch.Groups[1].Value.Should().Be("GlassOpacity",
            "the per-family sensor-card body alpha must never read FlyoutOpacity");
    }

    private static string RepoRoot([CallerFilePath] string thisSourceFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisSourceFile)!, "..", ".."));
}
