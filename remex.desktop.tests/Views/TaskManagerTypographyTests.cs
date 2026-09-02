using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using FluentAssertions;
using Xunit;

namespace Remex.Desktop.Tests.Views;

/// <summary>
/// Guards the Material type-scale sweep of TaskManagerView (RemEx-ygit2): no inline
/// <c>FontSize</c> on a <c>TextBlock</c>, the sole surviving <c>FontSize</c> belongs to the
/// search <c>TextBox</c> (exception 4, conventions brief), and the kill button carries the
/// danger button vocabulary instead of raw brush/geometry attributes.
/// </summary>
public class TaskManagerTypographyTests
{
    private const string Avalonia = "https://github.com/avaloniaui";

    [Fact]
    public void NoTextBlockInTaskManagerView_CarriesAnInlineFontSize()
    {
        var doc = XDocument.Parse(ViewSource("TaskManagerView"));

        var offenders = doc.Descendants(XName.Get("TextBlock", Avalonia))
            .Where(e => e.Attribute("FontSize") != null)
            .ToList();

        offenders.Should().BeEmpty("TextBlocks move onto Theme={StaticResource ...TextBlock} keys");
    }

    [Fact]
    public void TheOnlyFontSizeInTheFile_IsOnTheSearchTextBox()
    {
        var source = ViewSource("TaskManagerView");
        var matches = Regex.Matches(source, "FontSize=\"");

        matches.Count.Should().Be(1, "only the search TextBox keeps its inline size (exception 4)");

        var doc = XDocument.Parse(source);
        var textBoxesWithFontSize = doc.Descendants(XName.Get("TextBox", Avalonia))
            .Count(e => e.Attribute("FontSize") != null);

        textBoxesWithFontSize.Should().Be(1);
    }

    [Fact]
    public void TheKillButton_CarriesTheDangerVocabularyAndNoRawStyling()
    {
        var doc = XDocument.Parse(ViewSource("TaskManagerView"));

        var killButtons = doc.Descendants(XName.Get("Button", Avalonia))
            .Where(e => e.Attribute("Command")?.Value ==
                        "{Binding $parent[UserControl].((vm:TaskManagerViewModel)DataContext).KillProcessCommand}")
            .ToList();

        killButtons.Should().ContainSingle("exactly one Button in the row template kills the process");

        var button = killButtons[0];
        button.Attribute("Classes")?.Value.Split(' ').Should().Contain(new[] { "secondary", "danger", "compact" });

        button.Attribute("Background").Should().BeNull();
        button.Attribute("Foreground").Should().BeNull();
        button.Attribute("CornerRadius").Should().BeNull();
        button.Attribute("Padding").Should().BeNull();
        button.Attribute("BorderThickness").Should().BeNull();
        button.Attribute("Cursor").Should().BeNull();
        button.Attribute("FontSize").Should().BeNull();
        button.Attribute("FontWeight").Should().BeNull();
    }

    [Fact]
    public void ExactlyTwoElements_BindKillProcessCommandWithTheRowAsParameter()
    {
        var doc = XDocument.Parse(ViewSource("TaskManagerView"));
        const string killCommand = "{Binding $parent[UserControl].((vm:TaskManagerViewModel)DataContext).KillProcessCommand}";

        var binders = doc.Descendants()
            .Where(e => e.Attribute("Command")?.Value == killCommand)
            .ToList();

        binders.Should().HaveCount(2, "the row's Button and its ContextMenu MenuItem both kill the process");
        binders.Select(e => e.Name.LocalName).Should().BeEquivalentTo(new[] { "Button", "MenuItem" });
        binders.Should().OnlyContain(e => e.Attribute("CommandParameter")!.Value == "{Binding}");
    }

    private static string ViewSource(string viewName)
        => File.ReadAllText(Path.Combine(RepoRoot(), "remex.desktop", "Views", viewName + ".axaml"));

    private static string RepoRoot([CallerFilePath] string thisSourceFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisSourceFile)!, "..", ".."));
}
