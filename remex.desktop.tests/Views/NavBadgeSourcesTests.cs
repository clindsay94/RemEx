using System.IO;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace Remex.Desktop.Tests.Views;

/// <summary>
/// Guards the three badges RemEx-rjnbo.1 gave a real source: the Files nav active-transfer count,
/// the Diagnostics nav unread count, and the tray flyout's online-device count. Same philosophy as
/// <c>AlertBadgeTests</c> (RemEx-rjnbo): these assertions are about the WIRING - that a view binds
/// the count and hides (or, for the tray badge, drops back to a plain dot) at zero - not about the
/// counting logic itself, which <c>ShellTransferAndDiagnosticsBadgeTests</c> exercises directly.
/// The fourth badge the parent bead named, dashboard tile alert counts, is explicitly NOT built
/// here - see the bead's notes for why (RemEx-8wpvr is about to redesign that surface).
/// </summary>
public class NavBadgeSourcesTests
{
    [Fact]
    public void TheFilesNavBadgeIsBoundToTheActiveTransferCount()
    {
        var badge = FilesBadge();

        badge.Success.Should().BeTrue("the Files nav destination must carry a badge");
        badge.Value.Should().Contain("BadgeContent=\"{Binding ActiveTransferCount}\"",
            "ActiveTransferCount only matters if a screen shows it");
        badge.Value.Should().Contain("IsBadgeVisible=\"{Binding HasActiveTransfers}\"",
            "without an explicit visibility binding the badge renders 0 when nothing is transferring");
    }

    [Fact]
    public void TheDiagnosticsNavBadgeIsBoundToTheUnreadCount()
    {
        var badge = DiagnosticsBadge();

        badge.Success.Should().BeTrue("the Logs & Diagnostics nav destination must carry a badge");
        badge.Value.Should().Contain("BadgeContent=\"{Binding DiagnosticsBadgeCount}\"",
            "DiagnosticsBadgeCount only matters if a screen shows it");
        badge.Value.Should().Contain("IsBadgeVisible=\"{Binding HasUnreadDiagnostics}\"",
            "without an explicit visibility binding the badge renders 0 when nothing is unread");
    }

    [Fact]
    public void NavigatingToDiagnosticLogsClearsTheCount()
    {
        // Same acknowledgement shape as AlertBadgeTests.SomethingClearsTheCount: arriving on the
        // page IS the acknowledgement that whatever fired while the user was elsewhere is now seen.
        var shellViewModel = File.ReadAllText(
            Path.Combine(RepoRoot(), "remex.desktop", "ViewModels", "ShellViewModel.cs"));

        var navigate = Regex.Match(
            shellViewModel, @"public void NavigateToDiagnosticLogs\(\)\s*\{.*?\n    \}", RegexOptions.Singleline);

        navigate.Success.Should().BeTrue("NavigateToDiagnosticLogs moved or was renamed");
        navigate.Value.Should().Contain("DiagnosticsBadgeCount = 0",
            "arriving on the diagnostics page is what acknowledges the entries that fired while away");
    }

    [Fact]
    public void TheTrayFlyoutPresenceBadgeShowsTheOnlineDeviceCount()
    {
        var badge = TrayPresenceBadge();

        badge.Success.Should().BeTrue("the tray flyout header must carry the presence badge");
        badge.Value.Should().Contain("BadgeContent=\"{Binding OnlineDeviceCount}\"",
            "OnlineDeviceCount only matters if the badge shows it");
        badge.Value.Should().Contain("BadgeDisplayContent=\"{Binding HasOnlineDevices}\"",
            "a fixed BadgeDisplayContent=\"False\" would silently un-wire the content this bead adds");
        badge.Value.Should().Contain("Classes=\"presence\"",
            "the badge must keep the shared presence-dot colour vocabulary (RemEx-d7xj8)");
        badge.Value.Should().Contain("Classes.connected=\"{Binding Presence.IsPhoneAttached}\"",
            "the connected/disconnected colour switch must survive this bead's change");
    }

    [Fact]
    public void TheTrayFlyoutPresenceBadgeIsOnly10PixelsInTheDotState()
    {
        // Fix, MEDIUM, RemEx-rjnbo.1: a literal BadgeWidth/BadgeHeight="10" on this element clips
        // the device count once BadgeContent draws two digits. The size must instead come from a
        // Classes.dot="{Binding !HasOnlineDevices}" gate plus a material|Badged.dot style, so it
        // only applies while the badge is a plain dot - same shape as the nav count badges.
        var badge = TrayPresenceBadge();

        badge.Success.Should().BeTrue("the tray flyout header must carry the presence badge");
        badge.Value.Should().Contain("Classes.dot=\"{Binding !HasOnlineDevices}\"",
            "the fixed dot size must be gated to the no-devices-online state");
        badge.Value.Should().NotContain("BadgeWidth=\"10\"",
            "the size must come from the material|Badged.dot style, not a literal on the element");
        badge.Value.Should().NotContain("BadgeHeight=\"10\"",
            "the size must come from the material|Badged.dot style, not a literal on the element");

        var file = TrayFlyout();
        file.Should().MatchRegex(
            @"<Style Selector=""material\|Badged\.dot"">\s*<Setter Property=""BadgeWidth"" Value=""10""\s*/>\s*<Setter Property=""BadgeHeight"" Value=""10""\s*/>",
            "the dot-state 10x10 size must be declared once as a style, not scattered as literals");
    }

    [Fact]
    public void NoneOfTheThreeNewBadgesSetTheirOwnPlacement()
    {
        // BadgePlacementAndPaintAreDeclaredOnceForTheWholeApp already scrapes every *.axaml file for
        // this, but a dedicated check here means this bead's own tests fail by name if one of ITS
        // three badges is the offender, rather than a shared test elsewhere.
        FilesBadge().Value.Should().NotContain("BadgePlacement");
        DiagnosticsBadge().Value.Should().NotContain("BadgePlacement");
        TrayPresenceBadge().Value.Should().NotContain("BadgePlacement");
    }

    // ─────────────────────────── plumbing ───────────────────────────

    private static Match FilesBadge() => Regex.Match(
        Shell(), @"<!-- File Explorer -->.*?<material:Badged\b[^>]*?>.*?</material:Badged>", RegexOptions.Singleline);

    private static Match DiagnosticsBadge() => Regex.Match(
        Shell(), @"<!-- Logs & Diagnostics -->.*?<material:Badged\b[^>]*?>.*?</material:Badged>", RegexOptions.Singleline);

    private static Match TrayPresenceBadge() => Regex.Match(
        TrayFlyout(), @"<material:Badged\b[^>]*?>.*?</material:Badged>", RegexOptions.Singleline);

    private static string Shell()
        => File.ReadAllText(Path.Combine(RepoRoot(), "remex.desktop", "Views", "ShellView.axaml"));

    private static string TrayFlyout()
        => File.ReadAllText(Path.Combine(RepoRoot(), "remex.desktop", "Views", "TrayFlyoutWindow.axaml"));

    private static string RepoRoot([CallerFilePath] string thisSourceFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisSourceFile)!, "..", ".."));
}
