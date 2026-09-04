using Avalonia.Controls;

namespace Remex.Desktop.Views;

/// <summary>
/// Title + wrapped message hosted as the <see cref="Material.Dialog.CustomDialogBuilderParams.Content"/>
/// of the dialogs <see cref="MaterialDialogs.ConfirmAsync"/> and <see cref="MaterialDialogs.RestoreAsync"/>
/// build. Exists because Material.Avalonia.Dialogs' own <c>AlertDialog</c> (what those two builders used
/// before RemEx-x6a70.3 fix round 1) renders its <c>SupportingText</c> without <c>TextWrapping</c> set,
/// which clips rather than wraps a message longer than the dialog's width - moving both dialogs onto
/// <c>CreateCustomDialog</c> with this control as their content sidesteps that TextBlock entirely
/// instead of patching it. No x:Name'd control risk here (RemEx-wdqx does not apply): the generated
/// <c>InitializeComponent(bool loadXaml = true)</c> is not shadowed by a hand-written override.
/// </summary>
public partial class DialogContent : UserControl
{
    public DialogContent() : this(string.Empty, string.Empty) { }

    public DialogContent(string header, string message)
    {
        InitializeComponent();
        HeaderText.Text = header;
        MessageTextBlock.Text = message;
    }
}
