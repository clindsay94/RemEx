using System;
using Avalonia.Controls;
using Remex.Client.ViewModels;

namespace Remex.Client.Views;

public partial class PairingDialog : Window
{
    public PairingDialog()
    {
        InitializeComponent();
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        
        if (DataContext is PairingDialogViewModel vm)
        {
            // Close the dialog when the result task completes
            vm.ResultTask.ContinueWith(t =>
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(() => Close(t.Result));
            });
        }
    }
}
