using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Xml.Linq;
using FluentAssertions;
using Xunit;

namespace Remex.Desktop.Tests.Controls;

/// <summary>
/// The Aurora mesh in <c>DashboardBackgroundControl.axaml</c> (RemEx-ddynd): its own mode value,
/// blobs half again as large and visibly bolder than the old Wallpaper-named mesh, colours from
/// the seed-derived Aurora resources, and reduced motion FREEZING the mesh at its first keyframe
/// rather than hiding it. Source-text, because this test project has no headless render.
/// </summary>
public class AuroraMeshTests
{
    private const string Avalonia = "https://github.com/avaloniaui";

    // The old mesh's numbers, so "up by half" is a measurement against something.
    private const double OldMaxRadiusX = 0.70, OldMaxRadiusY = 0.75;
    private const double OldPeakOpacityLayer1 = 0.70, OldPeakOpacityLayer2 = 0.55, OldPeakOpacityLayer3 = 0.42;

    private static XElement AuroraPanel()
    {
        var doc = XDocument.Load(ControlPath());
        return doc.Descendants(XName.Get("Panel", Avalonia))
            .Single(p => (p.Attribute("IsVisible")?.Value ?? "").Contains("StringMatchConverter.IsAurora"));
    }

    private static XElement[] Layers() => AuroraPanel()
        .Elements(XName.Get("Rectangle", Avalonia))
        .Where(r => (r.Attribute("Name")?.Value ?? "").StartsWith("AuroraLayer", StringComparison.Ordinal))
        .ToArray();

    [Fact]
    public void TheMeshIsItsOwnModeAndNoLongerAnswersToWallpaper()
    {
        var text = File.ReadAllText(ControlPath());
        text.Should().Contain("StringMatchConverter.IsAurora");
        AuroraPanel().ToString().Should().NotContain("IsWallpaper",
            "Wallpaper is the real desktop wallpaper now (Task 5); the mesh is Aurora");
    }

    [Fact]
    public void ThreeLayersEachHalfAgainAsLargeAsBefore()
    {
        var layers = Layers();
        layers.Should().HaveCount(3);

        foreach (var layer in layers)
        {
            var brush = layer.Descendants(XName.Get("RadialGradientBrush", Avalonia)).Single();
            double.Parse(brush.Attribute("RadiusX")!.Value, CultureInfo.InvariantCulture)
                .Should().BeGreaterOrEqualTo(OldMaxRadiusX * 1.5 * 0.9,
                    "the spec asks for blob radius up by half, measured against the old largest blob");
            double.Parse(brush.Attribute("RadiusY")!.Value, CultureInfo.InvariantCulture)
                .Should().BeGreaterOrEqualTo(OldMaxRadiusY * 1.5 * 0.8);
        }
    }

    [Fact]
    public void PeakOpacitiesReadOnADarkSurfaceAtAGlance()
    {
        var peaks = Layers().Select(l => l.Descendants(XName.Get("Setter", Avalonia))
            .Where(s => s.Attribute("Property")?.Value == "Opacity")
            .Max(s => double.Parse(s.Attribute("Value")!.Value, CultureInfo.InvariantCulture))).ToArray();

        peaks[0].Should().BeGreaterThan(OldPeakOpacityLayer1);
        peaks[1].Should().BeGreaterThan(OldPeakOpacityLayer2);
        peaks[2].Should().BeGreaterThan(OldPeakOpacityLayer3);
        peaks.Should().OnlyContain(p => p <= 1.0);
    }

    [Fact]
    public void ColoursComeFromTheAuroraResourcesAndEndOnTheSurface()
    {
        var stops = AuroraPanel().Descendants(XName.Get("GradientStop", Avalonia)).Select(s => s.Attribute("Color")!.Value).ToArray();

        stops.Should().Contain(s => s.Contains("AuroraPrimary"))
            .And.Contain(s => s.Contains("AuroraSecondary"))
            .And.Contain(s => s.Contains("AuroraTertiary"));
        stops.Where(s => s.Contains("GlassBaseDark")).Should().HaveCount(3,
            "each blob fades to the surface so the glow ends invisibly against the base rectangle");
        stops.Should().NotContain(s => s.Contains("AccentPrimary") || s.Contains("AccentHover") || s.Contains("AccentPressed"),
            "the mesh no longer borrows the chrome accents; it has its own low/high-tone set");
    }

    [Fact]
    public void ReducedMotionFreezesTheMeshAtItsFirstKeyframeInsteadOfHidingIt()
    {
        foreach (var layer in Layers())
        {
            layer.Attribute("IsVisible").Should().BeNull("the layers stay visible under reduced motion; only the animation stops");
            (layer.Attribute("Classes.aurora-animated")?.Value ?? "").Should().Contain("!IsReducedMotion",
                "the animation is gated by a class bound to the inverse of the reduced-motion flag");

            var staticOpacity = double.Parse(layer.Attribute("Opacity")!.Value, CultureInfo.InvariantCulture);
            var firstKeyframe = layer.Descendants(XName.Get("KeyFrame", Avalonia)).First()
                .Elements(XName.Get("Setter", Avalonia)).Single(s => s.Attribute("Property")?.Value == "Opacity");
            double.Parse(firstKeyframe.Attribute("Value")!.Value, CultureInfo.InvariantCulture)
                .Should().Be(staticOpacity, "frozen means 'at the first keyframe', so the static value must equal it");

            layer.Descendants(XName.Get("Style", Avalonia)).Single().Attribute("Selector")!.Value
                .Should().Contain(".aurora-animated", "an ungated style would animate through reduced motion");
        }
    }

    private static string ControlPath() =>
        Path.Combine(RepoRoot(), "remex.desktop", "Controls", "DashboardBackgroundControl.axaml");

    private static string RepoRoot([CallerFilePath] string thisSourceFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisSourceFile)!, "..", ".."));
}
