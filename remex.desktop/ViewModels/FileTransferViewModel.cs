using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Remex.Core.Models;
using Remex.Desktop.Services;
using Remex.Desktop.Services.FileTransfer;

namespace Remex.Desktop.ViewModels;

/// <summary>How the remote file list is ordered.</summary>
public enum FileSortField
{
    Name,
    Size,
    Modified,
}

/// <summary>A single tappable breadcrumb segment: a display label and the remote path it navigates to.</summary>
public sealed record BreadcrumbSegment(string Label, string Path);

/// <summary>
/// Rich remote file-manager view-model (plan §WP10). Browses the connected host's shared roots with
/// breadcrumbs, sort, search, multi-select copy/move/mkdir/delete, a properties + thumbnail pane, a
/// full-device volumes list, a local transfer queue, and send-to-device / drag-drop upload. New v3
/// features are gated on the host's advertised <see cref="FileTransferClient.Capabilities"/> so nothing v3
/// is ever sent to a v2 peer.
/// </summary>
public sealed partial class FileTransferViewModel : ObservableObject, IDisposable
{
    private readonly ConnectionViewModel _connection;
    private readonly FileTransferClient _client;

    // Server-ordered snapshot of the current folder; the display list is a sorted projection of this.
    private readonly List<FileEntry> _rawEntries = new();

    private CancellationTokenSource? _searchCts;

    // ── Copy/cut clipboard (drives the v3 copy/move ops via a familiar paste flow) ──
    private readonly List<FileEntry> _clipboard = new();
    private string? _clipboardRootId;
    private string _clipboardFolder = string.Empty;
    private bool _clipboardIsMove;

    /// <summary>Wired by the view to a native file-open picker (for upload / send-to-device).</summary>
    public Func<FilePickerOpenOptions, Task<IReadOnlyList<IStorageFile>>>? PickUploadFileAsync { get; set; }

    /// <summary>Wired by the view to a native file-save picker (for download).</summary>
    public Func<FilePickerSaveOptions, Task<IStorageFile?>>? PickDownloadDestinationAsync { get; set; }

    /// <summary>Wired by the view so the VM can select every entry in the list for a multi-select op.</summary>
    public Action? SelectAllEntries { get; set; }

    public FileTransferViewModel(ConnectionViewModel connection)
    {
        _connection = connection;
        _client = new FileTransferClient(connection);
        TransferQueue = new FileTransferQueue();
        TransferQueue.Changed += OnQueueChanged;
        TransferQueue.ItemCompleted += OnTransferCompleted;
        _connection.PropertyChanged += OnConnectionPropertyChanged;
        _ = InitializeAsync();
    }

    // ─── Transfer queue ───────────────────────────────────────────────────────

    public FileTransferQueue TransferQueue { get; }

    public bool HasQueueItems => TransferQueue.Items.Count > 0;

    public bool HasActiveTransfer => TransferQueue.Items.Any(i => i.IsActive);

    private void OnQueueChanged()
    {
        OnPropertyChanged(nameof(HasQueueItems));
        OnPropertyChanged(nameof(HasActiveTransfer));
    }

    /// <summary>Records a successful user-driven transfer in the Home "Recent activity" feed.</summary>
    private static void OnTransferCompleted(FileTransferQueueItem item)
    {
        var kind = item.Kind switch
        {
            FileTransferQueueKind.Upload => ActivityKind.FileUploaded,
            FileTransferQueueKind.Download => ActivityKind.FileDownloaded,
            FileTransferQueueKind.SendToPhone => ActivityKind.FileSent,
            _ => ActivityKind.FileUploaded,
        };
        ActivityService.Instance.Record(kind, item.FileName);
    }

    [RelayCommand]
    private void ClearCompletedTransfers() => TransferQueue.ClearCompleted();

