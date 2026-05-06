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
using Remex.Client.Services;
using Remex.Client.Services.FileTransfer;

namespace Remex.Client.ViewModels;

public sealed partial class FileTransferViewModel : ObservableObject, IDisposable
{
    private readonly ConnectionViewModel _connection;
    private readonly FileTransferClient _client;
    private CancellationTokenSource? _transferCts;

    public Func<FilePickerOpenOptions, Task<IReadOnlyList<IStorageFile>>>? PickUploadFileAsync { get; set; }
    public Func<FilePickerSaveOptions, Task<IStorageFile?>>? PickDownloadDestinationAsync { get; set; }

    public FileTransferViewModel(ConnectionViewModel connection)
    {
        _connection = connection;
        _client = new FileTransferClient(connection);
        _connection.PropertyChanged += OnConnectionPropertyChanged;
        _ = InitializeAsync();
    }

    // ─── Remote side ──────────────────────────────────────────────────────────

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UploadCommand))]
    [NotifyCanExecuteChangedFor(nameof(NavigateRemoteUpCommand))]
    [NotifyPropertyChangedFor(nameof(CanNavigateRemoteUp))]
    private string _remotePath = "/";

    public ObservableCollection<FileSharedRoot> RemoteRoots { get; } = new();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UploadCommand))]
    [NotifyPropertyChangedFor(nameof(RemoteRootHint))]
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
            StatusText = string.Format(LocalizationService.Instance["FileTransfer_BrowseErrorFormat"], ex.Message);
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

    [RelayCommand(CanExecute = nameof(CanNavigateRemoteUp))]
    private void NavigateRemoteUp()
    {
        if (!CanNavigateRemoteUp)
            return;

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

    public string RemoteRootHint => SelectedRemoteRoot switch
    {
        null => LocalizationService.Instance["FileTransfer_HintNoneSelected"],
        { IsWritable: true } root => string.Format(LocalizationService.Instance["FileTransfer_HintWritableFormat"], root.DisplayName),
        { } root => string.Format(LocalizationService.Instance["FileTransfer_HintReadOnlyFormat"], root.DisplayName),
    };

    // ─── Transfer ─────────────────────────────────────────────────────────────

    [ObservableProperty]
    private double _transferProgress;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UploadCommand))]
    [NotifyCanExecuteChangedFor(nameof(DownloadCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    private bool _isTransferring;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string _statusText = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DownloadCommand))]
    [NotifyCanExecuteChangedFor(nameof(OpenSelectedRemoteCommand))]
    [NotifyPropertyChangedFor(nameof(CanOpenRemoteSelection))]
    private FileEntry? _selectedRemoteEntry;

    [RelayCommand(CanExecute = nameof(CanUpload))]
    private async Task UploadAsync()
    {
        if (SelectedRemoteRoot is null)
            return;

        if (PickUploadFileAsync is null)
        {
            StatusText = LocalizationService.Instance["FileTransfer_PickerUnavailable"];
            return;
        }

        var files = await PickUploadFileAsync(new FilePickerOpenOptions
        {
            Title = LocalizationService.Instance["FileTransfer_PickerTitle"],
            AllowMultiple = false,
        });

        if (files.Count == 0)
            return;

        var localFile = GetLocalPath(files[0]);
        if (string.IsNullOrWhiteSpace(localFile))
        {
            StatusText = LocalizationService.Instance["FileTransfer_LocalPathUnavailable"];
            return;
        }

        var fileName = Path.GetFileName(localFile);
        var remoteFile = CombineRemotePath(RemotePath, fileName);

        _transferCts = new CancellationTokenSource();
        IsTransferring = true;
        StatusText = string.Format(LocalizationService.Instance["FileTransfer_UploadingFormat"], fileName);

        try
        {
            var progress = new Progress<double>(p =>
            {
                TransferProgress = p * 100;
                StatusText = string.Format(LocalizationService.Instance["FileTransfer_UploadingProgressFormat"], fileName, p);
            });
            await _client.UploadAsync(localFile, SelectedRemoteRoot.RootId, remoteFile, progress, _transferCts.Token);
            StatusText = LocalizationService.Instance["FileTransfer_UploadComplete"];
            TransferProgress = 0;
            await BrowseRemoteAsync();
        }
        catch (OperationCanceledException)
        {
            StatusText = LocalizationService.Instance["FileTransfer_UploadCancelled"];
            TransferProgress = 0;
        }
        catch (Exception ex)
        {
            StatusText = string.Format(LocalizationService.Instance["FileTransfer_UploadFailedFormat"], ex.Message);
        }
        finally
        {
            IsTransferring = false;
            _transferCts = null;
        }
    }

    private bool CanUpload() =>
        SelectedRemoteRoot is { IsWritable: true }
        && _connection.IsConnected
        && !IsTransferring;

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

        var localFile = GetLocalPath(destination);
        if (string.IsNullOrWhiteSpace(localFile))
        {
            StatusText = LocalizationService.Instance["FileTransfer_SavePathUnavailable"];
            return;
        }

        var remoteFile = CombineRemotePath(RemotePath, SelectedRemoteEntry.Name);

        _transferCts = new CancellationTokenSource();
        IsTransferring = true;
        StatusText = string.Format(LocalizationService.Instance["FileTransfer_DownloadingFormat"], SelectedRemoteEntry.Name);

        try
        {
            var progress = new Progress<double>(p =>
            {
                TransferProgress = p * 100;
                StatusText = string.Format(LocalizationService.Instance["FileTransfer_DownloadingProgressFormat"], SelectedRemoteEntry.Name, p);
            });
            await _client.DownloadAsync(SelectedRemoteRoot.RootId, remoteFile, localFile, progress, _transferCts.Token);
            StatusText = LocalizationService.Instance["FileTransfer_DownloadComplete"];
            TransferProgress = 0;
        }
        catch (OperationCanceledException)
        {
            StatusText = LocalizationService.Instance["FileTransfer_DownloadCancelled"];
            TransferProgress = 0;
        }
        catch (Exception ex)
        {
            StatusText = string.Format(LocalizationService.Instance["FileTransfer_DownloadFailedFormat"], ex.Message);
        }
        finally
        {
            IsTransferring = false;
            _transferCts = null;
        }
    }

    private bool CanDownload() => SelectedRemoteEntry is { IsDirectory: false } && SelectedRemoteRoot is not null && _connection.IsConnected && !IsTransferring;

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel()
    {
        _transferCts?.Cancel();
    }

    private bool CanCancel() => IsTransferring;

    public void Dispose()
    {
        _connection.PropertyChanged -= OnConnectionPropertyChanged;
        _client.Dispose();
        _transferCts?.Cancel();
        _transferCts?.Dispose();
    }

    private void OnConnectionPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ConnectionViewModel.IsConnected))
            return;

        UploadCommand.NotifyCanExecuteChanged();
        DownloadCommand.NotifyCanExecuteChanged();
    }

    private static string? GetLocalPath(IStorageItem item)
        => item.TryGetLocalPath();

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
