using System.IO;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace Remex.Desktop.Tests.Views;

/// <summary>
/// Audit RemEx-4kv0g.5.5: the coach-mark scrim (<c>CoachOverlay</c>) carried a bare
/// <c>Background="#40000000"</c> instead of the app's own <c>GlassOverlayBrush</c> scrim resource.
/// Source-level, matching <see cref="CanvasViewTypographyTests"/>'s pattern: no headless render, so
/// a reintroduced literal has to be caught by reading the markup, not by looking at pixels.
/// </summary>
public class CanvasViewColorLiteralTests
{
    [Fact]
    public void CoachOverlayUsesTheGlassOverlayBrushResource()
    {
        ViewSource().Should().Contain(
            @"<Grid x:Name=""CoachOverlay"" IsVisible=""{Binding CoachMarkVisible}"" Background=""{DynamicResource GlassOverlayBrush}"">",
            "the coach-mark scrim must come from the theme's own overlay brush, not a literal alpha-black");
    }

    [Fact]
    public void NoHexColourLiteralSurvivesOutsideComments()
    {
        // Comments stripped first: a literal named in a prose comment (explaining why a value is
        // what it is) is not the same defect as one actually painted on an element.
        var withoutComments = Regex.Replace(ViewSource(), "<!--.*?-->", string.Empty, RegexOptions.Singleline);

        Regex.IsMatch(withoutComments, @"#[0-9A-Fa-f]{3,8}\b").Should().BeFalse(
            "every colour in CanvasView.axaml must be a {DynamicResource ...} lookup, not a raw hex literal");
    }

    private static string ViewSource()
        => File.ReadAllText(Path.Combine(RepoRoot(), "remex.desktop", "Views", "CanvasView.axaml"));

    private static string RepoRoot([CallerFilePath] string thisSourceFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisSourceFile)!, "..", ".."));
}
