using Avalonia.Controls;
using Remex.Client.ViewModels;
using Remex.Core.Models;

namespace Remex.Client.Views;

public partial class SettingsView : UserControl
{
    public SettingsView()
    {
        InitializeComponent();
    }

    private void OnConnectionHistorySelected(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is ComboBox cb && cb.SelectedItem is ConnectionProfile profile
            && DataContext is SettingsViewModel vm)
        {
            vm.HostAddress = profile.HostAddress;
            cb.SelectedItem = null; // clear so user can re-select
        }
    }
}
