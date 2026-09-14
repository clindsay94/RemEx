using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;
using Remex.Desktop.ViewModels;
using System;

namespace Remex.Desktop.Views;

public partial class PersonalizationPanelView : UserControl
{
    public PersonalizationPanelView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => ConfigureViewModel();
    }

    /// <summary>Wires the Share-palette seams (clipboard, save/open pickers) the same way
    /// <c>DiagnosticLogsView.ConfigureViewModel</c> wires log export/import (RemEx-a7uzb).</summary>
    private void ConfigureViewModel()
    {
        if (DataContext is not CustomizationViewModel vm)
            return;

        vm.CopyToClipboardAsync = async text =>
        {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard is not null)
                await clipboard.SetTextAsync(text);
        };

        vm.PickSaveFileAsync = async options =>
        {
            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel is null)
                return null;
            return await topLevel.StorageProvider.SaveFilePickerAsync(options);
        };

        vm.PickOpenFileAsync = async options =>
        {
            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel is null)
                return Array.Empty<IStorageFile>();
            return await topLevel.StorageProvider.OpenFilePickerAsync(options);
        };

        // Reload the Flyout section's Apps checklist on every attach (fix round 1, RemEx-4kv0g.18.6
        // review, HIGH 2). ShellViewModel caches this view model with ??=, so without this call an
        // app added to the launcher after the sheet first opened this session never appears - the
        // constructor's own RefreshFlyoutApps only ever runs once. DataContextChanged fires on every
        // attach (the sheet is shown/hidden, not recreated), so this reload is cheap and current.
        vm.RefreshFlyoutApps();
    }
}
