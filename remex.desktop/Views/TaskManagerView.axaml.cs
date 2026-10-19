using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Remex.Desktop.ViewModels;

namespace Remex.Desktop.Views;

public partial class TaskManagerView : UserControl
{
    public TaskManagerView()
    {
        InitializeComponent();
        // Guards Kill Process (RemEx-6p1f). Wired on DataContextChanged rather than in OnLoaded so
        // the delegate is present even if a kill is triggered before the view is first loaded.
        DataContextChanged += (_, _) =>
        {
            if (DataContext is not TaskManagerViewModel vm)
                return;

            vm.OnConfirmationRequested = ConfirmationDialogHost.For(this);
            vm.CopyToClipboardAsync = async text =>
            {
                var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
                if (clipboard is not null)
                    await clipboard.SetTextAsync(text);
            };
        };
    }

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        if (DataContext is TaskManagerViewModel vm)
        {
            vm.StartPolling();
        }
    }

    protected override void OnUnloaded(RoutedEventArgs e)
    {
        base.OnUnloaded(e);
        if (DataContext is TaskManagerViewModel vm)
        {
            vm.StopPolling();
        }
    }
}
