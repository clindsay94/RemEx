using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace Remex.Desktop.Tests.Views;

/// <summary>
/// Guards the settings overlay's move from a hand-rolled <c>Border</c> + backdrop to Material
/// <c>SideSheet</c> (RemEx-zrlze), and the drawer/settings-sheet interaction it required.
/// </summary>
/// <remarks>
/// <para>
/// THE SCRIM TRAP. Material.Styles 3.19.0's own <c>SideSheet.axaml</c> disables its
/// <c>PART_Scrim</c> outright for anything not carrying a <c>:mobile</c> pseudo-class — and nothing
/// in <c>SideSheet.axaml.cs</c> ever applies <c>:mobile</c> (its own comment says "TODO: mobile
/// variant"). Read straight off the DLL this looks like a working scrim; read off the actual
/// template source (per the RemEx-vryje/RemEx-04ftl lesson) it is dead on desktop, which is why
/// <see cref="TheSideSheetScrimOverride_ForcesItVisibleOnDesktop"/> exists — the type being present
/// proves nothing about the pixels.
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
/// A source scan, not a behavioural test — there is no headless Avalonia harness in this repo (see
/// <see cref="ShellNavListTests"/>, <see cref="ShellDrawerOverlayTests"/>), so nothing here can
/// actually open the sheet, press Esc, and look. What this proves is that the wiring exists and is
/// shaped the way the acceptance criteria require; it cannot prove the animation renders correctly.
/// Parsed with per-match, per-element regex the way <c>CommandPaletteLightDismissTests</c> already
/// does for this same reason (a loose <c>Should().Contain()</c> on the whole file would go green on
/// three unrelated substrings that never sit together, per the RemEx-hev1g/RemEx-thwlr lesson).
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
    public void TheSideSheetScrimOverride_ForcesItVisibleOnDesktop()
    {
        // THE STRUCTURAL GUARD for the dead-scrim trap described on this class. Material's own
        // ControlTheme disables PART_Scrim for anything lacking :mobile, which SideSheet never
        // applies - so without this override the sheet would slide in over nothing, no scrim, no
        // "does not double up" to even prove.
        var style = Regex.Match(ShellMarkup(),
            @"<Style Selector=""material\|SideSheet /template/ Border#PART_Scrim"">(?<body>.*?)</Style>",
            RegexOptions.Singleline);

        style.Success.Should().BeTrue(
            "an app-level Style targeting SideSheet's PART_Scrim has to exist to undo the base " +
            "theme's IsEnabled/IsVisible=False");

        var body = style.Groups["body"].Value;
        body.Should().MatchRegex(@"Setter Property=""IsEnabled""\s*Value=""True""",
            "IsEnabled=False on the base theme makes the scrim un-clickable even if painted");
        body.Should().MatchRegex(@"Setter Property=""IsVisible""\s*Value=""True""",
            "IsVisible=False on the base theme is what actually hides the scrim on desktop");
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
