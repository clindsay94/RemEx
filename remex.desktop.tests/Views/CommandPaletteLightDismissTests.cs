using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace Remex.Desktop.Tests.Views;

/// <summary>
/// Pins the command palette's light-dismiss behaviour — no bead, reported live by Connor
/// 2026-08-31 alongside the title-bar overlap and the popup opacity floor.
/// </summary>
/// <remarks>
/// <para>
/// THE BUG. <c>ShellViewModel.OpenCommandPalette</c> opened the palette with
/// <c>window.ShowDialog(mainWindow)</c> — a true modal, which disables <c>mainWindow</c> for as
/// long as the palette is open. A click meant to dismiss the palette landed on the disabled main
/// window instead: Windows played its "control is disabled" beep, the click never reached the
/// palette, and it never lost focus — so <c>Deactivated</c>, which the constructor already wires
/// to close it, never fired. Esc was the only way out, because <c>KeyDown</c> is handled locally by
/// the (still-active) palette window regardless of modality.
/// </para>
/// <para>
/// ASSERTED ON THE SOURCE. There is no Avalonia.Headless reference anywhere in this assembly (see
/// <c>DestructiveActionFailClosedTests</c>), so a modal-vs-non-modal window and a real OS focus
/// change cannot be exercised in this suite — only the shape that causes or prevents the bug can
/// be pinned.
/// </para>
/// </remarks>
public class CommandPaletteLightDismissTests
{
    [Fact]
    public void OpenCommandPalette_UsesShowNotShowDialog_WhenAMainWindowExists()
    {
        var source = ShellViewModelSource();
        var method = ExtractMethod(source, "OpenCommandPalette");

        method.Should().Contain("window.Show(mainWindow)",
            "ShowDialog disables mainWindow for the palette's whole lifetime, which is exactly "
            + "what turned an outside click into a beep instead of a dismiss");

        // Matched on the CALL, not the bare word — this method's own explanatory comment says
        // "ShowDialog" several times on purpose, and a substring check would fail on its own prose.
        method.Should().NotMatchRegex(@"\.ShowDialog\(",
            "any ShowDialog call on the palette window reintroduces the modal beep, whether or "
            + "not it is the same call site this fix touched");
    }

    [Fact]
    public void ThePaletteWindow_StaysTopmostAndCenteredOnItsOwner()
    {
        // Non-modal has no free "always above the owner" or "centered on the owner" behaviour the
        // way ShowDialog did — both have to be asked for explicitly, or the palette can fall behind
        // the main window or open somewhere else on screen.
        var window = File.ReadAllText(Path.Combine(RepoRoot(), "remex.desktop", "Views", "CommandPaletteWindow.axaml"));

        window.Should().MatchRegex(@"Topmost=""True""",
            "without Topmost, a non-modal palette can fall behind the main window it no longer disables");
        window.Should().MatchRegex(@"WindowStartupLocation=""CenterOwner""",
            "CenterOwner needs an Owner to center on — Show(mainWindow) still supplies one, ShowDialog is not required for this");
    }

    [Fact]
    public void ClickOutsideDismissal_RunsThroughTheSameCommandAsEscape_NotADirectClose()
    {
        // Esc goes through CommandPaletteViewModel.Dismiss() (the DismissCommand KeyBinding in the
        // .axaml). Before this fix, OnDeactivated (the click-outside path) called Close() directly,
        // bypassing Dismiss() entirely — currently a no-op difference, since Dismiss() does nothing
        // but raise CloseRequested, but a future Dismiss() that clears query state or unsubscribes
        // something would silently apply to Esc only. Routing both through DismissCommand keeps
        // them from drifting apart again.
        var axaml = File.ReadAllText(Path.Combine(RepoRoot(), "remex.desktop", "Views", "CommandPaletteWindow.axaml"));
        axaml.Should().MatchRegex(@"<KeyBinding Gesture=""Escape"" Command=""\{Binding DismissCommand\}""\s*/>",
            "Esc's own path must still go through DismissCommand for this test's premise to hold");

        var codeBehind = File.ReadAllText(Path.Combine(RepoRoot(), "remex.desktop", "Views", "CommandPaletteWindow.axaml.cs"));
        var onDeactivated = ExtractMethod(codeBehind, "OnDeactivated");

        onDeactivated.Should().MatchRegex(@"DataContext is CommandPaletteViewModel vm\)\s*\n\s*vm\.DismissCommand\.Execute\(null\)",
            "the click-outside path has to call the same DismissCommand Esc uses, not Close() directly");
    }

    [Fact]
    public void ClickOutsideDismissal_IsPosted_NotRunInsideTheDeactivationNotification()
    {
        // RemEx-27a0s. Deactivated is raised from inside WM_ACTIVATE(WA_INACTIVE) — Windows is
        // part-way through handing activation to whatever was clicked. Closing the window there
        // aborts that transfer, and Windows falls back to the next top-level window in the GLOBAL
        // z-order rather than the intended target. Topmost="True" makes the owner an unlikely
        // fallback, so the winner is usually an unrelated application.
        //
        // Measured before the fix, with the palette open and the click landing on empty RemEx
        // background: WindowFromPoint returned RemEx's own main window HWND, the palette closed,
        // and GetForegroundWindow came back as WindowsTerminal. Nothing fell through in the
        // hit-testing sense — the ACTIVATION did, which is why it reads to a user as "my click went
        // through the app".
        //
        // There is no headless render or window harness in this repo, so nothing here can open the
        // palette and click beside it. The guard has to be the shape of the handler.
        var codeBehind = File.ReadAllText(Path.Combine(RepoRoot(), "remex.desktop", "Views", "CommandPaletteWindow.axaml.cs"));
        var onDeactivated = ExtractMethod(codeBehind, "OnDeactivated");

        onDeactivated.Should().MatchRegex(@"Dispatcher\.UIThread\.Post\(",
            "the dismiss has to be queued so Windows can finish the activation transfer first");
        onDeactivated.Should().MatchRegex(@"DispatcherPriority\.Background",
            "Normal priority still runs ahead of the input and layout work the activation queues — " +
            "Background is what actually lands after it");

        // The posted callback can outlive the window: executing an entry raises CloseRequested and
        // deactivates, and a destructive entry's confirmation dialog takes activation while the
        // palette is still up. Both would otherwise dismiss a window that is already closing.
        onDeactivated.Should().Contain("_closing",
            "the posted callback has to bail out if the window started closing while it was queued");
        codeBehind.Should().MatchRegex(@"protected override void OnClosing\(WindowClosingEventArgs e\)\s*\{\s*_closing = true;",
            "_closing has to be set from OnClosing, or the guard never trips");
    }

    private static string ShellViewModelSource([CallerFilePath] string thisSourceFile = "")
        => File.ReadAllText(Path.Combine(RepoRoot(thisSourceFile), "remex.desktop", "ViewModels", "ShellViewModel.cs"));

    private static string ExtractMethod(string source, string methodName)
    {
        // Take everything from the method's opening brace to the matching closing brace at column
        // 4 (the class's own indent level) — every method in these files is a one-level-nested
        // member, so this is the same "next same-indent close brace" heuristic ThemeKeyCoverageTests
        // already relies on elsewhere in this suite.
        var match = Regex.Match(source, $@"{Regex.Escape(methodName)}\s*\([^)]*\)\s*\{{.*?\n    \}}", RegexOptions.Singleline);
        match.Success.Should().BeTrue($"{methodName} moved, was renamed, or changed shape — update this test's extraction");
        return match.Value;
    }

    private static string RepoRoot([CallerFilePath] string thisSourceFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisSourceFile)!, "..", ".."));
}
