using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Remex.Core.Models;
using Remex.Client.Services.FileTransfer;

namespace Remex.Client.ViewModels;

public sealed partial class FileTransferViewModel : ObservableObject, IDisposable
{
    private readonly ConnectionViewModel _connection;
    private readonly FileTransferClient _client;
    private CancellationTokenSource? _transferCts;

    public FileTransferViewModel(ConnectionViewModel connection)
    {
        _connection = connection;
        _client = new FileTransferClient(connection);
        LocalPath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        RefreshLocal();
        _ = InitializeAsync();
    }

    // ─── Local side ───────────────────────────────────────────────────────────

    [ObservableProperty]
    private string _localPath;

    public ObservableCollection<FileEntry> LocalEntries { get; } = new();

    [RelayCommand]
    private void RefreshLocal()
    {
        LocalEntries.Clear();
        try
        {
            var dir = new DirectoryInfo(LocalPath);
            if (!dir.Exists) return;

            if (dir.Parent is not null)
                LocalEntries.Add(new FileEntry { Name = "..", IsDirectory = true });

            foreach (var fsi in dir.EnumerateFileSystemInfos()
                .OrderByDescending(f => f is DirectoryInfo)
                .ThenBy(f => f.Name))
            {
                LocalEntries.Add(new FileEntry
                {
                    Name = fsi.Name,
                    IsDirectory = fsi is DirectoryInfo,
                    SizeBytes = fsi is FileInfo fi ? fi.Length : 0,
                    ModifiedUnixMs = new DateTimeOffset(fsi.LastWriteTimeUtc).ToUnixTimeMilliseconds()
                });
            }
        }
        catch (Exception ex)
        {
            StatusText = $"Local error: {ex.Message}";
        }
    }

    [RelayCommand]
    private void NavigateLocalEntry(FileEntry entry)
    {
        if (!entry.IsDirectory) return;
        LocalPath = entry.Name == ".."
            ? Path.GetDirectoryName(LocalPath) ?? LocalPath
            : Path.Combine(LocalPath, entry.Name);
        RefreshLocal();
    }

    // ─── Remote side ──────────────────────────────────────────────────────────

    [ObservableProperty]
    private string _remotePath = "/";

    public ObservableCollection<FileSharedRoot> RemoteRoots { get; } = new();

    [ObservableProperty]
    private FileSharedRoot? _selectedRemoteRoot;

    public ObservableCollection<FileEntry> RemoteEntries { get; } = new();

    partial void OnSelectedRemoteRootChanged(FileSharedRoot? value)
    {
        if (value is null)
        {
            RemoteEntries.Clear();
            RemotePath = "/";
            return;
        }

        RemotePath = "/";
        _ = BrowseRemoteAsync();
    }

    private async Task InitializeAsync()
    {
        await LoadRemoteRootsAsync();
    }

