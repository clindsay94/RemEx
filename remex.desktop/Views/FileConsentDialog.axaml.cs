using System;
using Avalonia.Controls;
using Avalonia.Threading;
using Remex.Desktop.ViewModels;

namespace Remex.Desktop.Views;

public partial class FileConsentDialog : Window
{
    public FileConsentDialog()
    {
        InitializeComponent();
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);

        if (DataContext is FileConsentDialogViewModel vm)
        {
            // Close the dialog when the user makes a decision.
            vm.ResultTask.ContinueWith(t =>
                Dispatcher.UIThread.Post(() => Close(t.Result)));
        }
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        // Dismissing the window without choosing is a clean deny (plan §2).
        if (DataContext is FileConsentDialogViewModel vm)
            vm.ResolveAsDeny();
        base.OnClosing(e);
    }
}
