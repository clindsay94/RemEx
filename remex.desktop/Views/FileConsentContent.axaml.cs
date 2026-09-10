using Avalonia.Controls;

namespace Remex.Desktop.Views;

/// <summary>
/// The message/detail/"remember"/Deny-Allow body of the file-consent prompt, hosted as the
/// <see cref="Material.Dialog.CustomDialogBuilderParams.Content"/> of the dialog
/// <see cref="MaterialDialogs.FileConsentAsync"/> builds. No x:Name'd controls, so no hand-written
/// InitializeComponent risk (RemEx-wdqx does not apply here). The Deny/Allow buttons bind straight to
/// the view model's <c>DenyCommand</c>/<c>AllowCommand</c> (RemEx-x6a70.3 fix round 2) rather than to
/// Material.Avalonia's own <c>DialogButtons</c>.
/// </summary>
/// <remarks>
/// RemEx-df08: deliberately NO Enter/<c>IsDefault</c> binding anywhere in this control, unlike
/// <see cref="DialogContent"/>. This is the one dialog in the app where the default action is
/// "accept something arriving from someone else" (an incoming file push/browse request) rather than
/// a decision the local user initiated - Enter defaulting to Allow would let a file transfer be
/// accepted just because the user was holding a key, or focus happened to land here, with no
/// deliberate click. Reviewed against RemEx-df08's own stated rule ("if the default action is
/// destructive, Enter should NOT be bound to it") and confirmed fail-closed: the absence is the fix,
/// not a gap to fill in.
/// </remarks>
public partial class FileConsentContent : UserControl
{
    public FileConsentContent()
    {
        InitializeComponent();
    }
}
