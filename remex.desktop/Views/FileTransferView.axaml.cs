using System;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using Remex.Desktop.ViewModels;
using Remex.Core.Models;

namespace Remex.Desktop.Views;

public partial class FileTransferView : UserControl
{
    public FileTransferView()
    {
        InitializeComponent();
        RemoteFileList.SelectionChanged += OnRemoteFileListSelectionChanged;
        DataContextChanged += (_, _) => ConfigureViewModel();

        // Accept files dragged from the OS file manager and enqueue them as uploads (plan §WP10).
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);
    }

    private void OnRemoteFileListSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (DataContext is not FileTransferViewModel vm) return;
        var selected = RemoteFileList.SelectedItems?.OfType<FileEntry>() ?? [];
        vm.SetSelectedEntries(selected);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (e.ClickCount != 2) return;
        if (DataContext is not FileTransferViewModel vm) return;

        // Navigate into directories on double-click for the remote browser.
        if (e.Source is Control { DataContext: FileEntry entry })
        {
            if (vm.RemoteEntries.Contains(entry))
                vm.NavigateRemoteEntryCommand.Execute(entry);
        }
    }

    private void OnSearchResultTapped(object? sender, Avalonia.Input.TappedEventArgs e)
    {
        if (DataContext is not FileTransferViewModel vm) return;
        if (sender is Control { DataContext: FileSearchEntry entry })
            vm.OpenSearchResultCommand.Execute(entry);
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        // Only allow the drop when files are present and a writable folder is in view.
        var canDrop = e.DataTransfer.Contains(DataFormat.File)
            && DataContext is FileTransferViewModel { SelectedRemoteRoot.IsWritable: true };
        e.DragEffects = canDrop ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        if (DataContext is not FileTransferViewModel vm)
            return;

        var files = e.DataTransfer.TryGetFiles();
        if (files is null)
            return;

        var localPaths = files
            .OfType<IStorageFile>()
            .Select(f => f.TryGetLocalPath())
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => p!)
            .ToList();

        if (localPaths.Count > 0)
            vm.EnqueueUploads(localPaths, FileTransferQueueKind.Upload);

        e.Handled = true;
    }

    private void ConfigureViewModel()
    {
        if (DataContext is not FileTransferViewModel vm)
            return;

        vm.SelectAllEntries = () => RemoteFileList.SelectAll();

        // Was an inline lambda; moved to ConfirmationDialogHost when RemEx-6p1f needed the same one
        // in four more views. Behaviour is identical, including returning false with no parent window.
        vm.OnConfirmationRequested = ConfirmationDialogHost.For(this);

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
