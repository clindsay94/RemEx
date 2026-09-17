using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Xml.Linq;
using FluentAssertions;
using Xunit;

namespace Remex.Desktop.Tests.Controls;

/// <summary>
/// Guards the translucency of the Acrylic / Glass overlay tints in
/// DashboardBackgroundControl (RemEx-c437b).
/// </summary>
/// <remarks>
/// <para>
/// The Acrylic and Glass canvas modes sit over a TRANSPARENT window whose real surface is
/// the OS backdrop — the overlay panels only tint it. GlassBaseDarkBrush is OPAQUE (ThemeService
/// overrides it with the HCT-solved Surface at full alpha, and 26 files consume it), so a bare
/// Fill here is a solid sheet: the backdrop vanishes with no exception, no log line, and no
/// headless render in this suite to catch it. Every tint therefore carries an explicit Opacity,
/// and this test pins them from the source.
/// </para>
/// <para>
/// THE VALUES ARE LOW ON PURPOSE, AND THAT IS THE SECOND HALF OF A REAL BUG. They were first set
/// to 0.63 / 0.5 / 0.16 to reproduce the effective opacity these panels shipped with back when
/// GlassBaseDark itself was <c>#A00A0A10</c>. That preserved the old look, but the old look was
/// measured while the backdrop was ALREADY dead for an unrelated reason — Material.Avalonia's
/// window-decorations underlay was covering it (see WindowChromeBackdropTests and
/// Themes/Chrome/WindowChrome.axaml). With the underlay cleared the backdrop genuinely composites, and a
/// 0.63 veil over Avalonia's already-dark Mica brush brings the wallpaper contribution back to
/// ~1/255 — alive and invisible, which is indistinguishable from the bug the user reported.
/// </para>
/// <para>
/// Do not raise these to "restore" the older, heavier look without re-measuring; that is the exact
/// change that made the feature look broken. Acrylic IS now bound to GlassOpacity
/// (RemEx-mmrgc) — but by SCALING, not raw binding: effective veil = GlassOpacity × ceiling
/// (0.30 / 0.25), via MultiplyConverter. That preserves this same ceiling as a maximum, so
/// "Frosted" (GlassOpacity = 1.0) reproduces exactly the old fixed value instead of going opaque;
/// "Clear" (0.01) leaves the veil almost fully transparent. A raw binding to GlassOpacity would
/// have broken the "Frosted = fully opaque veil, no backdrop at all" case this remark used to warn
/// against — scaling is what avoids that. Glass mode's tint stays a fixed literal, unscaled.
/// </para>
/// <para>
/// LIGHT MODE IS THE ONE EXCEPTION, AND IT GOES THE OTHER WAY (RemEx-4kv0g.5.1). GlassBaseDark
/// is the solved Surface, so in a Light palette the veil is a light sheet — and at 0.25 it is far
/// too thin to lift a dark wallpaper or backdrop, leaving the light palette's dark onSurface ink
/// on a dark ground on every view. The Wallpaper and Acrylic veils therefore bind through
/// VeilOpacityConverter, which multiplies exactly as MultiplyConverter did and then, only when the
/// rectangle's ActualThemeVariant is Light, raises the result to VeilOpacityConverter.LightFloor.
/// Dark's numbers are unchanged; the converter's own tests pin both halves. Mica is exempt: DWM
/// paints it light under a light theme already, and light Mica is faint enough that the floor
/// would erase it (RemEx-kir7r) — its test below pins the plain MultiplyConverter shape.
/// </para>
/// </remarks>
public class DashboardBackdropTintTests
{
    private const string Avalonia = "https://github.com/avaloniaui";

