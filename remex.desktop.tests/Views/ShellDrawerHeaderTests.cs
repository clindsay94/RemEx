using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace Remex.Desktop.Tests.Views;

/// <summary>
/// Guards the drawer header's move from a plain, unnamed <c>StackPanel</c> to a real Material
/// <c>material:ColorZone</c> identity block carrying the RemEx mark, the machine name and the
/// paired-device summary (RemEx-dnqws, phase 2 of RemEx-ajpug/RemEx-kb4im).
/// </summary>
/// <remarks>
/// <para>
/// THE PALETTE TRAP, A THIRD TIME. <c>ColorZone</c>'s <c>Mode</c> selectors
/// (<c>^[Mode=PrimaryMid]</c>, Material.Avalonia 3.19.0's own <c>ColorZone.axaml</c>) are ACTIVATED
/// at <c>BindingPriority.StyleTrigger</c> - the same
/// <c>material-avalonia-activated-setters-outrank-plain-overrides</c> shape RemEx-a3prn and
/// RemEx-zrlze both hit - so <see cref="TheDrawerHeaderZone_SetsModeAsAPlainAttribute"/> pins that
/// <c>Mode="PrimaryMid"</c> is a plain attribute on the element, not a Style selector that would
/// compile, match, and paint nothing.
/// </para>
/// <para>
/// THE UNSEEDED-BRUSH TRAP, THE ONE MODE=STANDARD DOESN'T HIT. <c>Mode=Standard</c> resolves to
/// <c>MaterialPaperBrush</c>/<c>MaterialBodyBrush</c>, which <c>PushSeedIntoMaterialTheme</c> never
/// writes (it sets Primary/Secondary only) - RemEx-a3prn's own trap. <c>PrimaryMid</c> is the mode
/// that IS seeded: <c>ThemeService.PushSeedIntoMaterialTheme</c> pushes the accent seed as Material's
/// own Primary swatch on every <c>ApplyCustomization</c>, which is what lets this block "pull its
/// colour from the generated palette automatically" the way the bead's own description asks.
/// <see cref="TheDrawerHeaderZone_UsesTheSeededPrimaryModeNotTheUnseededStandardOne"/> pins the choice.
/// </para>
/// <para>
/// THE NEAR-INVISIBLE-TEXT TRAP. The "RemEx" wordmark used to paint with <c>AccentPrimaryBrush</c>
/// (= <c>palette.Primary</c>, <c>ThemeService.cs:281</c>) - the same seed <c>MaterialPrimaryMidBrush</c>
/// derives from, so putting that text on this new background would have made the word nearly vanish
/// into its own surface. <see cref="TheDrawerHeaderText_UsesTheZonesOwnContrastSolvedForeground"/>
/// pins that every text/icon element inside the zone - the wordmark, the machine name, the presence
/// summary, and the command-palette hint (moved inside the zone by this same change) - points at
/// <c>MaterialPrimaryMidForegroundBrush</c>, Material's own contrast-solved on-primary brush from the
/// same swatch, instead of a RemEx brush authored against a different background.
/// </para>
/// <para>
/// THE ELEVATION TRAP, the same one <c>AppBarSurface</c>/<c>GearFabShadow</c> already document:
/// <c>ColorZone</c>'s default <c>ShadowAssist.ShadowDepth="Depth2"</c> renders Material's own fixed,
/// GlowStrength-blind black shadow. <see cref="TheDrawerHeaderZone_NeutralizesMaterialsFixedShadow"/>
/// pins <c>Depth0</c> - this block does not need its own elevation shadow inside the drawer rail.
/// </para>
/// <para>
/// THE STRAY-TOGGLE QUESTION. RemEx-dnqws's own drain instructions ask this bead to say whether the
/// identity block absorbs, keeps, or sits alongside the round brand-mark drawer-toggle button already
/// in <c>LeftDrawerContent</c>. Decision: ABSORBED, not duplicated - the button IS "the RemEx mark"
/// the bead's own description asks the header to carry, so it moved inside the new ColorZone rather
/// than gaining a second affordance beside it.
/// <see cref="TheDrawerHeaderZone_AbsorbsTheBrandMarkToggleRatherThanDuplicatingIt"/> pins that there
/// is still exactly one <c>ToggleDrawerCommand</c>-bound button in the drawer content, and that it now
/// lives inside <c>DrawerHeaderZone</c>.
/// </para>
/// <para>
/// A source scan, matching <see cref="Remex.Desktop.Tests.Views.ShellGearFabTests"/> and
/// <see cref="Remex.Desktop.Tests.Views.ShellAppBarTests"/>: there is no headless Avalonia render
/// harness in this repo, so nothing here can actually measure a rendered brush or contrast ratio.
/// What this proves is that the wiring exists and is shaped the way the acceptance criteria - and
/// every trap above - require.
/// </para>
/// </remarks>
public class ShellDrawerHeaderTests
{
    [Fact]
    public void TheDrawerHeaderZone_IsAMaterialColorZoneNotAPlainStackPanel()
    {
        var xaml = ShellMarkup();

        xaml.Should().MatchRegex(@"<material:ColorZone\b[^>]*\bName=""DrawerHeaderZone""",
            "the plain, unnamed drawer-header StackPanel has to become a real Material ColorZone identity block, not stay a bare panel with a comment on top of it");

        // The exact old element this replaced. If a bare, unnamed header StackPanel is back, the
        // migration was reverted.
        xaml.Should().NotMatchRegex(@"<StackPanel Grid\.Row=""0"" Margin=""0,16,0,8"">",
            "the old plain StackPanel header has to be gone, not merely restyled underneath a new comment");
    }

