using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Xml.Linq;
using FluentAssertions;
using Remex.Desktop.ViewModels;
using Xunit;

namespace Remex.Desktop.Tests.Views;

/// <summary>
/// Sidebar tooltips name the window shortcut that opens each item (PC-4.2), and the palette's results
/// list runs a row on click (PC-4.1). Source tests: this suite has no headless render.
/// </summary>
public class ShellNavTooltipShortcutTests
{
    private const string Avalonia = "https://github.com/avaloniaui";

    private static string RepoRoot([CallerFilePath] string f = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(f)!, "..", ".."));

    [Fact]
    public void EachNavItemTooltip_ShowsItsCtrlDigit_InDrawerOrder()
    {
        var doc = XDocument.Load(Path.Combine(RepoRoot(), "remex.desktop", "Views", "ShellView.axaml"));
        var items = doc.Descendants(XName.Get("ListBox", Avalonia))
            .Single(e => e.Attribute("Name")?.Value == "NavList")
            .Descendants(XName.Get("ListBoxItem", Avalonia))
            .Where(e => (e.Attribute("Classes")?.Value ?? "").Split(' ').Contains("nav-item"))
            .ToDictionary(e => int.Parse(e.Attribute("Tag")!.Value));

        // Home..Logs are drawer positions 1-8 and Settings is 9; About (the tenth) has no key.
        for (var position = 1; position <= 9; position++)
        {
            var tag = ShellViewModel.DrawerNavOrder[position - 1];
            var tip = items[tag].Element(XName.Get("ToolTip.Tip", Avalonia));
            tip.Should().NotBeNull($"nav item {position} should carry a tooltip element naming its shortcut");
            tip!.Descendants(XName.Get("TextBlock", Avalonia))
                .Select(t => t.Attribute("Text")?.Value)
                .Should().Contain($"Ctrl+{position}");
        }
    }

    [Fact]
    public void PaletteResultsList_RunsARowOnTap_AndTracksTheSelectedEntry()
    {
        var xaml = File.ReadAllText(Path.Combine(RepoRoot(), "remex.desktop", "Views", "CommandPaletteWindow.axaml"));
        var list = XDocument.Parse(xaml).Descendants(XName.Get("ListBox", Avalonia))
            .Single(e => e.Attribute(XName.Get("Name", "http://schemas.microsoft.com/winfx/2006/xaml"))?.Value == "ResultsList");

        (list.Attribute("Tapped")?.Value).Should().Be("OnResultTapped");
        (list.Attribute("SelectedItem")?.Value).Should().Contain("SelectedEntry").And.Contain("TwoWay");
    }
}
