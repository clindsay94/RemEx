using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Windows.Input;
using FluentAssertions;
using Remex.Desktop.ViewModels;
using Xunit;

namespace Remex.Desktop.Tests.Views;

/// <summary>
/// Pins what "unchanged" means for the dashboard, so the Material.Avalonia rewrite of
/// <c>HomeView.axaml</c> (RemEx-oszfm) can be checked against something other than a screenshot
/// (RemEx-1qpjh).
/// </summary>
/// <remarks>
/// <para>
/// A CHARACTERISATION TEST, not a correctness test. It asserts that the surface still has the parts
/// it has today; it says nothing about whether those parts are the right ones. That is deliberate —
/// the rewrite is allowed to change how the dashboard looks, and is not allowed to quietly lose a
/// button, a command, or a status indicator on the way.
/// </para>
/// <para>
/// A SOURCE-TEXT TEST, for the reason <c>StatusDotPresenceBindingTests</c> gives: Avalonia binding
/// failures are silent, there is no headless render here, and this view has already shipped two
/// buttons that were wired to nothing and looked completely normal (the SystemStatus Fix and Explain
/// buttons, RemEx-tb0a). A rewrite is exactly when that happens again.
/// </para>
/// <para>
/// The prose inventory — refresh behaviour, persistence, empty states, the things a test cannot
/// assert — is in <c>docs/DASHBOARD-CHARACTERISATION.md</c>. Localization keys are NOT checked here:
/// <c>scripts/check-localization.ps1</c> already fails the build on a key that is used but not
/// declared, and a second copy of that check would drift.
/// </para>
/// </remarks>
public class HomeViewCharacterisationTests
{
    /// <summary>
    /// Every command HomeView binds today, in the normalised form <see cref="CommandBindings"/>
    /// produces.
    /// </summary>
    /// <remarks>
    /// AN EXPLICIT LIST RATHER THAN A COUNT, because a count tells the next person that something
    /// went missing and this tells them what. Eighteen distinct commands behind twenty buttons:
    /// <c>NavigateToCanvasCommand</c> is bound three times (the workspace link, the no-sensors
    /// empty state, and the Sensors quick-launch tile).
    /// <para>
    /// Changing this list is legitimate — the rewrite may well move a command somewhere better. It
    /// has to be a deliberate edit somebody justifies, which is the whole point of writing it down
    /// before the rewrite rather than after.
    /// </para>
    /// </remarks>
    private static readonly string[] ExpectedCommands =
    [
        "ClearActivityCommand",
        "Connection.ConnectCommand",
        "Connection.DisconnectCommand",
        "NavigateToAboutCommand",
        "NavigateToAppLauncherCommand",
        "NavigateToCanvasCommand",
        "NavigateToCustomizationCommand",
        "NavigateToDiagnosticLogsCommand",
        "NavigateToFileTransferCommand",
        "NavigateToRemoteCommand",
        "NavigateToTaskManagerCommand",
        "OpenGitHubCommand",
        "OpenHwInfoCommand",
        "OpenPlayStoreCommand",
        "Shell.OpenCommandPaletteCommand",
        "SystemStatusViewModel.ExplainCommand",
        "SystemStatusViewModel.FixCommand",
        "SystemStatus.RefreshCommand",
    ];

    [Fact]
    public void EveryCommandBindingResolvesToARealCommand()
    {
        var unresolved = CommandBindings()
            .Distinct(StringComparer.Ordinal)
            .Where(path => Resolve(path) is null)
            .ToArray();

        unresolved.Should().BeEmpty(
            "a Command bound to a path that does not exist gives a button that does nothing, with no "
            + "error anywhere — that is how the SystemStatus Fix button shipped dead (RemEx-tb0a)");
    }

    [Fact]
    public void EveryCommandBindingResolvesToSomethingThatIsActuallyACommand()
    {
        // Resolving is not enough. A path that lands on a bool or a string binds without complaint
        // and the button is just as dead as one bound to nothing.
        var notCommands = CommandBindings()
            .Distinct(StringComparer.Ordinal)
            .Select(path => (path, type: Resolve(path)))
            .Where(x => x.type is not null && !typeof(ICommand).IsAssignableFrom(x.type))
            .Select(x => $"{x.path} is {x.type!.Name}")
            .ToArray();

        notCommands.Should().BeEmpty("Command must be bound to an ICommand, not to a value");
    }

