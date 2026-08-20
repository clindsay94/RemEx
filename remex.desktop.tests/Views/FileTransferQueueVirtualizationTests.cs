using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Xml.Linq;
using FluentAssertions;
using Xunit;

namespace Remex.Desktop.Tests.Views;

/// <summary>
/// Guards the three things that keep the transfer queue panel usable when a FOLDER transfer fills it
/// (RemEx-u3abk).
/// </summary>
/// <remarks>
/// <para>
/// A folder transfer enqueues one queue item PER FILE. Connor queued a 900-file folder off his phone
/// and the window stopped responding, which read as the transfer silently failing — the rows that had
/// rendered sat at "Queued" and the only way out was the per-row cancel, 900 times.
/// </para>
/// <para>
/// Nothing was wrong with the transfer machinery. The panel bound to a bare
/// <c>&lt;ItemsControl&gt;</c>, whose default items panel in Avalonia 11 is a plain
/// <c>StackPanel</c> — every row realized, ~8 controls each, re-measured on every one of the 900
/// <c>Items.Add</c> notifications.
/// </para>
/// <para>
/// THE FIX IS THREE PARTS AND ALL THREE ARE LOAD-BEARING, which is why this test checks all three
/// rather than just the panel type. A <c>VirtualizingStackPanel</c> given an unbounded height
/// realizes everything anyway, and the queue panel lives inside a <c>StackPanel</c>, so it is
/// measured with infinite height unless something above it says otherwise. The
/// <c>ScrollViewer</c>'s <c>MaxHeight</c> is what supplies a real viewport; deleting it as a styling
/// tweak would silently restore the freeze.
/// </para>
/// <para>
/// A SOURCE-TEXT TEST for the reason the other view tests here give: there is no headless render in
/// this suite, and the failure mode is a frozen UI rather than an exception, so nothing else would
/// catch the regression before a user did.
/// </para>
/// </remarks>
public class FileTransferQueueVirtualizationTests
{
    private const string Avalonia = "https://github.com/avaloniaui";

    [Fact]
    public void TheQueueList_IsVirtualized()
    {
        var itemsControl = QueueItemsControl();

        var panel = itemsControl
            .Element(XName.Get("ItemsControl.ItemsPanel", Avalonia))?
            .Element(XName.Get("ItemsPanelTemplate", Avalonia))?
            .Elements()
            .SingleOrDefault();

        panel.Should().NotBeNull(
            "a bare ItemsControl defaults to a non-virtualizing StackPanel, which realizes one row per queued file");
        panel!.Name.LocalName.Should().Be("VirtualizingStackPanel");
    }

    [Fact]
    public void TheQueueList_ScrollsInsideABoundedViewport()
    {
        var scrollViewer = QueueItemsControl().Parent;

        scrollViewer.Should().NotBeNull();
        scrollViewer!.Name.LocalName.Should().Be(
            "ScrollViewer",
            "virtualization needs a scrolling viewport, and the panel's parent StackPanel offers infinite height");

        var maxHeight = scrollViewer.Attribute("MaxHeight")?.Value;
        maxHeight.Should().NotBeNullOrWhiteSpace(
            "an unbounded ScrollViewer inside a StackPanel still measures every row");
        double.Parse(maxHeight!, System.Globalization.CultureInfo.InvariantCulture)
            .Should().BeGreaterThan(0);
    }

    /// <summary>
    /// The per-row cancel does not scale to a folder, and "Clear finished" cannot stand in for it —
    /// it removes terminal items, which is none of the ones you want to stop (RemEx-l1ddp).
    /// </summary>
    [Fact]
    public void TheQueuePanel_OffersACancelAllAlongsideClearFinished()
    {
        var source = ViewSource();

        source.Should().Contain("CancelAllTransfersCommand");
        source.Should().Contain("ClearCompletedTransfersCommand");
    }

    /// <summary>
    /// Finds the ItemsControl bound to the transfer queue. Located by its binding rather than by
    /// position, so reordering the page does not silently start testing the volumes list instead.
    /// </summary>
    private static XElement QueueItemsControl()
    {
        var matches = XDocument.Parse(ViewSource())
            .Descendants(XName.Get("ItemsControl", Avalonia))
            .Where(element => element.Attribute("ItemsSource")?.Value == "{Binding TransferQueue.Items}")
            .ToList();

        matches.Should().ContainSingle("the queue panel binds exactly one ItemsControl to TransferQueue.Items");
        return matches[0];
    }

    private static string ViewSource()
        => File.ReadAllText(Path.Combine(RepoRoot(), "remex.desktop", "Views", "FileTransferView.axaml"));

    private static string RepoRoot([CallerFilePath] string thisSourceFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisSourceFile)!, "..", ".."));
}
