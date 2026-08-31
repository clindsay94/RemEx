using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace Remex.Desktop.Tests.Views;

/// <summary>
/// Guards the settings overlay's move from a hand-rolled <c>Border</c> + backdrop to Material
/// <c>SideSheet</c> (RemEx-zrlze), the drawer/settings-sheet interaction it required, and the
/// review-round-1 findings that followed (dead scrim by style PRIORITY not order, an unbounded
/// content height, and SideSheet wrapping/reflowing the shell instead of overlaying it).
/// </summary>
/// <remarks>
/// <para>
/// THE SCRIM TRAP, ROUND 2. Material.Styles 3.19.0's own <c>SideSheet.axaml</c> disables its
/// <c>PART_Scrim</c> outright for anything not carrying a <c>:mobile</c> pseudo-class — and nothing
/// in <c>SideSheet.axaml.cs</c> ever applies <c>:mobile</c> (its own comment says "TODO: mobile
/// variant"). Round 1's fix used an UNCONDITIONED override (no pseudo-class), which resolves at
/// <c>BindingPriority.Style</c> — a strictly lower priority than the theme's own ACTIVATED
/// <c>:not(:mobile)</c> selector, which resolves at <c>BindingPriority.StyleTrigger</c>. Priority is
/// compared before application order, so the override compiled, matched, and still lost outright —
/// the scrim stayed invisible. <see cref="TheSideSheetScrimOverride_IsActivatedAtTheSamePriorityAsTheTheme"/>
/// is the reason this needs an activator, not just a matching selector string.
/// </para>
/// <para>
/// THE OTHER HALF. The bead's own text calls out that the left drawer (RemEx-q3mle) and this right
/// side sheet both want a scrim, and asks for a decision, written down, on what happens when both
/// are open. <c>ShellViewModel</c> now makes them mutually exclusive — opening either closes
/// the other — and <see cref="TheEscapeHandler_ChecksSettingsBeforeTheDrawer"/> pins the
/// deterministic order that falls out of it, rather than leaving Esc's precedence to whichever
/// <c>if</c> happened to be written first.
/// </para>
/// <para>
/// PUSH VS. OVERLAY. Round 1 made <c>material:SideSheet</c> the shell's outermost element, wrapping
/// everything else as its <c>Content</c>. Material's desktop <c>SideSheet</c> is a STANDARD (push)
/// sheet — <c>PART_SideSheet</c> docks and <c>PART_ContentPanel</c> takes the remainder — so that
/// reflowed the whole shell sideways on open instead of overlaying it, which the bead's "slides
/// identically or better" acceptance rules out. <see cref="TheSideSheetIsAnOverlaySiblingNotAShellWrapper"/>
/// pins the corrected shape: SideSheet as a Grid sibling occupying the same
/// <c>Grid.RowSpan="3"</c> cell the old backdrop+panel pair did, with nothing assigned to its own
/// <c>Content</c>.
/// </para>
/// <para>
/// A source scan, not a behavioural test — there is no headless Avalonia harness in this repo (see
/// <see cref="ShellNavListTests"/>, <see cref="ShellDrawerOverlayTests"/>), so nothing here can
/// actually open the sheet, press Esc, and look. What this proves is that the wiring exists and is
/// shaped the way the acceptance criteria require; it cannot prove the animation renders correctly.
/// Parsed with per-match, per-element regex the way <c>CommandPaletteLightDismissTests</c> already
/// does for this same reason (a loose <c>Should().Contain()</c> on the whole file would go green on
/// three unrelated substrings that never sit together, per the RemEx-hev1g/RemEx-thwlr lesson) — and
/// per review round 1's own MEDIUM finding, a test that can only observe a Style block's TEXT cannot
/// observe PRIORITY, so <see cref="TheSideSheetScrimOverride_IsActivatedAtTheSamePriorityAsTheTheme"/>
/// asserts on the activator specifically rather than merely on IsEnabled/IsVisible existing somewhere.
/// </para>
/// </remarks>
public class ShellSettingsSideSheetTests
{
    [Fact]
    public void TheSettingsPanel_IsAMaterialSideSheetNotAHandRolledBorder()
    {
        var xaml = ShellMarkup();

        xaml.Should().Contain("<material:SideSheet",
            "the settings panel has to be the real control, not a Border reimplementing one");

        // The exact hand-rolled mechanics this replaced. If any of these are back, the migration
        // was reverted or bypassed for a "quick fix".
        xaml.Should().NotContain("Classes=\"settings-overlay\"",
            "the hand-toggled open/closed class pair is retired - SideSheetOpened drives the slide now");
        xaml.Should().NotMatchRegex(@"Style Selector=""Border\.settings-overlay""",
            "the manual translateX slide style has to be gone, not merely unused");
        xaml.Should().NotContain("OnSettingsBackdropPressed",
            "the hand-rolled backdrop's click handler is retired - SideSheet's own PART_Scrim click does this now");

        var codeBehind = ShellCodeBehind();
        codeBehind.Should().NotContain("_settingsPanel",
            "the code-behind field that class-toggled the old Border has no reason to exist once " +
            "SideSheetOpened is bound directly");
        codeBehind.Should().NotContain("OnSettingsBackdropPressed",
            "the backdrop PointerPressed handler has no XAML hookup left to justify keeping it");
    }

