using System.ComponentModel;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Remex.Core.Models;
using Remex.Core.Validation;
using Remex.Desktop.Services;
using Remex.Desktop.Services.FilePreview;
using Remex.Desktop.Services.FileTransfer;

namespace Remex.Desktop.ViewModels;

/// <summary>What is being dragged out of the file list: entries from one folder on one device.</summary>
public sealed record FileDragPayload(FileSourceOption Source, string RootId, string Folder, IReadOnlyList<FileEntry> Entries);

/// <summary>
/// The explorer half of the File Transfer screen (2026-10-08 redesign): the folder tree, the preview pane,
/// thumbnails, and moving files between devices. Kept in its own file so the transfer, clipboard and conflict logic in
/// <c>FileTransferViewModel.cs</c> is reused as it is rather than grown.
/// </summary>
public sealed partial class FileTransferViewModel
{
    /// <summary>The folder tree: This PC and each connected phone.</summary>
    public FileTreeViewModel Tree { get; }

    /// <summary>The preview and details pane on the right.</summary>
    public FilePreviewViewModel Preview { get; }

    /// <summary>Thumbnails for image rows in the list and grid.</summary>
    public ThumbnailCache Thumbnails { get; } = new();

    /// <summary>Puts text on the clipboard. Wired by the view (it owns the top level), and shared with the preview pane.</summary>
    public Func<string, Task>? CopyText
    {
        get => Preview.CopyText;
        set => Preview.CopyText = value;
    }

    /// <summary>This PC's shared folders with their paths on disk. A seam: tests supply their own; null until first needed.</summary>
    internal Func<Task<IReadOnlyList<FileTransferRootConfiguration>>>? LoadPcRoots { get; set; }

    private IReadOnlyList<FileTransferRootConfiguration> _pcRootPaths = [];

    /// <summary>A separate connection per phone the TREE lists, so both devices can be listed at once.</summary>
    private readonly Dictionary<string, (IPhoneFileConnection Connection, FileTransferClient Client)> _treePhones = new();

    /// <summary>Where each device was last browsed, by <see cref="SourceKey"/>.</summary>
    private readonly Dictionary<string, (string RootId, string Folder)> _lastFolder = new();

    /// <summary>Where a pending device or shared-folder switch should land once its roots are listed.</summary>
    private (string RootId, string Path)? _pendingNavigation;

    /// <summary>Whether the preview pane is open. Space toggles it.</summary>
    [ObservableProperty]
    private bool _isPreviewPaneOpen = true;

    [RelayCommand]
    private void TogglePreviewPane() => IsPreviewPaneOpen = !IsPreviewPaneOpen;

    /// <summary>The device the screen is showing, in words, for the start of the breadcrumbs.</summary>
    public string CurrentDeviceLabel => FileTreeViewModel.DeviceLabel(SelectedSource ?? FileSourceOption.ThisPc);

    private static string SourceKey(FileSourceOption source) => source.ClientId ?? "this-pc";

    /// <summary>Called once from the constructor, after the tree and the preview exist.</summary>
    private void InitializeExplorer()
    {
        Tree.NavigateRequested += node => _ = NavigateToNodeAsync(node);
        Tree.SyncDevices(Sources);
        Preview.LastFolderOn = source => _lastFolder.TryGetValue(SourceKey(source), out var last) ? last : null;
        Preview.OtherDevices = () => Sources.Where(s => SelectedSource is null || !s.SameSourceAs(SelectedSource)).ToList();
        PropertyChanged += OnExplorerPropertyChanged;
    }

    /// <summary>
    /// Brings the tree's device rows in line with <see cref="Sources"/>, once a rebuild of it has finished. Called by
    /// <see cref="RefreshSources"/> rather than on every collection change, because the rebuild clears the list first:
    /// following each step would drop This PC's row (and everything opened under it) for an instant and rebuild it.
    /// </summary>
    private void SyncTreeWithSources()
    {
        Tree.SyncDevices(Sources);
        foreach (var gone in _treePhones.Keys.Where(id => !Sources.Any(s => s.ClientId == id)).ToList())
            CloseTreePhone(gone);
    }

