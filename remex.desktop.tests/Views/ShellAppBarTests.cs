using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace Remex.Desktop.Tests.Views;

/// <summary>
/// Guards the top bar's move from a transparent, non-hit-testable spacer <c>Border</c> to a real
/// Material <c>ColorZone</c> app bar (RemEx-a3prn), and two rounds of Opus review on it: an
/// unseeded Material palette painted opaque over the Mica backdrop (round 1), then RemEx's own
/// palette painted opaque over it too because the brush's documented ~63% alpha never survives
/// ThemeService's customization pass (round 2) — plus a fixed, GlowStrength-blind Material shadow,
/// a title width that traded "may overlap the caption strip" for "always truncates on a wide
/// window", and a hand-wired drag handler that silently dropped double-click-to-maximize and the
/// system menu.
/// </summary>
/// <remarks>
/// <para>
/// THE PALETTE TRAP, TWICE. Round 1: <c>ColorZone</c>'s <c>^[Mode=Standard]</c> (Material.Avalonia
/// 3.19.0's own <c>ColorZone.axaml</c>) is an ACTIVATED selector at
/// <c>BindingPriority.StyleTrigger</c> - the same trap
/// <c>material-avalonia-activated-setters-outrank-plain-overrides</c> documents - so a plain
/// attribute on the element (<c>BindingPriority.LocalValue</c>) is required, not an app-level
/// <c>Style</c> that would compile, match, and still lose. Round 2: even a correctly-placed local
/// value pointing at <c>GlassBaseDarkBrush</c> was STILL opaque at runtime - <c>ThemeService</c>
/// overrides both <c>GlassBaseDark</c> and <c>GlassBaseDarkBrush</c> with <c>palette.Surface</c> at
/// full 0xFF alpha (RemEx-c437b's own fix for this exact trap, documented in
/// <c>Controls/DashboardBackgroundControl.axaml</c>), so the brush's ~63% fallback alpha
/// (<c>Themes/Shared/FallbackPalette.axaml</c>) never ships past the first frame.
/// <see cref="TheAppBarSurface_CompensatesForTheBrushBeingOpaqueAtRuntime"/> pins the fix: a
/// separate <c>AppBarSurface</c> Border carries the brush AND an explicit compensating
/// <c>Opacity</c>, the same pattern <c>DashboardBackgroundControl</c>'s own Mica/Glass panels
/// already use, while <c>ColorZone</c> itself contributes nothing opaque of its own
/// (<c>Background="Transparent"</c>, still a local value, still beating the activated selector).
/// </para>
/// <para>
/// THE ELEVATION TRAP. <c>ColorZone</c> has no <c>BoxShadow</c> property of its own (only
/// <c>Border</c> does - the same reason <c>App.axaml</c>'s <c>material|Card</c> override reaches
/// <c>PART_BackgroundBorder</c> rather than <c>Card</c> directly), so its default
/// <c>ShadowAssist.ShadowDepth="Depth2"</c> renders Material's own FIXED black shadow regardless of
/// this app's GlowStrength-aware <c>Elevation2Shadow</c> ramp - every theme file names this control
/// as the level-2 consumer. <c>Depth0</c> does not clear that shadow, it REPLACES it with an
/// explicit zero-alpha <c>BoxShadows</c> local value (confirmed against Material.Avalonia 3.19.0's
/// own <c>Assists/ShadowAssist.cs</c>), which is exactly why the real shadow has to live on
/// <c>AppBarSurface</c> instead of fighting for the same property on <c>ColorZone</c>'s own
/// template border. <see cref="TheAppBarSurface_CompensatesForTheBrushBeingOpaqueAtRuntime"/> also
/// covers this half.
/// </para>
/// <para>
/// THE TWO-TITLES TRAP, then a MaxWidth THAT TRADED ONE BUG FOR ANOTHER.
/// <see cref="TheNativeWindowTitleText_IsHidden"/> pins WindowChrome's own static
/// "RemEx — Command Center" text (change #5 in that file's header) being turned off, so this bar's
/// title is the only one on screen. The first fix for IT bounded the title with a fixed
/// <c>MaxWidth="140"</c>, which then truncated several real locale strings on an ordinary,
/// non-narrow window (uk/es/fr/pt-BR "Logs &amp; Diagnostics"/"Remote Desktop" all exceed 140px at
/// the real font metrics). <see cref="TheAppBarTitle_UsesALiveMeasuredColumnNotAFixedMaxWidth"/>
/// pins the real fix: a <c>Grid</c> with <c>ColumnDefinitions="Auto,*"</c> gives the title
/// whatever width the window actually has, minus a margin reserving the caption strip, with
/// <c>CharacterEllipsis</c> only as the backstop for a window narrow enough that even the live
/// column cannot help.
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
/// its OWN role (<c>User</c> - application/user code, per Avalonia's own docs, as opposed to
/// <c>DecorationsElement</c> which those docs describe as theme-authored, the role
/// <c>RemexDrawnCaptionButton</c> correctly uses instead) so the ancestor walk does not resolve the
/// button's own clicks to TitleBar too.
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
/// nothing here can actually press the bar, watch the window move or maximize, or render a pixel to
/// confirm the backdrop bleeds through. What this proves is that the wiring exists and is shaped
/// the way the acceptance criteria - and both reviews - require.
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
    public void TheAppBarSurface_CompensatesForTheBrushBeingOpaqueAtRuntime()
    {
        // THE LOAD-BEARING DETAIL (Opus review round 2, HIGH). GlassBaseDarkBrush's ~63% fallback
        // alpha (#A00A0A10) is a PRE-CUSTOMIZATION value only - ThemeService.ApplyCustomization
        // overrides it with palette.Surface at full 0xFF the instant the window's first frame
        // lands, so a bare reference to the brush (round 1's fix) is opaque in the shipped app,
        // covering the Mica/Acrylic backdrop exactly the bug this bead's own predecessor
        // (RemEx-c437b) exists to prevent for the window frame itself.
        //
        // AppBarSurface has to be a SEPARATE, childless element (not Opacity on ColorZone itself):
        // Opacity is a control-level property that fades an entire visual subtree, and ColorZone's
        // subtree includes DrawerToggle and AppBarTitle - fading the surface must not fade them.
        var surface = Regex.Match(ShellMarkup(),
            @"<Border Grid\.Row=""0"" Name=""AppBarSurface""(?<attrs>.*?)/>",
            RegexOptions.Singleline);
        surface.Success.Should().BeTrue("a dedicated, childless surface element has to exist behind the ColorZone");

        var attrs = surface.Groups["attrs"].Value;
        attrs.Should().MatchRegex(@"Background=""\{DynamicResource GlassBaseDarkBrush\}""",
            "the fill has to be RemEx's own theme-tracked brush, not Material's un-seeded default");
        attrs.Should().MatchRegex(@"Opacity=""0\.63""",
            "without an explicit compensating Opacity, the brush's runtime alpha is 0xFF (ThemeService overrides " +
            "BOTH GlassBaseDark and GlassBaseDarkBrush with palette.Surface at full alpha) and the backdrop is covered again");
        attrs.Should().MatchRegex(@"BoxShadow=""\{DynamicResource Elevation2Shadow\}""",
            "the app bar is named as the level-2 elevation consumer in every theme file - a GlowStrength-blind " +
            "fixed shadow (ColorZone's own ShadowAssist default) is the wrong ramp for it");
        attrs.Should().MatchRegex(@"IsHitTestVisible=""False""",
            "the surface sits behind the ColorZone, which already owns the bar's hit-testing - a second " +
            "hit-testable layer here is redundant at best");

        // The trap this guards against: ShadowAssist writing a competing LocalValue BoxShadow that
        // no Style-priority override could ever beat, per Assists/ShadowAssist.cs.
        var openTag = AppBarOpenTag();
        openTag.Should().MatchRegex(@"assists:ShadowAssist\.ShadowDepth=""Depth0""",
            "ColorZone's own ShadowAssist default (Depth2) writes a FIXED black BoxShadow as a local value on " +
            "its template border - it has to be neutralized or it renders alongside AppBarSurface's real one");
        openTag.Should().MatchRegex(@"Background=""Transparent""",
            "ColorZone must not ALSO paint an opaque fill now that AppBarSurface owns the visible surface");
    }

    [Fact]
    public void TheNativeWindowTitleText_IsHidden()
    {
        // Two titles in one 32px bar was the wrong outcome, not a magic-number placement bug to
        // paper over (Opus review round 1, HIGH 2). WindowChrome's own decorations Overlay painted
        // "RemEx — Command Center" ON TOP of this control - Window.Title is still the taskbar/
        // Alt-Tab label either way, so turning off the on-screen copy costs nothing real.
        var chrome = ChromeMarkup();

        var panel = Regex.Match(chrome,
            @"<Panel x:Name=""PART_TitleTextPanel""(?<attrs>.*?)>(?<body>.*?)</Panel>",
            RegexOptions.Singleline);
        panel.Success.Should().BeTrue("PART_TitleTextPanel has to exist - it is the window title's host");

        panel.Groups["body"].Value.Should().MatchRegex(@"<TextBlock\b[^>]*\bIsVisible=""False""",
            "the title TextBlock itself has to be turned off, or it still paints over this bar's own live page title");
    }

    [Fact]
    public void TheAppBarTitle_UsesALiveMeasuredColumnNotAFixedMaxWidth()
    {
        // Opus review round 2, MEDIUM 2 / LOW 1. Round 1's fix (MaxWidth="140") traded "may overlap
        // the caption strip on a narrow window" for "always truncates on a wide one" - five of 81
        // real locale x destination strings (uk/es/fr Shell_LogsDiagnostics, uk/pt-BR
        // Nav_RemoteDesktop) exceed 140px at the app's actual font, and English never even
        // approaches it, so no English-locale run or check-localization pass could have caught it.
        //
        // The real fix removes the fixed number entirely: ColumnDefinitions="Auto,*" gives
        // DrawerToggle its own natural width and AppBarTitle everything else the window actually
        // has, so the column - not a guessed constant - is what bounds the text. This test is
        // scoped to AppBarTitle specifically (round 1's version matched ANY MaxWidth anywhere in
        // the ColorZone, including e.g. "1" or "9999") and asserts the width source is structural
        // (the column) rather than a magic number.
        var block = AppBarElement();

        block.Should().MatchRegex(@"<Grid ColumnDefinitions=""Auto,\*"">",
            "the inner layout has to be column-based so the title's available width tracks the real window width");

        var title = Regex.Match(block,
            @"<TextBlock Grid\.Column=""1"" Name=""AppBarTitle""(?<attrs>.*?)/>",
            RegexOptions.Singleline);
        title.Success.Should().BeTrue("AppBarTitle has to sit in the second ('*') column");

        var attrs = title.Groups["attrs"].Value;
        attrs.Should().MatchRegex(@"IsHitTestVisible=""False""",
            "without this, a press over the title text would stop there instead of reaching the ColorZone's TitleBar role");
        attrs.Should().MatchRegex(
            @"Text=""\{Binding ActiveNavIndex, Converter=\{x:Static conv:NavIndexToTitleConverter\.Instance\}\}""",
            "the title has to bind to ActiveNavIndex through the real converter");
        attrs.Should().MatchRegex(@"TextTrimming=""CharacterEllipsis""",
            "an ellipsis backstop is still needed for whatever narrow-window case even a live column cannot fully absorb");
        attrs.Should().NotMatchRegex(@"\bMaxWidth=""\d",
            "a fixed MaxWidth is exactly the mechanism review round 2 replaced - it should not be back " +
            "even at a different number");

        var toggle = Regex.Match(block, @"<Button Grid\.Column=""0"" Name=""DrawerToggle""", RegexOptions.Singleline);
        toggle.Success.Should().BeTrue("DrawerToggle has to sit in the first ('Auto') column, leading the title");
    }

    [Fact]
    public void TheAppBar_CarriesTheTitleBarElementRole()
    {
        // Opus review round 1, HIGH 3. A hand-wired PointerPressed/BeginMoveDrag handler (the first
        // version of this bar) only reproduces a plain window move - it cannot synthesize
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
        // opening the drawer. User (application/user code, per Avalonia's own docs) rather than
        // DecorationsElement (those same docs: theme-authored) - WindowChrome.axaml's own
        // RemexDrawnCaptionButton ControlTheme is the theme-authored case; DrawerToggle is
        // ShellView's own content, so User is the one that actually describes it. Both resolve
        // identically in HitTestVisual's switch, so this is a semantic fix, not a behavioural one.
        var block = AppBarElement();

        var toggle = Regex.Match(block, @"<Button Grid\.Column=""0"" Name=""DrawerToggle""(?<attrs>.*?)>", RegexOptions.Singleline);
        toggle.Success.Should().BeTrue("DrawerToggle has to exist inside the app bar");

        toggle.Groups["attrs"].Value.Should().MatchRegex(@"WindowDecorationProperties\.ElementRole=""User""",
            "without its own role, DrawerToggle inherits the bar's TitleBar role and every click on it starts a window drag instead of opening the drawer");
    }

    [Fact]
    public void TheAppBar_OwnsTheExistingDrawerToggle()
    {
        var block = AppBarElement();

        block.Should().MatchRegex(@"<Button Grid\.Column=""0"" Name=""DrawerToggle""",
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
        // Checking ONLY the ElementRole names (review round 1's first version of this test) is not
        // enough (Opus review round 1, MEDIUM 3): the natural way to add a duplicate caption button
        // is <Button Theme="{StaticResource RemexDrawnCaptionButton}">, which carries no ElementRole
        // of its own in ShellView.axaml at all - WindowChrome.axaml's ControlTheme sets that
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