    [Fact]
    public void TheSideSheetOpenStateBindsTwoWay()
    {
        // Mirrors ShellDrawerOverlayTests.TheDrawerOpenStateBindsTwoWay for the same reason: SideSheet
        // assigns SideSheetOpened = false directly on the control from its scrim click and its own
        // close button, so a one-way binding would let the sheet slide shut while IsSettingsPanelOpen
        // stayed true.
        ShellMarkup().Should().MatchRegex(
            @"SideSheetOpened\s*=\s*""\{Binding\s+IsSettingsPanelOpen\s*,\s*Mode\s*=\s*TwoWay\s*\}""",
            "SideSheetOpened has to be an explicit TwoWay binding against IsSettingsPanelOpen");
    }

    [Fact]
    public void TheSideSheetScrimOverride_IsActivatedAtTheSamePriorityAsTheTheme()
    {
        // THE LOAD-BEARING DETAIL (review round 1, HIGH). An override selector with no pseudo-class
        // resolves at BindingPriority.Style; the base ControlTheme's own `^:not(:mobile)` selector is
        // ACTIVATED and resolves at the strictly higher BindingPriority.StyleTrigger. Priority beats
        // application order, so a same-text override missing the activator compiles, matches, and
        // still loses - which is exactly what stayed green under the previous (loose) version of this
        // test. Repeating :not(:mobile) here is what makes this override land at the SAME priority
        // tier the theme's selector does, where application order can actually decide it.
        var style = Regex.Match(ShellMarkup(),
            @"<Style Selector=""material\|SideSheet:not\(:mobile\) /template/ Border#PART_Scrim"">(?<body>.*?)</Style>",
            RegexOptions.Singleline);

        style.Success.Should().BeTrue(
            "the scrim override's selector has to carry :not(:mobile) - without an activator it " +
            "resolves at a lower style priority than the base theme's own activated selector and " +
            "loses outright, regardless of which one is attached later");

        var body = style.Groups["body"].Value;
        body.Should().MatchRegex(@"Setter Property=""IsEnabled""\s*Value=""True""",
            "IsEnabled=False on the base theme makes the scrim un-clickable even if painted");
        body.Should().MatchRegex(@"Setter Property=""IsVisible""\s*Value=""True""",
            "IsVisible=False on the base theme is what actually hides the scrim on desktop");
    }

    [Fact]
    public void TheSideSheetScrimOverride_MatchesTheOldBackdropsOpacityWeight()
    {
        // The base ControlTheme's own `^:open` sets the scrim's render Opacity to 0.32. Composed
        // with GlassOverlayBrush's baked-in alpha (0.20, FallbackPalette.axaml:110), that multiplies
        // out to ~6.5% black - visibly lighter than the old backdrop's flat 20%. This asserts the
        // :open override that cancels the multiplication back out to the brush's own alpha, review
        // round 1's explicit "match the old weight" finding.
        ShellMarkup().Should().MatchRegex(
            @"<Style Selector=""material\|SideSheet:not\(:mobile\):open /template/ Border#PART_Scrim"">\s*" +
            @"<Setter Property=""Opacity""\s*Value=""1""\s*/>\s*</Style>",
            "the :open scrim opacity has to be forced back to 1 so GlassOverlayBrush's own alpha " +
            "is the only factor, matching the old backdrop's flat weight");
    }

