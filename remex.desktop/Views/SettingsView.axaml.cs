using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Remex.Desktop.Controls;
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

    /// <summary>
    /// Arms this view's first-paint entrance (RemEx-alwfa.2), reusing the dashboard's once-per-
    /// process gate (RemEx-dnfq0). Attachment, not the constructor, because DataContext is not yet
    /// set when the control is constructed.
    /// </summary>
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        if (DataContext is SettingsViewModel vm
            && StaggeredEntrance.ShouldPlay(nameof(SettingsView), vm.Shell.IsReducedMotion))
        {
            SettingsSections.Classes.Add(StaggeredEntrance.Class);
        }
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
