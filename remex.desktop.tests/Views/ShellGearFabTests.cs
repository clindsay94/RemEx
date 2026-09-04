using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace Remex.Desktop.Tests.Views;

/// <summary>
/// Guards the gear FAB's move from a hand-rolled <c>Button.gear-fab</c> (three style blocks in
/// <c>ShellView.axaml</c>'s <c>UserControl.Styles</c>) onto a real Material
/// <c>material:FloatingButton</c> (RemEx-bado6, phase 2 of RemEx-ajpug/RemEx-kb4im), plus the fixes
/// from an Opus review round 2 of the first landing (commit a4c0997).
/// </summary>
/// <remarks>
/// <para>
/// THE SIZE TRAP. Material.Avalonia 3.19.0's <c>FloatingButton.axaml</c> (read from source, not
/// assumed off the DLL, per the RemEx-vryje/RemEx-04ftl lesson) sets its default 56x56 footprint
/// from an UNCONDITIONED <c>&lt;Style Selector="^"&gt;</c> inside its own <c>ControlTheme</c>, and
/// its 40x40 (<c>.Mini</c>) / 48-tall (<c>[IsExtended=true]</c>) variants from ACTIVATED
/// class/attribute selectors — the same
/// <c>material-avalonia-activated-setters-outrank-plain-overrides</c> shape a plain app-level
/// <c>Style</c> override would silently lose to. <see cref="TheGearFab_KeepsItsFootprintAt52"/>
/// pins the fix instead: Width/Height/MinWidth/MinHeight/Padding as LOCAL VALUES directly on the
/// element, which beat every one of those regardless of which kind wins the priority fight — and
/// which have to stay 52/52/0, because <c>ShellSnackbarHostTests.TheSnackbarHostIsAnchoredClearOfTheGearFab</c>
/// measures the toast's own bottom margin (88 = 20 + 52 + 16) against exactly that number.
/// </para>
/// <para>
/// THE SHADOW TRAP. <c>FloatingButton</c>'s <c>ControlTheme</c> sets
/// <c>assists:ShadowAssist.ShadowDepth="Depth1"</c> by default, and <c>ShadowAssist</c> writes
/// <c>BoxShadow</c> as a LocalValue on <c>PART_ButtonRootBorder</c> the instant that property
/// changes (confirmed against <c>Assists/ShadowAssist.cs</c>, the same fact
/// <c>ShellAppBarTests</c> already pins for <c>ColorZone</c>) — a fixed, GlowStrength-blind black
/// shadow that no lower-priority Style could ever unseat. <see cref="TheGearFabShadow_UsesRemExsElevationRampNotMaterialsFixedOne"/>
/// pins the same fix <c>AppBarSurface</c> uses: <c>Depth0</c> neutralizes the button's own shadow,
/// and a separate, childless <c>GearFabShadow</c> Border behind it carries RemEx's own
/// <c>Elevation2Shadow</c> — the "raised resting" level every theme file's elevation-ramp comment
/// already assigns to persistently-elevated surfaces like the app bar.
/// </para>
/// <para>
/// ROUND 2, THE GEOMETRY-DRIFT TRAP (MEDIUM). The first landing duplicated Grid.Row/size/alignment/
/// Margin/IsVisible across BOTH the shadow Border and the FloatingButton — nothing stopped a future
/// resize of one from silently leaving the other behind, which is exactly how the shadow would stop
/// being a coincident circle. <c>GearFabWrap</c>, a plain <c>Panel</c>, now owns Grid.Row/alignment/
/// Margin/IsVisible exactly ONCE; <see cref="TheGearFab_ShadowGeometryCannotDriftFromTheWrapper"/>
/// pins what is left to drift — the wrapper's own Width/Height against the button's local Width/
/// Height, and the shadow's CornerRadius against half the wrapper's Width — by comparing the actual
/// captured NUMBERS rather than asserting three independent "52" literals that could each be edited
/// without the others noticing.
/// </para>
/// <para>
/// ROUND 2, THE ENTRANCE-ANIMATION TRAP (MEDIUM). FloatingButton.axaml's own ControlTheme animates
/// its template border from scale 0 to scale 1 over 0:0:0.5 whenever the control becomes visible.
/// GearFabShadow has no such animation, so exiting fullscreen remote desktop (or the shell's first
/// render) used to show a fully-visible shadow ring around an invisible, still-scaling-up button for
/// half a second. <see cref="TheGearFab_OptsOutOfTheEntranceAnimation"/> pins the theme's own
/// documented opt-out (<c>Classes="no-transitions"</c>, which collapses that one keyframe animation
/// to near-instant) rather than a hand-rolled workaround — matching the old plain Button, which
/// never animated in either.
/// </para>
/// <para>
/// ROUND 2, THE MISSING HOVER/PRESS FEEDBACK (MEDIUM). Pinning <c>ShadowDepth="Depth0"</c> also
/// silences <c>^:pointerover:not(.no-material)</c>'s <c>ShadowAssist.Darken</c> (nothing left to
/// darken), and Background can't be repainted on <c>:pointerover</c> the way the deleted
/// <c>Button.gear-fab:pointerover</c> style did — Background is a LocalValue on the button, and a
/// LocalValue permanently outranks the whole Styles system, activated selectors included.
/// <see cref="TheGearFab_LiftsAndDeepensItsShadowOnHover"/> and
/// <see cref="TheGearFab_ScalesDownOnPress"/> pin the fix instead: RenderTransform, which nothing in
/// FloatingButton's own theme touches at any priority, carries the scale(1.1)/scale(0.95) feedback
/// the deleted styles used, and the shadow itself steps up to <c>Elevation3Shadow</c> ("hover /
/// interactive lift" in every theme file's own elevation-ramp comment) via
/// <c>Panel#GearFabWrap:pointerover Border#GearFabShadow</c> — scoped to the WRAPPER's hover state
/// because the shadow Border is <c>IsHitTestVisible="False"</c> and could never receive
/// <c>:pointerover</c> on its own.
/// </para>
/// <para>
/// ROUND 2, THE INVISIBLE RIPPLE (LOW, corrected RemEx-alwfa.3). <c>assists:ButtonAssist.ClickFeedbackColor</c>
/// defaults to <c>#000000</c> at 26% opacity, but that default is NOT declared in
/// <c>Assists/ButtonAssist.cs</c> — that class only registers ClickFeedbackColorProperty with no
/// default of its own. The literal lives in <c>Controls/FloatingButton.axaml</c>'s own ControlTheme
/// (which sets it directly and binds the ripple's opacity to its own ButtonPressedOpacity resource,
/// 0.26) — FloatingButton's own choice, not a shared one: <c>Resources/Themes/Button.axaml</c>'s
/// MaterialButtonBase instead binds the same property to the control's own Foreground, so only the
/// FAB hardcodes a literal. That literal reads as a near-invisible dark smudge on the dark accents
/// CyberNOC/BaseDarkGlass already use for AccentPrimary.
/// <see cref="TheGearFab_InvertsItsRippleColorWithTheTheme"/> pins the fix: a local
/// value pointed at AccentForegroundBrush, the same inversion the icon and the shadow already use.
/// </para>
/// <para>
/// A source scan, matching <see cref="ShellAppBarTests"/> and <see cref="ShellSnackbarHostTests"/>:
/// there is no headless Avalonia render harness in this repo, so nothing here can actually press the
/// button, hover it, or measure a rendered shadow. What this proves is that the wiring exists and is
/// shaped the way the acceptance criteria — and every trap above — require.
/// </para>
/// </remarks>
public class ShellGearFabTests
{
    [Fact]
    public void TheGearFab_IsAMaterialFloatingButtonNotAPlainButton()
    {
        var xaml = ShellMarkup();

        xaml.Should().MatchRegex(@"<material:FloatingButton\b[^>]*\bName=""GearFab""",
            "the hand-rolled Button.gear-fab has to become the real Material FloatingButton control, not a Button pretending to be one");

        // The exact element this replaced. If it is back, the migration was reverted.
        xaml.Should().NotMatchRegex(@"<Button\b[^>]*\bClasses=""[^""]*\bgear-fab\b",
            "the old plain Button carrying the gear-fab class has to be gone, not merely restyled");
    }

