using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace Remex.Desktop.Tests.Views;

/// <summary>
/// Guards the top bar's move from a transparent, non-hit-testable spacer <c>Border</c> to a real
/// Material <c>ColorZone</c> app bar (RemEx-a3prn), and the two sharp edges the bead called out
/// explicitly: window dragging and toggle duplication.
/// </summary>
/// <remarks>
/// <para>
/// THE DRAG TRAP. The old spacer worked by NOT existing, hit-test-wise (<c>Background="Transparent"
/// IsHitTestVisible="False"</c>), so a press anywhere in it fell through to WindowChrome.axaml's
/// underlay <c>PART_TitleBar</c> two z-order layers down and Avalonia's own window-decorations
/// machinery moved the window. A real <c>ColorZone</c> has a real surface (Material.Avalonia
/// 3.19.0's own <c>ColorZone.axaml</c>, <c>^[Mode=Standard]</c>, confirmed against the template
/// source rather than assumed - <c>Background</c> resolves to <c>MaterialPaperBrush</c>), which
/// covers that fall-through path for its whole width. <see cref="TheAppBar_WiresItsOwnDragHandler"/>
/// and <see cref="TheDragHandler_OnlyMovesTheWindowOnALeftPress"/> pin the replacement: an explicit
/// <c>PointerPressed</c> → <c>BeginMoveDrag</c>, the same pattern <c>TrayFlyoutWindow.OnHeaderPressed</c>
/// already uses for its own header, rather than a second invisible-geometry trick.
/// </para>
/// <para>
/// THE TOGGLE TRAP. The bead's own text warns there were already two ways to open the drawer (the
/// title-bar button and the drawer's own round brand-mark button) and asks which one the app bar
/// should own, "so it does not end up with three toggles". <see cref="TheAppBar_OwnsTheExistingDrawerToggle"/>
/// and <see cref="ThereAreExactlyTwoDrawerToggleBindings"/> pin that DrawerToggle MOVED into the bar
/// rather than a new one being added alongside it, and that the drawer's own brand-mark toggle
/// (a different affordance for a different state - only on screen while the drawer is open) is the
/// only other one.
/// </para>
/// <para>
/// A source scan, not a behavioural test - there is no headless Avalonia harness in this repo (see
/// <see cref="ShellSettingsSideSheetTests"/> for the same limitation on the settings sheet), so
/// nothing here can actually press the bar and watch the window move. What this proves is that the
/// wiring exists and is shaped the way the acceptance criteria require.
/// </para>
/// </remarks>
public class ShellAppBarTests
{
    [Fact]
    public void TheAppBar_IsAMaterialColorZoneNotATransparentSpacer()
    {
        var xaml = ShellMarkup();

        xaml.Should().Contain("<material:ColorZone Grid.Row=\"0\" Name=\"AppBar\"",
            "the title-bar spacer has to become the real Material app bar control, not a Border pretending to be one");

        // The exact hand-rolled spacer this replaced. If it is back, the migration was reverted.
        xaml.Should().NotMatchRegex(
            @"<Border Grid\.Row=""0""[^>]*Background=""Transparent""[^>]*IsHitTestVisible=""False""",
            "the old fall-through spacer has to be gone, not merely unused, since it no longer does anything useful once a real surface sits in the same row");
    }

    [Fact]
    public void TheAppBar_OwnsTheExistingDrawerToggle()
    {
        var block = AppBarElement();

        block.Should().MatchRegex(@"<Button Name=""DrawerToggle""",
            "DrawerToggle has to live INSIDE the app bar now, not beside it as an overlapping sibling");
        block.Should().MatchRegex(@"Command=""\{Binding ToggleDrawerCommand\}""",
            "the toggle inside the bar has to be wired to the same command the old sibling button used");
    }

    [Fact]
    public void ThereAreExactlyTwoDrawerToggleBindings()
    {
        // ONE in the app bar (DrawerToggle, this bead) and ONE in the drawer's own round brand-mark
        // button (pre-existing, RemEx-zi3ua) - a different affordance for a different state, only on
        // screen while the drawer is actually open. A third would be the exact regression the bead's
        // own text warns about.
        var count = Regex.Matches(ShellMarkup(), @"Command=""\{Binding ToggleDrawerCommand\}""").Count;

        count.Should().Be(2,
            "exactly two ToggleDrawerCommand bindings should exist - the app bar's DrawerToggle and " +
            "the drawer's own brand-mark button - not a third");
    }

    [Fact]
    public void TheAppBar_DoesNotDuplicateTheWindowControlButtons()
    {
        // WindowChrome.axaml's BackdropSafeWindowDecorations already draws minimize/maximize/close
        // (RemexDrawnCaptionButton, top-right, independent of ShellView's own content) - adding a
        // second set here would be the same "three toggles" mistake the bead warns about, just for
        // window controls instead of the drawer.
        var block = AppBarElement();

        block.Should().NotMatchRegex(@"ElementRole=""(MinimizeButton|MaximizeButton|CloseButton|FullScreenButton)""",
            "window-control buttons already exist in WindowChrome.axaml's decorations overlay - this bar must not redraw them");
    }

