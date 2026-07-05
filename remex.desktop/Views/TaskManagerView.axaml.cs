using Avalonia.Controls;
using Avalonia.Interactivity;
using Remex.Desktop.ViewModels;

namespace Remex.Desktop.Views;

public partial class TaskManagerView : UserControl
{
    public TaskManagerView()
    {
        InitializeComponent();
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
