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
/// every blob half again as large as ITS OWN former self and visibly bolder than the old
/// Wallpaper-named mesh, the big/medium/small hierarchy intact, colours from the seed-derived
/// Aurora resources, and reduced motion FREEZING the mesh at its first keyframe rather than
/// hiding it. Source-text, because this test project has no headless render.
/// </summary>
public class AuroraMeshTests
{
    private const string Avalonia = "https://github.com/avaloniaui";

    // The old mesh's numbers, so "up by half" is a measurement against something. PER LAYER, not
    // against the old largest blob: the spec asks each blob to be half again as large as ITS OWN
    // former self, which is also what keeps the big/medium/small hierarchy intact. Measuring every
    // layer against the old maximum let AuroraLayer3 be inflated to near-full-window and still pass.
    private static readonly (double X, double Y) OldLayer1Radius = (0.65, 0.75);
    private static readonly (double X, double Y) OldLayer2Radius = (0.70, 0.60);
    private static readonly (double X, double Y) OldLayer3Radius = (0.55, 0.45);

    private static (string Name, (double X, double Y) OldRadius)[] OldRadiiInDocumentOrder() => new[]
    {
        ("AuroraLayer1", OldLayer1Radius),
        ("AuroraLayer2", OldLayer2Radius),
        ("AuroraLayer3", OldLayer3Radius),
    };

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

        var expected = OldRadiiInDocumentOrder();
        layers.Select(l => l.Attribute("Name")!.Value).Should().Equal(expected.Select(e => e.Name),
            "each layer is paired with its own former radius by x:Name in document order");

        foreach (var (layer, (name, old)) in layers.Zip(expected))
        {
            double.Parse(RadiusAttribute(layer, "RadiusX"), CultureInfo.InvariantCulture)
                .Should().BeGreaterOrEqualTo(old.X * 1.5 * 0.9,
                    $"{name}'s blob radius is up by half, measured against that layer's own former radius");
            double.Parse(RadiusAttribute(layer, "RadiusY"), CultureInfo.InvariantCulture)
                .Should().BeGreaterOrEqualTo(old.Y * 1.5 * 0.8,
                    $"{name}'s blob radius is up by half, measured against that layer's own former radius");
        }
    }

    /// <summary>
    /// Growing every blob is not the same as growing them into each other. The first cut of the
    /// growth test above measured all three against the OLD LARGEST blob, so inflating AuroraLayer3
    /// to near-full-window passed it — and flattened the mesh into three overlapping discs of the
    /// same size instead of the big/medium/small stack the spec draws. Nothing renders in this
    /// assembly, so the ordering has to be pinned in the numbers themselves.
    /// </summary>
    [Fact]
    public void TheThreeBlobsKeepTheirBigMediumSmallHierarchy()
    {
        // Both axes: a blob can be "smaller" on X and still swallow the others on Y.
        foreach (var axis in new[] { "RadiusX", "RadiusY" })
        {
            var radii = Layers().ToDictionary(
                l => l.Attribute("Name")!.Value,
                l => double.Parse(RadiusAttribute(l, axis), CultureInfo.InvariantCulture));

            radii["AuroraLayer3"].Should().BeLessThan(radii["AuroraLayer1"],
                $"the third blob is the small one on {axis}; equal-sized blobs read as one flat wash, not a mesh");
            radii["AuroraLayer3"].Should().BeLessThan(radii["AuroraLayer2"],
                $"the third blob is the small one on {axis}; equal-sized blobs read as one flat wash, not a mesh");
        }
    }

    private static string RadiusAttribute(XElement layer, string attribute) =>
        layer.Descendants(XName.Get("RadialGradientBrush", Avalonia)).Single().Attribute(attribute)!.Value;

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
