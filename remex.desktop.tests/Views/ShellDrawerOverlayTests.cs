using System.Text.RegularExpressions;
using Remex.Desktop.ViewModels;
using Xunit;

namespace Remex.Desktop.Tests.Views;

/// <summary>
/// The shell's navigation drawer is a Material <c>NavigationDrawer</c> in overlay mode (RemEx-q3mle).
/// </summary>
/// <remarks>
/// <para>
/// **THE BEAD ASKED FOR A CONTROL THAT DOES NOT EXIST.** It specified
/// <c>DrawerPage DrawerLayoutBehavior="Overlay"</c>. Material.Avalonia 3.19.0 has no <c>DrawerPage</c>
/// type and no <c>DrawerLayoutBehavior</c> property — <c>DrawerPage</c> survives in the package only as
/// the resource path of a theme dictionary. The control that does the job is
/// <c>Material.Styles.Controls.NavigationDrawer</c>, and overlay-versus-inline is not a mode flag on it
/// at all. It is a consequence of <c>LeftDrawerExpandThresholdWidth</c>, which is why these tests guard
/// that property rather than a mode.
/// </para>
/// <para>
/// **THE TRAP IS THAT "OVERLAY" IS THE DEFAULT-LOOKING SETTING AND IS WRONG.** Reading
/// <c>NavigationDrawer.UpdateDesktopExpand</c>: when the threshold is <c>null</c> the control takes the
/// <c>else</c> branch and sets <c>_isLeftDrawerDesktopExpanded = true</c> — *permanently expanded*, the
/// exact opposite of what leaving it unset reads like. <c>UpdateContentMargin</c> then computes
/// <c>left = _isLeftDrawerDesktopExpanded &amp;&amp; LeftDrawerOpened ? LeftDrawerWidth : 0</c>, so opening
/// the drawer pushes the content across by the drawer's width. That is precisely the layout shift the
/// bead's acceptance criteria forbid, and it arrives by writing nothing.
/// </para>
/// <para>
/// A threshold of <see cref="ShellViewModel.NeverExpandThresholdWidth"/> takes the other branch, where
/// <c>status = width &gt; threshold</c> is false at every width, so the content margin stays zero and the
/// scrim style <c>^:left:not(:left-expand)</c> keeps matching. Any finite value silently reintroduces the
/// shift on a monitor wider than it — which is why the constant is asserted rather than merely bound.
/// </para>
/// <para>
/// A source scan, matching <see cref="DialogsDismissOnEscapeTests"/> and the queue-virtualization guard:
/// there is no headless render here, and a shell that lays out wrong throws nothing.
/// </para>
/// </remarks>
public class ShellDrawerOverlayTests
{
    private static string ShellMarkup() => File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..",
            "remex.desktop", "Views", "ShellView.axaml"));

    private static string ShellCodeBehind() => File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..",
            "remex.desktop", "Views", "ShellView.axaml.cs"));

    [Fact]
    public void TheShellHostsItsPagesInANavigationDrawer()
    {
        var xaml = ShellMarkup();

        Assert.Contains("NavigationDrawer", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("<SplitView", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void TheDrawerOpenStateBindsTwoWay()
    {
        // The scrim's PointerPressed handler assigns LeftDrawerOpened = false on the CONTROL. The
        // property is registered without a default binding mode, so a one-way binding would drop that
        // on the floor: the drawer would slide shut while IsDrawerOpen stayed true, and every nav
        // label bound to IsDrawerOpen — plus the next ToggleDrawer — would disagree with the screen.
        Assert.Matches(
            new Regex(@"LeftDrawerOpened\s*=\s*""\{Binding\s+IsDrawerOpen\s*,\s*Mode\s*=\s*TwoWay\s*\}"""),
            ShellMarkup());
    }

    [Fact]
    public void TheDrawerWidthStillComesFromTheViewModel()
    {
        // OpenPaneLength was the SplitView's open width and stays the single source of truth, rather
        // than the control theme's own 320 default.
        Assert.Matches(
            new Regex(@"LeftDrawerWidth\s*=\s*""\{Binding\s+OpenPaneLength\s*\}"""),
            ShellMarkup());
    }

    [Fact]
    public void TheExpandThresholdIsBoundRatherThanLeftUnset()
    {
        Assert.Matches(
            new Regex(@"LeftDrawerExpandThresholdWidth\s*=\s*""\{Binding\s+DrawerExpandThresholdWidth\s*\}"""),
            ShellMarkup());
    }

    [Theory]
    [InlineData(800d)]      // a small laptop
    [InlineData(1920d)]     // 1080p
    [InlineData(3840d)]     // 4K
    [InlineData(15360d)]    // a wall of monitors, and then some
    public void NoPlausibleWindowWidthExpandsTheDrawer(double windowWidth)
    {
        // Mirrors NavigationDrawer.UpdateDesktopExpand: status = w > LeftDrawerExpandThresholdWidth.
        // While that is false the drawer floats over the content and the content margin stays zero.
        Assert.False(windowWidth > ShellViewModel.NeverExpandThresholdWidth);
    }

    [Fact]
    public void TheThresholdIsInfiniteSoNoWidthCanEverReachIt()
    {
        Assert.True(double.IsPositiveInfinity(ShellViewModel.NeverExpandThresholdWidth));
    }

    [Fact]
    public void EscapeClosesTheDrawer()
    {
        // NavigationDrawer binds nothing to the keyboard — the scrim is pointer-only — so Escape is
        // the shell's job.
        var cs = ShellCodeBehind();

        Assert.Contains("Key.Escape", cs, StringComparison.Ordinal);
        Assert.Contains("IsDrawerOpen", cs, StringComparison.Ordinal);
    }

    [Fact]
    public void EscapeClosesTheSettingsPanelBeforeTheDrawer()
    {
        // The settings overlay sits ON TOP of the drawer, so Escape has to peel it off first
        // (RemEx-8sfwp). Checking IsDrawerOpen first would shut the drawer out from under a panel
        // that stays on screen — the drawer is not even visible when the panel covers it, so the
        // keypress would look like it did nothing at all.
        var body = OnKeyDownBody();

        var settings = body.IndexOf("IsSettingsPanelOpen", StringComparison.Ordinal);
        var drawer = body.IndexOf("IsDrawerOpen", StringComparison.Ordinal);

        Assert.True(settings >= 0, "OnKeyDown no longer closes the settings panel on Escape");
        Assert.True(drawer >= 0, "OnKeyDown no longer closes the drawer on Escape");
        Assert.True(settings < drawer,
            "OnKeyDown checks IsDrawerOpen before IsSettingsPanelOpen; the panel must win");

        // Precedence alone is not enough: without an early exit, control falls through and closes
        // the drawer in the same keypress.
        var settingsBranch = body[settings..drawer];

        Assert.Contains("e.Handled = true", settingsBranch, StringComparison.Ordinal);
        Assert.Contains("return;", settingsBranch, StringComparison.Ordinal);
    }

    [Fact]
    public void TheDrawerCanBeReopenedFromOutsideItself()
    {
        // The SplitView was CompactOverlay: a 64px icon rail stayed on screen when the drawer was shut,
        // and the toggle lived on the brand mark inside it. An overlay drawer has no rail, so a toggle
        // that only exists inside LeftDrawerContent disappears with the drawer and strands every nav
        // destination behind a drawer nothing can open. This asserts one lives outside it.
        var xaml = ShellMarkup();

        var contentStart = xaml.IndexOf("<material:NavigationDrawer.LeftDrawerContent>", StringComparison.Ordinal);
        var contentEnd = xaml.IndexOf("</material:NavigationDrawer.LeftDrawerContent>", StringComparison.Ordinal);

        Assert.True(contentStart >= 0 && contentEnd > contentStart,
            "ShellView.axaml no longer declares NavigationDrawer.LeftDrawerContent as a property element");

        // Comments are stripped first: the drawer toggle is discussed in prose both here and in
        // ShellView.axaml, and a guard that a comment can satisfy is not a guard.
        var outsideTheDrawer = WithoutXmlComments(xaml.Remove(contentStart, contentEnd - contentStart));

        Assert.Contains("ToggleDrawerCommand", outsideTheDrawer, StringComparison.Ordinal);
    }

    [Fact]
    public void NothingStillReachesForTheShellSplitViewByAncestorType()
    {
        // The regression this bead nearly shipped. CanvasView gated its Undo/Redo labels on
        // {Binding $parent[SplitView].IsPaneOpen, FallbackValue=False} — a binding that reached out of
        // the page and up into the shell's layout container. Deleting that container did not break the
        // build, break a test, or log anything: the ancestor lookup simply failed and the FallbackValue
        // took over, hiding both labels forever. An $parent[T] binding is a dependency on a control
        // that no compiler checks, so it has to be checked here.
        // The whole project, not just Views/ — App.axaml, Controls/ and Themes/ carry markup too, and
        // a guard that stops at one directory only proves the bug moved.
        var desktopProject = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..",
            "remex.desktop");

        var offenders = Directory.EnumerateFiles(desktopProject, "*.axaml", SearchOption.AllDirectories)
            .Where(f => File.ReadAllText(f).Contains("$parent[SplitView]", StringComparison.Ordinal))
            .Select(Path.GetFileName)
            .ToList();

        Assert.True(offenders.Count == 0,
            "These views bind to an ancestor SplitView, which the shell no longer has: " +
            string.Join(", ", offenders));
    }

    /// <summary>
    /// The braces-matched body of <c>ShellView.OnKeyDown</c>, comments stripped.
    /// </summary>
    /// <remarks>
    /// Scoped to the method rather than the whole file because both property names appear elsewhere
    /// in the code-behind — <c>OnPropertyChanged</c> watches <c>IsSettingsPanelOpen</c> above it —
    /// so a file-wide index comparison would pass no matter what order Escape actually handles them
    /// in. Comments go for the same reason the drawer-toggle guard strips them: the precedence is
    /// discussed in prose right above the method, and a guard a comment can satisfy is not a guard.
    /// </remarks>
    private static string OnKeyDownBody()
    {
        var cs = ShellCodeBehind();

        var signature = cs.IndexOf("protected override void OnKeyDown(", StringComparison.Ordinal);
        Assert.True(signature >= 0, "ShellView.axaml.cs no longer overrides OnKeyDown");

        var open = cs.IndexOf('{', signature);
        Assert.True(open > signature, "ShellView.OnKeyDown has no body");

        var depth = 0;
        var close = -1;

        for (var i = open; i < cs.Length; i++)
        {
            if (cs[i] == '{')
            {
                depth++;
            }
            else if (cs[i] == '}' && --depth == 0)
            {
                close = i;
                break;
            }
        }

        Assert.True(close > open, "ShellView.OnKeyDown's braces do not balance");

        return WithoutCsComments(cs[open..(close + 1)]);
    }

    private static string WithoutCsComments(string source) =>
        Regex.Replace(
            Regex.Replace(source, @"/\*.*?\*/", string.Empty, RegexOptions.Singleline),
            @"//[^\r\n]*",
            string.Empty);

    private static string WithoutXmlComments(string xaml) =>
        Regex.Replace(xaml, "<!--.*?-->", string.Empty, RegexOptions.Singleline);
}