    // ─── Capabilities (v3 gating) ─────────────────────────────────────────────

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PasteCommand))]
    [NotifyCanExecuteChangedFor(nameof(CopySelectionCommand))]
    [NotifyCanExecuteChangedFor(nameof(CutSelectionCommand))]
    private bool _supportsCopyMove;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(NewFolderCommand))]
    private bool _supportsMkdir;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SearchCommand))]
    private bool _supportsSearch;

    [ObservableProperty]
    private bool _supportsFullBrowse;

    private void RefreshCapabilityFlags()
    {
        SupportsCopyMove = _client.SupportsOp(FileManageOperations.Copy) && _client.SupportsOp(FileManageOperations.Move);
        SupportsMkdir = _client.SupportsOp(FileManageOperations.Mkdir);
        SupportsSearch = _client.SupportsV3 && _client.SupportsOp("search");
        SupportsFullBrowse = _client.SupportsFullBrowse;
    }

    // ─── Roots ────────────────────────────────────────────────────────────────

    public ObservableCollection<FileSharedRoot> RemoteRoots { get; } = new();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UploadCommand))]
    [NotifyCanExecuteChangedFor(nameof(SendFileCommand))]
    [NotifyCanExecuteChangedFor(nameof(RemoveCurrentRootCommand))]
    [NotifyCanExecuteChangedFor(nameof(NewFolderCommand))]
    [NotifyCanExecuteChangedFor(nameof(PasteCommand))]
    [NotifyPropertyChangedFor(nameof(RemoteRootHint))]
    private FileSharedRoot? _selectedRemoteRoot;

    partial void OnSelectedRemoteRootChanged(FileSharedRoot? value)
    {
        ClearSearch();
        if (value is null)
        {
            RemoteEntries.Clear();
            _rawEntries.Clear();
            RemotePath = "/";
            RebuildBreadcrumbs();
            return;
        }

        RemotePath = "/";
        _ = BrowseRemoteAsync();
    }

    public string RemoteRootHint => SelectedRemoteRoot switch
    {
        null => LocalizationService.Instance["FileTransfer_HintNoneSelected"],
        { IsWritable: true } root => string.Format(LocalizationService.Instance["FileTransfer_HintWritableFormat"], root.DisplayName),
        { } root => string.Format(LocalizationService.Instance["FileTransfer_HintReadOnlyFormat"], root.DisplayName),
    };

    private async Task InitializeAsync()
    {
        if (_connection.IsConnected)
            await LoadRemoteRootsAsync();
    }

    [RelayCommand]
    private async Task LoadRemoteRootsAsync()
    {
        if (!_connection.IsConnected)
            return;

        try
        {
            IsLoading = true;
            StatusText = string.Empty;
            var roots = await _client.ListRemoteRootsAsync(CancellationToken.None);
            RefreshCapabilityFlags();
            var previousRootId = SelectedRemoteRoot?.RootId;
            RemoteRoots.Clear();
            foreach (var root in roots.OrderBy(root => root.DisplayName))
                RemoteRoots.Add(root);

            SelectedRemoteRoot = RemoteRoots.FirstOrDefault(root => root.RootId == previousRootId)
                ?? RemoteRoots.FirstOrDefault(root => root.IsWritable)
                ?? RemoteRoots.FirstOrDefault();

            if (SelectedRemoteRoot is null)
                StatusText = LocalizationService.Instance["FileTransfer_NoSharedFolders"];
        }
        catch (Exception ex)
        {
            StatusText = string.Format(LocalizationService.Instance["FileTransfer_SharedFoldersUnavailableFormat"], ex.Message);
        }
        finally
        {
            IsLoading = false;
        }
    }

    // ─── Browse + sort ─────────────────────────────────────────────────────────

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(NavigateRemoteUpCommand))]
    [NotifyCanExecuteChangedFor(nameof(PinCurrentFolderCommand))]
    [NotifyPropertyChangedFor(nameof(CanNavigateRemoteUp))]
    private string _remotePath = "/";

    public ObservableCollection<FileEntry> RemoteEntries { get; } = new();

    public ObservableCollection<BreadcrumbSegment> Breadcrumbs { get; } = new();

    [ObservableProperty]
    private FileSortField _sortField = FileSortField.Name;

    [ObservableProperty]
    private bool _sortDescending;

    partial void OnSortFieldChanged(FileSortField value) => RebuildEntryDisplay();

    partial void OnSortDescendingChanged(bool value) => RebuildEntryDisplay();

    // ─── View mode (details list / resizable icon grid) ───

    /// <summary>When true, files render as a resizable icon grid instead of a details list.</summary>
    [ObservableProperty]
    private bool _isIconView;

    /// <summary>Icon tile edge length (px) for the icon view, driven by the size slider.</summary>
    [ObservableProperty]
    private double _iconSize = 96;

    [RelayCommand]
    private void ToggleViewMode() => IsIconView = !IsIconView;

    [RelayCommand]
    private void SortBy(string field)
    {
        var requested = field switch
        {
            "size" => FileSortField.Size,
            "date" => FileSortField.Modified,
            _ => FileSortField.Name,
        };

        // Tapping the active field toggles direction; a new field resets to ascending.
        if (requested == SortField)
            SortDescending = !SortDescending;
        else
        {
            SortDescending = false;
            SortField = requested;
        }
    }

    [RelayCommand]
    private async Task BrowseRemoteAsync()
    {
        ClearSelection();

        if (SelectedRemoteRoot is null)
        {
            await LoadRemoteRootsAsync();
            if (SelectedRemoteRoot is null)
                return;
        }

        try
        {
            IsLoading = true;
            StatusText = string.Empty;
            var entries = await _client.BrowseRemoteAsync(SelectedRemoteRoot.RootId, RemotePath, CancellationToken.None);
            _rawEntries.Clear();
            if (RemotePath != "/" && RemotePath != "\\")
                _rawEntries.Add(new FileEntry { Name = "..", IsDirectory = true });
            _rawEntries.AddRange(entries);
            RebuildEntryDisplay();
            RebuildBreadcrumbs();
        }
        catch (Exception ex)
        {
            StatusText = string.Format(LocalizationService.Instance["FileTransfer_BrowseErrorFormat"], ex.Message);
        }
        finally
        {
            IsLoading = false;
        }
    }

    private void RebuildEntryDisplay()
    {
        var sorted = SortEntries(_rawEntries, SortField, SortDescending);
        RemoteEntries.Clear();
        foreach (var entry in sorted)
            RemoteEntries.Add(entry);
    }

    private void RebuildBreadcrumbs()
    {
        Breadcrumbs.Clear();
        var rootLabel = SelectedRemoteRoot?.DisplayName ?? LocalizationService.Instance["FileTransfer_BreadcrumbRoot"];
        foreach (var segment in BuildBreadcrumbSegments(RemotePath, rootLabel))
            Breadcrumbs.Add(segment);
    }

    [RelayCommand]
    private void NavigateBreadcrumb(BreadcrumbSegment segment)
    {
        if (segment is null || segment.Path == RemotePath)
            return;
        RemotePath = segment.Path;
        _ = BrowseRemoteAsync();
    }

    [RelayCommand]
    private void NavigateRemoteEntry(FileEntry entry)
    {
        if (entry is null || !entry.IsDirectory) return;
        RemotePath = entry.Name == ".."
            ? GetParentRemotePath(RemotePath)
            : CombineRemotePath(RemotePath, entry.Name);
        _ = BrowseRemoteAsync();
    }

    [RelayCommand(CanExecute = nameof(CanNavigateRemoteUp))]
    private void NavigateRemoteUp()
    {
        if (!CanNavigateRemoteUp) return;
        RemotePath = GetParentRemotePath(RemotePath);
        _ = BrowseRemoteAsync();
    }

    [RelayCommand(CanExecute = nameof(CanOpenRemoteSelection))]
    private void OpenSelectedRemote()
    {
        if (SelectedRemoteEntry is not null)
            NavigateRemoteEntry(SelectedRemoteEntry);
    }

    public bool CanNavigateRemoteUp => RemotePath != "/" && RemotePath != "\\";

    public bool CanOpenRemoteSelection => SelectedRemoteEntry?.IsDirectory == true;

    // ─── Selection ─────────────────────────────────────────────────────────────

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DownloadCommand))]
    [NotifyCanExecuteChangedFor(nameof(OpenSelectedRemoteCommand))]
    [NotifyCanExecuteChangedFor(nameof(DeleteRemoteCommand))]
    [NotifyCanExecuteChangedFor(nameof(StartRenameCommand))]
    [NotifyCanExecuteChangedFor(nameof(VerifyHashCommand))]
    [NotifyCanExecuteChangedFor(nameof(CopySelectionCommand))]
    [NotifyCanExecuteChangedFor(nameof(CutSelectionCommand))]
    [NotifyCanExecuteChangedFor(nameof(ShowPropertiesCommand))]
    [NotifyPropertyChangedFor(nameof(CanOpenRemoteSelection))]
    private FileEntry? _selectedRemoteEntry;

    private List<FileEntry> _selectedEntries = new();
    public IReadOnlyList<FileEntry> SelectedEntries => _selectedEntries;

    /// <summary>Number of currently-selected real entries (excludes "..").</summary>
    public int SelectionCount => EffectiveSelection().Count;

    public bool HasMultiSelection => SelectionCount > 1;

    public void SetSelectedEntries(IEnumerable<FileEntry> entries)
    {
        _selectedEntries = entries.Where(e => e.Name != "..").ToList();
        OnPropertyChanged(nameof(SelectionCount));
        OnPropertyChanged(nameof(HasMultiSelection));
        DeleteRemoteCommand.NotifyCanExecuteChanged();
        CopySelectionCommand.NotifyCanExecuteChanged();
        CutSelectionCommand.NotifyCanExecuteChanged();
    }

    private void ClearSelection()
    {
        _selectedEntries = new();
        SelectedRemoteEntry = null;
        OnPropertyChanged(nameof(SelectionCount));
        OnPropertyChanged(nameof(HasMultiSelection));
        DeleteRemoteCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Returns the effective set of entries a batch op should act on: the multi-selection when
    /// present, otherwise the single highlighted entry. Always excludes the "up" placeholder.</summary>
    private List<FileEntry> EffectiveSelection()
    {
        if (_selectedEntries.Count > 0)
            return _selectedEntries.Where(e => e.Name != "..").ToList();
        return SelectedRemoteEntry is { Name: not ".." } e ? new List<FileEntry> { e } : new List<FileEntry>();
    }

    [RelayCommand]
    private void SelectAll() => SelectAllEntries?.Invoke();

    // ─── Search ────────────────────────────────────────────────────────────────

    [ObservableProperty]
    private string _searchQuery = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowSearchResults))]
    private bool _isSearchActive;

    [ObservableProperty]
    private bool _searchTruncated;

    public ObservableCollection<FileSearchEntry> SearchResults { get; } = new();

    public bool ShowSearchResults => IsSearchActive;

    partial void OnSearchQueryChanged(string value)
    {
        // Debounced live search; empty query clears back to the folder view.
        _searchCts?.Cancel();
        if (string.IsNullOrWhiteSpace(value) || !SupportsSearch || SelectedRemoteRoot is null)
        {
            ClearSearch();
            return;
        }

        var cts = new CancellationTokenSource();
        _searchCts = cts;
        _ = RunSearchDebouncedAsync(value, cts.Token);
    }

    private async Task RunSearchDebouncedAsync(string query, CancellationToken ct)
    {
        try
        {
            await Task.Delay(300, ct);
            await RunSearchAsync(query, ct);
        }
        catch (OperationCanceledException) { /* superseded by a newer keystroke */ }
    }

    [RelayCommand(CanExecute = nameof(CanSearch))]
    private async Task SearchAsync()
    {
        if (!string.IsNullOrWhiteSpace(SearchQuery))
            await RunSearchAsync(SearchQuery, CancellationToken.None);
    }

    private bool CanSearch() => SupportsSearch && _connection.IsConnected && SelectedRemoteRoot is not null;

    private async Task RunSearchAsync(string query, CancellationToken ct)
    {
        if (SelectedRemoteRoot is null)
            return;

        try
        {
            IsSearchActive = true;
            IsLoading = true;
            var (entries, truncated) = await _client.SearchRemoteAsync(
                SelectedRemoteRoot.RootId, RelativeCurrentFolder(), query, FileTransferLimits.SearchMaxResults, ct);
            SearchResults.Clear();
            foreach (var entry in entries)
                SearchResults.Add(entry);
            SearchTruncated = truncated;
            StatusText = truncated
                ? string.Format(LocalizationService.Instance["FileTransfer_SearchTruncatedFormat"], SearchResults.Count)
                : string.Format(LocalizationService.Instance["FileTransfer_SearchResultsFormat"], SearchResults.Count);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            StatusText = string.Format(LocalizationService.Instance["FileTransfer_SearchFailedFormat"], ex.Message);
        }
        finally
        {
            IsLoading = false;
        }
    }

    private void ClearSearch()
    {
        _searchCts?.Cancel();
        _searchCts = null;
        if (SearchQuery.Length != 0)
            _searchQuery = string.Empty;
        OnPropertyChanged(nameof(SearchQuery));
        IsSearchActive = false;
        SearchTruncated = false;
        SearchResults.Clear();
    }

    /// <summary>Navigates the folder view to the directory that contains a search hit.</summary>
    [RelayCommand]
    private void OpenSearchResult(FileSearchEntry entry)
    {
        if (entry is null) return;
        var containerRelative = entry.IsDirectory
            ? entry.RelativePath
            : GetParentRemotePath("/" + entry.RelativePath.Replace('\\', '/'));
        ClearSearch();
        RemotePath = string.IsNullOrWhiteSpace(containerRelative) ? "/" : "/" + containerRelative.TrimStart('/');
        _ = BrowseRemoteAsync();
    }

    // ─── Upload / Download / Send (queued) ─────────────────────────────────────

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string _statusText = string.Empty;

    [RelayCommand(CanExecute = nameof(CanUpload))]
    private async Task UploadAsync()
    {
        if (SelectedRemoteRoot is null) return;
        await PickAndEnqueueUploadsAsync(FileTransferQueueKind.Upload);
    }

    [RelayCommand(CanExecute = nameof(CanUpload))]
    private async Task SendFileAsync()
    {
        if (SelectedRemoteRoot is null) return;
        await PickAndEnqueueUploadsAsync(FileTransferQueueKind.SendToPhone);
    }

    private async Task PickAndEnqueueUploadsAsync(FileTransferQueueKind kind)
    {
        if (PickUploadFileAsync is null)
        {
            StatusText = LocalizationService.Instance["FileTransfer_PickerUnavailable"];
            return;
        }

        var files = await PickUploadFileAsync(new FilePickerOpenOptions
        {
            Title = LocalizationService.Instance["FileTransfer_PickerTitle"],
            AllowMultiple = true,
        });

        if (files.Count == 0)
            return;

        var localPaths = files
            .Select(f => f.TryGetLocalPath())
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => p!)
            .ToList();

        if (localPaths.Count == 0)
        {
            StatusText = LocalizationService.Instance["FileTransfer_LocalPathUnavailable"];
            return;
        }

        EnqueueUploads(localPaths, kind);
    }

    private bool CanUpload() => SelectedRemoteRoot is { IsWritable: true } && _connection.IsConnected;

    /// <summary>Enqueues an upload of each local file into the current remote folder. Shared by the
    /// upload button, the send-to-device button, and drag-drop from the desktop.</summary>
    public void EnqueueUploads(IEnumerable<string> localPaths, FileTransferQueueKind kind = FileTransferQueueKind.Upload)
    {
        var root = SelectedRemoteRoot;
        if (root is not { IsWritable: true })
        {
            StatusText = LocalizationService.Instance["FileTransfer_DropTargetReadOnly"];
            return;
        }

        var targetFolder = RemotePath;
        foreach (var localPath in localPaths)
        {
            if (string.IsNullOrWhiteSpace(localPath) || !File.Exists(localPath))
                continue;

            var fileName = Path.GetFileName(localPath);
            var remoteFile = CombineRemotePath(targetFolder, fileName);
            var item = TransferQueue.Enqueue(kind, fileName, (progress, ct) =>
                _client.UploadAsync(localPath, root.RootId, remoteFile, progress, ct));

            // Refresh the folder if the completed upload landed in the folder currently on screen.
            item.Completion.Task.ContinueWith(_ =>
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    if (item.State == TransferState.Done
                        && SelectedRemoteRoot?.RootId == root.RootId
                        && RemotePath == targetFolder)
                    {
                        _ = BrowseRemoteAsync();
                    }
                }));
        }

        StatusText = LocalizationService.Instance["FileTransfer_QueuedForUpload"];
    }

    [RelayCommand(CanExecute = nameof(CanDownload))]
    private async Task DownloadAsync()
    {
        if (SelectedRemoteEntry is null || SelectedRemoteEntry.IsDirectory || SelectedRemoteRoot is null) return;

        if (PickDownloadDestinationAsync is null)
        {
            StatusText = LocalizationService.Instance["FileTransfer_SaveDialogUnavailable"];
            return;
        }

        var destination = await PickDownloadDestinationAsync(new FilePickerSaveOptions
        {
            Title = LocalizationService.Instance["FileTransfer_SavePickerTitle"],
            SuggestedFileName = SelectedRemoteEntry.Name,
            ShowOverwritePrompt = true,
        });

        if (destination is null)
            return;

        var localFile = destination.TryGetLocalPath();
        if (string.IsNullOrWhiteSpace(localFile))
        {
            StatusText = LocalizationService.Instance["FileTransfer_SavePathUnavailable"];
            return;
        }

        var root = SelectedRemoteRoot;
        var fileName = SelectedRemoteEntry.Name;
        var remoteFile = CombineRemotePath(RemotePath, fileName);
        TransferQueue.Enqueue(FileTransferQueueKind.Download, fileName, (progress, ct) =>
            _client.DownloadAsync(root.RootId, remoteFile, localFile!, progress, ct));
        StatusText = LocalizationService.Instance["FileTransfer_QueuedForDownload"];
    }

    private bool CanDownload() => SelectedRemoteEntry is { IsDirectory: false } && SelectedRemoteRoot is not null && _connection.IsConnected;

    // ─── Delete (single + multi) ────────────────────────────────────────────────

    /// <summary>
    /// Delegate set by the View to display a confirmation dialog.
    /// Parameters: (title, message, confirmButtonText). Returns true if the user confirmed.
    /// </summary>
    public Func<string, string, string, Task<bool>>? OnConfirmationRequested { get; set; }

    [RelayCommand(CanExecute = nameof(CanDeleteRemote))]
    private async Task DeleteRemoteAsync()
    {
        if (SelectedRemoteRoot is null) return;

        var toDelete = EffectiveSelection();
        if (toDelete.Count == 0) return;

        // Remote deletes are permanent — confirm before touching anything (RemEx-07jx).
        var previewNames = string.Join(", ", toDelete.Take(3).Select(e => e.Name))
            + (toDelete.Count > 3 ? "…" : string.Empty);
        var message = toDelete.Count == 1
            ? string.Format(LocalizationService.Instance["Confirm_DeleteRemote_SingleFormat"], toDelete[0].Name)
            : string.Format(LocalizationService.Instance["Confirm_DeleteRemote_MultipleFormat"], toDelete.Count, previewNames);
        if (OnConfirmationRequested is null
            || !await OnConfirmationRequested(
                LocalizationService.Instance["Confirm_DeleteRemote_Title"],
                message,
                LocalizationService.Instance["Confirm_DeleteRemote_Btn"]))
        {
            return;
        }

        try
        {
            IsLoading = true;
            StatusText = toDelete.Count == 1
                ? string.Format(LocalizationService.Instance["FileTransfer_DeletingFormat"], toDelete[0].Name)
                : string.Format(LocalizationService.Instance["FileTransfer_DeletingMultipleFormat"], toDelete.Count);

            foreach (var entry in toDelete)
            {
                var relativePath = CombineRemotePath(RemotePath, entry.Name);
                await _client.DeleteRemoteAsync(SelectedRemoteRoot.RootId, relativePath, CancellationToken.None);
            }

            StatusText = toDelete.Count == 1
                ? LocalizationService.Instance["FileTransfer_DeleteComplete"]
                : string.Format(LocalizationService.Instance["FileTransfer_DeleteMultipleCompleteFormat"], toDelete.Count);
            ClearSelection();
            await BrowseRemoteAsync();
        }
        catch (Exception ex)
        {
            StatusText = string.Format(LocalizationService.Instance["FileTransfer_DeleteFailedFormat"], ex.Message);
        }
        finally
        {
            IsLoading = false;
        }
    }

    private bool CanDeleteRemote()
    {
        if (SelectedRemoteRoot is not { CanDelete: true }) return false;
        if (IsRenaming || IsCreatingFolder) return false;
        return EffectiveSelection().Count > 0;
    }

    // ─── Copy / Cut / Paste (v3 copy/move) ──────────────────────────────────────

    public bool HasClipboard => _clipboard.Count > 0;

    [RelayCommand(CanExecute = nameof(CanCopySelection))]
    private void CopySelection() => SetClipboard(isMove: false);

    [RelayCommand(CanExecute = nameof(CanCopySelection))]
    private void CutSelection() => SetClipboard(isMove: true);

    private bool CanCopySelection() =>
        SupportsCopyMove && SelectedRemoteRoot is not null && EffectiveSelection().Count > 0;

    private void SetClipboard(bool isMove)
    {
        if (SelectedRemoteRoot is null) return;
        var selection = EffectiveSelection();
        if (selection.Count == 0) return;

        _clipboard.Clear();
        _clipboard.AddRange(selection);
        _clipboardRootId = SelectedRemoteRoot.RootId;
        _clipboardFolder = RemotePath;
        _clipboardIsMove = isMove;
        OnPropertyChanged(nameof(HasClipboard));
        PasteCommand.NotifyCanExecuteChanged();
        StatusText = isMove
            ? string.Format(LocalizationService.Instance["FileTransfer_CutReadyFormat"], selection.Count)
            : string.Format(LocalizationService.Instance["FileTransfer_CopyReadyFormat"], selection.Count);
    }

    [RelayCommand(CanExecute = nameof(CanPaste))]
    private async Task PasteAsync()
    {
        if (SelectedRemoteRoot is null || _clipboardRootId is null || _clipboard.Count == 0) return;

        // Copy/move are host-side single-root operations; a cross-root paste isn't supported (plan §1.2).
        if (!string.Equals(_clipboardRootId, SelectedRemoteRoot.RootId, StringComparison.Ordinal))
        {
            StatusText = LocalizationService.Instance["FileTransfer_PasteCrossRoot"];
            return;
        }

        var rootId = SelectedRemoteRoot.RootId;
        var isMove = _clipboardIsMove;
        var items = _clipboard.ToList();

        try
        {
            IsLoading = true;
            StatusText = isMove
                ? string.Format(LocalizationService.Instance["FileTransfer_MovingFormat"], items.Count)
                : string.Format(LocalizationService.Instance["FileTransfer_CopyingFormat"], items.Count);

            foreach (var entry in items)
            {
                var source = CombineRemotePath(_clipboardFolder, entry.Name);
                var destination = CombineRemotePath(RemotePath, entry.Name);
                if (string.Equals(source, destination, StringComparison.Ordinal))
                    continue; // no-op paste into the same folder

                if (isMove)
                    await _client.MoveRemoteAsync(rootId, source, destination, overwrite: false, CancellationToken.None);
                else
                    await _client.CopyRemoteAsync(rootId, source, destination, overwrite: false, CancellationToken.None);
            }

            StatusText = isMove
                ? LocalizationService.Instance["FileTransfer_MoveComplete"]
                : LocalizationService.Instance["FileTransfer_CopyComplete"];

            if (isMove)
            {
                _clipboard.Clear();
                OnPropertyChanged(nameof(HasClipboard));
                PasteCommand.NotifyCanExecuteChanged();
            }

            await BrowseRemoteAsync();
        }
        catch (Exception ex)
        {
            StatusText = string.Format(LocalizationService.Instance["FileTransfer_PasteFailedFormat"], ex.Message);
        }
        finally
        {
            IsLoading = false;
        }
    }

    private bool CanPaste() =>
        SupportsCopyMove
        && HasClipboard
        && SelectedRemoteRoot is { IsWritable: true }
        && !IsRenaming
        && !IsCreatingFolder;

    // ─── Rename ──────────────────────────────────────────────────────────────────

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartRenameCommand))]
    [NotifyCanExecuteChangedFor(nameof(DeleteRemoteCommand))]
    [NotifyCanExecuteChangedFor(nameof(PinCurrentFolderCommand))]
    [NotifyCanExecuteChangedFor(nameof(RemoveCurrentRootCommand))]
    [NotifyCanExecuteChangedFor(nameof(NewFolderCommand))]
    [NotifyCanExecuteChangedFor(nameof(PasteCommand))]
    private bool _isRenaming;

    [ObservableProperty]
    private string _renameInputText = string.Empty;

    [RelayCommand(CanExecute = nameof(CanStartRename))]
    private void StartRename()
    {
        if (SelectedRemoteEntry is null) return;
        RenameInputText = SelectedRemoteEntry.Name;
        IsRenaming = true;
    }

    private bool CanStartRename() =>
        SelectedRemoteEntry is { Name: not ".." }
        && SelectedRemoteRoot is { CanRename: true }
        && !IsRenaming
        && !IsCreatingFolder;

    [RelayCommand]
    private async Task ConfirmRenameAsync()
    {
        if (SelectedRemoteEntry is null || SelectedRemoteRoot is null) return;

        var newName = RenameInputText.Trim();
        if (string.IsNullOrWhiteSpace(newName) || newName == SelectedRemoteEntry.Name)
        {
            IsRenaming = false;
            return;
        }

        try
        {
            IsLoading = true;
            StatusText = string.Format(LocalizationService.Instance["FileTransfer_RenamingFormat"], SelectedRemoteEntry.Name, newName);
            var relativePath = CombineRemotePath(RemotePath, SelectedRemoteEntry.Name);
            await _client.RenameRemoteAsync(SelectedRemoteRoot.RootId, relativePath, newName, CancellationToken.None);
            StatusText = LocalizationService.Instance["FileTransfer_RenameComplete"];
            IsRenaming = false;
            ClearSelection();
            await BrowseRemoteAsync();
        }
        catch (Exception ex)
        {
            StatusText = string.Format(LocalizationService.Instance["FileTransfer_RenameFailedFormat"], ex.Message);
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private void CancelRename()
    {
        IsRenaming = false;
        RenameInputText = string.Empty;
    }

    // ─── New folder (mkdir) ───────────────────────────────────────────────────────

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(NewFolderCommand))]
    [NotifyCanExecuteChangedFor(nameof(StartRenameCommand))]
    private bool _isCreatingFolder;

    [ObservableProperty]
    private string _newFolderName = string.Empty;

    [RelayCommand(CanExecute = nameof(CanCreateFolder))]
    private void NewFolder()
    {
        NewFolderName = string.Empty;
        IsCreatingFolder = true;
    }

    private bool CanCreateFolder() =>
        SupportsMkdir
        && SelectedRemoteRoot is { IsWritable: true }
        && !IsRenaming
        && !IsCreatingFolder;

    [RelayCommand]
    private async Task ConfirmNewFolderAsync()
    {
        if (SelectedRemoteRoot is null) return;

        var name = NewFolderName.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            IsCreatingFolder = false;
            return;
        }

        try
        {
            IsLoading = true;
            StatusText = string.Format(LocalizationService.Instance["FileTransfer_CreatingFolderFormat"], name);
            await _client.MakeDirectoryRemoteAsync(SelectedRemoteRoot.RootId, RelativeCurrentFolder(), name, CancellationToken.None);
            StatusText = LocalizationService.Instance["FileTransfer_CreateFolderComplete"];
            IsCreatingFolder = false;
            await BrowseRemoteAsync();
        }
        catch (Exception ex)
        {
            StatusText = string.Format(LocalizationService.Instance["FileTransfer_CreateFolderFailedFormat"], ex.Message);
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private void CancelNewFolder()
    {
        IsCreatingFolder = false;
        NewFolderName = string.Empty;
    }

    // ─── Properties + thumbnail pane ───────────────────────────────────────────────

    [ObservableProperty]
    private bool _isPropertiesPaneOpen;

    [ObservableProperty]
    private FileMetadataResponse? _selectedMetadata;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasThumbnail))]
    private string? _selectedThumbnailBase64;

    public bool HasThumbnail => !string.IsNullOrWhiteSpace(SelectedThumbnailBase64);

    [RelayCommand(CanExecute = nameof(CanShowProperties))]
    private async Task ShowPropertiesAsync()
    {
        if (SelectedRemoteEntry is not { Name: not ".." } entry || SelectedRemoteRoot is null)
            return;

        IsPropertiesPaneOpen = true;
        SelectedMetadata = null;
        SelectedThumbnailBase64 = null;

        var relativePath = CombineRemotePath(RemotePath, entry.Name);

        try
        {
            SelectedMetadata = await _client.GetMetadataRemoteAsync(SelectedRemoteRoot.RootId, relativePath, CancellationToken.None);
        }
        catch (Exception ex)
        {
            StatusText = string.Format(LocalizationService.Instance["FileTransfer_PropertiesFailedFormat"], ex.Message);
        }

        if (!entry.IsDirectory && IsImageFile(entry.Name))
        {
            try
            {
                SelectedThumbnailBase64 = await _client.GetThumbnailRemoteAsync(
                    SelectedRemoteRoot.RootId, relativePath, FileTransferLimits.ThumbnailDefaultMaxDim, CancellationToken.None);
            }
            catch
            {
                // A missing/undecodable thumbnail is non-fatal — the pane still shows metadata.
                SelectedThumbnailBase64 = null;
            }
        }
    }

    private bool CanShowProperties() =>
        _client.SupportsV3 && SelectedRemoteEntry is { Name: not ".." } && SelectedRemoteRoot is not null && _connection.IsConnected;

    [RelayCommand]
    private void CloseProperties()
    {
        IsPropertiesPaneOpen = false;
        SelectedMetadata = null;
        SelectedThumbnailBase64 = null;
    }

    // ─── Full-device volumes (informational; consent-gated) ─────────────────────────

    public ObservableCollection<FileVolumeInfo> Volumes { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasVolumes))]
    private bool _fullBrowseGranted;

    public bool HasVolumes => Volumes.Count > 0;

    [RelayCommand(CanExecute = nameof(CanLoadVolumes))]
    private async Task LoadVolumesAsync()
    {
        if (!SupportsFullBrowse || !_connection.IsConnected)
            return;

        try
        {
            IsLoading = true;
            StatusText = LocalizationService.Instance["FileTransfer_VolumesRequesting"];
            var (volumes, granted) = await _client.ListVolumesAsync(CancellationToken.None);
            Volumes.Clear();
            foreach (var volume in volumes)
                Volumes.Add(volume);
            FullBrowseGranted = granted;
            OnPropertyChanged(nameof(HasVolumes));
            StatusText = granted
                ? string.Format(LocalizationService.Instance["FileTransfer_VolumesLoadedFormat"], Volumes.Count)
                : LocalizationService.Instance["FileTransfer_VolumesDenied"];
        }
        catch (Exception ex)
        {
            StatusText = string.Format(LocalizationService.Instance["FileTransfer_VolumesFailedFormat"], ex.Message);
        }
        finally
        {
            IsLoading = false;
        }
    }

    private bool CanLoadVolumes() => SupportsFullBrowse && _connection.IsConnected;

    // ─── SHA-256 verification (kept from 2.0) ───────────────────────────────────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasVerifiedHash))]
    private string? _verifiedHash;

    public bool HasVerifiedHash => VerifiedHash is not null;

    [RelayCommand(CanExecute = nameof(CanVerifyHash))]
    private async Task VerifyHashAsync()
    {
        if (SelectedRemoteEntry is null || SelectedRemoteRoot is null) return;

        try
        {
            IsLoading = true;
            VerifiedHash = null;
            StatusText = string.Format(LocalizationService.Instance["FileTransfer_HashComputingFormat"], SelectedRemoteEntry.Name);
            var relativePath = CombineRemotePath(RemotePath, SelectedRemoteEntry.Name);
            var hash = await _client.VerifyRemoteHashAsync(SelectedRemoteRoot.RootId, relativePath, CancellationToken.None);
            VerifiedHash = hash;
            StatusText = LocalizationService.Instance["FileTransfer_HashComplete"];
        }
        catch (Exception ex)
        {
            StatusText = string.Format(LocalizationService.Instance["FileTransfer_HashFailedFormat"], ex.Message);
        }
        finally
        {
            IsLoading = false;
        }
    }

    private bool CanVerifyHash() =>
        SelectedRemoteEntry is { IsDirectory: false }
        && SelectedRemoteRoot is not null
        && _connection.IsConnected;

    // ─── Root management (kept from 2.0) ─────────────────────────────────────────────

    [RelayCommand(CanExecute = nameof(CanPinCurrentFolder))]
    private async Task PinCurrentFolderAsync()
    {
        if (SelectedRemoteRoot is null || RemotePath == "/" || RemotePath == "\\") return;

        try
        {
            IsLoading = true;
            StatusText = LocalizationService.Instance["FileTransfer_PinningFolder"];
            var updatedRoots = await _client.AddRemoteRootAsync(SelectedRemoteRoot.RootId, RemotePath, CancellationToken.None);
            ReplaceRemoteRoots(updatedRoots);
            StatusText = LocalizationService.Instance["FileTransfer_PinComplete"];
        }
        catch (Exception ex)
        {
            StatusText = string.Format(LocalizationService.Instance["FileTransfer_PinFailedFormat"], ex.Message);
        }
        finally
        {
            IsLoading = false;
        }
    }

    private bool CanPinCurrentFolder() =>
        SelectedRemoteRoot is not null
        && RemotePath != "/" && RemotePath != "\\"
        && !IsRenaming;

    [RelayCommand(CanExecute = nameof(CanRemoveCurrentRoot))]
    private async Task RemoveCurrentRootAsync()
    {
        if (SelectedRemoteRoot is null) return;

        try
        {
            IsLoading = true;
            StatusText = string.Format(LocalizationService.Instance["FileTransfer_RemovingRootFormat"], SelectedRemoteRoot.DisplayName);
            var updatedRoots = await _client.RemoveRemoteRootAsync(SelectedRemoteRoot.RootId, CancellationToken.None);
            ReplaceRemoteRoots(updatedRoots);
            StatusText = LocalizationService.Instance["FileTransfer_RemoveRootComplete"];
        }
        catch (Exception ex)
        {
            StatusText = string.Format(LocalizationService.Instance["FileTransfer_RemoveRootFailedFormat"], ex.Message);
        }
        finally
        {
            IsLoading = false;
        }
    }

    private bool CanRemoveCurrentRoot() =>
        SelectedRemoteRoot is { CanRemoveRoot: true }
        && !IsRenaming;

    private void ReplaceRemoteRoots(IReadOnlyList<FileSharedRoot> roots)
    {
        var previousRootId = SelectedRemoteRoot?.RootId;
        RemoteRoots.Clear();
        foreach (var root in roots.OrderBy(r => r.DisplayName))
            RemoteRoots.Add(root);
        SelectedRemoteRoot = RemoteRoots.FirstOrDefault(r => r.RootId == previousRootId)
            ?? RemoteRoots.FirstOrDefault(r => r.IsWritable)
            ?? RemoteRoots.FirstOrDefault();
    }

    // ─── Lifecycle ───────────────────────────────────────────────────────────────────

    public void Dispose()
    {
        _connection.PropertyChanged -= OnConnectionPropertyChanged;
        TransferQueue.Changed -= OnQueueChanged;
        TransferQueue.ItemCompleted -= OnTransferCompleted;
        _searchCts?.Cancel();
        _client.Dispose();
    }

    private void OnConnectionPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ConnectionViewModel.IsConnected))
            return;

        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            UploadCommand.NotifyCanExecuteChanged();
            SendFileCommand.NotifyCanExecuteChanged();
            DownloadCommand.NotifyCanExecuteChanged();
            VerifyHashCommand.NotifyCanExecuteChanged();
            SearchCommand.NotifyCanExecuteChanged();
            LoadVolumesCommand.NotifyCanExecuteChanged();

            if (_connection.IsConnected && RemoteRoots.Count == 0)
                _ = LoadRemoteRootsAsync();
        });
    }

    // ─── Path helpers ─────────────────────────────────────────────────────────────────

    /// <summary>The current folder as a root-relative path the host understands (empty string == root).</summary>
    private string RelativeCurrentFolder() =>
        RemotePath is "/" or "\\" or "" ? string.Empty : RemotePath.Replace('\\', '/').TrimStart('/');

    private static string CombineRemotePath(string currentPath, string childName)
    {
        var normalizedCurrentPath = string.IsNullOrWhiteSpace(currentPath) ? "/" : currentPath.Replace('\\', '/');
        return normalizedCurrentPath == "/"
            ? childName
            : $"{normalizedCurrentPath.TrimEnd('/')}/{childName}";
    }

    private static string GetParentRemotePath(string currentPath)
    {
        var normalizedPath = string.IsNullOrWhiteSpace(currentPath) ? "/" : currentPath.Replace('\\', '/').TrimEnd('/');
        if (normalizedPath.Length == 0 || normalizedPath == "/")
            return "/";

        var separatorIndex = normalizedPath.LastIndexOf('/');
        return separatorIndex <= 0 ? "/" : normalizedPath[..separatorIndex];
    }

    private static bool IsImageFile(string name)
    {
        var ext = Path.GetExtension(name).ToLowerInvariant();
        return ext is ".png" or ".jpg" or ".jpeg" or ".gif" or ".bmp" or ".webp";
    }

    // ─── Pure helpers (unit-tested directly) ───────────────────────────────────────────

    /// <summary>
    /// Sorts a folder listing for display: the "up" placeholder ("..") always first, then directories
    /// before files, then by the chosen field/direction. Pure and side-effect free for unit testing.
    /// </summary>
    public static IReadOnlyList<FileEntry> SortEntries(IEnumerable<FileEntry> entries, FileSortField field, bool descending)
    {
        var list = entries.ToList();
        var parent = list.Where(e => e.Name == "..").ToList();
        var rest = list.Where(e => e.Name != "..");

        IOrderedEnumerable<FileEntry> ordered = rest.OrderByDescending(e => e.IsDirectory);
        ordered = field switch
        {
            FileSortField.Size => descending
                ? ordered.ThenByDescending(e => e.SizeBytes)
                : ordered.ThenBy(e => e.SizeBytes),
            FileSortField.Modified => descending
                ? ordered.ThenByDescending(e => e.ModifiedUnixMs)
                : ordered.ThenBy(e => e.ModifiedUnixMs),
            _ => descending
                ? ordered.ThenByDescending(e => e.Name, StringComparer.OrdinalIgnoreCase)
                : ordered.ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase),
        };

        return parent.Concat(ordered).ToList();
    }

    /// <summary>
    /// Builds the tappable breadcrumb trail for a remote path. The first segment is the root; each
    /// subsequent segment carries the absolute remote path it navigates to. Pure for unit testing.
    /// </summary>
    public static IReadOnlyList<BreadcrumbSegment> BuildBreadcrumbSegments(string path, string rootLabel)
    {
        var segments = new List<BreadcrumbSegment> { new(rootLabel, "/") };

        var normalized = string.IsNullOrWhiteSpace(path) ? "/" : path.Replace('\\', '/').Trim('/');
        if (normalized.Length == 0)
            return segments;

        var parts = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var running = string.Empty;
        foreach (var part in parts)
        {
            running = running.Length == 0 ? part : $"{running}/{part}";
            segments.Add(new BreadcrumbSegment(part, "/" + running));
        }

        return segments;
    }
}
