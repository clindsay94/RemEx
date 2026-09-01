using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Xml.Linq;
using FluentAssertions;
using Xunit;

namespace Remex.Desktop.Tests.Controls;

/// <summary>
/// Guards the translucency of the Mica / Acrylic / Glass overlay tints in
/// DashboardBackgroundControl (RemEx-c437b).
/// </summary>
/// <remarks>
/// <para>
/// The Mica, Acrylic and Glass canvas modes sit over a TRANSPARENT window whose real surface is
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
/// Measured 2026-08-26 after the fix: at 0.30 the Mica canvas shifts ~2.5/255 across a window
/// move over a varied wallpaper, and Acrylic at 0.25 is unmistakable.
/// </para>
/// <para>
/// Do not raise these to "restore" the older, heavier look without re-measuring; that is the exact
/// change that made the feature look broken. Mica and Acrylic ARE now bound to GlassOpacity
/// (RemEx-mmrgc) — but by SCALING, not raw binding: effective veil = GlassOpacity × ceiling
/// (0.30 / 0.25), via MultiplyConverter. That preserves this same ceiling as a maximum, so
/// "Frosted" (GlassOpacity = 1.0) reproduces exactly the old fixed value instead of going opaque;
/// "Clear" (0.01) leaves the veil almost fully transparent. A raw binding to GlassOpacity would
/// have broken the "Frosted = fully opaque veil, no backdrop at all" case this remark used to warn
/// against — scaling is what avoids that. Glass mode's tint stays a fixed literal, unscaled.
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

    [Theory]
    [InlineData("IsMica", 0.30)]
    [InlineData("IsAcrylic", 0.25)]
    public void TheBackdropTint_ScalesWithGlassOpacity(string modeConverter, double expectedCeiling)
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
            "the veil must bind to something — an omitted Opacity inherits full alpha from the " +
            "opaque GlassBaseDarkBrush and the OS backdrop disappears");

        var opacityValue = opacityAttribute!.Value;
        opacityValue.Should().Contain("Customization.GlassOpacity",
            "the veil scales with the Card Opacity slider (RemEx-mmrgc)");
        opacityValue.Should().Contain("MultiplyConverter.Instance",
            "scaling — not a raw binding — is what keeps Frosted (1.0) from going fully opaque");
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
            .Should().BeApproximately(expectedCeiling, 0.001,
                "the ceiling is what Frosted (GlassOpacity = 1.0) reproduces — it must match the " +
                "measured maximum veil, not drift from it");
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
