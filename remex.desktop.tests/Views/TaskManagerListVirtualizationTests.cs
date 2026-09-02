using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Xml.Linq;
using FluentAssertions;
using Xunit;

namespace Remex.Desktop.Tests.Views;

/// <summary>
/// Guards the three things that keep the Task Manager usable at a real process count (RemEx-3oy7x).
/// </summary>
/// <remarks>
/// <para>
/// A typical Windows box runs ~200 processes and the row template is ~8 controls, so an unvirtualized
/// list is ~1600 controls re-measured on every poll tick. The page used to root itself in a
/// <c>ScrollViewer</c> &gt; <c>StackPanel</c>, and a <c>StackPanel</c> measures its children with
/// INFINITE height — so the ListBox's <c>VirtualizingStackPanel</c> was handed an unbounded viewport
/// and realized every row anyway. That is the same trap
/// <see cref="FileTransferQueueVirtualizationTests"/> guards on the transfer queue, and the list-height
/// it produced is what let the list card slide over the search field in RemEx-zm6gp.
/// </para>
/// <para>
/// ALL THREE PARTS ARE LOAD-BEARING, which is why all three are asserted: a bounded container height,
/// nothing between that container and the list that restores an infinite one, and a virtualizing
/// items panel. Restoring any one of them on its own changes nothing.
/// </para>
/// <para>
/// A SOURCE-TEXT TEST for the reason the other view tests here give: there is no headless render in
/// this suite, and the failure mode is a sluggish UI rather than an exception, so nothing else would
/// catch the regression before a user did.
/// </para>
/// </remarks>
public class TaskManagerListVirtualizationTests
{
    private const string Avalonia = "https://github.com/avaloniaui";

    [Fact]
    public void TheProcessList_UsesAVirtualizingItemsPanel()
    {
        var panel = ProcessListBox()
            .Element(XName.Get("ListBox.ItemsPanel", Avalonia))?
            .Element(XName.Get("ItemsPanelTemplate", Avalonia))?
            .Elements()
            .SingleOrDefault();

        panel.Should().NotBeNull(
            "the items panel is stated outright rather than inherited, so swapping it for a plain " +
            "StackPanel is a visible edit instead of a silent default");
        panel!.Name.LocalName.Should().Be("VirtualizingStackPanel");
    }

    [Fact]
    public void TheProcessList_SitsInAStarRowSoItGetsAFiniteViewport()
    {
        var root = PageRoot();

        root.Name.LocalName.Should().Be(
            "Grid",
            "a ScrollViewer>StackPanel root measures the list with infinite height, and a " +
            "virtualizing panel given unbounded height realizes every row anyway");

        var rows = (root.Attribute("RowDefinitions")?.Value ?? string.Empty)
            .Split(',', System.StringSplitOptions.RemoveEmptyEntries)
            .Select(row => row.Trim())
            .ToList();

        rows.Should().NotBeEmpty("the root Grid needs explicit rows to bound the list");

        // The list lives in the star row; the header and the search bar sit in the Auto rows above
        // it, which is what keeps them pinned while the list scrolls on its own.
        var container = ProcessListBox().Parent!;
        var listRow = int.Parse(
            container.Attribute("Grid.Row")?.Value ?? "0",
            System.Globalization.CultureInfo.InvariantCulture);

        rows.Should().HaveCountGreaterThan(listRow, "the list's row index must exist in RowDefinitions");
        rows[listRow].Should().Be("*",
            "the container holding the ListBox must take the leftover height rather than size to content");
        rows.Take(listRow).Should().AllBe("Auto",
            "every row above the list is chrome that stays pinned; a second star row would split the height");
    }

    [Fact]
    public void NothingBetweenTheRootAndTheList_ReintroducesAnInfiniteHeight()
    {
        var root = PageRoot();

        for (var ancestor = ProcessListBox().Parent;
             ancestor is not null && ancestor != root;
             ancestor = ancestor.Parent)
        {
            ancestor.Name.LocalName.Should().NotBe("StackPanel",
                "a StackPanel below the bounded row measures the list with infinite height again");
            ancestor.Name.LocalName.Should().NotBe("ScrollViewer",
                "an outer ScrollViewer hands its content infinite height, undoing the star row");
        }
    }

    /// <summary>
    /// Finds the ListBox bound to the process list. Located by its binding rather than by position,
    /// so reordering the page does not silently start testing some other list.
    /// </summary>
    private static XElement ProcessListBox()
    {
        var matches = XDocument.Parse(ViewSource())
            .Descendants(XName.Get("ListBox", Avalonia))
            .Where(element => element.Attribute("ItemsSource")?.Value == "{Binding Processes}")
            .ToList();

        matches.Should().ContainSingle("the page binds exactly one ListBox to Processes");
        return matches[0];
    }

    /// <summary>
    /// The UserControl's single content child. Property elements (<c>UserControl.Styles</c>) carry a
    /// dot in their local name and are skipped.
    /// </summary>
    private static XElement PageRoot()
        => XDocument.Parse(ViewSource()).Root!
            .Elements()
            .Single(element => !element.Name.LocalName.Contains('.'));

    private static string ViewSource()
        => File.ReadAllText(Path.Combine(RepoRoot(), "remex.desktop", "Views", "TaskManagerView.axaml"));

    private static string RepoRoot([CallerFilePath] string thisSourceFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisSourceFile)!, "..", ".."));
}
