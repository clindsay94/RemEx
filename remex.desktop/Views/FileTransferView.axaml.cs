using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
// Avalonia 12 moved SetTextAsync off IClipboard onto ClipboardExtensions in this namespace (RemEx-jcma3).
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using Material.Icons;
using Material.Icons.Avalonia;
using Remex.Core.Models;
using Remex.Desktop.Services;
using Remex.Desktop.ViewModels;

namespace Remex.Desktop.Views;

/// <summary>
/// The File Transfer screen's view (2026-10-08 redesign). Everything here is input plumbing the view model cannot
/// do itself: selection from both views, keyboard shortcuts, right-click and More menus, dragging rows out (to the
/// tree, or to File Explorer for This PC's files) and dropping onto the tree, and the clipboard.
/// </summary>
public partial class FileTransferView : UserControl
{
    /// <summary>Rows dragged out of the list. In-process only: another app sees the plain files, never this.</summary>
    private static readonly DataFormat<FileDragPayload> EntriesFormat =
        DataFormat.CreateInProcessFormat<FileDragPayload>("remex-file-entries");

    /// <summary>How far the pointer must travel before a press on a row becomes a drag, so clicks stay clicks.</summary>
    private const double DragThreshold = 6;

    private Point? _pressPoint;
    private ListBox? _pressList;
    // Avalonia starts a drag from the PRESS that began it, so it is kept until the pointer has moved far enough.
    private PointerPressedEventArgs? _pressArgs;
    private FileTransferViewModel? _boundVm;

