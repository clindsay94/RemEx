using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace Remex.Desktop.Tests.ViewModels;

/// <summary>
/// Guards the palette-crossfade suppression contract (RemEx-zgtn1).
/// </summary>
/// <remarks>
/// <para>
/// EVERY FAILURE MODE HERE IS SILENT, which is the only reason these are worth writing. The whole
/// mechanism is a computed property feeding a <c>Classes</c> binding feeding a style selector. Break
/// any link and nothing throws, nothing logs, and the app keeps working — it just animates when the
/// user asked it not to, or fails to animate when it should. No test in this suite would notice.
/// </para>
/// <para>
/// The specific regression that prompted the XAML half: an earlier revision put the crossfade on a
/// bare <c>Window</c> selector, which reaches ConfirmationDialog, PairingDialog, FileConsentDialog,
/// TrayFlyoutWindow, CommandPaletteWindow and the rest — none of which carry the suppression class.
/// Those windows snapped before the bead and would have animated for 400ms afterwards with no way to
/// turn it off, including under reduced motion. The selector is opt-in now, and
/// <see cref="TheCrossfadeIsOptIn_NotOnBareWindow"/> is what keeps it that way.
/// </para>
/// <para>
/// A source scan for the XAML, because there is no headless render in this repo. It cannot prove the
/// animation looks right; it proves the wiring that decides whether one runs at all.
/// </para>
/// </remarks>
public class PaletteTransitionSuppressionTests
{
    [Fact]
    public void TheCrossfadeIsOptIn_NotOnBareWindow()
    {
        var app = AppMarkup();

        Regex.Match(app, @"<Style Selector=""Window\.palette-crossfade"">").Success
            .Should().BeTrue("the crossfade has to be opt-in via a class");

        // The exact regression: a bare `Window` selector carrying a Transitions setter. Scoped to a
        // Transitions setter specifically — `<Style Selector="Window">` legitimately exists for the
        // four inherited window properties and must not be flagged.
        //
        // MATCHES, NOT MATCH: there is one such block today, but the regression this guards recurs by
        // someone APPENDING a second bare Window style further down the file, which a first-match-only
        // check would never see (review LOW, round 2).
        var bareWindows = Regex.Matches(app,
            @"<Style Selector=""Window"">(?<body>.*?)</Style>", RegexOptions.Singleline);
        bareWindows.Count.Should().BeGreaterThan(0, "the inherited-properties Window style still exists");

        foreach (Match bare in bareWindows)
        {
            bare.Groups["body"].Value.Should().NotContain("Transitions",
                "a Transitions setter on a bare Window selector reaches every dialog and tray window " +
                "in the app, none of which bind the suppression class — that is an unturnoffable " +
                "animation for a reduced-motion user");
        }

        MainWindowMarkup().Should().Contain("Classes=\"palette-crossfade\"",
            "MainWindow has to opt in, or the crossfade never runs anywhere");
    }

    [Fact]
    public void SuppressionOutranksTheCrossfade_ByCarryingAnActivatorAndComingLast()
    {
        // RemEx-zrlze shipped a P0 because an override lost on PRIORITY despite being declared
        // later: Avalonia's StyleInstance.GetPriority returns StyleTrigger for a style with an
        // activator and Style otherwise, and priority is compared BEFORE application order. So the
        // suppression selector must itself carry a class activator (it does — two of them), which
        // puts it at the same StyleTrigger priority as the crossfade style, where declaration order
        // is what actually breaks the tie. Both halves are load-bearing.
        var app = AppMarkup();

        var crossfade = app.IndexOf(@"<Style Selector=""Window.palette-crossfade"">", System.StringComparison.Ordinal);
        var suppress = app.IndexOf(@"<Style Selector=""Window.palette-crossfade.palette-transition-suppressed"">", System.StringComparison.Ordinal);

        crossfade.Should().BeGreaterThan(-1, "the crossfade style has to exist");
        suppress.Should().BeGreaterThan(-1,
            "the suppression selector has to carry a class activator of its own, or it resolves at " +
            "BindingPriority.Style and loses to the crossfade regardless of where it is declared");
        suppress.Should().BeGreaterThan(crossfade,
            "at equal StyleTrigger priority the later declaration wins, so suppression must come after");
    }

