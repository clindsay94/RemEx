using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Remex.Core.Models;
using Remex.Desktop.Services;
using Remex.Desktop.Services.FileTransfer;

namespace Remex.Desktop.ViewModels;

/// <summary>What a row of the folder tree is.</summary>
public enum FileTreeNodeKind
{
    /// <summary>This PC, or one connected phone.</summary>
    Device,

    /// <summary>A shared folder (or whole-device volume) on that device.</summary>
    Root,

    /// <summary>A folder inside a shared folder.</summary>
    Folder,

    /// <summary>The "Loading…" / "Couldn't load" row shown until a node's real children arrive.</summary>
    Placeholder,
}

/// <summary>One row of the File Transfer folder tree (2026-10-08 redesign). Children load the first time it is expanded.</summary>
public sealed partial class FileTreeNode : ObservableObject
{
    private readonly FileTreeViewModel? _owner;

    internal FileTreeNode(
        FileTreeViewModel? owner, FileTreeNodeKind kind, FileSourceOption source, string? rootId,
        string path, string name, FileTreeNode? parent, bool isWritable = false)
    {
        _owner = owner;
        Kind = kind;
        Source = source;
        RootId = rootId;
        Path = path;
        Name = name;
        Parent = parent;
        IsWritable = isWritable;
        if (kind != FileTreeNodeKind.Placeholder)
            Children.Add(Placeholder(this, failed: false));
    }

    internal static FileTreeNode Placeholder(FileTreeNode parent, bool failed) =>
        new(null, FileTreeNodeKind.Placeholder, parent.Source, parent.RootId, parent.Path, string.Empty, parent)
        {
            LoadFailed = failed,
        };

    public FileTreeNodeKind Kind { get; }

    /// <summary>The device this row belongs to.</summary>
    public FileSourceOption Source { get; }

    /// <summary>The shared folder this row is in; null for a device row.</summary>
    public string? RootId { get; }

    /// <summary>Root-relative, '/'-separated; empty for a device or a shared folder itself.</summary>
    public string Path { get; }

    /// <summary>The folder's own name, or the shared folder's display name. Empty for a device row (see the device bindings).</summary>
    public string Name { get; }

    public FileTreeNode? Parent { get; }

    /// <summary>Whether files may be dropped here: a writable shared folder or any folder inside one.</summary>
    public bool IsWritable { get; }

    public ObservableCollection<FileTreeNode> Children { get; } = new();

    public bool IsDevice => Kind == FileTreeNodeKind.Device;
    public bool IsThisPc => IsDevice && Source.IsThisPc;
    public bool IsPhone => IsDevice && !Source.IsThisPc;
    public bool IsRoot => Kind == FileTreeNodeKind.Root;
    public bool IsFolder => Kind == FileTreeNodeKind.Folder;
    public bool IsPlaceholder => Kind == FileTreeNodeKind.Placeholder;

    /// <summary>A whole-device volume of a phone, rather than a folder it shares by name.</summary>
    public bool IsVolume { get; init; }

    /// <summary>A shared folder the PC may not write to (shown with a lock).</summary>
    public bool IsReadOnlyRoot => IsRoot && !IsWritable;

    /// <summary>True for the placeholder row of a node whose children could not be listed.</summary>
    [ObservableProperty]
    private bool _loadFailed;

    [ObservableProperty]
    private bool _isExpanded;

    [ObservableProperty]
    private bool _isLoading;

    internal bool ChildrenLoaded { get; set; }

    partial void OnIsExpandedChanged(bool value)
    {
        if (value && _owner is not null)
            _ = _owner.EnsureChildrenAsync(this);
    }

    /// <summary>The same place on the same device, whatever object stands for it.</summary>
    public bool IsSamePlaceAs(FileSourceOption source, string? rootId, string path) =>
        Source.SameSourceAs(source)
        && string.Equals(RootId, rootId, StringComparison.Ordinal)
        && string.Equals(Path, FileTreeViewModel.Normalize(path), StringComparison.Ordinal);
}