    [Fact]
    public void TheDashboardStillBindsTheSameCommands()
    {
        CommandBindings()
            .Distinct(StringComparer.Ordinal)
            .OrderBy(x => x, StringComparer.Ordinal)
            .Should()
            .BeEquivalentTo(ExpectedCommands.OrderBy(x => x, StringComparer.Ordinal),
                options => options.WithStrictOrdering(),
                "the Material rewrite may restyle the dashboard but must not silently drop a way to "
                + "reach a screen. If a command genuinely moved or went away, edit ExpectedCommands "
                + "on purpose and say why");
    }

    [Fact]
    public void TheDashboardStillHasTwentyButtons()
    {
        // A number in a diff, the same device TheAllowListHasNotGrown uses. Twenty is the figure
        // RemEx-1qpjh recorded and it is the one RemEx-oszfm is verified against.
        Count(Home(), @"<Button[\s>]").Should().Be(20,
            "the dashboard had 20 buttons when it was characterised; a rewrite that ends with fewer "
            + "has dropped an action, and one that ends with more has grown scope");
    }

    [Fact]
    public void TheDashboardStillHasItsFourStatusIndicators()
    {
        // One per SystemStatus row (state-dot, coloured by IsAttention / IsProblem), plus the three
        // that make up the footer radar: ring, halo, core. All three of those follow PHONE presence
        // and must keep doing so (RemEx-7zzw) — StatusDotPresenceBindingTests owns that half.
        Count(Home(), @"<Ellipse[\s>]").Should().Be(4);
    }

    [Fact]
    public void EveryQuickLaunchTileStillNavigatesAndStillHasAName()
    {
        var grid = Between(Home(), "<UniformGrid", "</UniformGrid>");
        var tiles = Elements(grid).Where(e => e.StartsWith("<Button", StringComparison.Ordinal)).ToArray();

        tiles.Should().HaveCount(8, "the quick-launch grid is eight tiles");

        // The tiles are icon-and-caption, and the caption is a child TextBlock rather than Content —
        // so a screen reader has nothing to read but AutomationProperties.Name. All eight carry one
        // today and all eight must keep carrying one.
        tiles.Where(t => !t.Contains("AutomationProperties.Name", StringComparison.Ordinal))
            .Should().BeEmpty("an icon tile with no automation name is unreachable to a screen reader");

        tiles.Where(t => !Regex.IsMatch(t, @"Command=""\{Binding NavigateTo\w+Command\}"""))
            .Should().BeEmpty("every quick-launch tile exists to navigate somewhere");
    }

    [Fact]
    public void TheLiveStatsStripStillBindsEveryTelemetryProperty()
    {
        // CPU and memory each render twice — the text and the bar behind it — and uptime once. A
        // rewrite that keeps the text and drops the ProgressBar value leaves a bar frozen at zero,
        // which looks like a working control reporting an idle machine.
        foreach (var path in new[] { "CpuText", "CpuPercent", "RamText", "RamPercent", "UptimeText" })
        {
            Home().Should().Contain($"{{Binding {path}}}",
                $"the always-on stats strip reads {path} from the ~1 Hz telemetry stream");
            Resolve(path).Should().NotBeNull($"{path} must still exist on HomeViewModel");
        }
    }

    [Fact]
    public void ThePinnedSensorTilesAreStillAWrapPanelWithBothStates()
    {
        var home = Home();

        home.Should().MatchRegex(@"<WrapPanel[^>]*ItemWidth=""280""",
            "pinned sensor tiles reflow in a fixed-width WrapPanel; the width is what makes the "
            + "columns line up as the window resizes");

        // Populated and empty are two separate subtrees keyed off the same count. Losing the second
        // one gives a blank area where the 'no sensors pinned' invitation used to be.
        home.Should().Contain(@"IsVisible=""{Binding PinnedSensors.Count}""");
        home.Should().Contain(@"IsVisible=""{Binding !PinnedSensors.Count}""");

        // Same shape for the activity feed.
        home.Should().Contain(@"IsVisible=""{Binding HasRecentActivity}""");
        home.Should().Contain(@"IsVisible=""{Binding !HasRecentActivity}""");
    }

