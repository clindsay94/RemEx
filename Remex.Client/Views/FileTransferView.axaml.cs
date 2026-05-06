using System;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using Remex.Client.ViewModels;

namespace Remex.Client.Views;

public partial class FileTransferView : UserControl
{
    public FileTransferView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => ConfigureViewModel();
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (e.ClickCount != 2) return;
        if (DataContext is not FileTransferViewModel vm) return;

        // Navigate into directories on double-click for the remote browser.
        if (e.Source is Control { DataContext: Remex.Core.Models.FileEntry entry })
        {
            if (vm.RemoteEntries.Contains(entry))
                vm.NavigateRemoteEntryCommand.Execute(entry);
        }
    }

    private void ConfigureViewModel()
    {
        if (DataContext is not FileTransferViewModel vm)
            return;

        vm.PickUploadFileAsync = async options =>
        {
            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel is null)
                return Array.Empty<IStorageFile>();

            return await topLevel.StorageProvider.OpenFilePickerAsync(options);
        };

        vm.PickDownloadDestinationAsync = async options =>
        {
            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel is null)
                return null;

            return await topLevel.StorageProvider.SaveFilePickerAsync(options);
        };
    }
}