    [Fact]
    public void TheDrawerHeaderZone_SetsModeAsAPlainAttribute()
    {
        // Mode has to be a LocalValue (plain attribute) on the element itself. A Style selector
        // targeting this ColorZone's Mode would lose to Material's own activated ^[Mode=...]
        // selectors regardless of what it set — the exact trap
        // material-avalonia-activated-setters-outrank-plain-overrides documents.
        var zone = DrawerHeaderZoneOpenTag();
        zone.Should().MatchRegex(@"\bMode=""PrimaryMid""",
            "Mode has to be set directly on the ColorZone as a plain attribute so it wins over Material's own StyleTrigger-priority selectors");
    }

    [Fact]
    public void TheDrawerHeaderZone_UsesTheSeededPrimaryModeNotTheUnseededStandardOne()
    {
        var zone = DrawerHeaderZoneOpenTag();

        // PrimaryMid/PrimaryMidForeground ARE seeded (PushSeedIntoMaterialTheme writes Primary).
        // Standard resolves to MaterialPaperBrush/MaterialBodyBrush, which are NOT — the exact
        // unseeded pair RemEx-a3prn's own trap warns about, and the wrong choice for a block whose
        // whole point is to "pull its colour from the generated palette automatically".
        zone.Should().NotMatchRegex(@"\bMode=""Standard""",
            "Mode=Standard resolves to the unseeded MaterialPaperBrush/MaterialBodyBrush pair, which does not track the generated palette");
    }

    [Fact]
    public void TheDrawerHeaderZone_NeutralizesMaterialsFixedShadow()
    {
        var zone = DrawerHeaderZoneOpenTag();
        zone.Should().MatchRegex(@"assists:ShadowAssist\.ShadowDepth=""Depth0""",
            "ColorZone's own Depth2 default renders Material's fixed, GlowStrength-blind black shadow — the same trap AppBarSurface and GearFabShadow already neutralize");
    }

    [Fact]
    public void TheDrawerHeaderZone_AbsorbsTheBrandMarkToggleRatherThanDuplicatingIt()
    {
        var xaml = ShellMarkup();

        // Exactly one drawer-toggle affordance lives in the drawer's own content: the round
        // brand-mark button, now inside DrawerHeaderZone. A second one next to it would be the same
        // "three toggles" mistake RemEx-a3prn was warned against, just for this button instead of
        // window controls.
        var toggleMatches = Regex.Matches(xaml, @"Command=""\{Binding ToggleDrawerCommand\}""");
        toggleMatches.Count.Should().Be(2,
            "there should be exactly two ToggleDrawerCommand bindings in the whole shell: the app-bar hamburger (DrawerToggle) and the drawer's own brand-mark button — a third would mean the toggle was duplicated instead of absorbed");

        var zoneBlock = DrawerHeaderZoneFullElement();
        zoneBlock.Should().Contain(@"Command=""{Binding ToggleDrawerCommand}""",
            "the brand-mark toggle has to live INSIDE the new identity block, not beside it");
        zoneBlock.Should().MatchRegex(@"<ctrl:BrandMark\s*/>",
            "the RemEx brand mark itself has to still be there — absorbed, not replaced");
    }