    [Fact]
    public void TheSystemStatusCardStillRendersAllFourOfItsStates()
    {
        var home = Home();

        // IsRendered gates the whole card; the other three are the states inside it. IsUnavailable
        // is here because it was once computed and rendered nowhere, so the card vanished on the one
        // screen a user opens to find out the host is down.
        foreach (var path in new[]
        {
            "SystemStatus.IsRendered",
            "SystemStatus.IsFullyReady",
            "SystemStatus.IsUnavailable",
        })
        {
            home.Should().Contain($"{{Binding {path}}}", $"the card still needs its {path} state");
            Resolve(path).Should().NotBeNull();
        }

        home.Should().Contain(@"IsVisible=""{Binding !IsFullyReady}""",
            "the row list is the fourth state, and it binds inside the card's own DataContext");
    }

    [Fact]
    public void TheSystemStatusRowsStillReachTheCardsCommandsTheSameWay()
    {
        // THE ONE THE OTHER COMMAND TESTS CANNOT SEE, and review is why it exists. Normalise()
        // rewrites the templated paths to OwnerType.MemberCommand so Resolve() has a root to walk
        // from — which means it throws away the $parent[ItemsControl] selector and the cast, and
        // those are precisely the halves that were broken in RemEx-tb0a. The command existing on
        // SystemStatusViewModel is a fact about the C# class; it is true whether or not the view can
        // reach it.
        //
        // MEASURED, NOT ASSUMED: rewriting $parent[ItemsControl] to $parent[Border] in HomeView left
        // all nine other assertions green with both buttons dead. This one fails on it.
        var home = Home();

        home.Should().Contain(@"<ItemsControl DataContext=""{Binding SystemStatus}""",
            "the rows reach FixCommand and ExplainCommand by casting the ItemsControl's own "
            + "DataContext. Move this binding to an enclosing element and the cast still parses, "
            + "still resolves against the type, and both buttons silently do nothing (RemEx-tb0a)");

        Count(home, @"\$parent\[ItemsControl\]\.\(\(vm:SystemStatusViewModel\)DataContext\)\.(?:Fix|Explain)Command")
            .Should().Be(2,
                "Fix and Explain each reach the card through $parent[ItemsControl]. If the rewrite "
                + "wraps or replaces the ItemsControl, this selector has to change with it — and "
                + "nothing else in this file would notice");
    }

    /// <summary>
    /// The colour literals this view is allowed to contain.
    /// </summary>
    /// <remarks>
    /// ONE ENTRY: the footer status node's drop-shadow alpha at <c>HomeView.axaml:439</c>,
    /// <c>BoxShadow="0 -8 32 0 #40000000"</c>. It is a 25% black shade rather than a hue, which is
    /// why it was written as a literal — but it is still a literal, and under a light theme it is
    /// the kind of thing that reads as a smudge. It is on the by-eye list in
    /// <c>docs/DASHBOARD-CHARACTERISATION.md</c> §8 for that reason.
    /// <para>
    /// AN EXACT SET, NOT AN EMPTINESS CHECK, because the first version of this assertion claimed the
    /// file had none and the document repeated the claim. Both were wrong, and the assertion could
    /// not have discovered it: see the remark on <see cref="ColourLiterals"/>.
    /// </para>
    /// </remarks>
    private static readonly string[] AllowedColourLiterals = ["#40000000"];

    [Fact]
    public void TheDashboardStillHasNoHardcodedColoursBeyondTheOneItIsAllowed()
    {
        // Four-theme safety is a hard guardrail, and a Material restyle is exactly the change that
        // reaches for a literal "just for this one card" — one that survives the default theme and
        // dies under SolarFlare.
        ColourLiterals()
            .Should().BeEquivalentTo(AllowedColourLiterals,
                "every colour on this view comes from a DynamicResource theme brush except the "
                + "footer drop-shadow alpha. A new literal must be a deliberate edit to "
                + "AllowedColourLiterals with a reason, not a colour that slipped in during a restyle");
    }

