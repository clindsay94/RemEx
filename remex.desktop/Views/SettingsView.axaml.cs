using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Remex.Desktop.Controls;
using Remex.Desktop.ViewModels;
using Remex.Core.Models;

namespace Remex.Desktop.Views;

public partial class SettingsView : UserControl
{
    // Rewired on every ConfigureViewModel call so a stale DataContext does not leave the previous
    // AlertsSection's EditRequested subscribed forever (DiagnosticLogsView's pattern).
    private SensorAlertsSectionViewModel? _wiredAlertsSection;

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

    // ═══════════════ Sensor Alerts (RemEx-8wpvr.5) ═══════════════

    /// <summary>
    /// Opens <c>SetAlertDialog</c> the same way <c>CanvasView.axaml.cs</c>'s
    /// <c>OnShowSetAlertRequested</c> does, then writes the result back through
    /// <see cref="SensorAlertsSectionViewModel.ApplyEditResult"/> — null (or the empty-SensorName
    /// sentinel ClearAlert passes) removes the alert; anything else upserts it.
    /// </summary>
    private async void OnAlertEditRequested(string sensorName, SensorAlert? existing)
    {
        try
        {
            if (DataContext is not SettingsViewModel vm) return;

            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel is not Window ownerWindow) return;

            SensorAlert? result = null;
            var dialog = new SetAlertDialog(sensorName, existing, r => result = r);
            await dialog.ShowDialog(ownerWindow);

            // null means cancelled/dismissed — don't touch the existing alert.
            if (result != null)
                vm.AlertsSection.ApplyEditResult(sensorName, result);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[SettingsView] SetAlert dialog error: {ex.Message}");
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

        // Sensor alerts card (RemEx-8wpvr.5). Guards Reset all; EditRequested opens SetAlertDialog
        // exactly as CanvasView.axaml.cs's OnShowSetAlertRequested does.
        if (_wiredAlertsSection is not null)
            _wiredAlertsSection.EditRequested -= OnAlertEditRequested;
        _wiredAlertsSection = vm.AlertsSection;
        _wiredAlertsSection.EditRequested += OnAlertEditRequested;
        _wiredAlertsSection.OnConfirmationRequested = ConfirmationDialogHost.For(this);

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