    [Fact]
    public void MainWindowBindsSuppression_AndTheChromeTransitionsRideIt()
    {
        MainWindowMarkup().Should().MatchRegex(
            @"Classes\.palette-transition-suppressed=""\{Binding SuppressPaletteTransitions\}""",
            "nothing else drives the class, so without this binding suppression can never engage " +
            "and reduced motion is ignored");

        // ORDER, NOT PRESENCE. The first version of this assertion checked only that the selector
        // strings appeared somewhere in App.axaml, and passed for hours against markup where the
        // feature was switched off: the suppressors were declared a thousand lines ABOVE the styles
        // they must outrank, all of which carry class activators and therefore resolve at the same
        // StyleTrigger priority, where the LATER declaration wins. Presence was never the property
        // that mattered. A green test asserting a feature works when it does not is worse than no
        // test, because the next person stops looking.
        var app = AppMarkup();

        // The tail of the chrome transitions: whichever of these is declared last is what every
        // suppressor has to sit below.
        var lastChromeTransition = new[] { @":is(Button).swatch", @"Ellipse.status-ring" }
            .Select(s => app.LastIndexOf(s, StringComparison.Ordinal))
            .Max();
        lastChromeTransition.Should().BeGreaterThan(-1,
            "the chrome styles this block has to outrank must still exist for the ordering to mean anything");

        foreach (var descendant in new[] { "material|Card", "Button", "Border", "Ellipse" })
        {
            var selector = $@"<Style Selector=""Window.palette-transition-suppressed {descendant}"">";
            var at = app.IndexOf(selector, StringComparison.Ordinal);

            at.Should().BeGreaterThan(-1,
                $"{descendant} colour transitions have to be suppressed during a drag as well as the " +
                "window's own, or the preview fights the animation");
            at.Should().BeGreaterThan(lastChromeTransition,
                $"the {descendant} suppressor is declared BEFORE the styles that set chrome " +
                "transitions, so those styles overwrite it and it does nothing — it has to be the " +
                "last thing in Application.Styles");
        }
    }

    [Fact]
    public void SuppressionIsDrivenByBothReducedMotionAndDragging()
    {
        // A computed property fed by two hand-written OnPropertyChanged calls. Drop either notify
        // and the class binding silently stops updating: reduced-motion users get animation back,
        // and nothing fails.
        var shell = ShellViewModelSource();

        shell.Should().MatchRegex(
            @"public bool SuppressPaletteTransitions => IsReducedMotion \|\| IsPaletteDragging;",
            "both inputs have to feed the flag");

        // EXTRACT THE BODY, THEN ASSERT INSIDE IT. Two regexes were wrong here before this one, in
        // opposite directions, and injection caught both:
        //   `(.|\n)*?` is unbounded, so deleting this method's notify let the match run on and find
        //   OnIsPaletteDraggingChanged's instead — the test stayed green against the real defect.
        //   `[^}]*` cannot cross ANY brace, and the body contains `current with { ... }`, so it
        //   stopped at the object initializer and failed against correct code.
        // Anchoring on the method's own closing brace at class indent is what actually scopes it,
        // and is the idiom CommandPaletteLightDismissTests already uses for the same reason.
        ExtractMethod(shell, "OnIsReducedMotionChanged").Should()
            .Contain("OnPropertyChanged(nameof(SuppressPaletteTransitions));",
                "a computed property raises nothing on its own — without this notify INSIDE " +
                "OnIsReducedMotionChanged, toggling reduced motion leaves the class binding stale " +
                "and a reduced-motion user silently keeps the animation");

        shell.Should().MatchRegex(
            @"partial void OnIsPaletteDraggingChanged\(bool value\) => OnPropertyChanged\(nameof\(SuppressPaletteTransitions\)\)",
            "same for the drag flag, which is the one that changes on every pointer press");
    }

    /// <summary>
    /// Everything from a method's opening brace to the matching close at class indent (four spaces).
    /// Same heuristic <c>CommandPaletteLightDismissTests</c> uses, and correct for the same reason:
    /// every method in these files is a one-level-nested member, so the first <c>\n    }</c> after
    /// the signature is that method's own end regardless of what braces the body contains.
    /// </summary>
    private static string ExtractMethod(string source, string methodName)
    {
        var match = Regex.Match(source, $@"{Regex.Escape(methodName)}\s*\([^)]*\)\s*\{{.*?\n    \}}",
            RegexOptions.Singleline);
        match.Success.Should().BeTrue($"{methodName} moved, was renamed, or changed shape — update this test");
        return match.Value;
    }

    private static string AppMarkup([CallerFilePath] string f = "")
        => File.ReadAllText(Path.Combine(RepoRoot(f), "remex.desktop", "App.axaml"));

    private static string MainWindowMarkup([CallerFilePath] string f = "")
        => File.ReadAllText(Path.Combine(RepoRoot(f), "remex.desktop", "MainWindow.axaml"));

    private static string ShellViewModelSource([CallerFilePath] string f = "")
        => File.ReadAllText(Path.Combine(RepoRoot(f), "remex.desktop", "ViewModels", "ShellViewModel.cs"));

    private static string RepoRoot([CallerFilePath] string thisSourceFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisSourceFile)!, "..", ".."));
}
