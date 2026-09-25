using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Xml.Linq;
using FluentAssertions;
using Xunit;

namespace Remex.Desktop.Tests.Views;

/// <summary>
/// Guards the same trap <see cref="TaskManagerListVirtualizationTests"/> guards on the process list
/// (RemEx-3oy7x), here for the Canvas staging drawer's two lists (P1-26).
/// </summary>
/// <remarks>
/// <para>
/// Both lists were wrapped in a bare <c>ScrollViewer</c>. A <c>ScrollViewer</c> measures its content
/// with INFINITE extent along the scrollable axis to compute a scrollbar range — including when it
/// has an explicit <c>MaxHeight</c>, which only bounds its own FINAL size, not what it hands its
/// child during measure. So the ListBox's <c>VirtualizingStackPanel</c> was handed an unbounded
/// viewport in both cases and realized every row anyway: <c>StagedCards</c> grows with every sensor
/// ever staged, and <c>SensorActivationItems</c> (round-1 review finding: NOT a small fixed list —
/// <c>CanvasDashboardViewModel.RefreshSensorActivation</c> fills it with active sensors plus every
/// staged sensor name, so it is at least as large as <c>StagedCards</c>) is no better bounded by its
/// own <c>ScrollViewer MaxHeight</c> than an unbounded one would be.
/// </para>
/// <para>
/// A SOURCE-TEXT TEST for the reason <see cref="TaskManagerListVirtualizationTests"/> gives: there is
/// no headless render in this suite, and the failure mode is a sluggish UI rather than an exception.
/// </para>
/// </remarks>
public class CanvasStagingDrawerVirtualizationTests
{
    private const string Avalonia = "https://github.com/avaloniaui";

    [Fact]
    public void StagedCardsList_UsesAVirtualizingItemsPanel()
    {
        AssertVirtualizingPanel(FindListBox("{Binding StagedCards}"));
    }

    [Fact]
    public void StagedCardsList_HasNoScrollViewerAncestorWithinTheDrawer()
    {
        AssertNoScrollViewerAncestor(FindListBox("{Binding StagedCards}"));
    }

    [Fact]
    public void SensorActivationList_UsesAVirtualizingItemsPanel()
    {
        AssertVirtualizingPanel(FindListBox("{Binding SensorActivationItems}"));
    }

    [Fact]
    public void SensorActivationList_HasNoScrollViewerAncestorWithinTheDrawer()
    {
        AssertNoScrollViewerAncestor(FindListBox("{Binding SensorActivationItems}"));
    }

    [Fact]
    public void SensorActivationList_BoundsItsOwnHeightDirectly()
    {
        // Round-1 review finding: a wrapping ScrollViewer's MaxHeight only bounds its OWN final size,
        // not what it hands the ListBox during measure - the bound has to live on the ListBox itself.
        var listBox = FindListBox("{Binding SensorActivationItems}");

        listBox.Attribute("MaxHeight").Should().NotBeNull(
            "the height bound must be on the ListBox itself, not a wrapping ScrollViewer");
        listBox.Attribute("MaxHeight")!.Value.Should().Be("300");
    }

    private static void AssertVirtualizingPanel(XElement listBox)
    {
        var panel = listBox
            .Element(XName.Get("ListBox.ItemsPanel", Avalonia))?
            .Element(XName.Get("ItemsPanelTemplate", Avalonia))?
            .Elements()
            .SingleOrDefault();

        panel.Should().NotBeNull(
            "the items panel is stated outright rather than inherited, so swapping it for a plain " +
            "StackPanel is a visible edit instead of a silent default");
        panel!.Name.LocalName.Should().Be("VirtualizingStackPanel");
    }

    private static void AssertNoScrollViewerAncestor(XElement listBox)
    {
        // Bounded by the drawer's own Border, not the document root - DockPanel/Grid ancestors
        // further up the page are legitimate and out of scope for this specific trap.
        for (var ancestor = listBox.Parent;
             ancestor is not null && ancestor.Name.LocalName != "Border";
             ancestor = ancestor.Parent)
        {
            ancestor.Name.LocalName.Should().NotBe("ScrollViewer",
                "an outer ScrollViewer hands its content infinite height regardless of its own " +
                "MaxHeight, undoing the ListBox's own virtualization");
        }
    }

    /// <summary>Finds the ListBox bound to the given ItemsSource. Located by its binding rather than
    /// by position, so reordering the drawer does not silently start testing some other list.</summary>
    private static XElement FindListBox(string itemsSourceBinding)
    {
        var matches = XDocument.Parse(ViewSource())
            .Descendants(XName.Get("ListBox", Avalonia))
            .Where(element => element.Attribute("ItemsSource")?.Value == itemsSourceBinding)
            .ToList();

        matches.Should().ContainSingle($"the drawer binds exactly one ListBox to {itemsSourceBinding}");
        return matches[0];
    }

    private static string ViewSource()
        => File.ReadAllText(Path.Combine(RepoRoot(), "remex.desktop", "Views", "CanvasView.axaml"));

    private static string RepoRoot([CallerFilePath] string thisSourceFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisSourceFile)!, "..", ".."));
}
