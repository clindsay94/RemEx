using System.IO;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace Remex.Desktop.Tests.Views;

/// <summary>
/// Source-scan guard for RemEx-1ufoa.3: below ~1400px of window width the toolbar's centred title
/// overprinted the Display label, and the Low/High captions under the Quality slider sat on top of
/// the slider thumb (measured at 1200px: title/Display touched with a 0px gutter; the ~20px-tall
/// thumb overlapped the captions by ~5-6px). There is no headless render in this repo, so this
/// asserts against the raw XAML text, matching <see cref="RemoteDesktopViewChromeTests"/>.
/// </summary>
public class RemoteDesktopToolbarLayoutTests
{
    private static string RepoRoot([CallerFilePath] string here = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", ".."));

    private static string ViewSource()
    {
        var path = Path.Combine(RepoRoot(), "remex.desktop", "Views", "RemoteDesktopView.axaml");
        return File.ReadAllText(path);
    }

    [Fact]
    public void Title_ClipsInAContainerAndTrimsInsteadOfOverprintingTheNeighbourColumn()
    {
        var source = ViewSource();

        var containerMatch = Regex.Match(
            source,
            @"<StackPanel\s+Grid\.Column=""1""[^>]*>",
            RegexOptions.Singleline);
        containerMatch.Success.Should().BeTrue("the title's column container must be present");
        Regex.IsMatch(containerMatch.Value, @"ClipToBounds\s*=\s*""True""")
            .Should().BeTrue($"the title container must fail closed when its column shrinks below the title's natural width, but found: {containerMatch.Value}");

        var titleMatch = Regex.Match(
            source,
            @"<TextBlock\s+Text=""\{local:Localize RemoteDesktop_Header\}""[^>]*/>",
            RegexOptions.Singleline);
        titleMatch.Success.Should().BeTrue("the RemoteDesktop_Header TextBlock must be present");
        Regex.IsMatch(titleMatch.Value, @"TextTrimming\s*=\s*""CharacterEllipsis""")
            .Should().BeTrue($"the title must trim rather than overflow into the neighbouring column, but found: {titleMatch.Value}");
    }

    [Fact]
    public void QualityCaptions_AreSeparatedFromTheSliderByTheMeasuredClearance()
    {
        var source = ViewSource();

        // The Slider (RemoteDesktop_Quality) and the Low/High captions Grid live in the same
        // StackPanel; its Spacing is the only clearance between the slider's own box and the
        // captions. Measured thumb overlap was ~5-6px at Spacing="1" - Spacing="8" is the chosen fix.
        var stackMatch = Regex.Match(
            source,
            @"<StackPanel\s+Spacing=""8""\s+VerticalAlignment=""Center"">\s*<Slider\s+Minimum=""10""\s+Maximum=""100""[^>]*RemoteDesktop_Quality[^>]*/>\s*<Grid\s+ColumnDefinitions=""\*,\*""\s+Width=""80"">",
            RegexOptions.Singleline);
        stackMatch.Success.Should().BeTrue(
            "the Quality slider and its Low/High captions Grid must sit in one StackPanel with Spacing=\"8\" (measured clearance for the ~20px Material thumb) - source scan did not find that structure");

        Regex.IsMatch(source, @"<TextBlock\s+Text=""\{local:Localize RemoteDesktop_Low\}""[^>]*/>").Should().BeTrue("the Low caption must remain present");
        Regex.IsMatch(source, @"<TextBlock\s+Grid\.Column=""1""\s+Text=""\{local:Localize RemoteDesktop_High\}""[^>]*/>").Should().BeTrue("the High caption must remain present");
    }

    [Fact]
    public void Toolbar_StillSitsInAHorizontalScrollViewerWithVerticalScrollingDisabled()
    {
        var source = ViewSource();
        var match = Regex.Match(
            source,
            @"<ScrollViewer\s+HorizontalScrollBarVisibility=""Auto""\s+VerticalScrollBarVisibility=""Disabled"">",
            RegexOptions.Singleline);

        match.Success.Should().BeTrue("the toolbar must keep degrading via horizontal scroll, not by hiding controls");
    }
}
