using System.Windows.Input;

namespace Remex.Desktop.Models;

/// <summary>
/// A single entry in the command palette — a searchable action with a label, category, and command.
/// </summary>
/// <param name="Label">Localized text shown in the results list.</param>
/// <param name="Category">Localized grouping label.</param>
/// <param name="Command">The command invoked when the entry is chosen.</param>
/// <param name="ConfirmTitleKey">
/// Resource key for a confirmation heading. When non-null the palette asks before running
/// <paramref name="Command"/> (RemEx-eifi). The palette invokes the underlying connection commands
/// rather than the confirmed wrappers used on the Remote page, so without this a destructive entry —
/// Force Shutdown most sharply — ran straight from Ctrl+K with no prompt while the identical button
/// on the Remote page asked. Null means the entry is not destructive and runs immediately.
/// </param>
/// <param name="ConfirmMessageKey">
/// Resource key for the confirmation body. Required when <paramref name="ConfirmTitleKey"/> is set.
/// </param>
/// <param name="ConfirmBtnKey">
/// Resource key for the confirm button. Required when <paramref name="ConfirmTitleKey"/> is set.
/// </param>
/// <param name="SearchAliases">
/// Extra localized terms the entry matches on but never displays. The palette searches labels and
/// categories only, so an action a user knows by a different word is unfindable: removing the
/// broken Wake-on-LAN entry (RemEx-paa7) left "wake" matching nothing at all, even though the
/// Remote screen is exactly where waking lives. Localized rather than English keywords, because a
/// search alias that only works in English is half a fix in an app that ships nine languages.
/// </param>
public sealed record CommandPaletteEntry(
    string Label,
    string Category,
    ICommand Command,
    string? ConfirmTitleKey = null,
    string? ConfirmMessageKey = null,
    string? ConfirmBtnKey = null,
    string? SearchAliases = null)
{
    /// <summary>True when this entry must be confirmed before its command runs.</summary>
    public bool RequiresConfirmation =>
        ConfirmTitleKey is not null && ConfirmMessageKey is not null && ConfirmBtnKey is not null;

    /// <summary>
    /// Whether this entry should show for <paramref name="query"/>. An empty query matches everything.
    /// </summary>
    /// <remarks>
    /// Lives on the entry rather than inside the view model's filter loop so the rule can be tested
    /// without standing up a <c>ShellViewModel</c> — the alias half of it exists precisely because a
    /// search term silently matching nothing went unnoticed (RemEx-efse).
    /// </remarks>
    public bool Matches(string query)
    {
        if (string.IsNullOrEmpty(query))
            return true;

        return Label.Contains(query, StringComparison.OrdinalIgnoreCase)
            || Category.Contains(query, StringComparison.OrdinalIgnoreCase)
            || SearchAliases?.Contains(query, StringComparison.OrdinalIgnoreCase) == true;
    }

    // Rejects a half-marked entry loudly, at construction. Without this the failure direction is
    // fail-OPEN: supply only two of the three keys and RequiresConfirmation is quietly false, so a
    // destructive entry runs unconfirmed with nothing reporting it anywhere. That is the one
    // mistake this type must not absorb silently, and it is the same shape as the miss caught in
    // RemEx-6p1f — a key added but never wired.
    //
    // A discarded field initializer is the mechanism because records have no constructor body to
    // put this in; initializers may reference the primary constructor's parameters and run as part
    // of construction, so an invalid entry cannot be built.
    private readonly bool _keysValidated =
        ValidateConfirmationKeys(Label, ConfirmTitleKey, ConfirmMessageKey, ConfirmBtnKey);

    private static bool ValidateConfirmationKeys(
        string label, string? titleKey, string? messageKey, string? btnKey)
    {
        var supplied = (titleKey is not null ? 1 : 0)
            + (messageKey is not null ? 1 : 0)
            + (btnKey is not null ? 1 : 0);
        if (supplied is not (0 or 3))
        {
            throw new System.ArgumentException(
                $"Confirmation keys for palette entry '{label}' must be all set or all null; got {supplied} of 3.",
                nameof(titleKey));
        }
        return true;
    }
}