    [Fact]
    public void TheOldGearFabStyleBlocksAreGone()
    {
        // ButtonVocabularyTests.NoViewDeclaresAButtonStyleOfItsOwn: "the per-view style blocks are
        // DELETED, not merely overridden". A Button-type selector can no longer even reach this
        // element once it is a FloatingButton, which makes the three old blocks dead code rather
        // than merely superseded — dead code this bead's own acceptance is the one place positioned
        // to notice and remove.
        ShellMarkup().Should().NotMatchRegex(@"Selector=""Button\.gear-fab",
            "Button.gear-fab styled a control type this element no longer is; leaving the block in place would style nothing");
    }

    [Fact]
    public void TheGearFab_KeepsItsFootprintAt52()
    {
        var fab = GearFabOpenTag();

        fab.Should().MatchRegex(@"\bWidth=""52""", "a different width silently breaks the SnackbarHost clearance math (20 + FAB height + 16 = 88)");
        fab.Should().MatchRegex(@"\bHeight=""52""", "a different height silently breaks the SnackbarHost clearance math (20 + FAB height + 16 = 88)");
        fab.Should().MatchRegex(@"\bMinWidth=""52""",
            "FloatingButton's own ControlTheme sets MinWidth=56 from an unconditioned Style — without a local MinWidth too, the effective size stays 56 even with Width=52");
        fab.Should().MatchRegex(@"\bMinHeight=""52""",
            "FloatingButton's own ControlTheme sets MinHeight=56 from an unconditioned Style — without a local MinHeight too, the effective size stays 56 even with Height=52");
    }

