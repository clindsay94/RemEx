using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using FluentAssertions;
using Xunit;

namespace Remex.Desktop.Tests.Views;

/// <summary>
/// Source-reading tests pinning CommandPaletteWindow's Material surface: the Magnify icon, the
/// Theme-based type scale, and the sub-150ms open transition that must not delay first-keystroke
/// focus (RemEx-ja246). There is no headless render for this repo — see
/// <c>CommandPaletteLightDismissTests</c> and <c>CommandPaletteSelectionFillTests</c> for the same
/// pattern applied to the parts of this window this bead must not touch.
/// </summary>
public class CommandPaletteMaterialSurfaceTests
{
    [Fact]
    public void SearchFieldUsesMagnifyMaterialIconWithNoEmoji()
    {
        var xaml = PaletteWindowXaml();

        xaml.Should().Contain("Kind=\"Magnify\"",
            "the search field's leading glyph is the Magnify MaterialIcon, not the old emoji");
        xaml.Should().NotContain("\U0001F50D", "the emoji magnifying glass must be gone");
    }

    [Fact]
    public void NoTextBlockCarriesAnInlineFontSize()
    {
        var xaml = PaletteWindowXaml();

        Regex.Matches(xaml, @"<TextBlock\b[^>]*\bFontSize=").Count.Should().Be(0,
            "every TextBlock in this window must use a Theme type-scale key instead of an inline FontSize");
    }

    [Fact]
    public void ExactlyOneInlineFontSizeSurvivesOnTheSearchTextBox()
    {
        var xaml = PaletteWindowXaml();

        Regex.Matches(xaml, @"FontSize=").Count.Should().Be(1,
            "the TextBox keeps its inline FontSize=\"15\" (exception 4); nothing else should");
        xaml.Should().Contain("FontSize=\"15\"");
    }

    [Fact]
    public void RowLabelAndCategoryAndEmptyStateCarryThemeTypeScale()
    {
        var xaml = PaletteWindowXaml();

        xaml.Should().MatchRegex(@"Classes=""palette-label""[^>]*Theme=""\{StaticResource Subtitle2TextBlock\}""|Theme=""\{StaticResource Subtitle2TextBlock\}""[^>]*Classes=""palette-label""",
            "palette-label moves to Subtitle2TextBlock (14, row title)");
        xaml.Should().MatchRegex(@"Classes=""palette-category""[^>]*Theme=""\{StaticResource CaptionTextBlock\}""|Theme=""\{StaticResource CaptionTextBlock\}""[^>]*Classes=""palette-category""",
            "palette-category moves to CaptionTextBlock");
        xaml.Should().MatchRegex(@"x:Name=""EmptyStateText""[^>]*Theme=""\{StaticResource CaptionTextBlock\}""",
            "the empty state text moves to CaptionTextBlock");
    }

    [Fact]
    public void RowLabelAndCategoryCarryNoInlineForegroundOrOpacity()
    {
        // Parsed-element rather than regex (RemEx-x6a70.2): the two row TextBlocks stopped being
        // self-closing once their text moved from a plain Text= binding to a MultiBinding on
        // Inlines (so the matched substring can render bold), which the old
        // `<TextBlock ...[^/]*/>` regex could never match. XDocument keeps the same intent -
        // no inline Foreground/Opacity on either row TextBlock - without depending on the tag
        // staying self-closing.
        var xaml = PaletteWindowXaml();
        var doc = XDocument.Parse(xaml);
        XNamespace ns = "https://github.com/avaloniaui";

        var label = doc.Descendants(ns + "TextBlock")
            .FirstOrDefault(e => (string?)e.Attribute("Classes") == "palette-label");
        var category = doc.Descendants(ns + "TextBlock")
            .FirstOrDefault(e => (string?)e.Attribute("Classes") == "palette-category");

        label.Should().NotBeNull("the palette-label TextBlock must still exist");
        category.Should().NotBeNull("the palette-category TextBlock must still exist");

        label!.Attribute("Foreground").Should().BeNull(
            "the row's Foreground/Opacity must come from the ListBox.Styles selectors, not inline (RemEx-o9gd)");
        label.Attribute("Opacity").Should().BeNull();
        category!.Attribute("Foreground").Should().BeNull();
        category.Attribute("Opacity").Should().BeNull();
    }

    [Fact]
    public void OpenTransitionDurationsStayUnder150Ms()
    {
        var xaml = PaletteWindowXaml();

        var transitionsBlock = Regex.Match(xaml, @"<Border\.Transitions>.*?</Border\.Transitions>", RegexOptions.Singleline);
        transitionsBlock.Success.Should().BeTrue("the palette surface must declare its own open transitions");

        var durations = Regex.Matches(transitionsBlock.Value, @"Duration=""0:0:0\.(\d+)""");
        durations.Count.Should().BeGreaterThan(0, "at least one transition (Opacity, RenderTransform) must be declared");

        foreach (Match d in durations)
        {
            var millis = int.Parse(d.Groups[1].Value.PadRight(3, '0')[..3]);
            millis.Should().BeLessThan(150, "a command palette open animation must stay under 150ms so it never feels like it is blocking");
        }
    }

    [Fact]
    public void SurfaceStartsHiddenAndScaledForTheOpenTransition()
    {
        var xaml = PaletteWindowXaml();

        xaml.Should().MatchRegex(@"x:Name=""PaletteSurface""[^>]*Opacity=""0""",
            "the surface starts invisible so the transition has something to animate from");
        xaml.Should().Contain("RenderTransform=\"scale(0.98)\"");
    }

    [Fact]
    public void OnOpenedFocusesSearchBoxBeforeRevealingTheSurface()
    {
        var codeBehind = File.ReadAllText(Path.Combine(
            RepoRoot(), "remex.desktop", "Views", "CommandPaletteWindow.axaml.cs"));

        var onOpened = ExtractMethod(codeBehind, "OnOpened");

        var focusIndex = onOpened.IndexOf("tb.Focus()", System.StringComparison.Ordinal);
        var revealMatch = System.Text.RegularExpressions.Regex.Match(
            onOpened, @"\bPaletteSurface\.Opacity\s*=\s*1");
        var revealIndex = revealMatch.Success ? revealMatch.Index : -1;

        focusIndex.Should().BeGreaterThan(-1, "OnOpened must still focus the SearchBox");
        revealIndex.Should().BeGreaterThan(-1, "OnOpened must reveal the PaletteSurface");
        focusIndex.Should().BeLessThan(revealIndex,
            "focus has to be set before the surface is revealed so the first keystroke is never dropped behind the transition");
    }

    private static string PaletteWindowXaml([CallerFilePath] string thisSourceFile = "")
        => File.ReadAllText(Path.Combine(
            RepoRoot(thisSourceFile), "remex.desktop", "Views", "CommandPaletteWindow.axaml"));

    private static string ExtractMethod(string source, string methodName)
    {
        var match = Regex.Match(source, $@"{Regex.Escape(methodName)}\s*\([^)]*\)\s*\{{.*?\n    \}}", RegexOptions.Singleline);
        match.Success.Should().BeTrue($"{methodName} moved, was renamed, or changed shape — update this test's extraction");
        return match.Value;
    }

    private static string RepoRoot([CallerFilePath] string thisSourceFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisSourceFile)!, "..", ".."));
}
