using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Xml.Linq;
using FluentAssertions;
using Xunit;

namespace Remex.Desktop.Tests.Views;

/// <summary>
/// Perf audit P3-60: Home's "Recent activity" bound a bare <c>ItemsControl</c>, whose default panel is
/// a non-virtualizing <c>StackPanel</c>, so all 60 stored rows were realized behind a 300 px viewport.
/// Same three-part rule as <see cref="FileTransferQueueVirtualizationTests"/>: a
/// <c>VirtualizingStackPanel</c> AND a parent <c>ScrollViewer</c> with a real <c>MaxHeight</c> (the
/// section sits in a <c>StackPanel</c>, which measures with infinite height). Source-text, because
/// this suite has no headless render.
/// </summary>
public class HomeRecentActivityVirtualizationTests
{
    private const string Avalonia = "https://github.com/avaloniaui";

    [Fact]
    public void TheRecentActivityList_IsVirtualized()
    {
        var panel = RecentActivityItemsControl()
            .Element(XName.Get("ItemsControl.ItemsPanel", Avalonia))?
            .Element(XName.Get("ItemsPanelTemplate", Avalonia))?
            .Elements()
            .SingleOrDefault();

        panel.Should().NotBeNull("a bare ItemsControl realizes every stored activity row");
        panel!.Name.LocalName.Should().Be("VirtualizingStackPanel");
    }

    [Fact]
    public void TheRecentActivityList_ScrollsInsideABoundedViewport()
    {
        var scrollViewer = RecentActivityItemsControl().Parent;

        scrollViewer.Should().NotBeNull();
        scrollViewer!.Name.LocalName.Should().Be("ScrollViewer");
        var maxHeight = scrollViewer.Attribute("MaxHeight")?.Value;
        maxHeight.Should().NotBeNullOrWhiteSpace("an unbounded ScrollViewer inside a StackPanel still measures every row");
        double.Parse(maxHeight!, System.Globalization.CultureInfo.InvariantCulture).Should().BeGreaterThan(0);
    }

    private static XElement RecentActivityItemsControl()
    {
        var matches = XDocument.Parse(ViewSource())
            .Descendants(XName.Get("ItemsControl", Avalonia))
            .Where(element => element.Attribute("ItemsSource")?.Value == "{Binding RecentActivity}")
            .ToList();

        matches.Should().ContainSingle("Home binds exactly one ItemsControl to RecentActivity");
        return matches[0];
    }

    private static string ViewSource()
        => File.ReadAllText(Path.Combine(RepoRoot(), "remex.desktop", "Views", "HomeView.axaml"));

    private static string RepoRoot([CallerFilePath] string thisSourceFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisSourceFile)!, "..", ".."));
}