    [Fact]
    public void TheGearFab_ShadowGeometryCannotDriftFromTheWrapper()
    {
        // Review round 2, MEDIUM. Three numbers used to be able to drift independently: the
        // wrapper's Width, the button's own local Width, and the shadow's CornerRadius. Comparing
        // the CAPTURED VALUES against each other (not each against a separately hardcoded "52")
        // is what actually catches a future edit to only one of them.
        var wrap = GearFabWrapOpenTag();
        var wrapWidth = ExtractInt(wrap, "Width");
        var wrapHeight = ExtractInt(wrap, "Height");

        var fab = GearFabOpenTag();
        ExtractInt(fab, "Width").Should().Be(wrapWidth, "the button has to fill the wrapper exactly, or it is off-centre inside its own shadow");
        ExtractInt(fab, "Height").Should().Be(wrapHeight, "the button has to fill the wrapper exactly, or it is off-centre inside its own shadow");

        var shadow = GearFabShadowOpenTag();
        var cornerRadius = ExtractInt(shadow, "CornerRadius");
        cornerRadius.Should().Be(wrapWidth / 2,
            "a circular shadow needs CornerRadius = Width / 2 exactly — Avalonia's rounded-rect clamping means a WRONG radius can still render as a circle, so a shape check alone would not catch this");
    }

    [Fact]
    public void TheGearFabShadow_UsesRemExsElevationRampNotMaterialsFixedOne()
    {
        var shadowTag = GearFabShadowOpenTag();
        shadowTag.Should().MatchRegex(@"\bIsHitTestVisible=""False""",
            "the shadow sits behind the real button, which already owns the FAB's hit-testing");

        // BoxShadow is no longer a local attribute on the Border (round 1 set it that way, which
        // would have permanently blocked any hover-triggered escalation to Elevation3Shadow the
        // same way Background is permanently blocked on the button itself) — it is a Style now, so
        // the activated :pointerover variant below can outrank it.
        shadowTag.Should().NotMatchRegex(@"\bBoxShadow=",
            "BoxShadow has to be a Style-driven value, not a LocalValue, or the hover escalation to Elevation3Shadow could never win");

        var xaml = ShellMarkup();
        Regex.Match(xaml, @"<Style Selector=""Border#GearFabShadow"">\s*<Setter Property=""BoxShadow"" Value=""\{DynamicResource Elevation2Shadow\}""\s*/>\s*</Style>")
            .Success.Should().BeTrue(
                "the FAB is a persistently-raised surface — RemEx's own GlowStrength-aware ramp names that level 2 (\"raised resting\"), not Material's fixed black default");

        var fab = GearFabOpenTag();
        fab.Should().MatchRegex(@"assists:ShadowAssist\.ShadowDepth=""Depth0""",
            "FloatingButton's own ShadowAssist default (Depth1) writes a FIXED black BoxShadow as a local value on its template border — it has to be neutralized or it renders alongside GearFabShadow's real one");
    }

