using System.IO;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace Remex.Desktop.Tests.Views;

/// <summary>
/// RemEx-9iz00.1: the first-run tutorial overlay's 17 hand-toggled <c>IsVisible</c> pages became a
/// real <c>Carousel</c> with a <c>PipsPager</c> indicator. A source scan, not a render test - this
/// assembly has no headless render (see <c>ButtonVocabularyTests</c> and its siblings for the same
/// approach) - so these pin the shape rather than the pixels.
/// </summary>
public class TutorialCarouselSourceScanTests
{
    [Fact]
    public void OverlayContainsACarouselBoundToThePageIndex()
    {
        var overlay = TutorialOverlaySource();

        overlay.Should().MatchRegex(@"<Carousel\b[\s\S]*?SelectedIndex=""\{Binding TutorialPageIndex,\s*Mode=TwoWay\}""",
            "the Carousel has to be the thing moving pages, bound two-way so the Back/Next/Finish " +
            "commands and the PipsPager below stay in sync with whatever page it lands on");
    }

    [Fact]
    public void CarouselTransitionIsGatedByReducedMotion()
    {
        TutorialOverlaySource().Should().Contain(
            "PageTransition=\"{Binding IsReducedMotion, Converter={x:Static conv:BoolToPageTransitionConverter.Instance}}\"",
            "IsReducedMotion has to silence the page-slide transition, the same rule every other " +
            "animation in ShellView already follows");
    }

    [Fact]
    public void OverlayHasAnIndicatorPerPage()
    {
        var overlay = TutorialOverlaySource();

        // RemEx-9iz00.1 fix round (HIGH): the PipsPager keys off the platform-filtered visible
        // list, not the raw 17-page TutorialPageCount/TutorialPageIndex the Carousel above uses -
        // otherwise a pip click or arrow key could reach a page this platform does not support.
        overlay.Should().MatchRegex(@"<PipsPager\b[\s\S]*?NumberOfPages=""\{Binding TutorialVisiblePageCount\}""",
            "PipsPager renders one pip per platform-visible page - the indicator this bead asked for");
        overlay.Should().Contain("SelectedPageIndex=\"{Binding TutorialVisiblePageIndex, Mode=TwoWay}\"",
            "the indicator has to track a position in the visible-page list, not the raw Carousel index, " +
            "so it can never select a page the running platform hides");
    }

    [Fact]
    public void PipsPagerPaintsFromTheThemeNotHardcodedColours()
    {
        // RemEx-9iz00.1 fix round (MEDIUM): Avalonia.Themes.Fluent/Simple are not merged in this app
        // (RemEx-prkot), so PipsPager has no ControlTheme of its own and would otherwise render pips
        // with no paint at all. The overlay supplies one locally, repainted with the same
        // DynamicResource brushes the removed dot ItemsControl used.
        var overlay = TutorialOverlaySource();

        overlay.Should().MatchRegex(@"x:Key=""\{x:Type PipsPager\}""",
            "a local ControlTheme keyed by PipsPager's type is what makes the pips render at all " +
            "without Fluent/Simple merged");
        overlay.Should().MatchRegex(@"Selector=""ListBoxItem:selected[^""]*""\s*>\s*<Setter Property=""Fill"" Value=""\{DynamicResource AccentPrimaryBrush\}""\s*/>",
            "the selected pip has to paint from the same accent the removed dot ItemsControl used, " +
            "conditioned on the pip actually being selected");
        overlay.Should().Contain("{DynamicResource TextMutedBrush}",
            "the unselected pips need an outline/variant brush of their own, not another opacity " +
            "trick on the same accent colour");
    }

    [Fact]
    public void BackNextAndFinishStillCarryButtonVocabularyClasses()
    {
        var overlay = TutorialOverlaySource();

        overlay.Should().Contain("Content=\"{conv:Localize Tutorial_Back}\"");
        overlay.Should().MatchRegex(@"Classes=""secondary pill""[^>]*Content=""\{conv:Localize Tutorial_Back\}""");
        overlay.Should().MatchRegex(@"Classes=""primary pill""[^>]*Content=""\{conv:Localize Tutorial_Next\}""");
        overlay.Should().MatchRegex(@"Classes=""primary pill success""[^>]*Content=""\{conv:Localize Tutorial_Finish\}""");

        // At most one Classes="...primary..." button visible at a time in the overlay - Next and
        // Finish are mutually exclusive by TutorialPageIndex, which is exactly what
        // ButtonVocabularyTests.AtMostOnePrimaryButtonPerViewSurface's exception list documents.
        Regex.Matches(overlay, @"Classes=""[^""]*\bprimary\b[^""]*""").Count.Should().Be(2,
            "Next and Finish are the only two primary buttons in the overlay, and they never show together");
    }

    [Fact]
    public void OverlayUsesNoLiteralColours()
    {
        var overlay = TutorialOverlaySource();

        // Every color-bearing property in the overlay resolves through a theme brush. A literal
        // hex color or an inline Color.Parse-able value would mean the Carousel/PipsPager rework
        // reintroduced the thing RemEx-z7pnx's palette sweep removed.
        overlay.Should().NotMatchRegex(@"=""#[0-9A-Fa-f]{3,8}""",
            "no hex colour literal belongs in the tutorial overlay - DynamicResource/StaticResource only");
    }

    /// <summary>The tutorial overlay Border, from its marker comment to the closing Card.</summary>
    private static string TutorialOverlaySource([CallerFilePath] string f = "")
    {
        var source = File.ReadAllText(Path.Combine(RepoRoot(f), "remex.desktop", "Views", "ShellView.axaml"));

        var start = source.IndexOf("TUTORIAL OVERLAY", System.StringComparison.Ordinal);
        start.Should().BeGreaterThan(0, "the tutorial overlay marker comment moved or was renamed");

        var end = source.IndexOf("</material:Card>", start, System.StringComparison.Ordinal);
        end.Should().BeGreaterThan(start, "the overlay's closing Card moved or was renamed");

        return source.Substring(start, end - start);
    }

    private static string RepoRoot([CallerFilePath] string thisSourceFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisSourceFile)!, "..", ".."));
}
