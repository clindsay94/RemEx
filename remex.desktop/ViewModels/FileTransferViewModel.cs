using System.ComponentModel;
using System.Globalization;
using System.Collections.ObjectModel;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Remex.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
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
    private readonly ILogger<FileTransferViewModel> _logger;
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

    /// <summary>Wired by the view to a native folder picker (for folder upload / folder download).</summary>
    public Func<FolderPickerOpenOptions, Task<IReadOnlyList<IStorageFolder>>>? PickLocalFolderAsync { get; set; }

    /// <summary>Wired by the view so the VM can select every entry in the list for a multi-select op.</summary>
    public Action? SelectAllEntries { get; set; }

    public FileTransferViewModel(
        ConnectionViewModel connection,
        ILogger<FileTransferViewModel>? logger = null,
        ILogger<FileTransferQueue>? queueLogger = null)
    {
        _connection = connection;
        _logger = logger ?? NullLogger<FileTransferViewModel>.Instance;
        _client = new FileTransferClient(connection);

        // Passed explicitly, for the reason ShellViewModel already documents one level up: this view
        // model is hand-constructed rather than DI-resolved, so a constructor default silently
        // degrades to no logger and discards every transfer-failure diagnostic (RemEx-6tvh).
        TransferQueue = new FileTransferQueue(post: null, logger: queueLogger);
        TransferQueue.Changed += OnQueueChanged;
        TransferQueue.ItemCompleted += OnTransferCompleted;
        _connection.PropertyChanged += OnConnectionPropertyChanged;
        LocalizationService.Instance.PropertyChanged += OnLocaleChanged;

        // These two properties are created once and only ever mutated, never reassigned, so
        // CollectionChanged really is a complete signal that their counts moved. That is worth
        // stating because it is the assumption the empty states rest on: had either been an
        // [ObservableProperty] that gets a fresh instance assigned, this subscription would follow
        // the old instance and the empty state would silently stop updating.
        RemoteEntries.CollectionChanged += (_, _) => OnPropertyChanged(nameof(ShowEmptyFolder));
        SearchResults.CollectionChanged += (_, _) => OnPropertyChanged(nameof(ShowNoSearchResults));
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
        CancelAllTransfersCommand.NotifyCanExecuteChanged();
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

    /// <summary>Stops everything still in flight or waiting. Enabled only while there is something to stop.</summary>
    [RelayCommand(CanExecute = nameof(HasCancellableTransfers))]
    private void CancelAllTransfers() => TransferQueue.CancelAll();

    private bool HasCancellableTransfers() => TransferQueue.Items.Any(item => !item.IsTerminal);

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

    /// <summary>
    /// True when the host answers <c>file_manifest_request</c>, i.e. a remote FOLDER can be downloaded.
    /// Folder UPLOAD deliberately does not read this: the client walks its own tree and sends ordinary
    /// per-file uploads, which every v2 host already accepts.
    /// </summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DownloadFolderCommand))]
    private bool _supportsFolderTransfer;

    private void RefreshCapabilityFlags()
    {
        SupportsCopyMove = _client.SupportsOp(FileManageOperations.Copy) && _client.SupportsOp(FileManageOperations.Move);
        SupportsMkdir = _client.SupportsOp(FileManageOperations.Mkdir);
        SupportsSearch = _client.SupportsV3 && _client.SupportsOp("search");
        SupportsFullBrowse = _client.SupportsFullBrowse;
        SupportsFolderTransfer = _client.SupportsV3 && _client.SupportsOp("manifest");
    }

    // ─── Roots ────────────────────────────────────────────────────────────────

    public ObservableCollection<FileSharedRoot> RemoteRoots { get; } = new();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UploadCommand))]
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
        catch (FileTransferHostException ex)
        {
            _logger.LogWarning(ex, "Listing the phone's shared folders failed - the phone refused it");
            // The phone answers these with copy a user can act on, so show it verbatim.
            StatusText = ex.HostMessage;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Listing the phone's shared folders failed");
            StatusText = LocalizationService.Instance["FileTransfer_SharedFoldersUnavailableFormat"];
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
            _logger.LogWarning(ex, "Browsing the connected device failed");
            StatusText = LocalizationService.Instance["FileTransfer_BrowseErrorFormat"];
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
    [NotifyCanExecuteChangedFor(nameof(DownloadFolderCommand))]
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
    [NotifyPropertyChangedFor(nameof(ShowEmptyFolder))]
    [NotifyPropertyChangedFor(nameof(ShowNoSearchResults))]
    private bool _isSearchActive;

    [ObservableProperty]
    private bool _searchTruncated;

    public ObservableCollection<FileSearchEntry> SearchResults { get; } = new();

    public bool ShowSearchResults => IsSearchActive;

    /// <summary>True when the current folder is genuinely empty, rather than still arriving.</summary>
    /// <remarks>
    /// The <see cref="IsLoading"/> term is not defensive padding — without it this flashes "this
    /// folder is empty" on every navigation, in the gap before the listing arrives, which looks
    /// exactly like a bug to the user. (RemEx-n69m.)
    /// </remarks>
    public bool ShowEmptyFolder => !IsLoading && !ShowSearchResults && RemoteEntries.Count == 0;

    /// <summary>True when a search finished and matched nothing.</summary>
    public bool ShowNoSearchResults => !IsLoading && ShowSearchResults && SearchResults.Count == 0;

    /// <summary>
    /// Set only while <see cref="ClearSearch"/> resets <see cref="SearchQuery"/> to empty. Clearing
    /// the query raises this view model's own change callback, which would call back into
    /// <see cref="ClearSearch"/> and run the teardown twice; the flag makes that callback a no-op.
    /// All three call sites run on the UI thread, so a plain field is sufficient here.
    /// </summary>
    private bool _isClearingSearch;

    partial void OnSearchQueryChanged(string value)
    {
        if (_isClearingSearch)
            return;

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
        catch (OperationCanceledException) { /* the user navigated away or started another search; superseding one is normal */ }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Searching the connected device failed");
            StatusText = LocalizationService.Instance["FileTransfer_SearchFailedFormat"];
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
        {
            _isClearingSearch = true;
            try
            {
                SearchQuery = string.Empty;
            }
            finally
            {
                _isClearingSearch = false;
            }
        }
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
    [NotifyPropertyChangedFor(nameof(ShowEmptyFolder))]
    [NotifyPropertyChangedFor(nameof(ShowNoSearchResults))]
    private bool _isLoading;

    [ObservableProperty]
    private string _statusText = string.Empty;

    [RelayCommand(CanExecute = nameof(CanUpload))]
    private async Task UploadAsync()
    {
        if (SelectedRemoteRoot is null) return;
        await PickAndEnqueueUploadsAsync(FileTransferQueueKind.Upload);
    }

    // SendFileAsync REMOVED with its button (RemEx-74kfg). It was UploadAsync with a different queue
    // label: same picker, same PickAndEnqueueUploadsAsync, same upload into the host's own shared root
    // over loopback. Nothing was ever offered to a phone, and when the picked file already lived in a
    // shared root the host opened that same path for write while the client held it for read, failing
    // the transfer with a sharing violation that named a local file.
    //
    // FileTransferQueueKind.SendToPhone STAYS — it is not dead. DropZoneResolver still resolves the
    // upper drop zone to it, so drag-and-drop reaches this behaviour even with the button gone. That
    // is deliberately left alone here rather than quietly collapsed: the two-zone drop is a separate
    // surface and a separate decision.

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

    // ─── Folder transfer (RemEx-q3twg) ─────────────────────────────────────────
    //
    // Both directions end in the SAME per-file queue items an individual transfer produces. The only
    // difference is where the list of files comes from: the host's paged manifest going down, a local
    // directory walk going up. Nothing below owns bytes, progress, resume or conflicts — that all stays
    // with the queue, which is the entire reason a folder transfer is cheap to have.

    /// <summary>
    /// Downloads the selected remote FOLDER: pages the host's manifest, recreates the directory shape
    /// locally, then enqueues one ordinary download per file.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanDownloadFolder))]
    private async Task DownloadFolderAsync()
    {
        var root = SelectedRemoteRoot;
        var entry = SelectedRemoteEntry;
        if (root is null || entry is not { IsDirectory: true })
            return;

        if (PickLocalFolderAsync is null)
        {
            StatusText = LocalizationService.Instance["FileTransfer_PickerUnavailable"];
            return;
        }

        var chosen = await PickLocalFolderAsync(new FolderPickerOpenOptions
        {
            Title = LocalizationService.Instance["FileTransfer_FolderDownloadPickerTitle"],
            AllowMultiple = false,
        });

        var destinationRoot = chosen.FirstOrDefault()?.TryGetLocalPath();
        if (string.IsNullOrWhiteSpace(destinationRoot))
            return;

        // The picked folder is the PARENT: a folder named "photos" lands as <chosen>/photos, matching
        // what every file manager does on a drag-drop and keeping two downloads of different folders
        // from merging into one another.
        var localRoot = Path.Combine(destinationRoot!, SanitizeLocalName(entry.Name));
        var remoteFolder = CombineRemotePath(RemotePath, entry.Name);

        RemoteSubtree subtree;
        IsLoading = true;
        StatusText = LocalizationService.Instance["FileTransfer_FolderScanning"];
        try
        {
            subtree = await _client.EnumerateRemoteSubtreeAsync(root.RootId, remoteFolder, null, CancellationToken.None);
        }
        catch (Exception ex)
        {
            StatusText = string.Format(
                CultureInfo.CurrentCulture, LocalizationService.Instance["FileTransfer_FolderScanFailed"], ex.Message);
            return;
        }
        finally
        {
            IsLoading = false;
        }

        var queued = EnqueueSubtreeDownloads(root, subtree, localRoot);
        if (queued == 0)
        {
            StatusText = LocalizationService.Instance["FileTransfer_FolderEmpty"];
            return;
        }

        // A truncated manifest means the host stopped describing the folder, so what was just queued is
        // NOT the folder. Saying so is the whole point — silently transferring a prefix looks identical
        // to success right up until something is missing.
        StatusText = subtree.Truncated
            ? string.Format(CultureInfo.CurrentCulture, LocalizationService.Instance["FileTransfer_FolderQueuedTruncated"], queued)
            : string.Format(CultureInfo.CurrentCulture, LocalizationService.Instance["FileTransfer_FolderQueuedForDownload"], queued);
    }

    private bool CanDownloadFolder()
        => SupportsFolderTransfer && SelectedRemoteEntry is { IsDirectory: true } && SelectedRemoteRoot is not null && _connection.IsConnected;

    /// <summary>
    /// Creates the local directory shape and enqueues one download per file. Returns how many transfers
    /// were queued.
    /// </summary>
    private int EnqueueSubtreeDownloads(FileSharedRoot root, RemoteSubtree subtree, string localRoot)
    {
        // Directories are created up front, empty ones included. Leaving them to the file downloads
        // would quietly drop every empty folder — the one part of the shape no file can imply.
        foreach (var directory in subtree.Entries.Where(candidate => candidate.IsDirectory))
        {
            var relative = ToSafeLocalRelativePath(subtree.ToDestinationRelative(directory));
            if (relative is null)
                continue;

            try
            {
                Directory.CreateDirectory(Path.Combine(localRoot, relative));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A folder that cannot be made will fail its own files' downloads with a real message;
                // aborting the whole fan-out here would lose the ones that are fine.
            }
        }

        var queued = 0;
        foreach (var file in subtree.Files)
        {
            var relative = ToSafeLocalRelativePath(subtree.ToDestinationRelative(file));
            if (relative is null)
                continue;

            var localPath = Path.Combine(localRoot, relative);
            var parent = Path.GetDirectoryName(localPath);
            if (!string.IsNullOrEmpty(parent))
            {
                try
                {
                    Directory.CreateDirectory(parent);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    continue;
                }
            }

            var remotePath = file.RelativePath;
            TransferQueue.Enqueue(FileTransferQueueKind.Download, Path.GetFileName(localPath), (progress, ct) =>
                _client.DownloadAsync(root.RootId, remotePath, localPath, progress, ct));
            queued++;
        }

        return queued;
    }

    /// <summary>
    /// Uploads a local FOLDER into the current remote folder. No manifest is involved — the tree is
    /// right here, so the client walks it directly and enqueues the same per-file uploads.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanUpload))]
    private async Task UploadFolderAsync()
    {
        var root = SelectedRemoteRoot;
        if (root is not { IsWritable: true })
        {
            StatusText = LocalizationService.Instance["FileTransfer_DropTargetReadOnly"];
            return;
        }

        if (PickLocalFolderAsync is null)
        {
            StatusText = LocalizationService.Instance["FileTransfer_PickerUnavailable"];
            return;
        }

        var chosen = await PickLocalFolderAsync(new FolderPickerOpenOptions
        {
            Title = LocalizationService.Instance["FileTransfer_FolderUploadPickerTitle"],
            AllowMultiple = false,
        });

        var localRoot = chosen.FirstOrDefault()?.TryGetLocalPath();
        if (string.IsNullOrWhiteSpace(localRoot) || !Directory.Exists(localRoot))
            return;

        await EnqueueFolderUploadAsync(localRoot!, FileTransferQueueKind.Upload);
    }

    /// <summary>
    /// Enqueues an upload of every file under <paramref name="localRoot"/>, preserving the folder shape
    /// under the current remote folder. Shared by the upload-folder button and folder drag-drop.
    /// </summary>
    public async Task EnqueueFolderUploadAsync(string localRoot, FileTransferQueueKind kind = FileTransferQueueKind.Upload)
    {
        var root = SelectedRemoteRoot;
        if (root is not { IsWritable: true })
        {
            StatusText = LocalizationService.Instance["FileTransfer_DropTargetReadOnly"];
            return;
        }

        List<string> localFiles;
        try
        {
            localFiles = [.. Directory.EnumerateFiles(localRoot, "*", new EnumerationOptions
            {
                RecurseSubdirectories = true,
                // Symlinked directories are not followed, for the same reason the host does not follow
                // them: one loop turns a folder upload into an unbounded one.
                AttributesToSkip = FileAttributes.ReparsePoint,
                IgnoreInaccessible = true,
            })];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusText = string.Format(
                CultureInfo.CurrentCulture, LocalizationService.Instance["FileTransfer_FolderScanFailed"], ex.Message);
            return;
        }

        if (localFiles.Count > FileTransferLimits.ManifestMaxTotalEntries)
        {
            StatusText = LocalizationService.Instance["FileTransfer_FolderTooLarge"];
            return;
        }

        var folderName = SanitizeLocalName(new DirectoryInfo(localRoot).Name);
        var targetFolder = CombineRemotePath(RemotePath, folderName);

        // THE REMOTE DIRECTORIES HAVE TO EXIST BEFORE THE FIRST FILE IS ENQUEUED, and nothing else
        // creates them. The download direction says so out loud - EnqueueSubtreeDownloads calls
        // Directory.CreateDirectory for every entry - but the upload direction had no counterpart,
        // and the two hosts disagree about whether that matters. The PC host creates the parent on
        // write (FileTransferService.EnsureParentDirectory), so a PC destination hid this; the
        // Android host resolves the parent and refuses when it is missing
        // (AndroidFileTransferHost.handleTransferStart, FileHostHandler at the v3 path), so a phone
        // destination failed EVERY file in the folder. Uploading a folder to a phone could not work
        // at all. (RemEx-0xves)
        if (!await EnsureRemoteFolderShapeAsync(root, targetFolder, localRoot, localFiles))
            return;

        var queued = 0;

        foreach (var localPath in localFiles)
        {
            var relative = Path.GetRelativePath(localRoot, localPath).Replace('\\', '/');
            var remoteFile = CombineRemotePath(targetFolder, relative);
            TransferQueue.Enqueue(kind, Path.GetFileName(localPath), (progress, ct) =>
                _client.UploadAsync(localPath, root.RootId, remoteFile, progress, ct));
            queued++;
        }

        StatusText = queued == 0
            ? LocalizationService.Instance["FileTransfer_FolderEmpty"]
            : string.Format(CultureInfo.CurrentCulture, LocalizationService.Instance["FileTransfer_FolderQueuedForUpload"], queued);
    }

    /// <summary>
    /// Creates <paramref name="targetFolder"/> and every subdirectory the upload will write into.
    /// Returns false when the shape could not be made, in which case nothing is enqueued.
    /// </summary>
    /// <remarks>
    /// <para>
    /// SHALLOWEST FIRST. mkdir names a parent and a child, so a nested folder cannot be created
    /// before the folder above it; ordering by depth is what makes one pass enough.
    /// </para>
    /// <para>
    /// AN EXISTING FOLDER IS NOT AN ERROR, and it has to be tolerated here rather than avoided.
    /// The hosts refuse a mkdir onto a name that is already taken - that refusal is right for the
    /// New-folder button, which is a user naming something - but uploading into a folder that is
    /// already there is the ordinary case, not a collision. There is no "create if missing" on the
    /// wire and adding one would be a protocol change, so the refusal is absorbed and the upload
    /// proceeds; if the name is a FILE rather than a folder, the per-file transfers fail with the
    /// host's own message, which is more specific than anything guessed here.
    /// </para>
    /// <para>
    /// A failure to reach the host at all is different, and stops the whole thing: enqueuing several
    /// hundred transfers that are all going to fail is precisely the outcome this feature keeps
    /// producing, and it costs the user a queue they then have to clear.
    /// </para>
    /// </remarks>
    private async Task<bool> EnsureRemoteFolderShapeAsync(
        FileSharedRoot root, string targetFolder, string localRoot, IReadOnlyList<string> localFiles)
    {
        // Empty directories are included deliberately: a file list cannot imply them, and losing
        // them silently is the same shape of bug the download side calls out.
        var localDirectories = Directory.EnumerateDirectories(localRoot, "*", new EnumerationOptions
        {
            RecurseSubdirectories = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
            IgnoreInaccessible = true,
        });

        foreach (var remoteFolder in PlanRemoteFolderCreation(localRoot, localDirectories, localFiles, targetFolder))
        {
            var trimmed = remoteFolder.Trim('/');
            var parent = trimmed.Contains('/') ? trimmed[..trimmed.LastIndexOf('/')] : string.Empty;
            var name = trimmed.Contains('/') ? trimmed[(trimmed.LastIndexOf('/') + 1)..] : trimmed;
            if (name.Length == 0)
                continue;

            try
            {
                await _client.MakeDirectoryRemoteAsync(root.RootId, parent, name, CancellationToken.None);
            }
            catch (FileTransferHostException ex)
            {
                // Already there, read-only, or a file in the way. Only the first is benign, and the
                // host does not distinguish them in a way worth branching on - the per-file transfers
                // report the other two accurately.
                _logger.LogDebug(ex, "Creating remote folder {Folder} was refused; continuing.", remoteFolder);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Creating remote folder {Folder} failed.", remoteFolder);
                StatusText = string.Format(
                    CultureInfo.CurrentCulture, LocalizationService.Instance["FileTransfer_FolderScanFailed"], ex.Message);
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Works out which remote folders a folder upload needs, and in what order to ask for them.
    /// Returns full remote paths, shallowest first.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Separated from the sending so it can be tested: the view model itself needs a live client and
    /// connection, and the part with any judgement in it is this one.
    /// </para>
    /// <para>
    /// ORDER IS THE CONTRACT. mkdir names a parent and a child, so <c>a/b/c</c> is unaskable until
    /// <c>a/b</c> exists. Sorting by depth is what lets a single forward pass work regardless of the
    /// order the local walk happened to produce, and the ordinal tiebreak only exists so the sequence
    /// is stable enough to assert on.
    /// </para>
    /// <para>
    /// Both sources are unioned rather than either taken alone. The directory walk is what preserves
    /// EMPTY folders, which no file can imply; the file parents are what survive a directory the walk
    /// could not read but whose files it still reached. Names are compared case-insensitively because
    /// the destinations are Windows and SAF, neither of which would treat two casings as two folders.
    /// </para>
    /// </remarks>
    internal static IReadOnlyList<string> PlanRemoteFolderCreation(
        string localRoot,
        IEnumerable<string> localDirectories,
        IEnumerable<string> localFiles,
        string targetFolder)
    {
        var relativeDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // EVERY ANCESTOR, not just the folder itself. Neither source is guaranteed to name the whole
        // chain - a walk may skip an unreadable level, and a file only ever names its immediate
        // parent - and mkdir cannot create "2024/may" while "2024" is missing. Adding the ancestors
        // here is what makes the single ordered pass below sufficient.
        void AddWithAncestors(string relative)
        {
            var path = relative.Replace('\\', '/').Trim('/');
            while (path.Length > 0 && path != ".")
            {
                relativeDirectories.Add(path);
                var cut = path.LastIndexOf('/');
                path = cut < 0 ? string.Empty : path[..cut];
            }
        }

        foreach (var directory in localDirectories)
            AddWithAncestors(Path.GetRelativePath(localRoot, directory));

        foreach (var localPath in localFiles)
        {
            var relative = Path.GetRelativePath(localRoot, localPath).Replace('\\', '/').Trim('/');
            var cut = relative.LastIndexOf('/');
            if (cut > 0)
                AddWithAncestors(relative[..cut]);
        }

        return relativeDirectories
            .Select(path => CombineRemotePath(targetFolder, path))
            .Prepend(targetFolder)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path.Trim('/').Count(character => character == '/'))
            .ThenBy(path => path, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// Turns a host-supplied subtree-relative path into one that is safe to combine with a local folder,
    /// or null when it is not.
    /// </summary>
    /// <remarks>
    /// <b>THE HOST'S PATHS ARE INPUT, NOT TRUTH.</b> A folder download is the one flow that takes remote
    /// strings and writes to arbitrary local paths built from them, so <c>..</c>, a rooted path, or a
    /// drive-qualified segment must be refused here rather than reaching <c>Path.Combine</c> —
    /// which would happily let a rooted segment discard the destination folder entirely.
    /// </remarks>
    private static string? ToSafeLocalRelativePath(string subtreeRelative)
    {
        if (string.IsNullOrWhiteSpace(subtreeRelative))
            return null;

        var segments = subtreeRelative.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
            return null;

        foreach (var segment in segments)
        {
            if (segment is "." or "..")
                return null;
            if (Path.IsPathRooted(segment) || segment.Contains(':', StringComparison.Ordinal))
                return null;
            if (segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                return null;
        }

        return Path.Combine(segments);
    }

    /// <summary>Folder name usable as a local directory, falling back when the remote name is not.</summary>
    private static string SanitizeLocalName(string name)
    {
        var cleaned = string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Trim(' ', '.');
        return string.IsNullOrEmpty(cleaned) ? "folder" : cleaned;
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
            _logger.LogWarning(ex, "Deleting an item on the connected device failed");
            StatusText = LocalizationService.Instance["FileTransfer_DeleteFailedFormat"];
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
        catch (FileTransferHostException ex)
        {
            _logger.LogWarning(ex, "Pasting the clipboard selection failed - the phone refused it");
            // The phone answers these with copy a user can act on, so show it verbatim.
            StatusText = ex.HostMessage;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Pasting the clipboard selection failed");
            StatusText = LocalizationService.Instance["FileTransfer_PasteFailedFormat"];
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
            _logger.LogWarning(ex, "Renaming an item on the connected device failed");
            StatusText = LocalizationService.Instance["FileTransfer_RenameFailedFormat"];
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
        catch (FileTransferHostException ex)
        {
            _logger.LogWarning(ex, "Creating a folder on the phone failed - the phone refused it");
            // The phone answers these with copy a user can act on, so show it verbatim.
            StatusText = ex.HostMessage;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Creating a folder on the phone failed");
            StatusText = LocalizationService.Instance["FileTransfer_CreateFolderFailedFormat"];
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
        catch (FileTransferHostException ex)
        {
            _logger.LogWarning(ex, "Loading item details from the phone failed - the phone refused it");
            // The phone answers these with copy a user can act on, so show it verbatim.
            StatusText = ex.HostMessage;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Loading item details from the phone failed");
            StatusText = LocalizationService.Instance["FileTransfer_PropertiesFailedFormat"];
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
            var (volumes, granted, denyReason) = await _client.ListVolumesAsync(CancellationToken.None);
            Volumes.Clear();
            foreach (var volume in volumes)
                Volumes.Add(volume);
            FullBrowseGranted = granted;
            OnPropertyChanged(nameof(HasVolumes));
            // A REFUSAL NOBODY WAS ASKED FOR READS DIFFERENTLY (RemEx-jc4q). "Not granted" is true of
            // both and useful for only one: the peer being unreachable is the case with an action
            // attached, and it used to arrive as the same flat no.
            StatusText = VolumesResponseClassifier.Classify(granted, denyReason, errorMessage: null) switch
            {
                VolumesOutcome.Granted => string.Format(
                    LocalizationService.Instance["FileTransfer_VolumesLoadedFormat"], Volumes.Count),
                VolumesOutcome.PeerUnreachable => LocalizationService.Instance["FileTransfer_VolumesPeerUnreachable"],
                _ => LocalizationService.Instance["FileTransfer_VolumesDenied"],
            };
        }
        catch (FileTransferHostException ex)
        {
            _logger.LogWarning(ex, "Listing the phone's drives failed - the phone refused it");
            // The phone answers these with copy a user can act on, so show it verbatim.
            StatusText = ex.HostMessage;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Listing the phone's drives failed");
            StatusText = LocalizationService.Instance["FileTransfer_VolumesFailedFormat"];
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
            _logger.LogWarning(ex, "Verifying a transferred file hash failed");
            StatusText = LocalizationService.Instance["FileTransfer_HashFailedFormat"];
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
        catch (FileTransferHostException ex)
        {
            _logger.LogWarning(ex, "Pinning the current folder failed - the phone refused it");
            // The phone answers these with copy a user can act on, so show it verbatim.
            StatusText = ex.HostMessage;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Pinning the current folder failed");
            StatusText = LocalizationService.Instance["FileTransfer_PinFailedFormat"];
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
        catch (FileTransferHostException ex)
        {
            _logger.LogWarning(ex, "Removing the shared folder failed - the phone refused it");
            // The phone answers these with copy a user can act on, so show it verbatim.
            StatusText = ex.HostMessage;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Removing the shared folder failed");
            StatusText = LocalizationService.Instance["FileTransfer_RemoveRootFailedFormat"];
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

    /// <summary>
    /// <see cref="RemoteRootHint"/> resolves its text from <see cref="LocalizationService"/> at
    /// get-time, so a language switch changes what it would return without anything on this
    /// view-model changing - and therefore without a notification. Re-raise it explicitly.
    /// </summary>
    private void OnLocaleChanged(object? sender, PropertyChangedEventArgs e) =>
        Dispatcher.UIThread.Post(() => OnPropertyChanged(nameof(RemoteRootHint)));

    public void Dispose()
    {
        // Not optional: LocalizationService.Instance is a process-lifetime singleton, so staying
        // subscribed would pin this view-model for the life of the app.
        LocalizationService.Instance.PropertyChanged -= OnLocaleChanged;

        _connection.PropertyChanged -= OnConnectionPropertyChanged;
        TransferQueue.Changed -= OnQueueChanged;
        TransferQueue.Dispose();
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
            DownloadCommand.NotifyCanExecuteChanged();
            DownloadFolderCommand.NotifyCanExecuteChanged();
            UploadFolderCommand.NotifyCanExecuteChanged();
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
