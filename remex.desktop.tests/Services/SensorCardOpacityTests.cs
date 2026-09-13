using System;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace Remex.Desktop.Tests.Services;

/// <summary>
/// Spec B §3 (RemEx-4kv0g.3.1): <c>GlassOpacity</c> reaches 0 for the sensor-card body brushes
/// while <c>CardBackgroundBrush</c> — the popup/dropdown floor's neighbour — keeps its 5% floor.
/// </summary>
/// <remarks>
/// SAME SEAM AS <c>PopupOpacityFloorTests</c> / <c>ThemeKeyCoverageTests</c>:
/// <c>ThemeService.ApplyCustomization</c> needs a live Avalonia <c>Application</c> to run, and
/// there is none in a unit test, so the arithmetic is pinned by reading the literal clamp bounds
/// out of the source and re-running the same formula here, rather than by constructing a
/// <c>ThemeService</c> and reading a resource back.
/// </remarks>
public class SensorCardOpacityTests
{
    [Fact]
    public void GlassOpacityZeroClearsTheCardBodyButNotThePopupFloor()
    {
        var source = File.ReadAllText(Path.Combine(RepoRoot(), "remex.desktop", "Services", "ThemeService.cs"));

        // CardBackgroundBrush's own alpha (cardAlpha) must still floor at 5% -- untouched by this change.
        var cardAlphaMatch = Regex.Match(source,
            @"byte cardAlpha = \(byte\)Math\.Round\(Math\.Clamp\(settings\.GlassOpacity,\s*([\d.]+),\s*([\d.]+)\)\s*\*\s*255\)");
        cardAlphaMatch.Success.Should().BeTrue("CardBackgroundBrush's alpha computation must still exist");
        double.Parse(cardAlphaMatch.Groups[1].Value, CultureInfo.InvariantCulture).Should().Be(0.05,
            "the popup/card-background floor (spec B §3) is untouched by this change");

        var cardBackgroundAlphaAtZero = (byte)Math.Round(Math.Clamp(0.0, 0.05, 1.0) * 255);
        cardBackgroundAlphaAtZero.Should().Be(13, "CardBackgroundBrush must hold its floor when GlassOpacity is 0");

        // The new per-family sensor-card body alpha (cardBodyAlpha) clamps 0..1, so it reaches fully clear.
        var cardBodyAlphaMatch = Regex.Match(source,
            @"var cardBodyAlpha = \(byte\)Math\.Round\(Math\.Clamp\(settings\.GlassOpacity,\s*([\d.]+),\s*([\d.]+)\)\s*\*\s*255\)");
        cardBodyAlphaMatch.Success.Should().BeTrue("the sensor-card body alpha computation must exist");
        double.Parse(cardBodyAlphaMatch.Groups[1].Value, CultureInfo.InvariantCulture).Should().Be(0.0,
            "sensor-card bodies must be able to reach fully clear (spec B §3)");

        var cardBodyAlphaAtZero = (byte)Math.Round(Math.Clamp(0.0, 0.0, 1.0) * 255);
        cardBodyAlphaAtZero.Should().Be(0, "CardBodyPrimaryBrush (and the other three families) must be fully clear at GlassOpacity 0");

        source.Should().Contain("SetResourceOverrideInternal(\"CardBodyPrimaryBrush\"",
            "CardBodyPrimaryBrush must actually be published from cardBodyAlpha");
    }

    private static string RepoRoot([CallerFilePath] string thisSourceFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisSourceFile)!, "..", ".."));
}
