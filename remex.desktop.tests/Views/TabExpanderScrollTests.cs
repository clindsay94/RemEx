using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace Remex.Desktop.Tests.Views;

/// <summary>
/// Guards the tab, expander and scroll surfaces after RemEx-04ftl.
/// </summary>
/// <remarks>
/// <para>
/// One root cause runs through all three. <c>ThemeService.PushSeedIntoMaterialTheme</c> writes the
/// seed's PRIMARY and SECONDARY into <c>MaterialTheme.CurrentTheme</c> and nothing else, so any
/// part of a Material template that draws from its Body, Selection or CardBackground brushes stays
/// on the component library's palette while the parts beside it follow the user's seed. Half a
/// control looking correct is the hardest kind of mismatch to spot — a selected tab on the app's
/// accent with its unselected neighbours in a stranger's grey reads as a deliberate choice.
/// </para>
/// <para>
/// The expander's defect is not a colour at all: <c>MaterialExpander</c> sets
/// <c>ContentTransition</c> to <c>{x:Null}</c>, so its content appeared and vanished with no
/// animation. About's FAQ is a column of them, and opening one made the page jump.
/// </para>
/// </remarks>
public class TabExpanderScrollTests
{
    [Fact]
    public void TheInactiveTabHeaderFollowsTheSeedLikeTheActiveOneDoes()
    {
        // The active header rode MaterialPrimaryMidBrush, which IS seeded and so was already
        // right. Only its neighbours were wrong, which is why nothing looked broken.
        var style = StyleFor("TabControl");

        style.Should().Contain("TabHeaderInactiveBrush",
            "the unselected headers came from MaterialBodyLightBrush, which the seed never reaches");
        style.Should().Contain("TabHeaderHighlightBrush",
            "the active indicator's colour is pinned here rather than left to Material's primary, "
            + "so the tab strip and the rest of the app cannot drift apart");

        MaterialOwnBrushes(style).Should().BeEmpty(
            "a Material brush key here is exactly the drift this style exists to end");
    }

    [Fact]
    public void TheExpanderActuallyAnimates()
    {
        // THE ACCEPTANCE CRITERION, and the reason it needed doing: Material nulls
        // ContentTransition, so an expander opened by snapping its content into existence. A
        // future edit deleting this setter would restore a page that jumps.
        var style = StyleFor("Expander");

        style.Should().MatchRegex(@"<Setter Property=""ContentTransition"">\s*<\w+",
            "MaterialExpander sets ContentTransition to null; without a transition declared here "
            + "the panel appears and disappears instead of opening");
    }

    [Fact]
    public void TheExpanderIsGlassRatherThanMaterialsPaper()
    {
        // Same brush, same reason, as RemEx-qbzl1 had to keep off Card: an opaque panel inside a
        // translucent card punches a hole in the window backdrop right where the eye already is.
        var style = StyleFor("Expander");

        // MATCHED ON THE SETTER, NOT ON THE KEY. Injection caught this: the first version asserted
        // the style merely contained "CardBackgroundBrush", which "MaterialCardBackgroundBrush"
        // also contains — so swapping in the exact brush this test exists to keep out left it
        // green. Anchoring on Property="Background" removes the substring trap, and the
        // Material-brush scan below closes it from the other side.
        style.Should().MatchRegex(
            @"<Setter Property=""Background"" Value=""\{DynamicResource CardBackgroundBrush\}""",
            "MaterialExpander's default is MaterialCardBackgroundBrush — opaque paper over glass");

        MaterialOwnBrushes(style).Should().BeEmpty(
            "the expander sits inside a translucent card; a Material brush here is the paper "
            + "this rule exists to replace");
    }

    [Fact]
    public void ScrollbarsAreOnTheAppsPaletteAndNotMaterialsOwn()
    {
        // Scrollbars were the one piece of chrome that ignored the user's chosen palette entirely:
        // Material draws them from MaterialBodyBrush and MaterialSelectionBrush, and the seed
        // reaches neither.
        var declared = AppStyleSelectors()
            .Where(selector => selector.StartsWith("ScrollBar", StringComparison.Ordinal))
            .ToList();

        declared.Should().NotBeEmpty("the scrollbar palette rule has to exist");

        foreach (var selector in declared)
        {
            MaterialOwnBrushes(StyleFor(selector)).Should().BeEmpty(
                $"{selector} must not reach back into Material's own brushes");
        }
    }

    // ─────────────────────────── plumbing ───────────────────────────

    /// <summary>
    /// Material brush keys the seed never writes. Referencing one from a RemEx style is the
    /// drift these tests exist to catch — it resolves, it renders, and it ignores the palette.
    /// </summary>
    private static string[] MaterialOwnBrushes(string styleText)
        => Regex.Matches(styleText, @"\{DynamicResource (Material\w+)\}")
            .Select(match => match.Groups[1].Value)
            .Distinct()
            .ToArray();

    private static string StyleFor(string selector)
    {
        var app = File.ReadAllText(Path.Combine(RepoRoot(), "remex.desktop", "App.axaml"));

        var match = Regex.Match(
            app, $@"<Style Selector=""{Regex.Escape(selector)}"">.*?</Style>", RegexOptions.Singleline);

        match.Success.Should().BeTrue($"App.axaml has to carry the shared {selector} rule");
        return match.Value;
    }

    private static string[] AppStyleSelectors()
        => Regex
            .Matches(File.ReadAllText(Path.Combine(RepoRoot(), "remex.desktop", "App.axaml")),
                @"<Style Selector=""([^""]+)"">")
            .Select(match => match.Groups[1].Value)
            .ToArray();

    private static string RepoRoot([CallerFilePath] string thisSourceFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisSourceFile)!, "..", ".."));
}
