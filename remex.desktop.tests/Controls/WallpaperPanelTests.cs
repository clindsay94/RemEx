using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Xml.Linq;
using FluentAssertions;
using Xunit;

namespace Remex.Desktop.Tests.Controls;

/// <summary>The Wallpaper panel draws the bitmap, blurs it once, and veils it with the surface (spec section 6).</summary>
public class WallpaperPanelTests
{
    private const string Avalonia = "https://github.com/avaloniaui";

    private static XElement WallpaperPanel() => XDocument.Load(ControlPath())
        .Descendants(XName.Get("Panel", Avalonia))
        .Single(p => (p.Attribute("IsVisible")?.Value ?? "").Contains("StringMatchConverter.IsWallpaper"));

    [Fact]
    public void EveryModePanelFollowsTheEffectiveTypeSoAFailedWallpaperCanFallBackToSolid()
    {
        var panels = XDocument.Load(ControlPath()).Descendants()
            .Where(e => (e.Attribute("IsVisible")?.Value ?? "").Contains("StringMatchConverter.Is")).ToArray();

        panels.Should().NotBeEmpty();
        panels.Select(p => p.Attribute("IsVisible")!.Value)
            .Should().OnlyContain(v => v.Contains("{Binding EffectiveBackgroundType,"),
                "Customization.CanvasBackgroundType is the SETTING; EffectiveBackgroundType is what renders this session");
    }

    [Fact]
    public void TheImageIsBlurredThroughTheConverterAndVeiledAtTheWindowOpacity()
    {
        var panel = WallpaperPanel();
        var image = panel.Elements(XName.Get("Image", Avalonia)).Single();

        image.Attribute("Source")!.Value.Should().Be("{Binding WallpaperBitmap}");
        image.Attribute("Stretch")!.Value.Should().Be("UniformToFill", "stretched to fill");
        image.Attribute("Effect")!.Value.Should().Contain("WallpaperBlurRadius").And.Contain("BlurRadiusToEffectConverter");

        var veil = panel.Elements(XName.Get("Rectangle", Avalonia))
            .Single(r => (r.Attribute("Fill")?.Value ?? "").Contains("GlassBaseDarkBrush"));
        // The veil's Opacity is a MultiBinding (window opacity + ActualThemeVariant) through
        // VeilOpacityConverter at factor 1.0 - the window-opacity knob passes straight through in
        // Dark, and Light raises it to the floor (RemEx-4kv0g.5.1). DashboardBackdropTintTests
        // pins the converter and the variant input; this test only cares that the knob is the
        // window opacity.
        veil.Attribute("Opacity").Should().BeNull("the veil's Opacity is the MultiBinding element below");
        var veilBinding = veil
            .Elements(XName.Get("Rectangle.Opacity", Avalonia))
            .Elements(XName.Get("MultiBinding", Avalonia))
            .Single();
        veilBinding.Attribute("ConverterParameter")!.Value.Should().Be("1.0",
            "the window-opacity knob is used as-is, not scaled like the Acrylic/Mica ceilings");
        veilBinding.Elements(XName.Get("Binding", Avalonia)).First().Attribute("Path")!.Value
            .Should().Be("Customization.AppWindowOpacity",
                "the surface sits over the image at the window opacity so text keeps its contrast");
        panel.Descendants(XName.Get("Animation", Avalonia)).Should().BeEmpty(
            "nothing animates in this panel, so the blurred bitmap is not re-rendered per frame");
    }

    private static string ControlPath() =>
        Path.Combine(RepoRoot(), "remex.desktop", "Controls", "DashboardBackgroundControl.axaml");

    private static string RepoRoot([CallerFilePath] string thisSourceFile = "")
    {
        var dir = Path.GetDirectoryName(thisSourceFile)!;
        while (!File.Exists(Path.Combine(dir, "Remex.sln"))) dir = Path.GetDirectoryName(dir)!;
        return dir;
    }
}