    /// <summary>Every colour literal in the markup, comments excluded.</summary>
    /// <remarks>
    /// <para>
    /// NOT ANCHORED TO THE ATTRIBUTE QUOTES, and that is the whole point of this helper. The first
    /// version matched <c>"#rrggbb"</c> — quotes inside the pattern — so it only ever saw a literal
    /// that filled an ENTIRE attribute value. Every multi-value attribute form was invisible to it:
    /// <c>BoxShadow</c>, gradient stops, transitions. The file's one real literal sits in a
    /// <c>BoxShadow</c> and went unseen, while the injection that was supposed to prove the
    /// assertion worked used the whole-attribute form and passed. Measured: injecting
    /// <c>Effect="drop-shadow(0 4 12 #80FF0000)"</c> left all thirteen assertions green.
    /// </para>
    /// <para>
    /// COMMENTS ARE STRIPPED FIRST because <c>HomeView.axaml:59-65</c> discusses <c>#40FF6B6B</c> in
    /// prose — a literal that was REMOVED, named in the comment explaining why. Matching it would
    /// fail this test for the presence of a note about good practice.
    /// </para>
    /// <para>
    /// <c>{3,8}</c> covers the short <c>#rgb</c> form too; Avalonia accepts it and a restyle could
    /// easily reach for one.
    /// </para>
    /// </remarks>
    private static string[] ColourLiterals()
    {
        var markup = Regex.Replace(Home(), @"<!--.*?-->", string.Empty, RegexOptions.Singleline);
        return [.. Regex.Matches(markup, @"#[0-9A-Fa-f]{3,8}\b").Select(m => m.Value)];
    }

    [Fact]
    public void EveryControlThatNeedsAnAutomationNameStillHasOne()
    {
        // Eighteen sites, not the eight the quick-launch test covers. THIRTEEN of them are
        // icon-or-glyph controls whose caption is a child TextBlock rather than Content, so the
        // automation name is the only thing a screen reader has: the 8 quick-launch tiles (:309–:351),
        // the 3 link cards (:400, :411, :422), the workspace button (:248) and the activity clear
        // button (:365). The other five are not that shape and are here anyway — the command-palette
        // pill (:89) and the SystemStatus recheck (:133), fix (:187) and explain (:199) buttons carry
        // a name alongside their visible text, and the radar core (:448) is an Ellipse with no
        // caption at all, naming the presence state itself.
        //
        // The earlier version of this comment enumerated seven controls, called it eighteen, and
        // claimed all eighteen were icon-or-glyph. Review caught it against §7 of the document, which
        // had the split right.
        Count(Home(), @"AutomationProperties\.Name").Should().Be(18,
            "18 controls on this view carry an automation name and 13 of them have no other "
            + "accessible label; a restyle that drops one makes that control unreachable to a screen "
            + "reader with no visible sign");
    }

    [Fact]
    public void TheFooterStatusNodeIsStillOutsideTheScrollViewer()
    {
        // Section 7 of the characterisation. The footer is row 1 of a two-row Grid and is always
        // visible; folding it into the scrolling body is a behaviour change wearing a layout
        // change's clothes.
        Home().Should().MatchRegex(@"<Border Grid\.Row=""1"" Classes=""glass-card""",
            "the status footer sits in row 1, pinned below the ScrollViewer");
    }

    // ─────────────────────────── plumbing ───────────────────────────

    /// <summary>
    /// Every path bound to a <c>Command</c> attribute, normalised so a templated command that
    /// reaches its owner through a cast reads as <c>OwnerType.MemberCommand</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// THE NEGATIVE LOOKBEHIND excludes the attached and dotted forms — <c>Button.Command=</c>,
    /// <c>i:Interaction.Command=</c> — of which HomeView has none today. It is NOT what keeps
    /// <c>CommandParameter="{Binding}"</c> out: that cannot match anyway, because the pattern needs
    /// the literal <c>Command="{Binding</c> with a trailing space and the bare <c>{Binding}</c> has
    /// neither the space nor anything to fill <c>([^}]+)</c>. An earlier version of this remark
    /// claimed otherwise and review caught it — a comment that tells the next maintainer to preserve
    /// something for a reason that does not hold is how a line survives long after it stopped
    /// mattering.
    /// </para>
    /// <para>
    /// MATCHES <c>{CompiledBinding}</c> TOO, deliberately and in advance. RemEx-oszfm converting
    /// this view to compiled bindings would be a strict improvement — everything here becomes
    /// build-time checked — and a matcher that only knew <c>{Binding}</c> would return an empty set
    /// the day that happened. Two of the assertions below are satisfied by an empty set, so the
    /// improvement would have silently gutted its own guard. Hence also the count check.
    /// </para>
    /// </remarks>
    private static string[] CommandBindings()
    {
        var found = Regex.Matches(Home(), @"(?<![\w.])Command=""\{(?:Compiled)?Binding ([^}]+)\}""")
            .Select(m => Normalise(m.Groups[1].Value.Trim()))
            .ToArray();

        // ANTI-VACUITY, and it is the specific bug this repo has already been bitten by: a matcher
        // that finds nothing makes every "no offenders" assertion pass. StatusDotPresenceBindingTests
        // was inert for months on exactly that shape.
        found.Should().HaveCount(20,
            "HomeView has 20 Command-bound buttons; finding a different number means this matcher has "
            + "stopped seeing the view, and the assertions built on it are no longer testing anything");

        return found;
    }