    [Fact]
    public void TheAppBarTitle_TracksTheActiveNavIndexAndDoesNotStealDragClicks()
    {
        var block = AppBarElement();

        block.Should().MatchRegex(
            @"<TextBlock Name=""AppBarTitle"" IsHitTestVisible=""False""\s*\n\s*Text=""\{Binding ActiveNavIndex, Converter=\{x:Static conv:NavIndexToTitleConverter\.Instance\}\}""",
            "the current-page title has to bind to ActiveNavIndex through the real converter, and IsHitTestVisible=\"False\" has to sit right on the element - " +
            "without it a press over the title text would stop there instead of reaching the drag handler");
    }

    [Fact]
    public void TheAppBar_WiresItsOwnDragHandler()
    {
        var block = AppBarElement();

        block.Should().MatchRegex(@"PointerPressed=""OnAppBarPointerPressed""",
            "the bar's own surface now blocks the old fall-through-to-WindowChrome drag path, so it has to drive BeginMoveDrag itself");

        var handler = ExtractMethod(ShellCodeBehind(), "OnAppBarPointerPressed");
        handler.Should().Contain("BeginMoveDrag",
            "dragging has to actually move the window, matching TrayFlyoutWindow.OnHeaderPressed's own pattern");
    }

    [Fact]
    public void TheDragHandler_OnlyMovesTheWindowOnALeftPress()
    {
        var handler = ExtractMethod(ShellCodeBehind(), "OnAppBarPointerPressed");

        handler.Should().Contain("IsLeftButtonPressed",
            "a right-click or other button on the bar must not kick off a window drag");

        // No explicit "was this DrawerToggle" guard should exist or is needed: Avalonia's own
        // Button.OnPointerPressed sets e.Handled=true on a left press, and this is a plain (non
        // handledEventsToo) bubble listener, so a press that started on the button never reaches
        // here. If a future edit adds e.Handled tracking, that is a sign the assumption changed and
        // this test's own doc comment is the place to re-verify it against Avalonia's source.
        handler.Should().Contain("BeginMoveDrag(e)");
    }

    [Fact]
    public void TheAppBar_HidesWithTheRestOfTheImmersiveChrome()
    {
        // Matches every other overlay chrome element in this file (drawer, FAB, snackbar) - all
        // gated on !IsShellChromeHidden so the fullscreen remote-desktop view has nothing left over
        // the content, including a bar someone could still try to drag.
        var block = AppBarElement();

        block.Should().MatchRegex(@"IsVisible=""\{Binding !IsShellChromeHidden\}""",
            "the app bar has to hide in immersive mode the same way the drawer/FAB/snackbar already do");
    }

    [Fact]
    public void MainWindow_StillAppliesUiScaleAroundShellView()
    {
        // Regression guard for the bead's "UI scale still applies cleanly" acceptance criterion.
        // This bead does not touch MainWindow.axaml at all - the app bar lives inside ShellView,
        // which the LayoutTransformControl already wraps wholesale - but pinning it here means a
        // FUTURE change that moves ShellView out from under the transform (or drops the transform)
        // fails loudly instead of silently un-scaling the new app bar along with everything else.
        var xaml = File.ReadAllText(
            Path.Combine(RepoRoot(), "remex.desktop", "MainWindow.axaml"));

        xaml.Should().MatchRegex(
            @"<LayoutTransformControl>\s*<LayoutTransformControl\.LayoutTransform>\s*" +
            @"<ScaleTransform ScaleX=""\{DynamicResource UiScale\}"" ScaleY=""\{DynamicResource UiScale\}""/>\s*" +
            @"</LayoutTransformControl\.LayoutTransform>\s*<views:ShellView\s*/>\s*</LayoutTransformControl>",
            "ShellView (and therefore the new app bar) has to stay inside the whole-shell UiScale transform");
    }

    // ─────────────────────────── plumbing ───────────────────────────

    private static string AppBarElement()
    {
        var xaml = ShellMarkup();
        var start = xaml.IndexOf("<material:ColorZone Grid.Row=\"0\" Name=\"AppBar\"", StringComparison.Ordinal);
        start.Should().BeGreaterThan(-1, "the app bar element has to exist");

        var end = xaml.IndexOf("</material:ColorZone>", start, StringComparison.Ordinal);
        end.Should().BeGreaterThan(-1, "the app bar element has to be closed");

        return xaml.Substring(start, end - start);
    }

    private static string ShellMarkup()
        => File.ReadAllText(Path.Combine(RepoRoot(), "remex.desktop", "Views", "ShellView.axaml"));

    private static string ShellCodeBehind()
        => File.ReadAllText(Path.Combine(RepoRoot(), "remex.desktop", "Views", "ShellView.axaml.cs"));

    /// <summary>
    /// Same "next same-indent close brace" heuristic <c>ShellSettingsSideSheetTests</c> already
    /// relies on: every member here is a one-level-nested method, so the first <c>\n    }</c> after
    /// the opening brace is the method's own close.
    /// </summary>
    private static string ExtractMethod(string source, string methodName)
    {
        var match = Regex.Match(source, $@"{Regex.Escape(methodName)}\s*\([^)]*\)\s*\{{.*?\n    \}}", RegexOptions.Singleline);
        match.Success.Should().BeTrue($"{methodName} moved, was renamed, or changed shape - update this test's extraction");
        return match.Value;
    }

    private static string RepoRoot([CallerFilePath] string thisSourceFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisSourceFile)!, "..", ".."));
}
