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
    }

    /// <summary>
    /// The seed wheel has finished an interaction — a drag released, or an arrow key let go — so the
    /// colour the user landed on joins the recently-used row.
    /// </summary>
    /// <remarks>
    /// IN CODE-BEHIND BECAUSE IT IS AN EVENT, NOT A COMMAND. The distinction the recents list needs
    /// is "the drag ended", which no bindable property carries: every colour a drag passes through
    /// raises the same change notification as the one it stops on, so binding to the seed would fill
    /// the row with eight colours nobody chose.
    /// </remarks>
    private void OnSeedCommitted(object? sender, EventArgs e)
    {
        (DataContext as CustomizationViewModel)?.CommitSeedToRecents();
    }
}
