using System;
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
            _previousViewModel.OnConfirmationRequested = null;

        _previousViewModel = DataContext as RemoteViewModel;

        if (_previousViewModel is not null)
        {
            _previousViewModel.OnConfirmationRequested = async (title, message, confirmText) =>
            {
                var dialog = new ConfirmationDialog(title, message, confirmText);
                var topLevel = TopLevel.GetTopLevel(this);
                if (topLevel is Window parentWindow)
                    return await dialog.ShowDialog<bool>(parentWindow);
                return false;
            };
        }
    }
}
