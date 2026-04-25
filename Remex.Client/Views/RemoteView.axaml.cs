using System;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Remex.Client.ViewModels;

namespace Remex.Client.Views;

public partial class RemoteView : UserControl
{
    public RemoteView()
    {
        InitializeComponent();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);

        if (DataContext is RemoteViewModel viewModel)
        {
            viewModel.OnConfirmationRequested = async (title, message, confirmText) =>
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