    [Theory]
    [InlineData("IsGlass", 0.16)]
    public void TheBackdropTint_StaysTranslucent(string modeConverter, double expectedOpacity)
    {
        var modePanel = FindModePanel(modeConverter);

        var tintRectangles = modePanel
            .Elements(XName.Get("Rectangle", Avalonia))
            .Where(rect => (rect.Attribute("Fill")?.Value ?? string.Empty)
                .Contains("GlassBaseDarkBrush"))
            .ToList();

        tintRectangles.Should().ContainSingle(
            "the mode's base tint is the one GlassBaseDarkBrush rectangle in its panel");

        var opacityAttribute = tintRectangles[0].Attribute("Opacity");
        opacityAttribute.Should().NotBeNull(
            "a bare Fill inherits the brush's FULL alpha — GlassBaseDarkBrush is overridden by " +
            "ThemeService with an opaque solved Surface, so without an explicit Opacity this " +
            "tint is a solid sheet and the OS backdrop behind the window disappears");

        double.Parse(opacityAttribute!.Value, System.Globalization.CultureInfo.InvariantCulture)
            .Should().BeApproximately(expectedOpacity, 0.001,
                "the tint has to stay light enough for the OS backdrop underneath to actually " +
                "register — a heavier veil is alive and invisible, which reads as broken")
            ;
    }

    [Fact]
    public void TheMicaTint_ScalesWithGlassOpacity_AndIsExemptFromTheLightFloor()
    {
        // Mica keeps the plain MultiplyConverter binding: DWM paints Mica light or dark to match
        // the theme (MicaBackdrop.TryApply sets DWMWA_USE_IMMERSIVE_DARK_MODE), so a light palette
        // already sits on a light backdrop and needs no floor — and light Mica is such a faint
        // wash that VeilOpacityConverter's 0.55 floor would erase it (RemEx-kir7r).
        var modePanel = FindModePanel("IsMica");

        var tintRectangles = modePanel
            .Elements(XName.Get("Rectangle", Avalonia))
            .Where(rect => (rect.Attribute("Fill")?.Value ?? string.Empty)
                .Contains("GlassBaseDarkBrush"))
            .ToList();

        tintRectangles.Should().ContainSingle(
            "the mode's base tint is the one GlassBaseDarkBrush rectangle in its panel");

        var opacityAttribute = tintRectangles[0].Attribute("Opacity");
        opacityAttribute.Should().NotBeNull(
            "the veil must bind to something — an omitted Opacity inherits full alpha from the " +
            "opaque GlassBaseDarkBrush and the OS backdrop disappears");

        var opacityValue = opacityAttribute!.Value;
        opacityValue.Should().Contain("Customization.GlassOpacity",
            "the veil scales with the Card Opacity slider (RemEx-mmrgc)");
        opacityValue.Should().Contain("MultiplyConverter.Instance",
            "scaling — not a raw binding — is what keeps Frosted (1.0) from going fully opaque; " +
            "and MultiplyConverter, not VeilOpacityConverter, is what keeps Mica floor-free");
        opacityValue.Should().NotContain("VeilOpacityConverter",
            "Mica is exempt from the Light floor — see the panel comment");
        opacityValue.Should().Contain("FallbackValue=0",
            "an unresolved binding skips the converter and leaves Opacity at its default of 1.0 — " +
            "an opaque sheet — so the fallback has to be 'no veil', not 'full veil'");
        opacityValue.Should().Contain("TargetNullValue=0",
            "a null source value must also fall to 'no veil' rather than the property default");

        var parameterMatch = System.Text.RegularExpressions.Regex.Match(
            opacityValue, @"ConverterParameter=(?<value>[0-9.]+)");
        parameterMatch.Success.Should().BeTrue(
            "the ConverterParameter carries the veil's ceiling — it must be present and numeric");

        double.Parse(parameterMatch.Groups["value"].Value, System.Globalization.CultureInfo.InvariantCulture)
            .Should().BeApproximately(0.25, 0.001,
                "the ceiling is what Frosted (GlassOpacity = 1.0) reproduces — it must match the " +
                "measured maximum veil, not drift from it");
    }

