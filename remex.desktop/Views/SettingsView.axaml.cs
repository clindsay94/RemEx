using System;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Remex.Desktop.ViewModels;
using Remex.Core.Models;

namespace Remex.Desktop.Views;

public partial class SettingsView : UserControl
{
    public SettingsView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => ConfigureViewModel();
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

    private void ConfigureViewModel()
    {
        if (DataContext is not SettingsViewModel vm)
            return;

        // Guards restore-defaults, remove-shared-folder and revoke-trust (RemEx-6p1f).
        vm.OnConfirmationRequested = ConfirmationDialogHost.For(this);

        vm.PickSharedFolderAsync = async options =>
        {
            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel is null)
                return Array.Empty<IStorageFolder>();

            return await topLevel.StorageProvider.OpenFolderPickerAsync(options);
        };

        vm.PickSaveFileAsync = async options =>
        {
            var topLevel = TopLevel.GetTopLevel(this);
            return topLevel is null ? null : await topLevel.StorageProvider.SaveFilePickerAsync(options);
        };

        vm.PickOpenFileAsync = async options =>
        {
            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel is null)
                return Array.Empty<IStorageFile>();

            return await topLevel.StorageProvider.OpenFilePickerAsync(options);
        };
    }
}
