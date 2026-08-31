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
/// writes (it sets Primary/Secondary only) - RemEx-a3prn's own trap - and the same is true of
/// ColorZone's three OTHER modes (<c>Inverted</c>, <c>Light</c>, <c>Dark</c>, <c>Error</c>), not just
/// <c>Standard</c>. <c>PrimaryMid</c> is the one mode that IS seeded: <c>ThemeService.
/// PushSeedIntoMaterialTheme</c> pushes the accent seed as Material's own Primary swatch on every
/// <c>ApplyCustomization</c>, which is what lets this block "pull its colour from the generated
/// palette automatically" the way the bead's own description asks.
/// <see cref="TheDrawerHeaderZone_SetsModeAsAPlainAttribute"/> pins the exact literal
/// <c>Mode="PrimaryMid"</c> - not merely "not Standard" (Opus review round 1, LOW: an earlier version
/// of this test asserted only <c>NotMatchRegex(Mode="Standard")</c>, which a switch to
/// <c>Mode="Light"</c> would have passed just as easily, since Light is equally unseeded).
/// </para>
/// <para>
/// THE NEAR-INVISIBLE-TEXT TRAP. The "RemEx" wordmark used to paint with <c>AccentPrimaryBrush</c>
/// (= <c>palette.Primary</c>, <c>ThemeService.cs:281</c>) - the same seed <c>MaterialPrimaryMidBrush</c>
/// derives from, so putting that text on this new background would have made the word nearly vanish
/// into its own surface. <see cref="TheDrawerHeaderText_UsesTheZonesOwnContrastSolvedForeground"/>
/// pins that every text/icon element carrying an EXPLICIT <c>Foreground</c> attribute inside the zone
/// - the wordmark, the machine name, the presence summary, and the command-palette hint (moved inside
/// the zone by this same change) - points at <c>MaterialPrimaryMidForegroundBrush</c>, Material's own
/// contrast-solved on-primary brush from the same swatch, instead of a RemEx brush authored against a
/// different background. It deliberately does NOT claim to cover Style-INHERITED foregrounds (Opus
/// review round 1, LOW): the absorbed brand-mark <c>Button Classes="nav-item"</c> inside the zone
/// still resolves <c>Foreground</c> from the app-level <c>.nav-item</c> Style
/// (<c>TextSecondaryBrush</c>, authored for the dark glass rail) rather than the zone's own brush, and
/// no source scan can see that resolution happen. Harmless today because
/// <c>Controls/BrandMark.cs</c> draws no text or icon that would consume it, but a future addition
/// inside that specific button would silently inherit the wrong colour with this test still green -
/// see that test's own remarks for why the scope stops here rather than pretending otherwise.
/// </para>
/// <para>
/// THE COMPOUNDED-DIMMING TRAP (Opus review round 1, MEDIUM). MaterialPrimaryMidForegroundBrush is
/// contrast-solved against MaterialPrimaryMidBrush ONLY at full opacity — the review traced the old
/// 0.7/0.5 <c>Opacity</c> values on the presence summary and command-palette hint, PLUS the rail-wide
/// glass tint that used to WRAP the whole header at its own <c>Opacity="0.9"</c>, to roughly 3:1
/// composited contrast on SolarFlare's light seed (black on-primary text there, from Material.
/// Avalonia's own <c>ColorPair.PickContrastColor</c> - the reviewer's own traced mapping:
/// <c>ColorZone.axaml</c>'s <c>^[Mode=PrimaryMid]</c> -&gt; <c>MaterialThemeBase.cs:110,113</c> -&gt;
/// <c>theme.PrimaryMid.ForegroundColor</c> -&gt; <c>PickContrastColor</c>), under the WCAG 4.5:1
/// floor for small text. Two
/// independent fixes, both required: <see cref="TheDrawerHeaderZone_IsNotDimmedByTheRailsGlassTint"/>
/// pins that the glass tint is now an ordinary Grid child spanning only the nav-list/footer rows
/// rather than a container around the header too, and
/// <see cref="TheDrawerHeaderZone_SecondaryTextClearsTheContrastFloor"/> pins that the dimmed
/// elements' own local Opacity was raised to 0.85 - the figure the review called defensible for the
/// machine-name row - rather than staying at 0.7/0.5 even after the compounding stopped.
/// </para>
/// <para>
/// THE TRUNCATED-HOSTNAME TRAP (Opus review round 1, LOW). <c>DrawerHeaderMachineName</c>'s
/// <c>MaxWidth="160"</c> + <c>TextTrimming="CharacterEllipsis"</c> can render a long hostname with no
/// way to see the rest of it — no tooltip, no screen-reader path — on a block whose whole job is
/// saying which PC this is. <see cref="TheDrawerHeaderMachineName_HasATooltipForTheFullValue"/> pins
/// <c>ToolTip.Tip="{Binding MachineName}"</c>, a hover path to the untruncated value.
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
        //
        // The EXACT LITERAL "PrimaryMid", not merely "not Standard" (Opus review round 1, LOW —
        // folded from a separate, weaker test). ColorZone has five modes besides PrimaryMid
        // (Standard, Inverted, Light, Dark, Error) and PushSeedIntoMaterialTheme seeds none of the
        // other four either — a NotMatchRegex(Mode="Standard") assertion would have stayed green
        // through a switch to any of the other three, none of which track the generated palette any
        // better than Standard does. Asserting the one correct value directly is what actually pins
        // the choice this bead's own description depends on ("pull its colour from the generated
        // palette automatically").
        var zone = DrawerHeaderZoneOpenTag();
        zone.Should().MatchRegex(@"\bMode=""PrimaryMid""",
            "Mode has to be the one plain-attribute value that both wins over Material's StyleTrigger-priority selectors AND is the mode PushSeedIntoMaterialTheme actually seeds — every other Mode value fails one of those two things");
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
        // SCOPE (Opus review round 1, LOW): this pins every EXPLICIT, INLINE Foreground attribute
        // inside the zone — it cannot and does not claim anything about Foreground a descendant
        // resolves from a Style instead. The absorbed brand-mark Button (Classes="nav-item") is
        // exactly that case: App.axaml's ".nav-item" Style sets TextSecondaryBrush — a brush
        // authored for the dark glass rail, not this primary band — and that resolution is invisible
        // to a source scan. TheDrawerHeaderZone_NavItemButtonCarriesNoInlineForegroundToFight is the
        // narrower, honest claim about that element: it has none to conflict with the Style today,
        // because Controls/BrandMark.cs draws no text or icon that would consume an inherited
        // Foreground. A future TextBlock/MaterialIcon added directly inside that button would
        // silently inherit the rail's brush, not the zone's — outside what either test can catch.
        var zoneBlock = DrawerHeaderZoneFullElement();

        // Every text/icon element inside the zone has to point at the zone's own contrast-solved
        // foreground. AccentPrimaryBrush (the wordmark's old value) and TextSecondaryBrush (the
        // command-palette hint's old value) were both authored against a different background and
        // are not guaranteed to contrast against MaterialPrimaryMidBrush.
        Regex.Matches(zoneBlock, @"Foreground=""\{DynamicResource (?<brush>[A-Za-z]+)\}""")
            .Should().NotBeEmpty("the identity block has to contain at least one explicit Foreground so this test is not vacuously true")
            .And.OnlyContain(m => m.Groups["brush"].Value == "MaterialPrimaryMidForegroundBrush",
                "every explicit, inline Foreground inside the identity block has to be the zone's own contrast-solved brush — a RemEx brush authored for a different background is not guaranteed to be legible here");

        zoneBlock.Should().NotMatchRegex(@"Foreground=""\{DynamicResource AccentPrimaryBrush\}""",
            "AccentPrimaryBrush is close enough in hue to MaterialPrimaryMidBrush (both derive from the same seed) that text painted with it would nearly vanish into the new background");
    }

    [Fact]
    public void TheDrawerHeaderZone_NavItemButtonCarriesNoInlineForegroundToFight()
    {
        // The narrower, honest counterpart to TheDrawerHeaderText_UsesTheZonesOwnContrastSolvedForeground's
        // scope note above (Opus review round 1, LOW). The absorbed brand-mark Button
        // (Classes="nav-item") resolves Foreground from the app-level ".nav-item" Style
        // (TextSecondaryBrush, authored for the dark glass rail) — a source scan cannot see that
        // Style resolution, so this pins only what IS observable: the button itself sets no
        // conflicting inline Foreground, which is what keeps today's element (BrandMark, which
        // consumes no Foreground at all) harmless. This test going green is NOT proof the button's
        // effective foreground contrasts against DrawerHeaderZone's background — it is proof the two
        // are not fighting over an inline value, which is a different and smaller claim.
        var zoneBlock = DrawerHeaderZoneFullElement();
        var navItemButton = Regex.Match(zoneBlock,
            @"<Button\b(?=[^>]*\bClasses=""nav-item"")(?<attrs>.*?)>",
            RegexOptions.Singleline);
        navItemButton.Success.Should().BeTrue("the absorbed brand-mark toggle button has to still be a Classes=\"nav-item\" Button inside the zone");
        navItemButton.Groups["attrs"].Value.Should().NotMatchRegex(@"\bForeground=",
            "the button relies on the .nav-item Style for its foreground today (BrandMark draws no text/icon) — an inline Foreground here would silently stop tracking that Style without this test noticing anything changed");
    }

    [Fact]
    public void TheShellViewModel_ExposesMachineName()
    {
        var source = File.ReadAllText(Path.Combine(RepoRoot(), "remex.desktop", "ViewModels", "ShellViewModel.cs"));

        source.Should().MatchRegex(@"public string MachineName => Environment\.MachineName;",
            "the drawer header binds {Binding MachineName} — ShellViewModel has to expose it, sourced from the same Environment.MachineName every other host-identity surface (PairingHandler, MdnsAdvertisingService) already uses");
    }

    [Fact]
    public void TheDrawerHeaderMachineName_HasATooltipForTheFullValue()
    {
        // Opus review round 1, LOW: MaxWidth + CharacterEllipsis truncates a long hostname
        // ("connor-workstation-lab-04" -> "connor-workstation-l…") with no hover, no tooltip, and no
        // screen-reader path to the full value — on a block whose entire job is saying which PC this
        // is. ToolTip.Tip restores a hover path to the untruncated MachineName.
        var zoneBlock = DrawerHeaderZoneFullElement();
        var match = Regex.Match(zoneBlock,
            @"<TextBlock\b(?=[^>]*\bName=""DrawerHeaderMachineName"")(?<attrs>.*?)/>",
            RegexOptions.Singleline);
        match.Success.Should().BeTrue("the machine-name TextBlock has to exist for its attributes to be inspected");
        match.Groups["attrs"].Value.Should().MatchRegex(@"ToolTip\.Tip=""\{Binding MachineName\}""",
            "a truncated hostname needs a hover path to the full value, the same binding the visible Text already uses");
    }

    [Fact]
    public void TheDrawerHeaderZone_SecondaryTextClearsTheContrastFloor()
    {
        // Opus review round 1, MEDIUM. MaterialPrimaryMidForegroundBrush is Material's own
        // contrast-solved on-primary colour ONLY at full opacity — dimming it blends the rendered
        // pixel toward whatever sits behind the zone. On SolarFlare's light, high-luminance seed
        // (PickContrastColor returns black on-primary text there) the presence summary's old 0.7 and
        // the command-palette hint's old 0.5 landed around 3:1, under the WCAG 4.5:1 floor for small
        // text — the reviewer's own worked example. 0.85 matches the figure the machine-name row
        // already used and the review called defensible.
        var zoneBlock = DrawerHeaderZoneFullElement();

        zoneBlock.Should().NotMatchRegex(@"Opacity=""0\.7""",
            "the presence summary's old 0.7 opacity is exactly the value the review measured landing under the WCAG 4.5:1 floor on SolarFlare");
        zoneBlock.Should().NotMatchRegex(@"Opacity=""0\.5""",
            "the command-palette hint's old 0.5 opacity is the reviewer's own worked worst-case example (roughly 3:1 black-on-light text)");

        var presenceSummary = Regex.Match(zoneBlock,
            @"<TextBlock\b(?=[^>]*\bName=""DrawerHeaderPresenceSummary"")(?<attrs>.*?)/>",
            RegexOptions.Singleline);
        presenceSummary.Success.Should().BeTrue("the presence-summary TextBlock has to exist for its attributes to be inspected");
        presenceSummary.Groups["attrs"].Value.Should().MatchRegex(@"\bOpacity=""0\.85""",
            "the presence summary has to be raised to the same 0.85 the review called defensible for the machine-name row");
    }

    [Fact]
    public void TheDrawerHeaderZone_IsNotDimmedByTheRailsGlassTint()
    {
        // Opus review round 1, MEDIUM. The rail-wide glass tint used to be a Border WRAPPING the
        // entire header/nav-list/footer Grid at Opacity="0.9" — compounding with the zone's own
        // dimmed elements to push contrast further under the WCAG floor than the local Opacity
        // values alone would suggest. It is now an ordinary Grid child spanning rows 1-2 (nav list +
        // footer) instead of a container, so DrawerHeaderZone (row 0) renders at full opacity while
        // the nav list and footer keep their previous translucent look.
        var xaml = ShellMarkup();

        xaml.Should().MatchRegex(@"<Border Grid\.Row=""1"" Grid\.RowSpan=""2"" Background=""\{DynamicResource GlassBaseDarkBrush\}"" Opacity=""0\.9""\s*/>",
            "the glass tint has to be a self-contained Grid child spanning only the nav-list/footer rows, not a wrapper around the whole Grid");

        // The exact old shape this replaced: a Border OPENING tag (no self-close) immediately
        // followed by the Grid it used to wrap. If this reappears, the header is back under the
        // ancestor opacity that caused the MEDIUM finding.
        xaml.Should().NotMatchRegex(@"<Border Background=""\{DynamicResource GlassBaseDarkBrush\}"" Opacity=""0\.9"">\s*<Grid RowDefinitions=""Auto,\*,Auto"">",
            "a wrapping Border around the header/nav-list/footer Grid would put DrawerHeaderZone's opaque background back under the rail's 0.9 opacity");
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