    public FileTransferView()
    {
        InitializeComponent();
        RemoteFileList.SelectionChanged += OnRemoteFileListSelectionChanged;
        RemoteIconGrid.SelectionChanged += OnRemoteFileListSelectionChanged;
        DataContextChanged += (_, _) => ConfigureViewModel();

        // Double-click opens a folder, in both the details list and the icon grid (live-check C5).
        // handledEventsToo is load-bearing: ListBoxItem marks every mouse press Handled when it
        // updates the selection, so a PointerPressed/ClickCount handler on this UserControl never
        // saw a press on a row and folders could not be opened. DoubleTapped is raised by Gestures
        // from the press's RouteFinished, independent of Handled, and reaches the list itself.
        RemoteFileList.AddHandler(DoubleTappedEvent, OnRemoteEntryDoubleTapped, handledEventsToo: true);
        RemoteIconGrid.AddHandler(DoubleTappedEvent, OnRemoteEntryDoubleTapped, handledEventsToo: true);

        // Dragging rows out: the same reason for handledEventsToo as above.
        foreach (var list in new[] { RemoteFileList, RemoteIconGrid })
        {
            list.AddHandler(PointerPressedEvent, OnListPointerPressed, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
            list.AddHandler(PointerMovedEvent, OnListPointerMoved, handledEventsToo: true);
            list.AddHandler(PointerReleasedEvent, (_, _) => _pressPoint = null, handledEventsToo: true);
            list.ContextMenu = new ContextMenu();
            list.ContextMenu.Opening += (sender, _) => FillMenu(((ContextMenu)sender!).Items, forMoreButton: false);
        }

        if (MoreButton.Flyout is MenuFlyout more)
            more.Opening += (_, _) => FillMenu(more.Items, forMoreButton: true);

        // Accept files dragged from the OS file manager and enqueue them as uploads (plan §WP10).
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);

        // Rows and OS files dropped onto a folder in the tree.
        FolderTree.AddHandler(DragDrop.DragOverEvent, OnTreeDragOver);
        FolderTree.AddHandler(DragDrop.DropEvent, OnTreeDrop);

        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Bubble);
    }

    private void OnRemoteFileListSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (DataContext is not FileTransferViewModel vm || sender is not ListBox list) return;
        // BOTH views forward their selection now. Before the redesign only the details list did, so a
        // multi-selection made in the icon grid never reached Copy, Cut or Delete.
        var selected = list.SelectedItems?.OfType<FileEntry>() ?? [];
        vm.SetSelectedEntries(selected);
    }

    private void OnRemoteEntryDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is not FileTransferViewModel vm) return;

        // Navigate into directories on double-click for the remote browser. NavigateRemoteEntry
        // ignores files, so a double-click on one is a no-op rather than a failed browse.
        if (e.Source is Control { DataContext: FileEntry entry } && vm.RemoteEntries.Contains(entry))
        {
            vm.NavigateRemoteEntryCommand.Execute(entry);
            e.Handled = true;
        }
    }

    private void OnSearchResultTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is not FileTransferViewModel vm) return;
        if (sender is Control { DataContext: FileSearchEntry entry })
            vm.OpenSearchResultCommand.Execute(entry);
    }

    // ── OS files dropped onto the list ──

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        // Our own rows dragged back onto the list are not an upload of themselves.
        if (e.DataTransfer.Contains(EntriesFormat))
        {
            e.DragEffects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        // Only allow the drop when files are present and a writable folder is in view.
        var canDrop = e.DataTransfer.Contains(DataFormat.File)
            && DataContext is FileTransferViewModel { SelectedRemoteRoot.IsWritable: true };
        e.DragEffects = canDrop ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        if (DataContext is not FileTransferViewModel vm || e.DataTransfer.Contains(EntriesFormat))
            return;

        var localPaths = LocalPathsIn(e);
        if (localPaths.Count > 0)
            vm.EnqueueUploads(localPaths, FileTransferQueueKind.Upload);

        e.Handled = true;
    }

    private static List<string> LocalPathsIn(DragEventArgs e) =>
        (e.DataTransfer.TryGetFiles() ?? [])
            .OfType<IStorageFile>()
            .Select(f => f.TryGetLocalPath())
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => p!)
            .ToList();

    // ── Dragging rows out ──

    private void OnListPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is ListBox list && e.GetCurrentPoint(list).Properties.IsLeftButtonPressed
            && (e.Source as Visual)?.FindAncestorOfType<ListBoxItem>(includeSelf: true) is not null)
        {
            _pressPoint = e.GetPosition(list);
            _pressList = list;
            _pressArgs = e;
        }
    }

    private async void OnListPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_pressPoint is not { } start || _pressList is not { } list || _pressArgs is not { } press || !ReferenceEquals(sender, list)
            || DataContext is not FileTransferViewModel vm || vm.SelectedRemoteRoot is not { } root)
            return;
        var now = e.GetPosition(list);
        if (Math.Abs(now.X - start.X) < DragThreshold && Math.Abs(now.Y - start.Y) < DragThreshold)
            return;
        _pressPoint = null;

        var entries = (list.SelectedItems?.OfType<FileEntry>() ?? []).Where(x => x.Name != "..").ToList();
        if (entries.Count == 0)
            return;

        var source = vm.SelectedSource ?? FileSourceOption.ThisPc;
        var folder = FileTreeViewModel.Normalize(vm.RemotePath);
        var data = new DataTransfer();
        data.Add(DataTransferItem.Create(EntriesFormat, new FileDragPayload(source, root.RootId, folder, entries)));

        // This PC's files are real files on this disk, so they can also be dropped into File Explorer. A phone's
        // cannot: Avalonia has no deferred (virtual-file) drag source, so phone files use Download to… instead.
        if (source.IsThisPc && TopLevel.GetTopLevel(this)?.StorageProvider is { } storage)
        {
            foreach (var entry in entries.Where(x => !x.IsDirectory))
            {
                if (vm.TryLocalPath(root.RootId, FileTreeViewModel.Combine(folder, entry.Name)) is { } path
                    && await storage.TryGetFileFromPathAsync(path) is { } file)
                {
                    data.Add(DataTransferItem.CreateFile(file));
                }
            }
        }

        _pressArgs = null;
        await DragDrop.DoDragDropAsync(press, data, DragDropEffects.Copy | DragDropEffects.Move);
    }

    // ── Drops onto the tree ──

    private static FileTreeNode? NodeUnder(DragEventArgs e) =>
        (e.Source as Visual)?.FindAncestorOfType<TreeViewItem>(includeSelf: true)?.DataContext as FileTreeNode;

    private void OnTreeDragOver(object? sender, DragEventArgs e)
    {
        var node = NodeUnder(e);
        var ok = node is { IsDevice: false, IsPlaceholder: false };
        if (ok && e.DataTransfer.TryGetValue(EntriesFormat) is { } payload)
        {
            // Same device: a move, or a copy with Ctrl. Another device: always a copy.
            e.DragEffects = payload.Source.SameSourceAs(node!.Source) && !e.KeyModifiers.HasFlag(KeyModifiers.Control)
                ? DragDropEffects.Move
                : DragDropEffects.Copy;
        }
        else
        {
            e.DragEffects = ok && e.DataTransfer.Contains(DataFormat.File) && node!.IsWritable
                ? DragDropEffects.Copy
                : DragDropEffects.None;
        }
        e.Handled = true;
    }

    private async void OnTreeDrop(object? sender, DragEventArgs e)
    {
        if (DataContext is not FileTransferViewModel vm || NodeUnder(e) is not { } node)
            return;
        e.Handled = true;
        if (e.DataTransfer.TryGetValue(EntriesFormat) is { } payload)
            await vm.DropOntoNodeAsync(payload, node, copy: e.KeyModifiers.HasFlag(KeyModifiers.Control));
        else if (LocalPathsIn(e) is { Count: > 0 } paths)
            await vm.DropLocalFilesOntoNodeAsync(paths, node);
    }

    // ── Keyboard ──

    /// <summary>
    /// Explorer's shortcuts. Ignored while typing in a box, so Space, Delete and Backspace type rather than act.
    /// Escape still leaves an inline rename or new-folder box.
    /// </summary>
    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not FileTransferViewModel vm)
            return;

        if (e.Key == Key.Escape)
        {
            if (vm.IsRenaming) { vm.CancelRenameCommand.Execute(null); e.Handled = true; }
            else if (vm.IsCreatingFolder) { vm.CancelNewFolderCommand.Execute(null); e.Handled = true; }
            return;
        }

        if (e.Source is TextBox)
            return;

        var ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        var alt = e.KeyModifiers.HasFlag(KeyModifiers.Alt);
        bool Run(System.Windows.Input.ICommand command)
        {
            if (!command.CanExecute(null)) return false;
            command.Execute(null);
            return true;
        }

        e.Handled = e.Key switch
        {
            Key.Space when !ctrl && !alt => Run(vm.TogglePreviewPaneCommand),
            Key.Enter when vm.SelectedRemoteEntry is { IsDirectory: true } => Run(vm.OpenSelectedRemoteCommand),
            Key.Enter => Run(vm.TogglePreviewPaneCommand),
            Key.Back => Run(vm.NavigateRemoteUpCommand),
            Key.Up when alt => Run(vm.NavigateRemoteUpCommand),
            Key.F2 => Run(vm.StartRenameCommand),
            Key.Delete => Run(vm.DeleteRemoteCommand),
            Key.F5 => Run(vm.BrowseRemoteCommand),
            Key.C when ctrl => Run(vm.CopySelectionCommand),
            Key.X when ctrl => Run(vm.CutSelectionCommand),
            Key.V when ctrl => Run(vm.PasteCommand),
            Key.A when ctrl => Run(vm.SelectAllCommand),
            Key.F when ctrl => SearchBox.IsVisible && SearchBox.Focus(),
            _ => false,
        };
    }

    // ── Menus ──

    /// <summary>
    /// Builds the right-click and More menus fresh on every opening, from what the selection and device allow
    /// right now: items that cannot apply are left out, not greyed, and every label follows the current language.
    /// </summary>
    private void FillMenu(ItemCollection items, bool forMoreButton)
    {
        items.Clear();
        if (DataContext is not FileTransferViewModel vm)
            return;
        var loc = LocalizationService.Instance;

        void Add(string key, System.Windows.Input.ICommand command, MaterialIconKind icon, bool show = true, object? parameter = null)
        {
            if (!show) return;
            items.Add(new MenuItem
            {
                Header = loc[key],
                Command = command,
                CommandParameter = parameter,
                Icon = new MaterialIcon { Kind = icon },
            });
        }

        var hasSelection = vm.SelectedRemoteEntry is { Name: not ".." };
        var isFile = vm.SelectedRemoteEntry is { IsDirectory: false };

        Add("FileTransfer_PreviewMenu", vm.TogglePreviewPaneCommand, MaterialIconKind.DockRight, hasSelection && !vm.IsPreviewPaneOpen);
        Add("FileTransfer_DownloadBtn", vm.DownloadCommand, MaterialIconKind.Download, isFile);
        Add("FileTransfer_DownloadFolderBtn", vm.DownloadFolderCommand, MaterialIconKind.FolderDownloadOutline, vm.SupportsFolderTransfer && !isFile && hasSelection);

        var others = vm.OtherDevices;
        if (hasSelection && others.Count > 0)
        {
            var sendTo = new MenuItem { Header = loc["FileTransfer_SendTo"], Icon = new MaterialIcon { Kind = MaterialIconKind.SendOutline } };
            foreach (var device in others)
                sendTo.Items.Add(new MenuItem { Header = FileTreeViewModel.DeviceLabel(device), Command = vm.SendSelectionToCommand, CommandParameter = device });
            items.Add(sendTo);
        }

        if (forMoreButton)
        {
            Add("FileTransfer_UploadFolderBtn", vm.UploadFolderCommand, MaterialIconKind.FolderUploadOutline, vm.CanChangeFiles);
            Add("FileTransfer_SelectAllBtn", vm.SelectAllCommand, MaterialIconKind.SelectAll);
        }

        items.Add(new Separator());
        Add("FileTransfer_RenameBtn", vm.StartRenameCommand, MaterialIconKind.RenameOutline, vm.CanChangeFiles && hasSelection);
        Add("FileTransfer_CopyBtn", vm.CopySelectionCommand, MaterialIconKind.ContentCopy, vm.SupportsCopyMove && hasSelection);
        Add("FileTransfer_CutBtn", vm.CutSelectionCommand, MaterialIconKind.ContentCut, vm.SupportsCopyMove && hasSelection);
        Add("FileTransfer_PasteBtn", vm.PasteCommand, MaterialIconKind.ContentPaste, vm.SupportsCopyMove && vm.HasClipboard);
        Add("FileTransfer_DeleteBtn", vm.DeleteRemoteCommand, MaterialIconKind.DeleteOutline, vm.CanChangeFiles && hasSelection);
        items.Add(new Separator());
        Add("FileTransfer_CopyPath", vm.CopyPathCommand, MaterialIconKind.LinkVariant);
        Add("FileTransfer_ComputeHashMenu", vm.ComputeSelectionHashCommand, MaterialIconKind.Fingerprint, isFile);

        if (forMoreButton)
        {
            Add("FileTransfer_PinFolderTooltip", vm.PinCurrentFolderCommand, MaterialIconKind.Pin, vm.CanManageFiles);
            Add("FileTransfer_VolumesTooltip", vm.LoadVolumesCommand, MaterialIconKind.Harddisk, vm.ShowVolumesButton);
        }

        // No separator left dangling at either end.
        while (items.Count > 0 && items[^1] is Separator) items.RemoveAt(items.Count - 1);
        while (items.Count > 0 && items[0] is Separator) items.RemoveAt(0);
    }

    // ── View model wiring ──

    private void ConfigureViewModel()
    {
        if (_boundVm is not null)
            _boundVm.Preview.Lines.CollectionChanged -= OnPreviewLinesChanged;
        _boundVm = DataContext as FileTransferViewModel;
        if (_boundVm is not { } vm)
            return;

        vm.Preview.Lines.CollectionChanged += OnPreviewLinesChanged;
        vm.SelectAllEntries = () => (vm.IsIconView ? RemoteIconGrid : RemoteFileList).SelectAll();

        // Was an inline lambda; moved to ConfirmationDialogHost when RemEx-6p1f needed the same one
        // in four more views. Behaviour is identical, including returning false with no parent window.
        vm.OnConfirmationRequested = ConfirmationDialogHost.For(this);

        vm.CopyText = async text =>
        {
            if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
                await clipboard.SetTextAsync(text);
        };

        vm.PickUploadFileAsync = async options =>
        {
            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel is null)
                return Array.Empty<IStorageFile>();

            return await topLevel.StorageProvider.OpenFilePickerAsync(options);
        };

        vm.PickLocalFolderAsync = async options =>
        {
            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel is null)
                return Array.Empty<IStorageFolder>();

            return await topLevel.StorageProvider.OpenFolderPickerAsync(options);
        };

        vm.PickDownloadDestinationAsync = async options =>
        {
            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel is null)
                return null;

            return await topLevel.StorageProvider.SaveFilePickerAsync(options);
        };

        // Drag-out needs This PC's shared folders on disk without awaiting mid-gesture; read them once now.
        vm.LocalPathForAsync(string.Empty, string.Empty).FireAndForget("reading this PC's shared folders");
    }

    /// <summary>A live tail keeps the newest line in view.</summary>
    private void OnPreviewLinesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_boundVm?.Preview.IsLive == true && e.Action == NotifyCollectionChangedAction.Add)
            PreviewTextScroll.ScrollToEnd();
    }
}
