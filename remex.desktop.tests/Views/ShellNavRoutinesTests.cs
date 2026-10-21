using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using FluentAssertions;
using Xunit;

namespace Remex.Desktop.Tests.Views;

/// <summary>
/// The Routines drawer item and page host (routines spec §2.3, R-UX-02, R-UX-04, S4b). Since
/// RemEx-172ba Tag 10 sits directly after Sensors, in the group between the two dividers; the page
/// is reachable through PageHost's DataTemplate switch.
/// </summary>
public sealed class ShellNavRoutinesTests
{
    private static readonly XNamespace Avalonia = "https://github.com/avaloniaui";

    [Fact]
    public void RoutinesIsTheNinthDrawerChildWithTagTen()
    {
        var children = NavList().Elements(Avalonia + "ListBoxItem").ToList();
        children.Should().HaveCount(13, "eleven destinations plus two dividers");

        var routines = children[8];
        (routines.Attribute("Tag")?.Value).Should().Be("10", "Routines sits right after Sensors");
        (children[7].Attribute("Tag")?.Value).Should().Be("1", "Sensors is directly above it");
        (routines.Attribute("AutomationProperties.Name")?.Value).Should().Be("{conv:Localize Nav_Routines}");
        routines.Element(Avalonia + "ToolTip.Tip")!.Descendants(Avalonia + "TextBlock")
            .Select(t => t.Attribute("Text")?.Value).Should().Contain("{conv:Localize Nav_Routines}", "the tooltip still names the page, then its Ctrl+8 shortcut");
        (routines.Attribute("Classes")?.Value).Should().Be("nav-item");
        routines.Descendants().Any(e => e.Name.LocalName == "MaterialIcon" && e.Attribute("Kind")?.Value == "Routes")
            .Should().BeTrue("§2.3 names the Routes icon");
    }

    [Fact]
    public void TheLastChildHasAnEntranceStyleAndTheStaggerStillEndsBy300Ms()
    {
        var xaml = ShellViewXaml();
        xaml.Should().Contain("ListBox#NavList.entrance > ListBoxItem:nth-child(13)");

        var last = Regex.Match(xaml,
            @"ListBox#NavList\.entrance > ListBoxItem:nth-child\(13\)"">[\s\S]*?Duration=""0:0:0\.(\d{3})"" Delay=""0:0:0\.(\d{3})""");
        last.Success.Should().BeTrue();
        (int.Parse(last.Groups[1].Value) + int.Parse(last.Groups[2].Value)).Should().BeLessOrEqualTo(300);
    }

    [Fact]
    public void PageHostRendersRoutinesView()
    {
        var host = XDocument.Parse(ShellViewXaml()).Descendants(Avalonia + "TransitioningContentControl")
            .Single(e => e.Attribute("Name")?.Value == "PageHost");
        var templates = host.Descendants(Avalonia + "DataTemplate").ToList();
        templates.Should().Contain(t => t.Attribute("DataType")!.Value == "vm:RoutinesViewModel"
                                        && t.Elements().Single().Name.LocalName == "RoutinesView");
    }

    [Fact]
    public void ActivateNavItemRoutesTagTenToNavigateToRoutines()
    {
        var code = File.ReadAllText(Path.Combine(RepoRoot(), "remex.desktop", "Views", "ShellView.axaml.cs"));
        code.Should().Contain("case 10: vm.NavigateToRoutinesCommand.Execute(null); break;");
    }

    private static XElement NavList() =>
        XDocument.Parse(ShellViewXaml()).Descendants(Avalonia + "ListBox").Single(e => e.Attribute("Name")?.Value == "NavList");

    private static string ShellViewXaml() =>
        File.ReadAllText(Path.Combine(RepoRoot(), "remex.desktop", "Views", "ShellView.axaml"));

    private static string RepoRoot([CallerFilePath] string thisSourceFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisSourceFile)!, "..", ".."));
}
