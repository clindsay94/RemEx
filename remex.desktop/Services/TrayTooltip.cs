namespace Remex.Desktop.Services;

/// <summary>
/// Composes the tray icon's hover text so it carries the same fact the shell does (RemEx-3s4v).
/// </summary>
/// <remarks>
/// <para>
/// **THE TOOLTIP USED TO BE A CONSTANT.** It said "RemEx Desktop - Remote Execution" whether a phone
/// was attached or not, which is the one thing a tray icon is well placed to answer: the window is
/// closed to the tray most of the time, so for most of this app's life the tooltip IS the status
/// surface.
/// </para>
/// <para>
/// NOT BOUND TO <c>Connection.IsConnected</c>, and the bead is explicit about why: that is the
/// desktop's own loopback socket to its embedded host, up essentially always, so a tooltip fed from
/// it tells the user nothing. It is fed from <c>PhonePresence</c> — the same source the shell's dot
/// reads — which is the loopback conflation RemEx-porg exists to fix.
/// </para>
/// <para>
/// A pure function so it can be tested without an Avalonia application: the tray icon itself is
/// declared in App.axaml and there is no headless way to construct one.
/// </para>
/// </remarks>
public static class TrayTooltip
{
    /// <summary>Joins the product name and the current presence reading.</summary>
    /// <remarks>
    /// FALLS BACK TO THE PRODUCT NAME ALONE when there is no reading yet — the monitor publishes on
    /// its first refresh, and a tooltip reading "RemEx — " with nothing after it during startup is
    /// worse than the constant it replaced.
    /// </remarks>
    public static string Compose(string productName, string? presenceText) =>
        string.IsNullOrWhiteSpace(presenceText)
            ? productName
            : string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                LocalizationService.Instance["Tray_TooltipFormat"],
                productName,
                presenceText.Trim());
}
