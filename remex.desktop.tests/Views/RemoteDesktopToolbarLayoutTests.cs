using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using FluentAssertions;
using Xunit;

namespace Remex.Desktop.Tests.Views;

/// <summary>
/// Source-scan guard for RemEx-1ufoa.3: below ~1400px of window width the toolbar's title
/// touched the Display label, and the Low/High captions under the Quality slider sat on top of
/// the slider thumb. Measured at 1200px: the title TextBlock ended at the same x the Display
/// TextBlock began (a 0px gutter between two content-sized columns, because a Grid inside a
/// horizontally infinite ScrollViewer sizes even its star column to content), and the ~20px-tall
/// Material thumb overlapped the captions by ~5-6px. There is no headless render in this repo,
/// so this parses the XAML and asserts each attribute on the located element, independent of
/// attribute order, matching <see cref="RemoteDesktopViewChromeTests"/> in spirit.
/// </summary>
/// <remarks>
/// The 12px margin is the fix; ClipToBounds and TextTrimming are the fail-closed guard for a
/// narrower arrange than the ScrollViewer gives today. The review round on the first version of
/// this file found that deleting the margin left every test green, which is why the margin is
/// asserted first and by itself.
/// </remarks>
public class RemoteDesktopToolbarLayoutTests
{
    private static readonly XNamespace Avalonia = "https://github.com/avaloniaui";

    private static string RepoRoot([CallerFilePath] string here = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", ".."));

    private static XDocument View() =>
        XDocument.Parse(File.ReadAllText(
            Path.Combine(RepoRoot(), "remex.desktop", "Views", "RemoteDesktopView.axaml")));

    private static string Attr(XElement element, string name) =>
        Regex.Replace(element.Attribute(name)?.Value ?? string.Empty, @"\s+", " ").Trim();

    /// <summary>The TextBlock that shows the localised toolbar title, located by its own text
    /// binding so a second column-1 StackPanel elsewhere in the view cannot be mistaken for it.</summary>
    private static XElement TitleTextBlock(XDocument view)
    {
        var titles = view.Descendants(Avalonia + "TextBlock")
            .Where(t => Attr(t, "Text") == "{local:Localize RemoteDesktop_Header}")
            .ToList();
        titles.Should().ContainSingle("the toolbar shows RemoteDesktop_Header exactly once");
        return titles[0];
    }

    [Fact]
    public void Title_KeepsAGutterFromTheNeighbourColumn()
    {
        var title = TitleTextBlock(View());
        var container = title.Parent!;

        container.Name.Should().Be(Avalonia + "StackPanel", "the title sits in its own column container");
        Attr(container, "Margin").Should().Be("12,0",
            "the title and the Display label are content-sized neighbours inside a horizontally "
            + "infinite ScrollViewer, so this margin is the only gutter between them (measured 0px "
            + "without it at 1200px)");
    }

    [Fact]
    public void Title_FailsClosedIfItsColumnIsEverArrangedNarrowerThanTheText()
    {
        var title = TitleTextBlock(View());
        var container = title.Parent!;

        Attr(container, "ClipToBounds").Should().Be("True",
            "a narrower arrange must clip the title inside its own column rather than paint over Display");
        Attr(title, "TextTrimming").Should().Be("CharacterEllipsis",
            "a narrower arrange must trim the title rather than let it overflow the column");
    }

    [Fact]
    public void QualityCaptions_AreSeparatedFromTheSliderByTheMeasuredClearance()
    {
        var view = View();
        var low = view.Descendants(Avalonia + "TextBlock")
            .Where(t => Attr(t, "Text") == "{local:Localize RemoteDesktop_Low}")
            .ToList();
        low.Should().ContainSingle("the Quality slider has exactly one Low caption");

        var captionsGrid = low[0].Parent!;
        captionsGrid.Name.Should().Be(Avalonia + "Grid", "Low and High share one captions Grid under the track");
        captionsGrid.Elements(Avalonia + "TextBlock")
            .Select(t => Attr(t, "Text"))
            .Should().Contain("{local:Localize RemoteDesktop_High}", "the High caption shares the Grid");

        var stack = captionsGrid.Parent!;
        stack.Name.Should().Be(Avalonia + "StackPanel", "the slider and its captions stack vertically");
        var slider = stack.Elements(Avalonia + "Slider").ToList();
        slider.Should().ContainSingle("the captions belong to exactly one slider");
        Attr(slider[0], "AutomationProperties.Name").Should().Be("{local:Localize RemoteDesktop_Quality}",
            "the captions belong to the Quality slider");
        slider[0].ElementsAfterSelf().Should().Contain(captionsGrid, "the captions sit below the track");

        // Spacing is the only clearance between the slider's own box and the captions. The
        // Material thumb is ~20px tall on a 6px slider box and overlapped the captions by ~5-6px
        // at Spacing="1"; 8 clears it with headroom.
        Attr(stack, "Spacing").Should().Be("8",
            "the Low/High captions must sit clear of the slider thumb (measured ~5-6px overlap at Spacing 1)");
    }

    [Fact]
    public void Toolbar_StillSitsInAHorizontalScrollViewerWithVerticalScrollingDisabled()
    {
        var view = View();
        var title = TitleTextBlock(view);
        var scrollViewer = title.Ancestors(Avalonia + "ScrollViewer").FirstOrDefault();

        scrollViewer.Should().NotBeNull("the toolbar must keep degrading via horizontal scroll, not by hiding controls");
        Attr(scrollViewer!, "HorizontalScrollBarVisibility").Should().Be("Auto");
        Attr(scrollViewer!, "VerticalScrollBarVisibility").Should().Be("Disabled");
    }
}
