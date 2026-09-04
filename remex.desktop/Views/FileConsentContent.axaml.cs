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
public partial class FileConsentContent : UserControl
{
    public FileConsentContent()
    {
        InitializeComponent();
    }
}