    [Fact]
    public void TheDrawerHeader_CarriesTheMachineName()
    {
        var zoneBlock = DrawerHeaderZoneFullElement();
        zoneBlock.Should().MatchRegex(@"<TextBlock\b[^>]*\bName=""DrawerHeaderMachineName""[^>]*\bText=""\{Binding MachineName\}""",
            "the identity block has to show this PC's machine name, per the bead's own acceptance");
    }

    [Fact]
    public void TheDrawerHeader_CarriesThePairedDeviceSummary()
    {
        var zoneBlock = DrawerHeaderZoneFullElement();
        zoneBlock.Should().MatchRegex(@"<TextBlock\b[^>]*\bName=""DrawerHeaderPresenceSummary""[^>]*\bText=""\{Binding Presence\.PresenceText\}""",
            "the identity block has to show the paired-device summary — PhonePresenceMonitor.PresenceText, the same already-localized line every other surface uses, not a new ad-hoc string");
    }

    [Fact]
    public void TheDrawerHeaderText_UsesTheZonesOwnContrastSolvedForeground()
    {
        var zoneBlock = DrawerHeaderZoneFullElement();

        // Every text/icon element inside the zone has to point at the zone's own contrast-solved
        // foreground. AccentPrimaryBrush (the wordmark's old value) and TextSecondaryBrush (the
        // command-palette hint's old value) were both authored against a different background and
        // are not guaranteed to contrast against MaterialPrimaryMidBrush.
        Regex.Matches(zoneBlock, @"Foreground=""\{DynamicResource (?<brush>[A-Za-z]+)\}""")
            .Should().NotBeEmpty("the identity block has to contain at least one explicit Foreground so this test is not vacuously true")
            .And.OnlyContain(m => m.Groups["brush"].Value == "MaterialPrimaryMidForegroundBrush",
                "every Foreground inside the identity block has to be the zone's own contrast-solved brush — a RemEx brush authored for a different background is not guaranteed to be legible here");

        zoneBlock.Should().NotMatchRegex(@"Foreground=""\{DynamicResource AccentPrimaryBrush\}""",
            "AccentPrimaryBrush is close enough in hue to MaterialPrimaryMidBrush (both derive from the same seed) that text painted with it would nearly vanish into the new background");
    }

    [Fact]
    public void TheShellViewModel_ExposesMachineName()
    {
        var source = File.ReadAllText(Path.Combine(RepoRoot(), "remex.desktop", "ViewModels", "ShellViewModel.cs"));

        source.Should().MatchRegex(@"public string MachineName => Environment\.MachineName;",
            "the drawer header binds {Binding MachineName} — ShellViewModel has to expose it, sourced from the same Environment.MachineName every other host-identity surface (PairingHandler, MdnsAdvertisingService) already uses");
    }

    // ─────────────────────────── plumbing ───────────────────────────

    private static string DrawerHeaderZoneOpenTag()
    {
        // Anchored on the Name via a lookahead, matching ShellGearFabTests' own fix for this: a
        // reordering of Mode/Margin/Padding/ShadowAssist (a semantically null edit) cannot fail
        // this scan.
        var match = Regex.Match(ShellMarkup(),
            @"<material:ColorZone\b(?=[^>]*\bName=""DrawerHeaderZone"")(?<attrs>.*?)>",
            RegexOptions.Singleline);
        match.Success.Should().BeTrue("the DrawerHeaderZone ColorZone has to exist for its attributes to be inspected");
        return match.Groups["attrs"].Value;
    }

    private static string DrawerHeaderZoneFullElement()
    {
        // Captures the WHOLE element, open tag through matching close tag, so nested content
        // (the brand-mark button, the machine-name/presence TextBlocks, the command-palette hint)
        // can be asserted on without the scan spilling into the rest of the drawer content below it.
        var match = Regex.Match(ShellMarkup(),
            @"<material:ColorZone\b(?=[^>]*\bName=""DrawerHeaderZone"")[^>]*>(?<body>.*?)</material:ColorZone>",
            RegexOptions.Singleline);
        match.Success.Should().BeTrue("the DrawerHeaderZone ColorZone has to have a closing tag with its content in between");
        return match.Groups["body"].Value;
    }

    private static string ShellMarkup()
        => File.ReadAllText(Path.Combine(RepoRoot(), "remex.desktop", "Views", "ShellView.axaml"));

    private static string RepoRoot([CallerFilePath] string thisSourceFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisSourceFile)!, "..", ".."));
}