/// <summary>
/// The folder tree on the left of the File Transfer screen (2026-10-08 redesign): This PC and each connected phone,
/// their shared folders, and the folders inside, listed lazily as they are expanded. Picking a row asks the screen
/// to go there (<see cref="NavigateRequested"/>); the screen, once it has, calls <see cref="RevealAsync"/> so the
/// tree follows navigation done any other way (a double-click, a breadcrumb, Up).
/// </summary>
public sealed partial class FileTreeViewModel : ObservableObject
{
    private readonly Func<FileSourceOption, CancellationToken, Task<FileTransferClient?>> _clientFor;
    private readonly ILogger _logger;
    private bool _revealing;

    /// <param name="clientFor">
    /// A client for a device's files, or null when it cannot be reached now. The screen supplies its own client
    /// for the device on show and a separate one per other phone, so the tree lists both sides at once.
    /// </param>
    public FileTreeViewModel(
        Func<FileSourceOption, CancellationToken, Task<FileTransferClient?>> clientFor, ILogger? logger = null)
    {
        _clientFor = clientFor;
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>One row per device, This PC first.</summary>
    public ObservableCollection<FileTreeNode> Devices { get; } = new();

    [ObservableProperty]
    private FileTreeNode? _selectedNode;

    /// <summary>The person picked a row: the screen should show that device, shared folder and folder.</summary>
    public event Action<FileTreeNode>? NavigateRequested;

    partial void OnSelectedNodeChanged(FileTreeNode? value)
    {
        if (_revealing || value is null || value.IsPlaceholder)
            return;
        NavigateRequested?.Invoke(value);
    }

    /// <summary>
    /// Matches the device rows to <paramref name="sources"/>: new devices are added, departed ones removed, and the
    /// rows of devices that stayed keep what they had already listed and how far they were opened.
    /// </summary>
    public void SyncDevices(IEnumerable<FileSourceOption> sources)
    {
        var wanted = sources.ToList();
        for (var i = Devices.Count - 1; i >= 0; i--)
        {
            if (!wanted.Any(s => s.SameSourceAs(Devices[i].Source)))
                Devices.RemoveAt(i);
        }

        for (var i = 0; i < wanted.Count; i++)
        {
            var existing = Devices.FirstOrDefault(d => d.Source.SameSourceAs(wanted[i]));
            if (existing is null)
            {
                Devices.Insert(Math.Min(i, Devices.Count),
                    new FileTreeNode(this, FileTreeNodeKind.Device, wanted[i], null, string.Empty, string.Empty, null));
            }
            else if (Devices.IndexOf(existing) != i)
            {
                Devices.Move(Devices.IndexOf(existing), i);
            }
        }
    }

    /// <summary>Lists <paramref name="node"/>'s children once; later calls do nothing until <see cref="RefreshAsync"/>.</summary>
    public async Task EnsureChildrenAsync(FileTreeNode node)
    {
        if (node.ChildrenLoaded || node.IsLoading || node.IsPlaceholder)
            return;
        await LoadChildrenAsync(node);
    }

    /// <summary>Lists <paramref name="node"/>'s children again, after something in it changed.</summary>
    public async Task RefreshAsync(FileTreeNode node)
    {
        node.ChildrenLoaded = false;
        await LoadChildrenAsync(node);
    }

    private async Task LoadChildrenAsync(FileTreeNode node)
    {
        node.IsLoading = true;
        try
        {
            var client = await _clientFor(node.Source, CancellationToken.None);
            if (client is null)
            {
                ShowFailure(node);
                return;
            }

            var children = new List<FileTreeNode>();
            if (node.IsDevice)
            {
                var roots = await client.ListRemoteRootsAsync(CancellationToken.None);
                foreach (var root in roots.OrderBy(r => r.DisplayName, StringComparer.CurrentCultureIgnoreCase))
                {
                    children.Add(new FileTreeNode(this, FileTreeNodeKind.Root, node.Source, root.RootId, string.Empty,
                        root.DisplayName, node, root.IsWritable));
                }

                // A phone whose owner turned on whole-device browsing also lists its volumes, read-only, after the
                // shared folders. Never for This PC: the PC's own drives are File Explorer's (RemEx-xt0af).
                if (!node.Source.IsThisPc && client.SupportsFullBrowse)
                {
                    var (volumes, granted, _) = await client.ListVolumesAsync(CancellationToken.None);
                    if (granted)
                    {
                        foreach (var volume in volumes.Where(v => children.All(c => c.RootId != v.Id)))
                        {
                            children.Add(new FileTreeNode(this, FileTreeNodeKind.Root, node.Source, volume.Id, string.Empty,
                                volume.Label, node, isWritable: false) { IsVolume = true });
                        }
                    }
                }
            }
            else
            {
                var entries = await client.BrowseRemoteAsync(node.RootId!, node.Path, CancellationToken.None);
                foreach (var entry in entries
                    .Where(e => e.IsDirectory && e.Name is not (".." or "."))
                    .OrderBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase))
                {
                    children.Add(new FileTreeNode(this, FileTreeNodeKind.Folder, node.Source, node.RootId,
                        Combine(node.Path, entry.Name), entry.Name, node, node.IsWritable));
                }
            }

            // Keep a child that is open (or already listed) when the same folder comes back: refreshing a
            // parent must not collapse what the person had opened under it.
            var kept = children
                .Select(fresh => node.Children.FirstOrDefault(old =>
                    !old.IsPlaceholder && old.IsSamePlaceAs(fresh.Source, fresh.RootId, fresh.Path)) ?? fresh)
                .ToList();
            node.Children.Clear();
            foreach (var child in kept)
                node.Children.Add(child);
            node.ChildrenLoaded = true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogInformation(ex, "Listing a folder for the tree failed");
            ShowFailure(node);
        }
        finally
        {
            node.IsLoading = false;
        }
    }

