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
/// the OS backdrop — the overlay panels only tint it. They were authored when
/// <c>GlassBaseDark</c> was <c>#A00A0A10</c> (alpha 0xA0, 63%), so a bare
/// <c>GlassBaseDarkBrush</c> Fill already let the backdrop through. When ThemeService started
/// overriding that key with the HCT-solved Surface at full alpha, the Mica tint silently went
/// 63% → 100% opaque and the OS backdrop vanished behind a solid sheet; Acrylic went ~50% → 80%.
/// Connor reported it live on 2026-08-26: "mica and acrylic don't do anything anymore."
/// </para>
/// <para>
/// The fix keeps the brush opaque (26 files consume it) and puts the old EFFECTIVE opacity on the
/// overlay rectangles explicitly. This test pins those opacities from the source, because the
/// failure is a backdrop that silently stops showing — no exception, no log line, and no headless
/// render in this suite to see it.
/// </para>
/// </remarks>
public class DashboardBackdropTintTests
{
    private const string Avalonia = "https://github.com/avaloniaui";

    [Theory]
    [InlineData("IsMica", 0.63)]
    [InlineData("IsAcrylic", 0.5)]
    [InlineData("IsGlass", 0.16)]
    public void TheBackdropTint_StaysTranslucent(string modeConverter, double expectedOpacity)
    {
        var control = XDocument.Parse(File.ReadAllText(Path.Combine(
            RepoRoot(), "remex.desktop", "Controls", "DashboardBackgroundControl.axaml")));

        var modePanel = control
            .Descendants(XName.Get("Panel", Avalonia))
            .Where(panel => (panel.Attribute("IsVisible")?.Value ?? string.Empty)
                .Contains("StringMatchConverter." + modeConverter))
            .Should().ContainSingle($"exactly one overlay panel should own the {modeConverter} mode")
            .Subject;

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
                "the explicit opacity restores the tint these panels shipped with while " +
                "GlassBaseDark still carried alpha 0xA0")
            ;
    }

    private static string RepoRoot([CallerFilePath] string thisSourceFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisSourceFile)!, "..", ".."));
}
