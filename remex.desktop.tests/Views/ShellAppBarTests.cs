using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace Remex.Desktop.Tests.Views;

/// <summary>
/// Guards the top bar's move from a transparent, non-hit-testable spacer <c>Border</c> to a real
/// Material <c>ColorZone</c> app bar (RemEx-a3prn), and what an Opus review of the first landed
/// version (commit 6522b12) found wrong with it: an unseeded Material palette painted opaque over
/// the Mica backdrop, two competing titles in one 32px strip, and a hand-wired drag handler that
/// silently dropped double-click-to-maximize and the system menu.
/// </summary>
/// <remarks>
/// <para>
/// THE PALETTE TRAP. <c>ColorZone</c>'s <c>^[Mode=Standard]</c> (Material.Avalonia 3.19.0's own
/// <c>ColorZone.axaml</c>) is an ACTIVATED selector at <c>BindingPriority.StyleTrigger</c> - the
/// same trap <c>material-avalonia-activated-setters-outrank-plain-overrides</c> documents - so
/// <see cref="TheAppBarsBackgroundAndForeground_AreLocalValuesFromRemExsPalette"/> asserts the
/// override sits as a plain attribute directly on the element (<c>BindingPriority.LocalValue</c>,
/// which actually outranks it) rather than in an app-level <c>Style</c> that would compile, match,
/// and still lose. It also pins the specific brush: <c>MaterialTheme.CurrentTheme</c> is never
/// seeded with Material's own Body/Paper/Card roles (only Primary/Secondary), so the un-overridden
/// default resolves to Material's OWN #FAFAFA/#303030 constants - WindowChrome.axaml already names
/// #303030 as "MaterialPaperBrush exactly" - not this app's palette, and it is fully opaque over a
/// window that runs <c>TransparencyLevelHint="Mica, AcrylicBlur, None"</c>.
/// </para>
/// <para>
/// THE TWO-TITLES TRAP, twice in one bead. <see cref="TheNativeWindowTitleText_IsHidden"/> and
/// <see cref="TheAppBarTitle_IsLeadingWithABoundedWidth"/> pin the resolution: WindowChrome's own
/// static "RemEx — Command Center" text (change #5 in that file's header) is turned off entirely,
/// and this bar's own live page title sits at the conventional leading position, right of
/// <c>DrawerToggle</c>, with a bounded <c>MaxWidth</c> and <c>CharacterEllipsis</c> rather than
/// growing unbounded toward the caption strip.
/// </para>
/// <para>
/// THE DRAG TRAP, corrected. A hand-wired <c>PointerPressed</c> → <c>BeginMoveDrag</c> handler (the
/// first version of this bar) only ever produces a plain move - Windows' double-click-to-maximize
/// and right-click system menu are separate NC (non-client) message flows that a client-side
/// pointer handler cannot synthesize. <see cref="TheAppBar_CarriesTheTitleBarElementRole"/> and
/// <see cref="TheDrawerToggle_OverridesTheInheritedTitleBarRole"/> pin the real fix instead:
/// <c>WindowDecorationProperties.ElementRole="TitleBar"</c> directly on this control, which both
/// Win32's <c>WindowImpl.CustomCaptionProc</c> and X11's <c>X11Window</c> resolve by walking UP the
/// visual-parent chain from the topmost hit-test-visible element at the click point
/// (<c>PresentationSource.GetChromeRoleFromVisual</c>, confirmed against Avalonia 12.1.1's own
/// source) - restoring the full native contract at once, and requiring <c>DrawerToggle</c> to carry
/// its OWN role (<c>DecorationsElement</c>, the same one <c>RemexDrawnCaptionButton</c> already uses
/// for the caption buttons) so the ancestor walk does not resolve the button's own clicks to
/// TitleBar too.
/// </para>
/// <para>
/// THE TOGGLE TRAP. The bead's own text warns there were already two ways to open the drawer (the
/// title-bar button and the drawer's own round brand-mark button) and asks which one the app bar
/// should own, "so it does not end up with three toggles". <see cref="TheAppBar_OwnsTheExistingDrawerToggle"/>
/// and <see cref="ThereAreExactlyTwoDrawerToggleBindings"/> pin that DrawerToggle MOVED into the bar
/// rather than a new one being added alongside it, and that the drawer's own brand-mark toggle
/// (a different affordance for a different state - only on screen while the drawer is open) is the
/// only other one. <see cref="TheAppBar_DoesNotDuplicateTheWindowControlButtons"/> pins the same
/// non-duplication for minimize/maximize/close/fullscreen, checking for a reused
/// <c>RemexDrawnCaptionButton</c> theme reference and not merely the attached-property names.
/// </para>
/// <para>
/// A source scan, not a behavioural test - there is no headless Avalonia harness in this repo (see
/// <see cref="ShellSettingsSideSheetTests"/> for the same limitation on the settings sheet), so
/// nothing here can actually press the bar and watch the window move or maximize. What this proves
/// is that the wiring exists and is shaped the way the acceptance criteria - and the review - require.
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
    public void TheAppBarsBackgroundAndForeground_AreLocalValuesFromRemExsPalette()
    {
        // THE LOAD-BEARING DETAIL (Opus review of 6522b12, HIGH 1). ColorZone's own
        // ^[Mode=Standard] selector is ACTIVATED at BindingPriority.StyleTrigger - an app-level
        // Style override would compile, match, and still lose to it, exactly like the memory entry
        // material-avalonia-activated-setters-outrank-plain-overrides describes for SideSheet's
        // scrim. A plain attribute on the element itself resolves at BindingPriority.LocalValue,
        // which DOES outrank StyleTrigger, so this has to be an attribute on the opening tag, not a
        // Style block anywhere in the file.
        var openTag = AppBarOpenTag();

        openTag.Should().MatchRegex(@"Background=""\{DynamicResource GlassBaseDarkBrush\}""",
            "MaterialTheme.CurrentTheme is never seeded with Material's own Body/Paper/Card roles, " +
            "so the un-overridden default is Material's OWN opaque #FAFAFA/#303030 constants, not " +
            "this app's palette, and it fully covers the Mica/Acrylic backdrop this window runs");
        openTag.Should().MatchRegex(@"Foreground=""\{DynamicResource TextPrimaryBrush\}""",
            "the bar's own content (DrawerToggle's icon) already paints with TextPrimaryBrush - the " +
            "surface has to read from the SAME palette as its content, not Material's MaterialBodyBrush");

        // Style blocks in this file that could re-introduce the same trap via a different route.
        ShellMarkup().Should().NotMatchRegex(@"Style Selector=""material\|ColorZone[^""]*""\s*>\s*<Setter Property=""(Background|Foreground)""",
            "an app-level Style setter for ColorZone's Background/Foreground would lose to the " +
            "theme's own activated selector regardless of what it sets - the override has to be a " +
            "local value on the element");
    }

    [Fact]
    public void TheNativeWindowTitleText_IsHidden()
    {
        // Two titles in one 32px bar was the wrong outcome, not a magic-number placement bug to
        // paper over (Opus review of 6522b12, HIGH 2). WindowChrome's own decorations Overlay
        // painted "RemEx — Command Center" ON TOP of this control - Window.Title is still the
        // taskbar/Alt-Tab label either way, so turning off the on-screen copy costs nothing real.
        var chrome = ChromeMarkup();

        var panel = Regex.Match(chrome,
            @"<Panel x:Name=""PART_TitleTextPanel""(?<attrs>.*?)>(?<body>.*?)</Panel>",
            RegexOptions.Singleline);
        panel.Success.Should().BeTrue("PART_TitleTextPanel has to exist - it is the window title's host");

        panel.Groups["body"].Value.Should().MatchRegex(@"<TextBlock\b[^>]*\bIsVisible=""False""",
            "the title TextBlock itself has to be turned off, or it still paints over this bar's own live page title");
    }

    [Fact]
    public void TheAppBarTitle_IsLeadingWithABoundedWidth()
    {
        // Opus review of 6522b12, HIGH 2 / LOW 1. The first version right-aligned this text with a
        // fixed 190px margin that assumed the caption strip was 180px (it measures 187px in
        // WindowChrome.axaml) and lived inside the UiScale transform while the real caption strip
        // does not - a scale/window-width combination could paint text under the caption buttons.
        // Leading + a small bounded MaxWidth + ellipsis removes the growth direction entirely
        // rather than tuning the same magic number tighter.
        var block = AppBarElement();

        block.Should().MatchRegex(
            @"<TextBlock Name=""AppBarTitle"" IsHitTestVisible=""False""\s*\n\s*" +
            @"Text=""\{Binding ActiveNavIndex, Converter=\{x:Static conv:NavIndexToTitleConverter\.Instance\}\}""\s*\n\s*" +
            @"HorizontalAlignment=""Left""",
            "the title has to sit at the conventional leading position now, not right-aligned toward the caption strip");
        block.Should().MatchRegex(@"MaxWidth=""\d+""",
            "an unbounded title can still grow into the caption strip on a long locale string at a narrow window");
        block.Should().MatchRegex(@"TextTrimming=""CharacterEllipsis""",
            "a bounded MaxWidth without trimming just clips the text abruptly instead of showing that it was cut");
    }

    [Fact]
    public void TheAppBar_CarriesTheTitleBarElementRole()
    {
        // Opus review of 6522b12, HIGH 3. A hand-wired PointerPressed/BeginMoveDrag handler (the
        // first version of this bar) only reproduces a plain window move - it cannot synthesize
        // WM_NCLBUTTONDBLCLK (double-click-to-maximize) or the right-click system menu, because
        // those are native NC message flows a client-side pointer handler never enters.
        // WindowDecorationProperties.ElementRole="TitleBar" restores the full contract at once, and
        // Avalonia's own doc comment on the property confirms it: "Can be applied to any element in
        // the visual tree, not limited to decoration children."
        var openTag = AppBarOpenTag();

        openTag.Should().MatchRegex(@"WindowDecorationProperties\.ElementRole=""TitleBar""",
            "the bar has to carry the real chrome role for drag/maximize/system-menu to work at all, not a hand-rolled substitute for one of the three");

        // The hand-wired substitute this replaced. If it is back, the review finding was reverted.
        ShellMarkup().Should().NotContain("PointerPressed=\"OnAppBarPointerPressed\"",
            "the manual drag handler is superseded by the ElementRole - keeping both is dead code pretending to be the real fix");
        ShellCodeBehind().Should().NotContain("OnAppBarPointerPressed",
            "the code-behind method has no XAML hookup left to justify keeping it");
    }

    [Fact]
    public void TheDrawerToggle_OverridesTheInheritedTitleBarRole()
    {
        // THE LOAD-BEARING DETAIL for HIGH 3's fix actually working. Avalonia resolves
        // WindowDecorationProperties.ElementRole by walking UP the visual-parent chain from the
        // topmost hit-test-visible element at the click point until a role is found
        // (PresentationSource.GetChromeRoleFromVisual) - without its own role, DrawerToggle would
        // inherit the app bar's TitleBar role for its own clicks and drag the window instead of
        // opening the drawer. DecorationsElement is the exact role WindowChrome.axaml's own
        // RemexDrawnCaptionButton ControlTheme already uses for the caption buttons, which sit
        // inside the same extended title-bar area for the same reason.
        var block = AppBarElement();

        var toggle = Regex.Match(block, @"<Button Name=""DrawerToggle""(?<attrs>.*?)>", RegexOptions.Singleline);
        toggle.Success.Should().BeTrue("DrawerToggle has to exist inside the app bar");

        toggle.Groups["attrs"].Value.Should().MatchRegex(@"WindowDecorationProperties\.ElementRole=""DecorationsElement""",
            "without its own role, DrawerToggle inherits the bar's TitleBar role and every click on it starts a window drag instead of opening the drawer");
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
        //
        // Checking ONLY the ElementRole names (review round 1's version of this test) is not enough
        // (Opus review of 6522b12, MEDIUM 3): the natural way to add a duplicate caption button is
        // <Button Theme="{StaticResource RemexDrawnCaptionButton}">, which carries no ElementRole of
        // its own in ShellView.axaml at all - WindowChrome.axaml's ControlTheme sets that
        // separately. So this also checks for the theme reference and for any window-state/close
        // command a hand-rolled duplicate would need instead.
        var block = AppBarElement();

        block.Should().NotMatchRegex(@"ElementRole=""(MinimizeButton|MaximizeButton|CloseButton|FullScreenButton)""",
            "window-control buttons already exist in WindowChrome.axaml's decorations overlay - this bar must not redraw them");
        block.Should().NotContain("RemexDrawnCaptionButton",
            "reusing the caption button theme here would be a second, redundant set of window controls");
        block.Should().NotMatchRegex(@"(WindowState|Close)\s*=\s*""",
            "a hand-rolled window-control button would need to set WindowState or call Close - neither belongs in this bar's content");
    }

    [Fact]
    public void TheAppBar_HidesWithTheRestOfTheImmersiveChrome()
    {
        // Matches every other overlay chrome element in this file (drawer, FAB, snackbar) - all
        // gated on !IsShellChromeHidden so the fullscreen remote-desktop view has nothing left over
        // the content, including a bar someone could still try to drag.
        var openTag = AppBarOpenTag();

        openTag.Should().MatchRegex(@"IsVisible=""\{Binding !IsShellChromeHidden\}""",
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

    private static string AppBarOpenTag()
    {
        var match = Regex.Match(ShellMarkup(), @"<material:ColorZone Grid\.Row=""0"" Name=""AppBar"".*?>", RegexOptions.Singleline);
        match.Success.Should().BeTrue("the app bar's opening tag has to exist and be well-formed");
        return match.Value;
    }

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

    private static string ChromeMarkup()
        => File.ReadAllText(Path.Combine(RepoRoot(), "remex.desktop", "Themes", "Chrome", "WindowChrome.axaml"));

    private static string RepoRoot([CallerFilePath] string thisSourceFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisSourceFile)!, "..", ".."));
}