    [Fact]
    public void TheGearFab_LiftsAndDeepensItsShadowOnHover()
    {
        // Review round 2, MEDIUM. Depth0 silences FloatingButton's own ShadowAssist.Darken hover
        // feedback; this is what replaces it — scoped to the WRAPPER's :pointerover because
        // GearFabShadow itself is IsHitTestVisible="False" and could never receive that pseudo-class.
        var xaml = ShellMarkup();
        Regex.Match(xaml, @"<Style Selector=""Panel#GearFabWrap:pointerover Border#GearFabShadow"">\s*<Setter Property=""BoxShadow"" Value=""\{DynamicResource Elevation3Shadow\}""\s*/>\s*</Style>")
            .Success.Should().BeTrue(
                "\"hover / interactive lift\" is level 3 in every theme file's own elevation-ramp comment — the FAB is exactly the control that description fits, and nothing used it before this");
    }

    [Fact]
    public void TheGearFab_ScalesDownOnPress()
    {
        // Review round 2, MEDIUM. RenderTransform is free of the LocalValue-vs-Style conflict that
        // blocks a Background repaint on hover — nothing in FloatingButton's own theme sets it at
        // any priority — so it carries the scale feedback the deleted Button.gear-fab:pointerover/
        // :pressed styles used to.
        var xaml = ShellMarkup();

        Regex.Match(xaml, @"<Style Selector=""material\|FloatingButton#GearFab:pointerover"">\s*<Setter Property=""RenderTransform"" Value=""scale\(1\.1\)""\s*/>\s*</Style>")
            .Success.Should().BeTrue("hovering the FAB has to visibly lift it, the same as the old Button did");
        Regex.Match(xaml, @"<Style Selector=""material\|FloatingButton#GearFab:pressed"">\s*<Setter Property=""RenderTransform"" Value=""scale\(0\.95\)""\s*/>\s*</Style>")
            .Success.Should().BeTrue("pressing the FAB has to visibly compress it, the same as the old Button did");
    }

    [Fact]
    public void TheGearFab_OptsOutOfTheEntranceAnimation()
    {
        // Review round 2, MEDIUM. Without this, FloatingButton.axaml's own 0:0:0.5 scale-from-zero
        // entrance animation runs on a button whose shadow (GearFabShadow) has no matching
        // animation — a full-strength shadow ring around an invisible, still-scaling button for half
        // a second, worst on the deepest ramps (CyberNOC/BaseDarkGlass). This is the theme's own
        // documented opt-out, not a workaround, and matches the old plain Button, which never
        // animated in either.
        var fab = GearFabOpenTag();
        fab.Should().MatchRegex(@"\bClasses=""no-transitions""",
            "without the theme's own opt-out class, the button fades/scales in over half a second while its shadow appears instantly");
    }

    [Fact]
    public void TheGearFab_InvertsItsRippleColorWithTheTheme()
    {
        // Review round 2, LOW (corrected RemEx-alwfa.3). FloatingButton.axaml's own ControlTheme
        // hardcodes ClickFeedbackColor="#000000" at 26% ripple opacity - nearly invisible on the
        // dark accents CyberNOC/BaseDarkGlass already use for AccentPrimary. A local value here
        // beats that theme default regardless of tier. (Not Assists/ButtonAssist.cs — that class
        // carries no default at all; see the type doc comment above.)
        var fab = GearFabOpenTag();
        fab.Should().MatchRegex(@"assists:ButtonAssist\.ClickFeedbackColor=""\{DynamicResource AccentForegroundBrush\}""",
            "the ripple has to invert with the theme the same way the icon and the shadow do, or it stays a near-invisible dark smudge on the dark accents");
    }

