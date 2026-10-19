using Remex.Desktop.Services;

namespace Remex.Desktop.ViewModels;

/// <summary>
/// The one rule for the link status line shared by Home's footer and the Sensors Connection card
/// (RemEx-pp4cm.23, .24), so the two cannot drift.
/// </summary>
internal static class PhonePresenceText
{
    /// <summary>The localization key the status line shows, or null to show the link's own status
    /// text. Pure, so the rule can be tested without a live connection.</summary>
    internal static string? LinkStatusKey(bool linkConnected, bool hostUpWithNoPhone) =>
        linkConnected && hostUpWithNoPhone ? "Home_WaitingForPhone" : null;

    internal static string LinkStatusText(ConnectionViewModel connection, PhonePresenceMonitor presence) =>
        LinkStatusKey(connection.IsConnected, presence.HasNoPhone) is { } key
            ? LocalizationService.Instance[key]
            : connection.StatusText;
}