    [Fact]
    public void TheSideSheetIsAnOverlaySiblingNotAShellWrapper()
    {
        // THE STRUCTURAL GUARD for review round 1's HIGH finding: Material's desktop SideSheet is a
        // STANDARD (push) sheet - wrapping the shell as its Content reflows everything sideways by
        // SideSheetWidth on open, which is not "slides identically or better". The fix keeps SideSheet
        // as a Grid sibling occupying the same Grid.RowSpan="3" cell the old backdrop+panel pair did,
        // with nothing assigned to its own Content - so nothing outside its own bounds ever reflows.
        var xaml = ShellMarkup();

        var sheetOpenTag = Regex.Match(xaml, @"<material:SideSheet\b[^>]*>", RegexOptions.Singleline);
        sheetOpenTag.Success.Should().BeTrue("the SideSheet element has to exist");

        sheetOpenTag.Value.Should().MatchRegex(@"Grid\.Row=""0""",
            "the sheet has to sit in the same full-bleed Grid cell the old backdrop+panel did");
        sheetOpenTag.Value.Should().MatchRegex(@"Grid\.RowSpan=""3""",
            "RowSpan=3 is what made the old backdrop cover the whole shell rather than one row");

        var sheetStart = sheetOpenTag.Index;
        var sheetEnd = xaml.IndexOf("</material:SideSheet>", sheetStart, StringComparison.Ordinal);
        sheetEnd.Should().BeGreaterThan(-1, "the SideSheet element has to be closed");

        var sheetElement = xaml.Substring(sheetStart, sheetEnd - sheetStart);
        sheetElement.Should().NotContain("DashboardBackgroundControl",
            "the shell must NOT be nested inside SideSheet's own Content - that is the push-reflow " +
            "regression this test exists to catch");
    }

    [Fact]
    public void TheSideSheetDoesNotSetBackgroundOrBorderBrushOnItself()
    {
        // Those attributes template-bind to PART_RootBorder, which wraps BOTH the sliding panel AND
        // the scrim's remainder - setting them on the control paints/borders the entire shell rather
        // than the 440px panel (review round 1, MEDIUM). They belong on PART_SideSheet specifically.
        var sheetOpenTag = Regex.Match(ShellMarkup(), @"<material:SideSheet\b[^>]*>", RegexOptions.Singleline);
        sheetOpenTag.Success.Should().BeTrue("the SideSheet element has to exist");

        sheetOpenTag.Value.Should().NotMatchRegex(@"\bBackground=",
            "Background on the control itself paints the whole shell, not just the panel");
        sheetOpenTag.Value.Should().NotMatchRegex(@"\bBorderBrush=",
            "BorderBrush on the control itself borders the whole shell, not just the panel");
    }

    [Fact]
    public void ThePanelsSurfaceAndShadow_AreStyledOnPART_SideSheetUnconditioned()
    {
        // Background and BoxShadow are uncontested: SideSheet's own template/ControlTheme never
        // sets either property on PART_SideSheet from ANY selector, activated or not, so an
        // unconditioned override is sufficient and correct for these two specifically.
        var style = Regex.Match(ShellMarkup(),
            @"<Style Selector=""material\|SideSheet /template/ Border#PART_SideSheet"">(?<body>.*?)</Style>",
            RegexOptions.Singleline);

        style.Success.Should().BeTrue("PART_SideSheet - the actual sliding panel - needs its own surface style");

        var body = style.Groups["body"].Value;
        body.Should().Contain("Property=\"Background\"",
            "the panel needs an opaque surface, or the dimmed shell would show through it too");
        body.Should().MatchRegex(@"Property=""BoxShadow""\s*Value=""-8 0 40 0 #40000000""",
            "the old panel's depth shadow (review round 1, LOW) has to be restored, not merely present");

        // NOT here - see the activated block below. BorderBrush is contested (round 2, MEDIUM 1),
        // so if it ever migrates back into this unconditioned block it silently loses again.
        body.Should().NotContain("Property=\"BorderBrush\"",
            "BorderBrush is contested by an activated theme selector and belongs in the " +
            ":not(:mobile) block, not this unconditioned one - see " +
            nameof(ThePanelsBorderBrush_IsActivatedAtTheSamePriorityAsTheTheme));
    }

