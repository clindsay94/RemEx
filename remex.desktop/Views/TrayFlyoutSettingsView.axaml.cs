using Avalonia.Controls;
using Remex.Desktop.ViewModels;

namespace Remex.Desktop.Views;

/// <summary>Settings › Tray pop-up. Moved off Personalize › Layout in RemEx-172ba.</summary>
public partial class TrayFlyoutSettingsView : UserControl
{
    public TrayFlyoutSettingsView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => ConfigureViewModel();
    }

    private void ConfigureViewModel()
    {
        // ShellViewModel caches CustomizationViewModel, and its constructor loads the Apps checklist
        // once. Reloading on every attach is what lets an app added to the launcher later show up
        // (RemEx-4kv0g.18.6 review, HIGH 2).
        if (DataContext is CustomizationViewModel vm)
            vm.RefreshFlyoutApps();
    }
}
