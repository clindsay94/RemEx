using Avalonia;
using Avalonia.Controls;
// Avalonia 12 moved SetTextAsync off IClipboard onto ClipboardExtensions in this namespace
// (RemEx-jcma3). The interface now speaks IDataTransfer; the text convenience is an extension.
using Avalonia.Input.Platform;
using Avalonia.Markup.Xaml;
using Remex.Desktop.Controls;
using Remex.Desktop.ViewModels;

namespace Remex.Desktop.Views;

public partial class RemoteView : UserControl
{
    private RemoteViewModel? _previousViewModel;

    public RemoteView()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Arms this view's first-paint entrance (RemEx-alwfa.2), reusing the dashboard's once-per-
    /// process gate (RemEx-dnfq0). Attachment, not the constructor, because DataContext is not yet
    /// set when the control is constructed.
    /// </summary>
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        if (DataContext is RemoteViewModel vm
            && StaggeredEntrance.ShouldPlay(nameof(RemoteView), vm.Shell.IsReducedMotion))
        {
            RemoteSections.Classes.Add(StaggeredEntrance.Class);
        }
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);

        if (_previousViewModel is not null)
        {
            _previousViewModel.OnConfirmationRequested = null;
            _previousViewModel.CopyToClipboardAsync = null;
        }

        _previousViewModel = DataContext as RemoteViewModel;

        if (_previousViewModel is not null)
        {
            // Was an inline lambda, identical to the one FileTransferView had; both now share
            // ConfirmationDialogHost so the six sites RemEx-6p1f added did not make a seventh copy.
            _previousViewModel.OnConfirmationRequested = ConfirmationDialogHost.For(this);

            _previousViewModel.CopyToClipboardAsync = async text =>
            {
                var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
                if (clipboard is not null)
                    await clipboard.SetTextAsync(text);
            };
        }
    }
}