    [RelayCommand]
    private async Task LoadRemoteRootsAsync()
    {
        try
        {
            IsLoading = true;
            StatusText = string.Empty;
            var roots = await _client.ListRemoteRootsAsync(CancellationToken.None);
            RemoteRoots.Clear();
            foreach (var root in roots.OrderBy(root => root.DisplayName))
                RemoteRoots.Add(root);

            SelectedRemoteRoot ??= RemoteRoots.FirstOrDefault();
            if (SelectedRemoteRoot is null)
                StatusText = "The host has not exposed any shared folders yet.";
        }
        catch (Exception ex)
        {
            StatusText = $"Shared folders unavailable: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private async Task BrowseRemoteAsync()
    {
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
            RemoteEntries.Clear();
            if (RemotePath != "/" && RemotePath != "\\")
                RemoteEntries.Add(new FileEntry { Name = "..", IsDirectory = true });
            foreach (var e in entries)
                RemoteEntries.Add(e);
        }
        catch (Exception ex)
        {
            StatusText = $"Browse error: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private void NavigateRemoteEntry(FileEntry entry)
    {
        if (!entry.IsDirectory) return;
        RemotePath = entry.Name == ".."
            ? GetParentRemotePath(RemotePath)
            : CombineRemotePath(RemotePath, entry.Name);
        _ = BrowseRemoteAsync();
    }

    // ─── Transfer ─────────────────────────────────────────────────────────────

    [ObservableProperty]
    private double _transferProgress;

    [ObservableProperty]
    private bool _isTransferring;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string _statusText = string.Empty;

    [ObservableProperty]
    private FileEntry? _selectedLocalEntry;

    [ObservableProperty]
    private FileEntry? _selectedRemoteEntry;

    [RelayCommand(CanExecute = nameof(CanUpload))]
    private async Task UploadAsync()
    {
        if (SelectedLocalEntry is null || SelectedLocalEntry.IsDirectory || SelectedRemoteRoot is null) return;

        var localFile = Path.Combine(LocalPath, SelectedLocalEntry.Name);
        var remoteFile = CombineRemotePath(RemotePath, SelectedLocalEntry.Name);

        _transferCts = new CancellationTokenSource();
        IsTransferring = true;
        StatusText = $"Uploading {SelectedLocalEntry.Name}…";

        try
        {
            var progress = new Progress<double>(p =>
            {
                TransferProgress = p * 100;
                StatusText = $"Uploading {SelectedLocalEntry.Name}… {p:P0}";
            });
            await _client.UploadAsync(localFile, SelectedRemoteRoot.RootId, remoteFile, progress, _transferCts.Token);
            StatusText = "Upload complete.";
            TransferProgress = 0;
            await BrowseRemoteAsync();
        }
        catch (OperationCanceledException)
        {
            StatusText = "Upload cancelled.";
            TransferProgress = 0;
        }
        catch (Exception ex)
        {
            StatusText = $"Upload failed: {ex.Message}";
        }
        finally
        {
            IsTransferring = false;
            _transferCts = null;
        }
    }

    private bool CanUpload() =>
        SelectedLocalEntry is { IsDirectory: false }
        && SelectedRemoteRoot is { IsWritable: true }
        && _connection.IsConnected
        && !IsTransferring;

    [RelayCommand(CanExecute = nameof(CanDownload))]
    private async Task DownloadAsync()
    {
        if (SelectedRemoteEntry is null || SelectedRemoteEntry.IsDirectory || SelectedRemoteRoot is null) return;

        var remoteFile = CombineRemotePath(RemotePath, SelectedRemoteEntry.Name);
        var localFile = Path.Combine(LocalPath, SelectedRemoteEntry.Name);

        _transferCts = new CancellationTokenSource();
        IsTransferring = true;
        StatusText = $"Downloading {SelectedRemoteEntry.Name}…";

        try
        {
            var progress = new Progress<double>(p =>
            {
                TransferProgress = p * 100;
                StatusText = $"Downloading {SelectedRemoteEntry.Name}… {p:P0}";
            });
            await _client.DownloadAsync(SelectedRemoteRoot.RootId, remoteFile, localFile, progress, _transferCts.Token);
            StatusText = "Download complete.";
            TransferProgress = 0;
            RefreshLocal();
        }
        catch (OperationCanceledException)
        {
            StatusText = "Download cancelled.";
            TransferProgress = 0;
        }
        catch (Exception ex)
        {
            StatusText = $"Download failed: {ex.Message}";
        }
        finally
        {
            IsTransferring = false;
            _transferCts = null;
        }
    }

    private bool CanDownload() => SelectedRemoteEntry is { IsDirectory: false } && _connection.IsConnected && !IsTransferring;

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel()
    {
        _transferCts?.Cancel();
    }

    private bool CanCancel() => IsTransferring;

    public void Dispose()
    {
        _client.Dispose();
        _transferCts?.Cancel();
        _transferCts?.Dispose();
    }

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
}
