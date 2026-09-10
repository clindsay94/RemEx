using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace Remex.Desktop.Tests.Views;

/// <summary>
/// Guards the drawer-footer presence badge's pulse styling in App.axaml (RemEx-d7xj8): the badge
/// carries a class-gated <c>Style.Animations</c> pulse rather than <c>MaterialAnimationAssist.
/// ContinuousAnimation</c>, whose Changed handler cannot be stopped by clearing it back to null
/// (verified against the upstream Material.Avalonia source) — unfit for a pulse that has to stop
/// when the phone disconnects or when reduced motion is on.
/// </summary>
/// <remarks>
/// A source scan, matching <see cref="ShellConnectionStatusControlTests"/> and
/// <see cref="Remex.Desktop.Tests.ViewModels.PaletteTransitionSuppressionTests"/> — there is no
/// headless Avalonia render in this suite to actually watch the badge breathe.
/// </remarks>
public class PresenceBadgePulseTests
{
    [Fact]
    public void ThePulseStyleAnimatesForeverWhileThePulseClassIsPresent()
    {
        PresenceStyleBlock().Should().MatchRegex(
            @"<Style Selector=""material\|Badged\.presence\.pulse /template/ material\|Badge"">",
            "the pulse animation has to be gated on the .pulse class specifically, not on .presence " +
            "alone — otherwise it would run for a disconnected phone too");

        PresenceStyleBlock().Should().MatchRegex(
            @"<Animation\b[^>]*IterationCount=""Infinite""",
            "the breathing effect has to repeat indefinitely while the badge is pulsing");
    }

    [Fact]
    public void TheBadgeColourAndCrossfadeSitOnTheTemplateBadgeNotOnBadgeBackground()
    {
        // BOTH COLOURS HAVE TO BE SET ON THE TEMPLATE'S Badge.Background, never on
        // Badged.BadgeBackground. Badge's own control theme carries a `[ColorMode=Error]` style
        // (Badged.BadgeColorMode defaults to Error) that pins Badge.Background to the Material
        // error red at Style priority, above the template binding Badged uses to pass
        // BadgeBackground down - so a BadgeBackground setter draws Material red no matter what
        // it says. Seen on the running app: the badge stayed red with a phone attached until the
        // setters moved here. And BadgeBackground is typed as the concrete Brush, so a
        // BrushTransition declared against it throws during App population, before any window.
        var block = PresenceStyleBlock();

        block.Should().MatchRegex(
            @"<Style Selector=""material\|Badged\.presence /template/ material\|Badge"">\s*<Setter Property=""Background"" Value=""\{DynamicResource SystemErrorBrush\}""",
            "no phone has to read as the template Badge's Background being red");
        block.Should().MatchRegex(
            @"<Style Selector=""material\|Badged\.presence\.connected /template/ material\|Badge"">\s*<Setter Property=""Background"" Value=""\{DynamicResource SystemSuccessBrush\}""",
            "an attached phone has to turn the template Badge green");
        block.Should().MatchRegex(
            @"<Transitions>\s*<BrushTransition Property=""Background""",
            "the crossfade has to target the template Badge's IBrush Background, not Badged.BadgeBackground");
        block.Should().NotContain(@"Property=""BadgeBackground""",
            "a BadgeBackground setter is overridden by Badge's [ColorMode=Error] theme style and a BadgeBackground transition crashes App population");
    }

    [Fact]
    public void DroppingThePulseClassStopsTheAnimation_NotMaterialAnimationAssist()
    {
        // Scoped to the actual style elements, not the comment above them — that comment names
        // MaterialAnimationAssist.ContinuousAnimation deliberately, to explain why it was rejected.
        var block = PresenceStyleBlock();

        block.Should().NotContain("MaterialAnimationAssist",
            "ContinuousAnimation's Changed handler only ever starts a new animation on a non-null " +
            "value — clearing it back to null does not cancel the one already running, so it cannot " +
            "express a pulse that stops on disconnect or reduced motion");
        block.Should().NotContain("ContinuousAnimation");
    }

    [Fact]
    public void TheSuppressorNullsTransitionsAfterThePresenceStyleInstallsThem()
    {
        // Same ordering rule RemEx-zgtn1 established for every other chrome Transitions collection
        // (see App.axaml's own comment on the suppressor block): at equal StyleTrigger priority the
        // LATER declaration wins, so a suppressor declared before the style whose Transitions it
        // has to null does nothing at all.
        var app = AppMarkup();

        var installs = app.IndexOf(@"<Style Selector=""material|Badged.presence /template/ material|Badge"">", StringComparison.Ordinal);
        var suppresses = app.IndexOf(
            @"<Style Selector=""Window.palette-transition-suppressed material|Badged.presence /template/ material|Badge"">",
            StringComparison.Ordinal);

        installs.Should().BeGreaterThan(-1, "the presence badge's Transitions-installing style has to exist");
        suppresses.Should().BeGreaterThan(-1,
            "the suppressor block has to null the presence badge's Transitions during a palette drag");
        suppresses.Should().BeGreaterThan(installs,
            "the suppressor must be declared after the style that installs the template Badge's Background " +
            "Transitions, or it is overwritten and does nothing during a seed-wheel drag");
    }

    [Fact]
    public void TheSuppressorStillSitsLastInApplicationStyles()
    {
        // RemEx-zgtn1's whole suppressor block must stay last in Application.Styles (its own
        // comment says so) — the new presence-badge suppressor is part of that block, not a
        // separate one that could drift below it.
        var app = AppMarkup();

        var suppresses = app.IndexOf(
            @"<Style Selector=""Window.palette-transition-suppressed material|Badged.presence /template/ material|Badge"">",
            StringComparison.Ordinal);
        var stylesClose = app.IndexOf("</Application.Styles>", StringComparison.Ordinal);

        suppresses.Should().BeGreaterThan(-1);
        stylesClose.Should().BeGreaterThan(-1);

        var between = app[(suppresses + 1)..stylesClose];
        between.Should().NotContain("<Style Selector=",
            "the presence-badge suppressor has to be the last style declared in Application.Styles, " +
            "matching every other suppressor in this block");
    }

    /// <summary>
    /// Just the <c>material|Badged.presence...</c> style elements — the text between the end
    /// of their introducing comment and the start of the next section's comment — so assertions
    /// about what the styling DOES reference can't accidentally match the comment explaining what
    /// it deliberately does NOT.
    /// </summary>
    private static string PresenceStyleBlock()
    {
        var app = AppMarkup();
        var start = app.IndexOf(@"<Style Selector=""material|Badged.presence"">", StringComparison.Ordinal);
        var end = app.IndexOf("<!-- The in-app toast", StringComparison.Ordinal);

        start.Should().BeGreaterThan(-1, "the presence badge styles have to exist");
        end.Should().BeGreaterThan(start, "the toast comment has to still follow the presence styles");

        return app[start..end];
    }

    private static string AppMarkup([CallerFilePath] string f = "")
        => File.ReadAllText(Path.Combine(RepoRoot(f), "remex.desktop", "App.axaml"));

    private static string RepoRoot([CallerFilePath] string thisSourceFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisSourceFile)!, "..", ".."));
}
