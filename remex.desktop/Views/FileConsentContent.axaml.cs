using Avalonia.Controls;

namespace Remex.Desktop.Views;

/// <summary>
/// The message/detail/"remember" body of the file-consent prompt, hosted as the
/// <see cref="Material.Dialog.CustomDialogBuilderParams.Content"/> of the dialog
/// <see cref="MaterialDialogs.FileConsentAsync"/> builds. No x:Name'd controls, so no hand-written
/// InitializeComponent risk (RemEx-wdqx does not apply here).
/// </summary>
public partial class FileConsentContent : UserControl
{
    public FileConsentContent()
    {
        InitializeComponent();
    }
}
