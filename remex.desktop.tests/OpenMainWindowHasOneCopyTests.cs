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
    public void NobodyHandRollsAPartialCopyByActivatingAWindowThemselves()
    {
        // THE ABOVE TEST CANNOT SEE A TWO-STEP COPY, and one was written (RemEx-6bfyt). A consent
        // dialog needed a surfaced owner, so it grew a local `if (!owner.IsVisible) Show();` plus
        // `owner.Activate();` - no WindowState line at all, in App.axaml.cs, which is the file the
        // scan above EXPECTS to match. Both of its assertions passed on a live violation of the
        // invariant they exist to protect.
        //
        // A partial copy is the dangerous shape, not a whole one: it works in every window state the
        // author happened to try. The one it misses is Minimized, which reports IsVisible true, so
        // Show is skipped and Activate leaves the window in the taskbar.
        //
        // Activate is the load-bearing step that a copy cannot omit and still appear to work, so it
        // is the honest thing to count. Exactly one, and it is the helper's own line.
        var activations = File
            .ReadAllLines(Path.Combine(RepoRoot(), "remex.desktop", "App.axaml.cs"))
            .Select(line => line.Trim())
            .Where(line => !line.StartsWith("//", System.StringComparison.Ordinal)
                        && !line.StartsWith("///", System.StringComparison.Ordinal)
                        && line.Contains(".Activate()", System.StringComparison.Ordinal))
            .ToArray();

        // An exact set, like the test above: a second call site fails whatever it is named, and
        // renaming or moving the helper's line fails too rather than silently emptying the check.
        activations.Should().Equal(["desktop.MainWindow.Activate();"],
            "every entry point must surface the main window through BringMainWindowToFront(), which "
            + "does all three load-bearing steps - a local Show()/Activate() pair silently skips a "
            + "MINIMIZED window");
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