    /// <summary>
    /// Rewrites <c>$parent[ItemsControl].((vm:SomeViewModel)DataContext).XCommand</c> as
    /// <c>SomeViewModel.XCommand</c>, and leaves every other path alone.
    /// </summary>
    private static string Normalise(string path)
    {
        var cast = Regex.Match(path, @"\(\((?:\w+:)?(\w+)\)DataContext\)\.(\w+)$");
        return cast.Success ? $"{cast.Groups[1].Value}.{cast.Groups[2].Value}" : path;
    }

    /// <summary>
    /// Walks a dotted binding path from its root type and returns the type it lands on, or
    /// <c>null</c> if any segment does not exist.
    /// </summary>
    /// <remarks>
    /// The root is <see cref="HomeViewModel"/> — the view's <c>x:DataType</c> — unless the first
    /// segment names a view model type AND is not a property of HomeViewModel, which is how the
    /// normalised templated paths arrive.
    /// <para>
    /// THE PROPERTY LOOKUP HAS TO WIN, and review caught this being the other way round. That
    /// namespace holds 53 types and is not view-models-only (<c>CanvasLayoutMerge</c>,
    /// <c>BreadcrumbSegment</c>, <c>DesktopTargetOption</c>). Someone adding a
    /// <c>record Presence</c> or <c>enum Connection</c> to it would silently re-root
    /// <c>Connection.ConnectCommand</c> onto the new type — failing this test while blaming
    /// HomeView for a change in an unrelated file, or worse, resolving to something that happens to
    /// fit and turning the ICommand assertion into a false green.
    /// </para>
    /// </remarks>
    private static Type? Resolve(string path)
    {
        var segments = path.Split('.');
        var start = 0;
        Type current;

        if (PropertyOn(typeof(HomeViewModel), segments[0]) is not null)
        {
            current = typeof(HomeViewModel);
        }
        else if (ViewModelNamed(segments[0]) is { } named)
        {
            current = named;
            start = 1;
        }
        else
        {
            current = typeof(HomeViewModel);
        }

        for (var i = start; i < segments.Length; i++)
        {
            var property = PropertyOn(current, segments[i]);
            if (property is null) return null;
            current = property.PropertyType;
        }

        return current;
    }

    private static PropertyInfo? PropertyOn(Type type, string name)
        => type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);

    private static Type? ViewModelNamed(string name)
        => typeof(HomeViewModel).Assembly.GetType($"Remex.Desktop.ViewModels.{name}");

    /// <summary>Whole elements, so an element whose attributes wrap is still seen whole.</summary>
    private static string[] Elements(string axaml) =>
        [.. Regex.Matches(axaml, @"<[A-Za-z][\w:.]*(?:\s[^<>]*)?>", RegexOptions.Singleline)
            .Select(m => Regex.Replace(m.Value, @"\s+", " "))];

    private static string Between(string text, string open, string close)
    {
        var from = text.IndexOf(open, StringComparison.Ordinal);
        var to = text.IndexOf(close, StringComparison.Ordinal);
        (from >= 0 && to > from).Should().BeTrue($"HomeView should still contain a {open} block");
        return text[from..to];
    }

    private static int Count(string text, string pattern) => Regex.Matches(text, pattern).Count;

    private static string Home()
        => File.ReadAllText(Path.Combine(RepoRoot(), "remex.desktop", "Views", "HomeView.axaml"));

    private static string RepoRoot([CallerFilePath] string thisSourceFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisSourceFile)!, "..", ".."));
}