    [Theory]
    [InlineData("IsAcrylic", "Customization.GlassOpacity", 0.25)]
    [InlineData("IsWallpaper", "Customization.AppWindowOpacity", 1.0)]
    public void TheBackdropTint_ScalesWithTheKnobAndFloorsInLight(
        string modeConverter, string knobPath, double expectedCeiling)
    {
        var modePanel = FindModePanel(modeConverter);

        var tintRectangles = modePanel
            .Elements(XName.Get("Rectangle", Avalonia))
            .Where(rect => (rect.Attribute("Fill")?.Value ?? string.Empty)
                .Contains("GlassBaseDarkBrush"))
            .ToList();

        tintRectangles.Should().ContainSingle(
            "the mode's base tint is the one GlassBaseDarkBrush rectangle in its panel");

        var tint = tintRectangles[0];
        tint.Attribute("Opacity").Should().BeNull(
            "the veil's Opacity is a MultiBinding (knob + ActualThemeVariant) in property-element " +
            "form — an Opacity attribute here would be a second, competing value");

        var multiBinding = tint
            .Elements(XName.Get("Rectangle.Opacity", Avalonia))
            .Elements(XName.Get("MultiBinding", Avalonia))
            .Should().ContainSingle(
                "the veil must bind to something — an omitted Opacity inherits full alpha from the " +
                "opaque GlassBaseDarkBrush and the OS backdrop disappears")
            .Subject;

        (multiBinding.Attribute("Converter")?.Value ?? string.Empty)
            .Should().Contain("VeilOpacityConverter.Instance",
                "VeilOpacityConverter is MultiplyConverter's scaling plus the Light-mode floor " +
                "(RemEx-4kv0g.5.1) — scaling, not a raw binding, keeps Frosted (1.0) from going " +
                "fully opaque in Dark, and the converter itself returns 'no veil' for an " +
                "unresolved knob rather than leaving Opacity at its default of 1.0");

        var bindingPaths = multiBinding
            .Elements(XName.Get("Binding", Avalonia))
            .Select(b => b.Attribute("Path")?.Value ?? string.Empty)
            .ToList();

        bindingPaths.Should().HaveCount(2,
            "the converter reads exactly [0] the opacity knob and [1] the theme variant");
        bindingPaths[0].Should().Be(knobPath,
            "input [0] is the user's opacity knob for this mode (RemEx-mmrgc / RemEx-ddynd)");
        bindingPaths[1].Should().Be("$self.ActualThemeVariant",
            "input [1] is the rectangle's own resolved theme variant — ThemeService sets " +
            "Application.RequestedThemeVariant from ResolveIsLight, so this is what makes the " +
            "floor apply in Light and re-evaluate on a Light/Dark switch without a VM property");

        var parameter = multiBinding.Attribute("ConverterParameter")?.Value;
        parameter.Should().NotBeNullOrEmpty(
            "the ConverterParameter carries the veil's ceiling — it must be present and numeric");

        double.Parse(parameter!, System.Globalization.CultureInfo.InvariantCulture)
            .Should().BeApproximately(expectedCeiling, 0.001,
                "the ceiling is what Frosted (knob = 1.0) reproduces in Dark — it must match the " +
                "measured maximum veil, not drift from it");
    }

    [Fact]
    public void TheMicaPanel_HasNoAccentTintAndNoWallpaperImage()
    {
        // Mica supplies the colour itself (DWM paints it, MicaBackdrop.cs) - this panel is only
        // the shared GlassOpacity veil pinned above. An accent tint or a wallpaper Image here
        // would be re-tinting or duplicating what DWM already draws.
        var modePanel = FindModePanel("IsMica");

        modePanel.Elements(XName.Get("Image", Avalonia)).Should().BeEmpty(
            "Mica's imagery comes from the DWM backdrop, not an Image element");

        var accentRectangles = modePanel
            .Elements(XName.Get("Rectangle", Avalonia))
            .Where(rect => (rect.Attribute("Fill")?.Value ?? string.Empty).Contains("AccentPrimary")
                || rect.Descendants(XName.Get("SolidColorBrush", Avalonia))
                    .Any(brush => (brush.Attribute("Color")?.Value ?? string.Empty).Contains("AccentPrimary")))
            .ToList();

        accentRectangles.Should().BeEmpty(
            "Mica must not be re-tinted with an accent colour on top of what DWM already paints");
    }

    private static XElement FindModePanel(string modeConverter)
    {
        var control = XDocument.Parse(File.ReadAllText(Path.Combine(
            RepoRoot(), "remex.desktop", "Controls", "DashboardBackgroundControl.axaml")));

        return control
            .Descendants(XName.Get("Panel", Avalonia))
            .Where(panel => (panel.Attribute("IsVisible")?.Value ?? string.Empty)
                .Contains("StringMatchConverter." + modeConverter))
            .Should().ContainSingle($"exactly one overlay panel should own the {modeConverter} mode")
            .Subject;
    }

    private static string RepoRoot([CallerFilePath] string thisSourceFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisSourceFile)!, "..", ".."));
}