    private static void ShowFailure(FileTreeNode node)
    {
        node.Children.Clear();
        node.Children.Add(FileTreeNode.Placeholder(node, failed: true));
        node.ChildrenLoaded = false; // expanding again tries again
    }

    /// <summary>
    /// Opens the tree down to <paramref name="path"/> in <paramref name="rootId"/> on <paramref name="source"/> and
    /// selects that row, without asking the screen to navigate (it already has). Stops quietly at the deepest
    /// row it can find, e.g. when a folder was just deleted.
    /// </summary>
    public async Task RevealAsync(FileSourceOption source, string rootId, string path)
    {
        var device = Devices.FirstOrDefault(d => d.Source.SameSourceAs(source));
        if (device is null)
            return;

        var current = device;
        await ExpandAsync(current);
        var root = current.Children.FirstOrDefault(c => c.IsRoot && c.RootId == rootId);
        if (root is null)
        {
            Select(current);
            return;
        }

        current = root;
        foreach (var segment in Normalize(path).Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            await ExpandAsync(current);
            var next = current.Children.FirstOrDefault(c => c.IsFolder && c.Name == segment);
            if (next is null)
                break;
            current = next;
        }

        Select(current);
    }

    private async Task ExpandAsync(FileTreeNode node)
    {
        await EnsureChildrenAsync(node);
        node.IsExpanded = true;
    }

    private void Select(FileTreeNode node)
    {
        _revealing = true;
        try
        {
            SelectedNode = node;
        }
        finally
        {
            _revealing = false;
        }
    }

    /// <summary>Finds the row for a place that is already listed, or null.</summary>
    public FileTreeNode? Find(FileSourceOption source, string? rootId, string path)
    {
        foreach (var device in Devices)
        {
            if (FindIn(device, source, rootId, path) is { } found)
                return found;
        }
        return null;
    }

    private static FileTreeNode? FindIn(FileTreeNode node, FileSourceOption source, string? rootId, string path)
    {
        if (node.IsSamePlaceAs(source, rootId, path) && !node.IsDevice)
            return node;
        foreach (var child in node.Children)
        {
            if (FindIn(child, source, rootId, path) is { } found)
                return found;
        }
        return null;
    }

    /// <summary>The tree's path form: no leading or trailing '/', '/' between names, "" for the shared folder itself.</summary>
    public static string Normalize(string? path) => (path ?? string.Empty).Replace('\\', '/').Trim('/');

    internal static string Combine(string folder, string name) =>
        Normalize(folder) is { Length: > 0 } f ? $"{f}/{name}" : name;

    /// <summary>The words a device row shows: "This PC", the phone's name, or "Phone" when it has none.</summary>
    public static string DeviceLabel(FileSourceOption source) => source.IsThisPc
        ? LocalizationService.Instance["FileTransfer_SourceThisPc"]
        : source.HasDeviceName ? source.DeviceName! : LocalizationService.Instance["FileTransfer_SourceUnnamedPhone"];
}
