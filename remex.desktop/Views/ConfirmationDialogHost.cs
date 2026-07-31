using Avalonia.Controls;

namespace Remex.Desktop.Views;

/// <summary>
/// Supplies the <c>OnConfirmationRequested</c> delegate that destructive ViewModel commands await
/// before doing anything irreversible.
/// </summary>
/// <remarks>
/// The pattern this serves was established by RemEx-07jx: a ViewModel owns no UI, so it exposes
/// <c>Func&lt;string, string, string, Task&lt;bool&gt;&gt;? OnConfirmationRequested</c> and the View
/// supplies a dialog. RemEx-6p1f then guarded five more destructive actions across four more
/// ViewModels, at which point the same six-line lambda would have been copy-pasted into five views —
/// so it lives here once instead.
/// <para>
/// Returning <c>false</c> when there is no parent <see cref="Window"/> is deliberate and is the
/// whole safety property of the pattern: every caller treats "not confirmed" as "do not proceed", so
/// a view that cannot host a dialog cancels the destructive action rather than performing it
/// unconfirmed. Callers must keep testing <c>OnConfirmationRequested is null</c> first for the same
/// reason — an unwired ViewModel must also decline, not proceed.
/// </para>
/// </remarks>
internal static class ConfirmationDialogHost
{
    /// <summary>
    /// Builds a confirmation delegate that shows a modal <see cref="ConfirmationDialog"/> owned by
    /// <paramref name="owner"/>'s window.
    /// </summary>
    /// <param name="owner">The control whose top-level window parents the dialog.</param>
    internal static Func<string, string, string, Task<bool>> For(Control owner)
    {
        ArgumentNullException.ThrowIfNull(owner);

        return async (title, message, confirmText) =>
        {
            // Resolve and vet the owner BEFORE constructing the dialog: building a Window only to
            // abandon it unshown is waste, and ShowDialog throws on a non-visible owner — which is
            // reachable because the main window Hides to the tray. From an `async void` handler
            // (two of the Settings call sites) that exception would reach the dispatcher unhandled
            // and take the process down, so it is refused here instead.
            if (TopLevel.GetTopLevel(owner) is not Window { IsVisible: true } parentWindow)
            {
                // Refusing is correct, but it is also silent: the caller just declines and the
                // button appears to do nothing. Leave a trace so that if some future host ever does
                // put one of these views in a window that is not shown yet, there is something to
                // find rather than an inert control with no symptom.
                System.Diagnostics.Debug.WriteLine(
                    $"ConfirmationDialogHost: no visible parent window for {owner.GetType().Name}; " +
                    "declining the destructive action.");
                return false;
            }

            var dialog = new ConfirmationDialog(title, message, confirmText);
            return await dialog.ShowDialog<bool>(parentWindow);
        };
    }
}