    [Fact]
    public void TheGearFab_KeepsItsTooltipAndAutomationName()
    {
        var fab = GearFabOpenTag();

        fab.Should().MatchRegex(@"AutomationProperties\.Name=""\{conv:Localize Personalize_FabTooltip\}""",
            "screen readers need the same localized name the old Button carried");
        fab.Should().MatchRegex(@"ToolTip\.Tip=""\{conv:Localize Personalize_FabTooltip\}""",
            "the visible tooltip has to survive the migration too, not just the automation name");
    }

    [Fact]
    public void TheGearFab_KeepsItsCommandBinding()
    {
        GearFabOpenTag().Should().MatchRegex(@"Command=""\{Binding ToggleSettingsPanelCommand\}""",
            "the FAB still has to open the settings side sheet");
    }

    [Fact]
    public void TheGearFabWrap_KeepsItsVisibilityBinding()
    {
        // IsVisible moved off the FloatingButton itself onto the shared wrapper (round 2's geometry
        // fix) — it has to still be bound to the same thing, just from the new location.
        GearFabWrapOpenTag().Should().MatchRegex(@"IsVisible=""\{Binding !IsShellChromeHidden\}""",
            "the FAB (and its shadow) still has to hide during the fullscreen remote-desktop view, the same as before");
    }

    // ─────────────────────────── plumbing ───────────────────────────

    private static int ExtractInt(string attrs, string property)
    {
        var match = Regex.Match(attrs, $@"\b{Regex.Escape(property)}=""(?<value>-?\d+)""");
        match.Success.Should().BeTrue($"the captured element has to carry a numeric {property} attribute");
        return int.Parse(match.Groups["value"].Value);
    }

    private static string GearFabOpenTag()
    {
        var match = Regex.Match(ShellMarkup(),
            @"<material:FloatingButton\b(?<attrs>.*?)>",
            RegexOptions.Singleline);
        match.Success.Should().BeTrue("the FloatingButton element has to exist for its attributes to be inspected");
        return match.Groups["attrs"].Value;
    }

    private static string GearFabWrapOpenTag()
    {
        // Anchored on the Name via a lookahead rather than attribute position, so reordering
        // Grid.Row/Width/Height/etc. on this element (a semantically null edit) cannot fail this scan.
        var match = Regex.Match(ShellMarkup(),
            @"<Panel\b(?=[^>]*\bName=""GearFabWrap"")(?<attrs>.*?)>",
            RegexOptions.Singleline);
        match.Success.Should().BeTrue("the shared GearFabWrap Panel has to exist — it is what keeps the shadow and the button's geometry from drifting apart");
        return match.Groups["attrs"].Value;
    }

    private static string GearFabShadowOpenTag()
    {
        // Same anchor-on-Name fix as GearFabWrapOpenTag (review round 2, LOW): the original regex
        // was locked to attribute order (Grid.Row before Name), so a semantically null reordering
        // would have failed with a message pointing at "the element doesn't exist" instead of at
        // whatever attribute actually changed.
        var match = Regex.Match(ShellMarkup(),
            @"<Border\b(?=[^>]*\bName=""GearFabShadow"")(?<attrs>.*?)/>",
            RegexOptions.Singleline);
        match.Success.Should().BeTrue("a dedicated, childless shadow element has to exist behind the FloatingButton, matching AppBarSurface's own pattern");
        return match.Groups["attrs"].Value;
    }

    private static string ShellMarkup()
        => File.ReadAllText(Path.Combine(RepoRoot(), "remex.desktop", "Views", "ShellView.axaml"));

    private static string RepoRoot([CallerFilePath] string thisSourceFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisSourceFile)!, "..", ".."));
}
