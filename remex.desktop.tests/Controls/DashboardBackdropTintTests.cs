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
/// change that made the feature look broken. They are NOT bound to GlassOpacity either: that
/// slider is Card Opacity, and wiring the window backdrop to it means a user who wants readable
/// cards (Frosted = 1.0) gets a fully opaque veil and no backdrop at all.
/// </para>
/// </remarks>
public class DashboardBackdropTintTests
{
    private const string Avalonia = "https://github.com/avaloniaui";

    [Theory]
    [InlineData("IsMica", 0.30)]
    [InlineData("IsAcrylic", 0.25)]
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
                "the tint has to stay light enough for the OS backdrop underneath to actually " +
                "register — a heavier veil is alive and invisible, which reads as broken")
            ;
    }

    private static string RepoRoot([CallerFilePath] string thisSourceFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisSourceFile)!, "..", ".."));
}