    private void OnExplorerPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(SelectedRemoteEntry):
            case nameof(IsPreviewPaneOpen):
                UpdatePreview();
                break;
            case nameof(SelectedSource):
                OnPropertyChanged(nameof(CurrentDeviceLabel));
                break;
        }
    }

    /// <summary>Points the preview pane at the selected entry, or clears it.</summary>
    private void UpdatePreview()
    {
        if (!IsPreviewPaneOpen || SelectedRemoteEntry is not { Name: not ".." } entry || SelectedRemoteRoot is not { } root)
        {
            Preview.Show(null);
            return;
        }

        Preview.Show(new PreviewTarget(
            _client, SelectedSource ?? FileSourceOption.ThisPc, root.RootId,
            FileTreeViewModel.Combine(ToRootRelative(RemotePath), entry.Name), entry));
    }

    // ── Transfer strip ──

    private readonly HashSet<FileTransferQueueItem> _trackedQueueItems = new();

    /// <summary>Transfers queued or in flight, for the strip along the bottom.</summary>
    public int ActiveTransferCount => TransferQueue.Items.Count(i => !i.IsTerminal);

    /// <summary>Finished transfers whose SHA-256 was compared and matched.</summary>
    public int VerifiedTransferCount => TransferQueue.Items.Count(i => i.IsVerified);

    /// <summary>Transfers that failed.</summary>
    public int FailedTransferCount => TransferQueue.Items.Count(i => i.State == TransferState.Failed);

    public bool HasActiveTransfers => ActiveTransferCount > 0;
    public bool HasVerifiedTransfers => VerifiedTransferCount > 0;
    public bool HasFailedTransfers => FailedTransferCount > 0;

    /// <summary>Whether the strip is opened out into the full list.</summary>
    [ObservableProperty]
    private bool _isQueueExpanded;

    [RelayCommand]
    private void ToggleQueue() => IsQueueExpanded = !IsQueueExpanded;

    /// <summary>
    /// Follows every row's state, because the queue itself only says when rows are added or cleared and the strip's
    /// counts change as each one finishes.
    /// </summary>
    private void TrackQueueItems()
    {
        foreach (var gone in _trackedQueueItems.Where(i => !TransferQueue.Items.Contains(i)).ToList())
        {
            gone.PropertyChanged -= OnQueueItemChanged;
            _trackedQueueItems.Remove(gone);
        }
        foreach (var item in TransferQueue.Items)
        {
            if (_trackedQueueItems.Add(item))
                item.PropertyChanged += OnQueueItemChanged;
        }
        RaiseQueueCounts();
    }

    private void OnQueueItemChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(FileTransferQueueItem.State) or nameof(FileTransferQueueItem.IsVerified))
            RaiseQueueCounts();
    }

    private void RaiseQueueCounts()
    {
        OnPropertyChanged(nameof(ActiveTransferCount));
        OnPropertyChanged(nameof(VerifiedTransferCount));
        OnPropertyChanged(nameof(FailedTransferCount));
        OnPropertyChanged(nameof(HasActiveTransfers));
        OnPropertyChanged(nameof(HasVerifiedTransfers));
        OnPropertyChanged(nameof(HasFailedTransfers));
    }

    /// <summary>Copies a finished transfer's verified SHA-256 (hex).</summary>
    [RelayCommand]
    private async Task CopyTransferHashAsync(FileTransferQueueItem? item)
    {
        if (item?.VerifiedSha256Hex is { } hex && CopyText is { } copy)
            await copy(hex);
    }

    // ── Clients per device ──

    /// <summary>
    /// A client for <paramref name="source"/>'s files: the screen's own when it is the device on show, otherwise one
    /// the tree keeps open. Its capabilities are known by the time it is returned. Null when the device cannot be reached.
    /// </summary>
    internal async Task<FileTransferClient?> ClientForAsync(FileSourceOption source, CancellationToken ct)
    {
        FileTransferClient? client;
        if (source.IsThisPc)
        {
            client = _pcClient;
        }
        else if (IsPhoneSource && _phoneConnection?.ClientId == source.ClientId && _phoneClient is not null)
        {
            client = _phoneClient;
        }
        else if (_treePhones.TryGetValue(source.ClientId!, out var open) && open.Connection.IsConnected)
        {
            client = open.Client;
        }
        else
        {
            client = OpenTreePhone(source.ClientId!);
        }

        if (client is not null && client.Capabilities is null)
            await client.ListRemoteRootsAsync(ct); // learns what the device can do
        return client;
    }

    private FileTransferClient? OpenTreePhone(string clientId)
    {
        CloseTreePhone(clientId);
        try
        {
            var access = _resolvePhoneAccess() ?? throw new PhoneNotConnectedException();
            var connection = access.Open(clientId);
            var client = new FileTransferClient(connection);
            connection.Disconnected += () => PostToUi(() => CloseTreePhone(clientId));
            _treePhones[clientId] = (connection, client);
            return client;
        }
        catch (PhoneNotConnectedException ex)
        {
            _logger.LogInformation(ex, "A phone in the tree could not be reached");
            return null;
        }
    }

    private void CloseTreePhone(string clientId)
    {
        if (_treePhones.Remove(clientId, out var open))
        {
            open.Client.Dispose();
            open.Connection.Dispose();
        }
    }

    private void DisposeExplorer()
    {
        foreach (var item in _trackedQueueItems)
            item.PropertyChanged -= OnQueueItemChanged;
        _trackedQueueItems.Clear();
        PropertyChanged -= OnExplorerPropertyChanged;
        foreach (var id in _treePhones.Keys.ToList())
            CloseTreePhone(id);
        Preview.Dispose();
    }

    // ── Navigation ──

    private Task NavigateToNodeAsync(FileTreeNode node) =>
        NavigateToAsync(node.Source, node.IsDevice ? null : node.RootId, node.Path);

    /// <summary>
    /// Shows <paramref name="path"/> in <paramref name="rootId"/> on <paramref name="source"/>, switching device and
    /// shared folder as needed. A null <paramref name="rootId"/> just shows the device.
    /// </summary>
    public async Task NavigateToAsync(FileSourceOption source, string? rootId, string path)
    {
        if (SelectedSource is null || !source.SameSourceAs(SelectedSource))
        {
            if (Sources.FirstOrDefault(s => s.SameSourceAs(source)) is not { } listed)
                return;
            _pendingNavigation = rootId is null ? null : (rootId, path);
            SelectedSource = listed; // SwitchSource lists the roots, then TakePendingRoot lands on the right one
            return;
        }

        if (rootId is null)
            return;

        if (SelectedRemoteRoot?.RootId != rootId)
        {
            if (RemoteRoots.FirstOrDefault(r => r.RootId == rootId) is not { } root)
            {
                // Not listed yet: a phone's whole-device volume joins the list only once volumes are loaded.
                _pendingNavigation = (rootId, path);
                if (ShowVolumesButton)
                {
                    await LoadVolumesCommand.ExecuteAsync(null);
                    if (RemoteRoots.FirstOrDefault(r => r.RootId == rootId) is { } volume)
                    {
                        SelectedRemoteRoot = volume;
                        return;
                    }
                }
                await LoadRemoteRootsAsync();
                return;
            }
            _pendingNavigation = (rootId, path);
            SelectedRemoteRoot = root;
            return;
        }

        RemotePath = ToBreadcrumbPath(path);
        await BrowseRemoteAsync();
    }

    /// <summary>The root a pending navigation wants, if it is among <paramref name="roots"/>; consulted when roots are listed.</summary>
    private FileSharedRoot? TakePendingRoot(IEnumerable<FileSharedRoot> roots) =>
        _pendingNavigation is { } pending ? roots.FirstOrDefault(r => r.RootId == pending.RootId) : null;

    /// <summary>The folder a pending navigation wants inside <paramref name="rootId"/>, consumed; "/" when there is none.</summary>
    private string TakePendingPath(string rootId)
    {
        if (_pendingNavigation is { } pending && pending.RootId == rootId)
        {
            _pendingNavigation = null;
            return ToBreadcrumbPath(pending.Path);
        }
        _pendingNavigation = null;
        return "/";
    }

    private static string ToBreadcrumbPath(string path) =>
        FileTreeViewModel.Normalize(path) is { Length: > 0 } p ? "/" + p : "/";

    /// <summary>After every successful listing: remember where this device was, and bring the tree along.</summary>
    private void AfterBrowse()
    {
        if (SelectedRemoteRoot is not { } root)
            return;
        var source = SelectedSource ?? FileSourceOption.ThisPc;
        var folder = ToRootRelative(RemotePath);
        _lastFolder[SourceKey(source)] = (root.RootId, folder);
        _ = Tree.RevealAsync(source, root.RootId, folder);
    }

    // ── Thumbnails ──

    /// <summary>
    /// The thumbnail for an image row in the folder on show, or null for anything else. Called by the list and grid
    /// as rows appear, so only what is on screen is ever fetched.
    /// </summary>
    public Task<IImage?> GetThumbnailAsync(FileEntry entry, CancellationToken ct)
    {
        if (entry.IsDirectory || SelectedRemoteRoot is not { } root || !_client.SupportsV3
            || PreviewClassifier.Classify(entry.Name, false) is not (PreviewKind.Image or PreviewKind.ImageThumbnailOnly))
        {
            return Task.FromResult<IImage?>(null);
        }

        var key = new ThumbnailCache.Key(
            SourceKey(SelectedSource ?? FileSourceOption.ThisPc), root.RootId,
            FileTreeViewModel.Combine(ToRootRelative(RemotePath), entry.Name), entry.ModifiedUnixMs);
        return Thumbnails.GetAsync(_client, key, ct);
    }

    // ── Context-menu helpers ──

    /// <summary>Copies "Device › Shared folder › path" for the selection, the way the screen shows it.</summary>
    [RelayCommand]
    private async Task CopyPathAsync()
    {
        if (SelectedRemoteRoot is not { } root || CopyText is not { } copy)
            return;
        var relative = SelectedRemoteEntry is { Name: not ".." } entry
            ? FileTreeViewModel.Combine(ToRootRelative(RemotePath), entry.Name)
            : ToRootRelative(RemotePath);
        var parts = new[] { CurrentDeviceLabel, root.DisplayName }.Concat(relative.Split('/', StringSplitOptions.RemoveEmptyEntries));
        await copy(string.Join(" › ", parts));
    }

    /// <summary>Opens the preview pane on the selection and starts its SHA-256.</summary>
    [RelayCommand]
    private async Task ComputeSelectionHashAsync()
    {
        IsPreviewPaneOpen = true;
        UpdatePreview();
        // The pane loads after its debounce; wait for it rather than hashing the previous selection.
        await Task.Delay(FilePreviewViewModel.Debounce + TimeSpan.FromMilliseconds(50));
        if (Preview.ComputeHashCommand.CanExecute(null))
            await Preview.ComputeHashCommand.ExecuteAsync(null);
    }

    /// <summary>The devices the selection can be sent to: every connected device except the one on show.</summary>
    public IReadOnlyList<FileSourceOption> OtherDevices =>
        Sources.Where(s => SelectedSource is null || !s.SameSourceAs(SelectedSource)).ToList();

    /// <summary>
    /// Sends the selection to <paramref name="device"/>, into the folder last opened there (or its first writable
    /// shared folder). The keyboard and context-menu twin of dragging onto that device in the tree.
    /// </summary>
    [RelayCommand]
    private async Task SendSelectionToAsync(FileSourceOption? device)
    {
        if (device is null || SelectedRemoteRoot is not { } root)
            return;
        var selection = EffectiveSelection();
        if (selection.Count == 0)
            return;

        string targetRoot;
        string targetFolder;
        if (_lastFolder.TryGetValue(SourceKey(device), out var last))
        {
            (targetRoot, targetFolder) = last;
        }
        else
        {
            var client = await ClientForAsync(device, CancellationToken.None);
            var roots = client is null ? [] : await client.ListRemoteRootsAsync(CancellationToken.None);
            if (roots.FirstOrDefault(r => r.IsWritable) is not { } writable)
            {
                SetStatus(() => LocalizationService.Instance["FileTransfer_DropTargetReadOnly"]);
                return;
            }
            (targetRoot, targetFolder) = (writable.RootId, string.Empty);
        }

        var payload = new FileDragPayload(SelectedSource ?? FileSourceOption.ThisPc, root.RootId, ToRootRelative(RemotePath), selection);
        await TransferBetweenDevicesAsync(payload, device, targetRoot, targetFolder);
    }

    // ── Drag and drop ──

    /// <summary>
    /// Entries dropped onto a tree row. Another device: queued as transfers into that folder. The same device and
    /// shared folder: moved there (or copied, when <paramref name="copy"/>), through the same paste and conflict
    /// prompts as Cut and Paste. Anything else is refused with a reason.
    /// </summary>
    public async Task DropOntoNodeAsync(FileDragPayload payload, FileTreeNode target, bool copy)
    {
        if (target.IsDevice || target.IsPlaceholder || target.RootId is null || payload.Entries.Count == 0)
            return;

        if (!payload.Source.SameSourceAs(target.Source))
        {
            await TransferBetweenDevicesAsync(payload, target.Source, target.RootId, target.Path);
            return;
        }

        if (payload.RootId != target.RootId)
        {
            SetStatus(() => LocalizationService.Instance["FileTransfer_PasteCrossRoot"]);
            return;
        }

        if (!SupportsCopyMove)
            return;

        // Reuse Cut/Copy + Paste: same conflict prompts, same status lines. The screen moves into the target folder,
        // which is also where the result can be seen.
        _clipboard.Clear();
        _clipboard.AddRange(payload.Entries);
        _clipboardRootId = payload.RootId;
        _clipboardFolder = ToBreadcrumbPath(payload.Folder);
        _clipboardIsMove = !copy;
        OnPropertyChanged(nameof(HasClipboard));
        RemotePath = ToBreadcrumbPath(target.Path);
        await BrowseRemoteAsync();
        await PasteAsync();
    }

    /// <summary>OS files dropped onto a tree row: shown there, then uploaded the way a drop onto the list is.</summary>
    public async Task DropLocalFilesOntoNodeAsync(IReadOnlyList<string> localPaths, FileTreeNode target)
    {
        if (target.IsDevice || target.IsPlaceholder || target.RootId is null)
            return;
        await NavigateToAsync(target.Source, target.RootId, target.Path);
        if (SelectedRemoteRoot?.RootId == target.RootId)
            EnqueueUploads(localPaths);
    }

    /// <summary>
    /// Queues each FILE in <paramref name="payload"/> for transfer to <paramref name="targetRoot"/>/<paramref name="targetFolder"/>
    /// on <paramref name="target"/>. PC → phone is an upload of the file on this PC's disk; phone → PC is a download
    /// straight into the PC shared folder (named "x (2).ext" rather than replacing what is there). Phone → phone and
    /// folders are not moved this way, and say so.
    /// </summary>
    private async Task TransferBetweenDevicesAsync(FileDragPayload payload, FileSourceOption target, string targetRoot, string targetFolder)
    {
        var files = payload.Entries.Where(e => !e.IsDirectory && e.Name != "..").ToList();
        if (files.Count == 0)
        {
            SetStatus(() => LocalizationService.Instance["FilePreview_FoldersNotSent"]);
            return;
        }

        if (!payload.Source.IsThisPc && !target.IsThisPc)
        {
            SetStatus(() => LocalizationService.Instance["FilePreview_PhoneToPhoneUnsupported"]);
            return;
        }

        var transfers = _resolvePhoneTransfers();
        if (transfers is null)
        {
            SetStatus(() => LocalizationService.Instance["FileTransfer_PhoneNotConnected"]);
            return;
        }

        if (payload.Source.IsThisPc)
        {
            // PC → phone: the bytes are already on this PC's disk.
            var phoneId = target.ClientId!;
            foreach (var entry in files)
            {
                if (await LocalPathForAsync(payload.RootId, FileTreeViewModel.Combine(payload.Folder, entry.Name)) is not { } local)
                    continue;
                TransferQueue.Enqueue(FileTransferQueueKind.Upload, entry.Name,
                    (progress, ct) => transfers.UploadAsync(phoneId, local, targetRoot, FileTreeViewModel.Normalize(targetFolder), progress, ct));
            }
            SetStatus(() => LocalizationService.Instance["FileTransfer_QueuedForUpload"]);
        }
        else
        {
            // Phone → PC: straight into the PC shared folder on disk.
            var phoneId = payload.Source.ClientId!;
            if (await LocalPathForAsync(targetRoot, targetFolder) is not { } localFolder || !Directory.Exists(localFolder))
            {
                SetStatus(() => LocalizationService.Instance["FileTransfer_DropTargetReadOnly"]);
                return;
            }
            foreach (var entry in files)
            {
                var localFile = UniqueLocalPath(Path.Combine(localFolder, SanitizeLocalName(entry.Name)));
                var remote = FileTreeViewModel.Combine(payload.Folder, entry.Name);
                TransferQueue.Enqueue(FileTransferQueueKind.Download, entry.Name,
                    (progress, ct) => transfers.DownloadAsync(phoneId, payload.RootId, remote, localFile, progress, ct));
            }
            SetStatus(() => LocalizationService.Instance["FileTransfer_QueuedForDownload"]);
        }
    }

    /// <summary>
    /// Where a file in one of THIS PC's shared folders is on disk, or null when the shared folder is unknown or the
    /// path would leave it. Used to send PC files to a phone and to drag them out to Explorer.
    /// </summary>
    public async Task<string?> LocalPathForAsync(string rootId, string relativePath)
    {
        if (_pcRootPaths.Count == 0)
        {
            try
            {
                var load = LoadPcRoots ?? (() => new FileTransferRootSettingsService().LoadAsync());
                _pcRootPaths = await load();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
            {
                _logger.LogInformation(ex, "Reading this PC's shared folders failed");
                return null;
            }
        }

        return TryLocalPath(rootId, relativePath);
    }

    /// <summary>The synchronous half of <see cref="LocalPathForAsync"/>, once the shared folders have been read (for drag-out).</summary>
    public string? TryLocalPath(string rootId, string relativePath)
    {
        if (_pcRootPaths.FirstOrDefault(r => r.RootId == rootId) is not { } root)
            return null;
        return FilePathValidation.TryResolveWithinRoot(root.AbsolutePath, FileTreeViewModel.Normalize(relativePath), out var resolved, out _)
            ? resolved
            : null;
    }

    /// <summary>
    /// <paramref name="path"/> itself when nothing is there, otherwise "name (2).ext", "name (3).ext"… A transfer between
    /// devices never replaces a file on this PC without asking; it keeps both, the way the conflict prompt's default does.
    /// </summary>
    internal static string UniqueLocalPath(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path))
            return path;
        var folder = Path.GetDirectoryName(path) ?? string.Empty;
        var stem = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        for (var n = 2; n < 10_000; n++)
        {
            var candidate = Path.Combine(folder, $"{stem} ({n}){ext}");
            if (!File.Exists(candidate) && !Directory.Exists(candidate))
                return candidate;
        }
        return Path.Combine(folder, $"{stem} ({Guid.NewGuid():N}){ext}");
    }
}
