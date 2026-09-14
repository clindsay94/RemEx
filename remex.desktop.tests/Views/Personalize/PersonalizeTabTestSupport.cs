using System.IO;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using FluentAssertions;

namespace Remex.Desktop.Tests.Views.Personalize;

/// <summary>
/// Shared source-text helpers for the five <c>Personalize{Tab}Tests</c> files (RemEx-4kv0g.4.3).
/// Not itself a test class — <c>remex.desktop.tests</c> has no headless render, so every one of the
/// five reads its tab's .axaml as text, the same way <c>PersonalizationFlyoutSectionTests</c> and
/// its siblings already do.
/// </summary>
internal static class PersonalizeTabTestSupport
{
    public static string TabMarkup(string fileName) => File.ReadAllText(
        Path.Combine(RepoRoot(), "remex.desktop", "Views", "Personalize", fileName));

    /// <summary>The markup with every XML comment stripped, so a comment that MENTIONS a retired
    /// key, a hex colour, or an inline size (for narration, e.g. "used to be #050505") cannot
    /// false-positive the checks below — they care about what renders, not what a comment says.</summary>
    public static string WithoutComments(string markup) =>
        Regex.Replace(markup, @"<!--.*?-->", string.Empty, RegexOptions.Singleline);

    /// <summary>No literal colour anywhere outside a comment: no hex triplet, no <c>Colors.</c>,
    /// no <c>Brushes.</c> — every colour is a bound brush or a <c>DynamicResource</c>.</summary>
    public static void AssertNoLiteralColour(string markup)
    {
        var code = WithoutComments(markup);
        Regex.IsMatch(code, @"#[0-9A-Fa-f]{6}").Should().BeFalse("no literal colour - every colour is a bound brush or a DynamicResource");
        code.Should().NotContain("Colors.").And.NotContain("Brushes.");
    }

    /// <summary>
    /// Every <c>Text=</c>/<c>Content=</c>/<c>Header=</c>/<c>ToolTip.Tip=</c> attribute value is a
    /// markup extension (a <c>{local:Localize …}</c> key or a <c>{Binding …}</c>/<c>{x:Static …}</c>)
    /// — never a bare English literal. <c>OnContent=</c>/<c>OffContent=</c> are deliberately not in
    /// this list: the empty-string ToggleSwitch content this file inherited is not one of the four
    /// named attributes.
    /// </summary>
    public static void AssertEveryVisibleStringRoutesThroughLocalizeOrABinding(string markup)
    {
        var code = WithoutComments(markup);
        Regex.Matches(code, @"\b(?:Text|Content|Header|ToolTip\.Tip)=""(?!\{)[^""]*""").Should().BeEmpty(
            "every static string on this tab must be a {local:Localize ...} markup extension or a binding, never a bare literal");
    }

    /// <summary>
    /// The number of inline <c>FontSize="N"</c> sites is pinned to exactly what this tab inherited
    /// from the pre-move sections (spec 2026-09-13-personalize-tabs is a re-housing, not a sweep —
    /// the project-wide ratchet in <see cref="Views.TypographyVocabularyTests"/> already tracks the
    /// standing backlog). A pin, not a zero, because the moved controls keep every attribute they
    /// had; a NEW inline size added to this tab in a future change fails here first, one file at a
    /// time, rather than only showing up as a +1 on the whole-project ratchet.
    /// </summary>
    public static void AssertInlineFontSizeCountIs(string markup, int expected)
    {
        Regex.Matches(WithoutComments(markup), @"FontSize=""\d").Count.Should().Be(expected,
            $"this tab inherited exactly {expected} inline FontSize site(s) from the pre-move sections - " +
            "a different count means something was added or swept without updating this pin");
    }

    public static void AssertRootsInScrollViewer(string markup)
    {
        markup.TrimStart().Should().StartWith("<UserControl",
            "the file's root element must be the UserControl itself");

        // Comments stripped first: an introductory comment between <UserControl> and <ScrollViewer>
        // (some tabs have one) must not defeat the "the ScrollViewer is the UserControl's only real
        // child" check.
        var code = WithoutComments(markup);
        Regex.IsMatch(code, @"<UserControl\b[^>]*>\s*<ScrollViewer\b[^>]*HorizontalScrollBarVisibility=""Disabled""")
            .Should().BeTrue("each tab owns its own scrolling (spec §1) - the strip stays pinned while the tab's content scrolls");
    }

    private static string RepoRoot([CallerFilePath] string thisSourceFile = "")
    {
        var dir = Path.GetDirectoryName(thisSourceFile)!;
        while (!File.Exists(Path.Combine(dir, "Remex.sln"))) dir = Path.GetDirectoryName(dir)!;
        return dir;
    }
}
