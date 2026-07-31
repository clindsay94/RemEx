using Avalonia.Controls;
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
            if (DataContext is TaskManagerViewModel vm)
                vm.OnConfirmationRequested = ConfirmationDialogHost.For(this);
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