    [Fact]
    public void ThePanelsBorderBrush_IsActivatedAtTheSamePriorityAsTheTheme()
    {
        // THE LOAD-BEARING DETAIL (review round 2, MEDIUM 1) - HIGH 1's exact trap, on a different
        // property. Material's own ControlTheme sets BorderBrush on PART_SideSheet from an ACTIVATED
        // selector - `^:not(:mobile) /template/ Border#PART_SideSheet { BorderBrush = ... }` - at
        // BindingPriority.StyleTrigger. An unconditioned override resolves at the strictly lower
        // BindingPriority.Style and loses regardless of attach order, leaving the panel's left-edge
        // divider painted Material's MaterialDividerBrush instead of this app's CardBorderBrush -
        // invisible in markup, wrong the moment a theme switch makes the two brushes differ.
        var style = Regex.Match(ShellMarkup(),
            @"<Style Selector=""material\|SideSheet:not\(:mobile\) /template/ Border#PART_SideSheet"">(?<body>.*?)</Style>",
            RegexOptions.Singleline);

        style.Success.Should().BeTrue(
            "the panel's BorderBrush override has to carry :not(:mobile) - without an activator it " +
            "resolves at a lower style priority than the theme's own activated selector for the " +
            "same property and loses outright");

        style.Groups["body"].Value.Should().MatchRegex(
            @"Setter Property=""BorderBrush""\s*Value=""\{DynamicResource CardBorderBrush\}""",
            "the panel's edge divider has to be this app's own resource, not Material's default");
    }

    [Fact]
    public void TheSideSheetIsHitTestGatedOnWhetherItIsOpen()
    {
        // The sheet now occupies the full shell area at all times (closed state is off-screen via
        // Margin, not a shrunk control) - Avalonia hit-tests a transparent Background the same as an
        // opaque one (the exact trap this file's own SnackbarHost comment documents), so without this
        // the always-present root would swallow every click over the shell while closed.
        var sheetOpenTag = Regex.Match(ShellMarkup(), @"<material:SideSheet\b[^>]*>", RegexOptions.Singleline);
        sheetOpenTag.Success.Should().BeTrue("the SideSheet element has to exist");

        sheetOpenTag.Value.Should().MatchRegex(
            @"IsHitTestVisible=""\{Binding IsSettingsPanelOpen\}""",
            "without this, clicks anywhere over the shell are swallowed while the sheet is closed");
    }

    [Fact]
    public void TheSideSheetContent_HasABoundedScrollableHeight()
    {
        // THE STRUCTURAL GUARD for review round 1's other HIGH finding: SideSheetContent is presented
        // inside a vertical StackPanel, which measures every child with infinite available height, so
        // an unconstrained ScrollViewer sizes itself to its full content instead of scrolling -
        // everything below the fold becomes unreachable, and PART_RootBorder's ClipToBounds="False"
        // means it does not even get visually clipped, just runs off the window.
        //
        // Captured as ONE Singleline block, not three independent whole-file checks (review round 2,
        // MEDIUM 2): three unscoped checks cannot tell this MaxHeight from any other MultiBinding
        // anywhere in the file, cannot tell ORDER - and SubtractHeightConverter computes
        // values[0] - values[1], so swapping the two ElementName bindings silently changes the sum
        // to headerHeight - sheetHeight, floors at 0, and the whole content area collapses to
        // nothing, with a loose three-check version of this test staying green throughout (measured:
        // this is the exact defect the previous version of this test could not see).
        var block = Regex.Match(ShellMarkup(),
            @"<ScrollViewer\.MaxHeight>(?<body>.*?)</ScrollViewer\.MaxHeight>",
            RegexOptions.Singleline);

        block.Success.Should().BeTrue("the settings ScrollViewer has to declare an explicit MaxHeight");

        var body = block.Groups["body"].Value;
        body.Should().Contain("Converter=\"{x:Static conv:SubtractHeightConverter.Instance}\"",
            "the bound has to be the real, live-measured converter, not an unconstrained ScrollViewer");
        body.Should().Contain("ConverterParameter=\"5\"",
            "PART_SideContentPresenter carries its own Margin=\"0,5,0,0\" in SideSheet's ControlTheme " +
            "that neither live Bounds reading can see - without cancelling it, MaxHeight overshoots " +
            "by exactly that margin and the last 5px of the viewport sit past the window edge, " +
            "unclipped (review round 2, LOW)");

        var sheetIndex = body.IndexOf(
            "<Binding ElementName=\"SettingsSideSheet\" Path=\"Bounds.Height\"/>", StringComparison.Ordinal);
        var headerIndex = body.IndexOf(
            "<Binding ElementName=\"SettingsSheetHeader\" Path=\"Bounds.Height\"/>", StringComparison.Ordinal);

        sheetIndex.Should().BeGreaterThan(-1, "the sheet's own live height has to feed the MultiBinding");
        headerIndex.Should().BeGreaterThan(-1, "the header's own live height has to feed the MultiBinding");
        sheetIndex.Should().BeLessThan(headerIndex,
            "SubtractHeightConverter computes values[0] - values[1] - the sheet's total height has to " +
            "be FIRST and the header's SECOND, or the result inverts to a negative number that floors " +
            "at 0 and the content area silently collapses to nothing");
    }

