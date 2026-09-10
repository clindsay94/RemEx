namespace Remex.Desktop.ViewModels;

/// <summary>The power actions the tray flyout's Power submenu offers.</summary>
/// <remarks>
/// The Force variants (<c>ForceShutdown</c>, <c>ForceRestart</c>, <c>RestartToUefi</c>) are
/// deliberately absent. They exist on <c>ConnectionViewModel</c> and stay reachable from the main
/// window; a tray popup you open by mis-clicking an icon is the wrong place to offer an action
/// that discards unsaved work without asking the OS first.
/// </remarks>
public enum TrayPowerAction
{
    Restart,
    Shutdown,
    SignOut,
    Hibernate,
}

/// <summary>
/// The tray flyout's enablement and confirmation policy, kept free of Avalonia so it can be tested
/// without a running application.
/// </summary>
public static class TrayTileRules
{
    /// <summary>
    /// Remote desktop needs a phone on the other end. Note this is PHONE presence, not the
    /// desktop's own loopback link — see <c>PhonePresence.IsPhone</c> for why those differ.
    /// </summary>
    public static bool IsRemoteDesktopEnabled(bool isPhoneAttached) => isPhoneAttached;

    /// <summary>
    /// Whether an action ends the session and so must be confirmed first.
    /// </summary>
    /// <remarks>
    /// Hibernate is excluded on purpose: it restores the session exactly as it was, so confirming
    /// it would only teach the habit of dismissing the dialog that guards Shutdown.
    /// <para>
    /// The default arm THROWS rather than returning <c>false</c>. A new member of
    /// <see cref="TrayPowerAction"/> must be classified deliberately; inheriting "no confirmation
    /// needed" by falling through is how a session-ending action ships without a dialog. Leaving
    /// the arm off entirely — so the compiler flags the gap — is not available here: CS8524 covers
    /// unnamed values like <c>(TrayPowerAction)4</c>, which no set of named arms satisfies, and
    /// <c>TreatWarningsAsErrors</c> makes that fatal. The guard therefore lives in
    /// <c>TrayTileRulesTests.Every_power_action_has_an_explicit_confirmation_verdict</c>, which
    /// walks every declared member and fails on the throw.
    /// </para>
    /// </remarks>
    public static bool RequiresConfirmation(TrayPowerAction action) => action switch
    {
        TrayPowerAction.Restart => true,
        TrayPowerAction.Shutdown => true,
        TrayPowerAction.SignOut => true,
        TrayPowerAction.Hibernate => false,
        _ => throw new ArgumentOutOfRangeException(
            nameof(action), action, "Unclassified tray power action - decide whether it ends the session."),
    };
}

/// <summary>
/// Runs a power action, asking for confirmation first when the policy demands it.
/// </summary>
/// <remarks>
/// SEPARATE FROM THE VIEW MODEL SO IT CAN BE TESTED. <c>TrayFlyoutViewModel</c> needs
/// <c>ShellViewModel</c> and <c>HomeViewModel</c> to construct, neither of which stands up in a
/// unit test without the whole container — which would have left the single most safety-critical
/// path in this feature covered by nothing but a manual click. The policy being correct is not
/// the same property as the policy being consulted, and it is the second one that turns a PC off
/// without asking.
/// </remarks>
public static class TrayPowerInvoker
{
    /// <param name="action">The action to run.</param>
    /// <param name="confirm">
    /// Asks the user. <c>null</c> means there is no way to ask, which must be read as "do not
    /// proceed" — never as "no confirmation needed".
    /// </param>
    /// <param name="execute">Performs the action once it is cleared to run.</param>
    /// <returns><c>true</c> if the action ran.</returns>
    public static async Task<bool> InvokeAsync(
        TrayPowerAction action,
        Func<TrayPowerAction, Task<bool>>? confirm,
        Func<TrayPowerAction, Task> execute)
    {
        if (TrayTileRules.RequiresConfirmation(action))
        {
            if (confirm is null)
                return false;

            if (!await confirm(action))
                return false;
        }

        await execute(action);
        return true;
    }
}
