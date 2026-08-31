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
/// <c>material:FloatingButton</c> (RemEx-bado6, phase 2 of RemEx-ajpug/RemEx-kb4im).
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
/// A source scan, matching <see cref="ShellAppBarTests"/> and <see cref="ShellSnackbarHostTests"/>:
/// there is no headless Avalonia render harness in this repo, so nothing here can actually press the
/// button or measure a rendered shadow. What this proves is that the wiring exists and is shaped the
/// way the acceptance criteria — and the two traps above — require.
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
    public void TheGearFabShadow_UsesRemExsElevationRampNotMaterialsFixedOne()
    {
        var shadow = Regex.Match(ShellMarkup(),
            @"<Border Grid\.Row=""1"" Name=""GearFabShadow""(?<attrs>.*?)/>",
            RegexOptions.Singleline);
        shadow.Success.Should().BeTrue("a dedicated, childless shadow element has to exist behind the FloatingButton, matching AppBarSurface's own pattern");

        var attrs = shadow.Groups["attrs"].Value;
        attrs.Should().MatchRegex(@"BoxShadow=""\{DynamicResource Elevation2Shadow\}""",
            "the FAB is a persistently-raised surface — RemEx's own GlowStrength-aware ramp names that level 2 (\"raised resting\"), not Material's fixed black default");
        attrs.Should().MatchRegex(@"IsHitTestVisible=""False""",
            "the shadow sits behind the real button, which already owns the FAB's hit-testing");

        var fab = GearFabOpenTag();
        fab.Should().MatchRegex(@"assists:ShadowAssist\.ShadowDepth=""Depth0""",
            "FloatingButton's own ShadowAssist default (Depth1) writes a FIXED black BoxShadow as a local value on its template border — it has to be neutralized or it renders alongside GearFabShadow's real one");
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
    public void TheGearFab_KeepsItsCommandAndVisibilityBindings()
    {
        var fab = GearFabOpenTag();

        fab.Should().MatchRegex(@"Command=""\{Binding ToggleSettingsPanelCommand\}""",
            "the FAB still has to open the settings side sheet");
        fab.Should().MatchRegex(@"IsVisible=""\{Binding !IsShellChromeHidden\}""",
            "the FAB still has to hide during the fullscreen remote-desktop view, the same as before");
    }

    // ─────────────────────────── plumbing ───────────────────────────

    private static string GearFabOpenTag()
    {
        var match = Regex.Match(ShellMarkup(),
            @"<material:FloatingButton\b(?<attrs>.*?)>",
            RegexOptions.Singleline);
        match.Success.Should().BeTrue("the FloatingButton element has to exist for its attributes to be inspected");
        return match.Groups["attrs"].Value;
    }

    private static string ShellMarkup()
        => File.ReadAllText(Path.Combine(RepoRoot(), "remex.desktop", "Views", "ShellView.axaml"));

    private static string RepoRoot([CallerFilePath] string thisSourceFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisSourceFile)!, "..", ".."));
}
