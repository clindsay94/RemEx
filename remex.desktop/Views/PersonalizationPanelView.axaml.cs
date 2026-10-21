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

        vm.OnConfirmationRequested = ConfirmationDialogHost.For(this);

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
    }
}
