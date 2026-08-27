using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace Remex.Desktop.Tests.Views;

/// <summary>
/// Guards the sensor-alert badge wired by RemEx-rjnbo.
/// </summary>
/// <remarks>
/// <para>
/// THE JOIN IS WHAT THIS GUARDS. <c>ShellViewModel</c> incremented <c>AlertBadgeCount</c> on every
/// sensor alert, exposed <c>HasAlerts</c>, kept twenty <c>AlertNotifications</c> and offered a
/// dismiss command — and no view bound any of it. The pipeline ran correctly into a number nobody
/// could see, which is the failure shape AGENTS.md calls out: logic that is fully tested and
/// consumed by nothing. Tests over the view model alone would all have passed, and did.
/// </para>
/// <para>
/// So these assertions are deliberately about the WIRING, not the counting: that a view binds the
/// count, that it hides at zero, and that something clears it. Each of those can break without any
/// view-model test noticing.
/// </para>
/// </remarks>
public class AlertBadgeTests
{
    [Fact]
    public void TheAlertCountIsBoundByAView()
    {
        // The whole bead in one line. If this fails, the alert pipeline is back to incrementing a
        // counter with no consumer.
        Shell().Should().Contain("BadgeContent=\"{Binding AlertBadgeCount}\"",
            "AlertBadgeCount is only worth computing if a screen shows it");
    }

    [Fact]
    public void AZeroCountHidesTheBadgeRatherThanShowingZero()
    {
        // Material renders a badge containing "0" quite happily. A notification that there is
        // nothing to notify is worse than no badge: it trains people to ignore the one that
        // matters. This is the bead's acceptance criterion, word for word.
        var badge = Regex.Match(Shell(), @"<material:Badged\b[^>]*?>", RegexOptions.Singleline);

        badge.Success.Should().BeTrue("the Sensors destination carries the badge");
        badge.Value.Should().Contain("IsBadgeVisible=\"{Binding HasAlerts}\"",
            "without an explicit visibility binding the badge renders 0 when nothing has fired");
    }

    [Fact]
    public void SomethingClearsTheCount()
    {
        // A badge that never clears is a badge people stop reading, so "appear AND clear correctly
        // with their source state" is half the acceptance. Opening the sensors page is the
        // acknowledgement: the alerts are sensor alerts and that is where they came from.
        var shellViewModel = File.ReadAllText(
            Path.Combine(RepoRoot(), "remex.desktop", "ViewModels", "ShellViewModel.cs"));

        var navigate = Regex.Match(
            shellViewModel, @"public void NavigateToCanvas\(\)\s*\{.*?\n    \}", RegexOptions.Singleline);

        navigate.Success.Should().BeTrue("NavigateToCanvas moved or was renamed");
        navigate.Value.Should().Contain("AlertBadgeCount = 0",
            "arriving on the sensors page is what acknowledges the alerts that fired while away");
    }

    [Fact]
    public void ClearingTheCountKeepsTheNotificationHistory()
    {
        // A DELIBERATE ASYMMETRY, recorded so it is not "tidied" into symmetry. The count means
        // unacknowledged; the twenty-entry list is the history. Nothing displays the history yet,
        // and clearing it on navigation would throw away the only record the moment a future
        // flyout wants it. DismissAlerts still clears both, because dismissing IS discarding.
        var shellViewModel = File.ReadAllText(
            Path.Combine(RepoRoot(), "remex.desktop", "ViewModels", "ShellViewModel.cs"));

        var navigate = Regex.Match(
            shellViewModel, @"public void NavigateToCanvas\(\)\s*\{.*?\n    \}", RegexOptions.Singleline);

        navigate.Value.Should().NotContain("AlertNotifications.Clear()",
            "navigation acknowledges the count; it does not discard the history");
    }

    [Fact]
    public void BadgePlacementAndPaintAreDeclaredOnceForTheWholeApp()
    {
        // The bead asked for consistent placement. The cheapest guarantee is that no call site can
        // choose: it lives in App.axaml, and a Badged that sets its own placement is the drift.
        var app = File.ReadAllText(Path.Combine(RepoRoot(), "remex.desktop", "App.axaml"));

        var style = Regex.Match(app, @"<Style Selector=""material\|Badged"">.*?</Style>", RegexOptions.Singleline);

        style.Success.Should().BeTrue("the shared Badged rule has to exist");
        style.Value.Should().Contain("BadgePlacement",
            "placement is the property the bead singled out as needing to be consistent");

        var perCallSite = XamlFiles()
            .SelectMany(pair => Regex
                .Matches(pair.Text, @"<material:Badged\b[^>]*?>", RegexOptions.Singleline)
                .Select(match => (pair.File, match.Value)))
            .Where(row => row.Value.Contains("BadgePlacement", StringComparison.Ordinal))
            .Select(row => Path.GetFileName(row.File))
            .ToList();

        perCallSite.Should().BeEmpty("placement is App.axaml's to decide, not a call site's");
    }

    // ─────────────────────────── plumbing ───────────────────────────

    private static string Shell()
        => File.ReadAllText(Path.Combine(RepoRoot(), "remex.desktop", "Views", "ShellView.axaml"));

    private static (string File, string Text)[] XamlFiles()
        => Directory
            .EnumerateFiles(Path.Combine(RepoRoot(), "remex.desktop"), "*.axaml", SearchOption.AllDirectories)
            .Select(file => (file, File.ReadAllText(file)))
            .ToArray();

    private static string RepoRoot([CallerFilePath] string thisSourceFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisSourceFile)!, "..", ".."));
}