    [Fact]
    public void OpeningTheDrawer_ClosesTheSettingsPanel()
    {
        var hook = ExtractMethod(ShellViewModelSource(), "OnIsDrawerOpenChanged");

        hook.Should().Contain("if (value)",
            "only the opening transition should cascade - closing the drawer must not reopen settings");
        hook.Should().Contain("IsSettingsPanelOpen = false",
            "opening the drawer has to close the settings sheet, or both scrims can show at once");
    }

    [Fact]
    public void OpeningTheSettingsPanel_ClosesTheDrawer()
    {
        var hook = ExtractMethod(ShellViewModelSource(), "OnIsSettingsPanelOpenChanged");

        hook.Should().Contain("if (value)",
            "only the opening transition should cascade - closing the settings sheet must not reopen the drawer");
        hook.Should().Contain("IsDrawerOpen = false",
            "opening the settings sheet has to close the drawer, or both scrims can show at once");
    }

    [Fact]
    public void TheEscapeHandler_ChecksSettingsBeforeTheDrawer()
    {
        // Deterministic, not accidental: the settings sheet is declared (and therefore composited)
        // after ShellDrawer, so it is the topmost surface whenever both flags could somehow disagree
        // with the mutual-exclusion invariant above. Checking it first is what makes "Esc closes the
        // topmost surface" true by construction rather than by whichever branch happened to run first.
        var onKeyDown = ExtractMethod(ShellCodeBehind(), "OnKeyDown");

        onKeyDown.Should().Contain("IsSettingsPanelOpen",
            "OnKeyDown has to react to the settings sheet - it had no Escape handling at all before this bead");
        onKeyDown.Should().Contain("IsDrawerOpen",
            "OnKeyDown still has to react to the drawer");

        var settingsIndex = onKeyDown.IndexOf("IsSettingsPanelOpen", StringComparison.Ordinal);
        var drawerIndex = onKeyDown.IndexOf("IsDrawerOpen", StringComparison.Ordinal);

        settingsIndex.Should().BeLessThan(drawerIndex,
            "the settings branch has to be checked (and therefore win) before the drawer branch");
    }

    // ─────────────────────────── plumbing ───────────────────────────

    private static string ShellMarkup()
        => File.ReadAllText(Path.Combine(RepoRoot(), "remex.desktop", "Views", "ShellView.axaml"));

    private static string ShellCodeBehind()
        => File.ReadAllText(Path.Combine(RepoRoot(), "remex.desktop", "Views", "ShellView.axaml.cs"));

    private static string ShellViewModelSource()
        => File.ReadAllText(Path.Combine(RepoRoot(), "remex.desktop", "ViewModels", "ShellViewModel.cs"));

    /// <summary>
    /// Same "next same-indent close brace" heuristic <c>CommandPaletteLightDismissTests</c> already
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
