using System.Runtime.CompilerServices;

namespace Remex.Desktop.Tests.Views;

/// <summary>
/// RemEx-133ik. Pins <c>TrayBalloonWindow</c> to the Material toast restyle: the app-wide
/// <c>material:Card</c> surface, a <c>MaterialIcon</c> severity glyph resolved through the same
/// <c>SnackbarSeverityMapping</c> the in-app snackbar uses, and no leftover hand-styled Border,
/// hardcoded shadow/brush or Unicode glyph.
/// </summary>
/// <remarks>
/// Source-scan rather than a rendered check, per RemEx-r8c6: there is no Avalonia headless harness
/// here, so this reads the .axaml and .axaml.cs text directly instead of constructing the window.
/// </remarks>
public class TrayBalloonMaterialToastTests
{
    // [CallerFilePath] rather than walking up from the assembly, so building with --artifacts-path
    // outside the repo does not break this with an unrelated-looking error (RemEx-6i1l).
    private static string RepoRoot([CallerFilePath] string thisSourceFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisSourceFile)!, "..", ".."));

    private static string AxamlText()
        => File.ReadAllText(Path.Combine(RepoRoot(), "remex.desktop", "Views", "TrayBalloonWindow.axaml"));

    private static string CodeBehindText()
        => File.ReadAllText(Path.Combine(RepoRoot(), "remex.desktop", "Views", "TrayBalloonWindow.axaml.cs"));

    [Fact]
    public void Axaml_uses_the_material_card_surface()
        => Assert.Contains("<material:Card", AxamlText());

    [Fact]
    public void Axaml_uses_a_material_icon_for_the_severity_glyph()
        => Assert.Contains("mi:MaterialIcon", AxamlText());

    [Fact]
    public void Axaml_has_no_hardcoded_box_shadow()
        => Assert.DoesNotContain("BoxShadow=", AxamlText());

    [Fact]
    public void Axaml_has_no_hardcoded_glass_brush()
        => Assert.DoesNotContain("GlassBaseDarkBrush", AxamlText());

    [Fact]
    public void Axaml_has_no_leftover_glyph_text_block()
        => Assert.DoesNotContain("GlyphText", AxamlText());

    [Fact]
    public void Axaml_keeps_the_accent_stripe_that_present_recolours()
    {
        // Present() touches AccentStripe by name; a sweep that drops it passes every other
        // assertion here and only fails at runtime on a surface no test can render.
        Assert.Contains("x:Name=\"AccentStripe\"", AxamlText());
        Assert.Contains("Classes=\"accent-stripe\"", AxamlText());
    }

    [Fact]
    public void Card_states_its_inside_clipping()
        // The stripe relies on the Card clipping its content to the rounded corners. Material's
        // default is True today; stating it keeps a package bump from squaring the corner silently.
        => Assert.Contains("InsideClipping=\"True\"", AxamlText());

    [Fact]
    public void CodeBehind_resolves_the_glyph_through_the_shared_severity_mapping()
        => Assert.Contains("SnackbarSeverityMapping.For(", CodeBehindText());

    [Fact]
    public void CodeBehind_still_drives_dismissal_with_the_dispatcher_timer()
        => Assert.Contains("DispatcherTimer", CodeBehindText());
}
