using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Xml.Linq;
using FluentAssertions;
using Xunit;

namespace Remex.Desktop.Tests.Views;

/// <summary>Source pins for the PC-3 search-box clear buttons and the Task Manager Delete key.</summary>
public class TaskManagerViewSourceTests
{
    [Fact]
    public void TheListHasADeleteKeyBindingThatRunsTheConfirmedKill()
    {
        var doc = XDocument.Parse(ViewSource("TaskManagerView"));

        var binding = doc.Descendants().Where(e => e.Name.LocalName == "KeyBinding").Should().ContainSingle().Subject;

        binding.Attribute("Gesture")!.Value.Should().Be("Delete");
        binding.Attribute("Command")!.Value.Should().Be("{Binding KillProcessCommand}");
        binding.Attribute("CommandParameter")!.Value.Should().Be("{Binding SelectedProcess}");
    }

    [Theory]
    [InlineData("FileTransferView")]
    [InlineData("TaskManagerView")]
    [InlineData("AppLauncherView")]
    [InlineData("DiagnosticLogsView")]
    public void TheSearchBoxHasAClearButton(string view)
    {
        var doc = XDocument.Parse(ViewSource(view));

        var search = doc.Descendants()
            .Where(e => e.Name.LocalName == "TextBox")
            .Where(e => e.Attribute("Text")?.Value is "{Binding SearchText}" or "{Binding SearchQuery}")
            .ToList();

        search.Should().NotBeEmpty("anti-vacuity: the search box must be found");
        search.Should().OnlyContain(e =>
            e.Attribute("Classes")!.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains("clearButton"));
    }

    private static string ViewSource(string viewName)
        => File.ReadAllText(Path.Combine(RepoRoot(), "remex.desktop", "Views", viewName + ".axaml"));

    private static string RepoRoot([CallerFilePath] string thisSourceFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisSourceFile)!, "..", ".."));
}
