using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace Remex.Desktop.Tests;

/// <summary>
/// "Open the main window" exists once, not once per entry point (RemEx-b3bi).
/// </summary>
/// <remarks>
/// The tray menu, the tray flyout's open button and a pressed balloon each carried their own copy.
/// They agreed, which is what makes the drift invisible: the failure is one entry point quietly not
/// restoring a MINIMIZED window while the other two do, and nobody notices until they use that entry
/// point with the window in that state. A source scan is the honest instrument — the three call
/// sites are event handlers on Avalonia windows, which a headless test assembly cannot construct.
/// </remarks>
public class OpenMainWindowHasOneCopyTests
{
    [Fact]
    public void OnlyOneFileSetsMainWindowStateBackToNormal()
    {
        var offenders = Directory
            .GetFiles(Path.Combine(RepoRoot(), "remex.desktop"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                     && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .Where(f => File.ReadLines(f).Any(line =>
                !line.TrimStart().StartsWith("//", System.StringComparison.Ordinal)
                && Regex.IsMatch(line, @"MainWindow\.WindowState\s*=")))
            .Select(Path.GetFileName)
            .OrderBy(f => f, System.StringComparer.Ordinal)
            .ToArray();

        // App.axaml.cs and nothing else. Asserted as an exact set rather than a count, so a NEW
        // fourth copy fails and so does the helper being moved without this being updated.
        offenders.Should().Equal(["App.axaml.cs"]);
    }

    [Fact]
    public void TheTwoTrayEntryPointsGoThroughTheHelper()
    {
        // The floor. "No copy in TrayFlyoutWindow" passes just as happily if somebody deletes the
        // open button altogether, which is the failure this whole area started as.
        foreach (var view in new[] { "TrayFlyoutWindow.axaml.cs", "TrayBalloonWindow.axaml.cs" })
        {
            var source = File.ReadAllText(Path.Combine(RepoRoot(), "remex.desktop", "Views", view));
            source.Should().Contain("App.BringMainWindowToFront()",
                $"{view} must still open the main window, and through the one copy of that logic");
        }
    }

    private static string RepoRoot([CallerFilePath] string thisSourceFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisSourceFile)!, ".."));
}
