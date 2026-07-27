using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Remex.Desktop.ViewModels;

namespace Remex.Desktop.Views;

public partial class RemoteView : UserControl
{
    private RemoteViewModel? _previousViewModel;

    public RemoteView()
    {
        InitializeComponent();
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
