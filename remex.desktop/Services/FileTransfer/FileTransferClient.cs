using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Remex.Core.Messages;
using Remex.Core.Models;
using Remex.Desktop.ViewModels;

namespace Remex.Desktop.Services.FileTransfer;

/// <summary>
/// Client-side file transfer service. Uses the existing WebSocket connection
/// in <see cref="ConnectionViewModel"/> to send/receive file transfer messages.
/// </summary>
public sealed class FileTransferClient : IDisposable
{
    private readonly ConnectionViewModel _connection;
    private TaskCompletionSource<RemexMessage>? _rootsWaiter;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<RemexMessage>> _browseWaiters = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<RemexMessage>> _transferEndWaiters = new();
    private readonly ConcurrentDictionary<string, IProgress<double>?> _progressReporters = new();
    private readonly ConcurrentDictionary<string, Channel<byte[]>> _downloadChannels = new();
    private readonly ConcurrentDictionary<string, IncrementalHash> _downloadHashers = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<RemexMessage>> _manageWaiters = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<RemexMessage>> _hashWaiters = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<RemexMessage>> _rootManageWaiters = new();
    // ── 2.1 File Sharing Overhaul (protocolVersion 3) response waiters ──
    private readonly ConcurrentDictionary<string, TaskCompletionSource<RemexMessage>> _volumesWaiters = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<RemexMessage>> _searchWaiters = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<RemexMessage>> _metadataWaiters = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<RemexMessage>> _thumbnailWaiters = new();

    /// <summary>
    /// v3 file-transfer capabilities advertised by the connected host in the most recent
    /// <c>file_roots_response</c>. Null for a v2 host (or before roots are first fetched). The UI gates
    /// every v3 feature on this so a v3 message is never sent to a v2 peer (plan §1.5).
    /// </summary>
    public FileCapabilities? Capabilities { get; private set; }

    public FileTransferClient(ConnectionViewModel connection)
    {
        _connection = connection;
        _connection.FileTransferMessageReceived += OnFileTransferMessage;
    }

    public async Task<IReadOnlyList<FileSharedRoot>> ListRemoteRootsAsync(CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<RemexMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        _rootsWaiter = tcs;

        using var reg = ct.Register(() => tcs.TrySetCanceled(ct));

        await _connection.SendAsync(new RemexMessage
        {
            Type = MessageTypes.FileRootsRequest,
            FileRootsRequest = new FileRootsRequest()
        });

        RemexMessage response;
        try
        {
            response = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(ControlRequestTimeoutSeconds), ct);
        }
        finally
        {
            if (ReferenceEquals(_rootsWaiter, tcs))
                _rootsWaiter = null;
        }

        // Capture the v3 capability handshake (additive field; null for v2 hosts). Roots are always
        // fetched first, so this doubles as the negotiation — no separate handshake message.
        Capabilities = response.FileRootsResponse?.FileCapabilities;

        if (response.FileRootsResponse?.ErrorMessage is string err && !string.IsNullOrWhiteSpace(err))
            throw new IOException($"Root listing error: {err}");

        return response.FileRootsResponse?.Roots ?? [];
    }

    /// <summary>True when the connected host advertises the v3 file-manager protocol (copy/move/mkdir/
    /// search/metadata/thumbnail on <c>/ws</c>). Gates every v3 message so none is sent to a v2 peer.</summary>
    public bool SupportsV3 => (Capabilities?.Protocol ?? 0) >= 3;

    /// <summary>True when the host advertises a given file-manager op in its capability set.</summary>
    public bool SupportsOp(string op) =>
        Capabilities?.Ops is { } ops && System.Array.IndexOf(ops, op) >= 0;

    /// <summary>True when full-device (volume) browsing may be offered for this host.</summary>
    public bool SupportsFullBrowse => SupportsV3 && Capabilities?.FullBrowse == true;

    public async Task<IReadOnlyList<FileEntry>> BrowseRemoteAsync(string rootId, string relativePath, CancellationToken ct)
    {
        var requestId = Guid.NewGuid().ToString("N");
        var tcs = new TaskCompletionSource<RemexMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        _browseWaiters[requestId] = tcs;

        using var reg = ct.Register(() => tcs.TrySetCanceled(ct));

        await _connection.SendAsync(new RemexMessage
        {
            Type = MessageTypes.FileBrowseRequest,
            FileBrowseRequest = new FileBrowseRequest
            {
                RequestId = requestId,
                Path = relativePath,
                RootId = rootId,
                RelativePath = relativePath,
            }
        });

        RemexMessage response;
        try
        {
            response = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(ControlRequestTimeoutSeconds), ct);
        }
        finally
        {
            _browseWaiters.TryRemove(requestId, out _);
        }

        if (response.FileBrowseResponse?.ErrorMessage is string err)
            throw new IOException($"Browse error: {err}");

        return response.FileBrowseResponse?.Entries ?? [];
    }

    public async Task DeleteRemoteAsync(string rootId, string relativePath, CancellationToken ct)
    {
        var requestId = Guid.NewGuid().ToString("N");
        var tcs = new TaskCompletionSource<RemexMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        _manageWaiters[requestId] = tcs;
        using var reg = ct.Register(() => tcs.TrySetCanceled(ct));

        await _connection.SendAsync(new RemexMessage
        {
            Type = MessageTypes.FileManageRequest,
            FileManageRequest = new FileManageRequest { RequestId = requestId, RootId = rootId, RelativePath = relativePath, Operation = "delete" }
        });

        RemexMessage response;
        try
        {
            response = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(ManageRequestTimeoutSeconds), ct);
        }
        finally
        {
            _manageWaiters.TryRemove(requestId, out _);
        }

        if (response.FileManageResponse?.Success == false)
            throw new IOException($"Delete failed: {response.FileManageResponse.ErrorMessage}");
    }

    public async Task RenameRemoteAsync(string rootId, string relativePath, string newName, CancellationToken ct)
    {
        var requestId = Guid.NewGuid().ToString("N");
        var tcs = new TaskCompletionSource<RemexMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        _manageWaiters[requestId] = tcs;
        using var reg = ct.Register(() => tcs.TrySetCanceled(ct));

        await _connection.SendAsync(new RemexMessage
        {
            Type = MessageTypes.FileManageRequest,
            FileManageRequest = new FileManageRequest { RequestId = requestId, RootId = rootId, RelativePath = relativePath, Operation = "rename", NewName = newName }
        });

        RemexMessage response;
        try
        {
            response = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(ManageRequestTimeoutSeconds), ct);
        }
        finally
        {
            _manageWaiters.TryRemove(requestId, out _);
        }

        if (response.FileManageResponse?.Success == false)
            throw new IOException($"Rename failed: {response.FileManageResponse.ErrorMessage}");
    }

    /// <summary>
    /// Seconds to wait for a peer to answer a <see cref="MessageTypes.FileHashRequest"/>.
    /// Deliberately generous: hashing is CPU-bound on the peer and a large file can take a
    /// while. The bound exists to stop an indefinite hang, not to enforce responsiveness —
    /// the Android client has no handler for this request at all today (RemEx-0e54), so
    /// without it the UI waited forever with IsLoading stuck on.
    /// </summary>
    private const int HashRequestTimeoutSeconds = 30;

    /// <summary>
    /// Bounds for awaiting a peer reply, in seconds, split by operation class rather than
    /// shared. A single value would be wrong at both ends: too short turns a legitimate
    /// recursive search or large delete into a false failure, too long leaves the UI
    /// spinning on a control request. Before these existed every helper awaited its
    /// TaskCompletionSource unbounded, so a peer with no handler for the request hung the
    /// view model forever (RemEx-q2bu; RemEx-0e54 was one instance of it).
    /// </summary>
    private const int ControlRequestTimeoutSeconds = 30;

    /// <inheritdoc cref="ControlRequestTimeoutSeconds"/>
    private const int ManageRequestTimeoutSeconds = 60;

    /// <inheritdoc cref="ControlRequestTimeoutSeconds"/>
    private const int SearchRequestTimeoutSeconds = 120;

    public async Task<string> VerifyRemoteHashAsync(string rootId, string relativePath, CancellationToken ct)
    {
        var requestId = Guid.NewGuid().ToString("N");
        var tcs = new TaskCompletionSource<RemexMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        _hashWaiters[requestId] = tcs;
        using var reg = ct.Register(() => tcs.TrySetCanceled(ct));

        RemexMessage response;
        try
        {
            await _connection.SendAsync(new RemexMessage
            {
                Type = MessageTypes.FileHashRequest,
                FileHashRequest = new FileHashRequest { RequestId = requestId, RootId = rootId, RelativePath = relativePath }
            });

            // Bounded wait, matching ConnectionViewModel's command pattern. A peer that never
            // answers has to surface a failure the caller can show, not hang the view model.
            response = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(HashRequestTimeoutSeconds), ct);
        }
        finally
        {
            // Reap the waiter on every exit. The previous code removed it only after a
            // successful await, so each timeout or cancellation leaked an entry.
            _hashWaiters.TryRemove(requestId, out _);
        }

        if (response.FileHashResponse?.ErrorMessage is string err)
            throw new IOException($"Hash verification failed: {err}");

        return response.FileHashResponse?.Sha256Base64 ?? string.Empty;
    }

    // ──────────────────────────────────────────────────────────────────────────
    // 2.1 File Sharing Overhaul (protocolVersion 3) — file-manager ops on /ws.
    // Field names/type-strings mirror remex.core verbatim (RemexMessage / FileTransferMessages).
    // Callers must gate on Capabilities so these are never sent to a v2 host.
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>Copies <paramref name="relativePath"/> to <paramref name="destinationRelativePath"/> within the same root.</summary>
    public Task CopyRemoteAsync(string rootId, string relativePath, string destinationRelativePath, bool overwrite, CancellationToken ct)
        => ManageAsync(rootId, relativePath, Remex.Core.Models.FileManageOperations.Copy, newName: null, destinationRelativePath, overwrite, ct);

    /// <summary>Moves <paramref name="relativePath"/> to <paramref name="destinationRelativePath"/> within the same root.</summary>
    public Task MoveRemoteAsync(string rootId, string relativePath, string destinationRelativePath, bool overwrite, CancellationToken ct)
        => ManageAsync(rootId, relativePath, Remex.Core.Models.FileManageOperations.Move, newName: null, destinationRelativePath, overwrite, ct);

    /// <summary>Creates a new folder named <paramref name="folderName"/> under <paramref name="parentRelativePath"/> (wire: RelativePath=parent, NewName=folder).</summary>
    public Task MakeDirectoryRemoteAsync(string rootId, string parentRelativePath, string folderName, CancellationToken ct)
        => ManageAsync(rootId, parentRelativePath, Remex.Core.Models.FileManageOperations.Mkdir, newName: folderName, destinationPath: null, overwrite: false, ct);

    private async Task ManageAsync(string rootId, string relativePath, string operation, string? newName, string? destinationPath, bool overwrite, CancellationToken ct)
    {
        var requestId = Guid.NewGuid().ToString("N");
        var tcs = new TaskCompletionSource<RemexMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        _manageWaiters[requestId] = tcs;
        using var reg = ct.Register(() => tcs.TrySetCanceled(ct));

        await _connection.SendAsync(new RemexMessage
        {
            Type = MessageTypes.FileManageRequest,
            FileManageRequest = new FileManageRequest
            {
                RequestId = requestId,
                RootId = rootId,
                RelativePath = relativePath,
                Operation = operation,
                NewName = newName,
                DestinationPath = destinationPath,
                Overwrite = overwrite,
            }
        });

        // mkdir answers immediately, but copy and move stream the whole file before the peer
        // replies, so their duration scales with FILE SIZE. A wall-clock bound on those would
        // abort a large but healthy paste and — worse — reap the waiter, so the peer's eventual
        // success reply is dropped and the user is told the paste failed while the file appears
        // anyway. Those two need an idle watchdog rather than a deadline (RemEx-l519).
        var scalesWithFileSize =
            operation == FileManageOperations.Copy || operation == FileManageOperations.Move;

        RemexMessage response;
        try
        {
            response = scalesWithFileSize
                ? await tcs.Task
                : await tcs.Task.WaitAsync(TimeSpan.FromSeconds(ManageRequestTimeoutSeconds), ct);
        }
        finally
        {
            _manageWaiters.TryRemove(requestId, out _);
        }

        if (response.FileManageResponse?.Success == false)
            throw new IOException(response.FileManageResponse.ErrorMessage ?? $"{operation} failed.");
    }

    /// <summary>Bounded recursive search under a root subtree. Returns hits plus whether results were capped.</summary>
    public async Task<(IReadOnlyList<FileSearchEntry> Entries, bool Truncated)> SearchRemoteAsync(
        string rootId, string? relativePath, string query, int maxResults, CancellationToken ct)
    {
        var requestId = Guid.NewGuid().ToString("N");
        var tcs = new TaskCompletionSource<RemexMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        _searchWaiters[requestId] = tcs;
        using var reg = ct.Register(() => tcs.TrySetCanceled(ct));

        await _connection.SendAsync(new RemexMessage
        {
            Type = MessageTypes.FileSearchRequest,
            FileSearchRequest = new FileSearchRequest
            {
                RequestId = requestId,
                RootId = rootId,
                RelativePath = relativePath,
                Query = query,
                MaxResults = maxResults,
            }
        });

        RemexMessage response;
        try
        {
            response = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(SearchRequestTimeoutSeconds), ct);
        }
        finally
        {
            _searchWaiters.TryRemove(requestId, out _);
        }

        if (response.FileSearchResponse?.ErrorMessage is string err && !string.IsNullOrWhiteSpace(err))
            throw new IOException($"Search error: {err}");

        var resp = response.FileSearchResponse;
        return (resp?.Entries ?? [], resp?.Truncated ?? false);
    }

    /// <summary>Detailed metadata (size, timestamps, item count, mime, read-only) for a single item.</summary>
    public async Task<FileMetadataResponse> GetMetadataRemoteAsync(string rootId, string relativePath, CancellationToken ct)
    {
        var requestId = Guid.NewGuid().ToString("N");
        var tcs = new TaskCompletionSource<RemexMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        _metadataWaiters[requestId] = tcs;
        using var reg = ct.Register(() => tcs.TrySetCanceled(ct));

        await _connection.SendAsync(new RemexMessage
        {
            Type = MessageTypes.FileMetadataRequest,
            FileMetadataRequest = new FileMetadataRequest { RequestId = requestId, RootId = rootId, RelativePath = relativePath }
        });

        RemexMessage response;
        try
        {
            response = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(ControlRequestTimeoutSeconds), ct);
        }
        finally
        {
            _metadataWaiters.TryRemove(requestId, out _);
        }

        if (response.FileMetadataResponse is not { } meta)
            throw new IOException("No metadata response received.");
        if (meta.ErrorMessage is string err && !string.IsNullOrWhiteSpace(err))
            throw new IOException($"Metadata error: {err}");
        return meta;
    }

    /// <summary>Requests a small base64 JPEG thumbnail (images only in v1). Returns null when unavailable.</summary>
    public async Task<string?> GetThumbnailRemoteAsync(string rootId, string relativePath, int maxDim, CancellationToken ct)
    {
        var requestId = Guid.NewGuid().ToString("N");
        var tcs = new TaskCompletionSource<RemexMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        _thumbnailWaiters[requestId] = tcs;
        using var reg = ct.Register(() => tcs.TrySetCanceled(ct));

        await _connection.SendAsync(new RemexMessage
        {
            Type = MessageTypes.FileThumbnailRequest,
            FileThumbnailRequest = new FileThumbnailRequest { RequestId = requestId, RootId = rootId, RelativePath = relativePath, MaxDim = maxDim }
        });

        RemexMessage response;
        try
        {
            response = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(ControlRequestTimeoutSeconds), ct);
        }
        finally
        {
            _thumbnailWaiters.TryRemove(requestId, out _);
        }

        return response.FileThumbnailResponse?.JpegBase64;
    }

    /// <summary>Enumerates the host's mounted volumes/drives once full-browse consent is granted (plan §1.2).</summary>
    public async Task<(IReadOnlyList<FileVolumeInfo> Volumes, bool FullBrowseGranted)> ListVolumesAsync(CancellationToken ct)
    {
        var requestId = Guid.NewGuid().ToString("N");
        var tcs = new TaskCompletionSource<RemexMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        _volumesWaiters[requestId] = tcs;
        using var reg = ct.Register(() => tcs.TrySetCanceled(ct));

        await _connection.SendAsync(new RemexMessage
        {
            Type = MessageTypes.FileVolumesRequest,
            FileVolumesRequest = new FileVolumesRequest { RequestId = requestId }
        });

        RemexMessage response;
        try
        {
            response = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(ControlRequestTimeoutSeconds), ct);
        }
        finally
        {
            _volumesWaiters.TryRemove(requestId, out _);
        }

        var resp = response.FileVolumesResponse;
        if (resp?.ErrorMessage is string err && !string.IsNullOrWhiteSpace(err))
            throw new IOException($"Volumes error: {err}");
        return (resp?.Volumes ?? [], resp?.FullBrowseGranted ?? false);
    }

    public async Task<IReadOnlyList<FileSharedRoot>> AddRemoteRootAsync(string sourceRootId, string sourceRelativePath, CancellationToken ct)
    {
        var requestId = Guid.NewGuid().ToString("N");
        var tcs = new TaskCompletionSource<RemexMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        _rootManageWaiters[requestId] = tcs;
        using var reg = ct.Register(() => tcs.TrySetCanceled(ct));

        await _connection.SendAsync(new RemexMessage
        {
            Type = MessageTypes.FileRootManageRequest,
            FileRootManageRequest = new FileRootManageRequest { RequestId = requestId, Operation = "add", SourceRootId = sourceRootId, SourceRelativePath = sourceRelativePath }
        });

        RemexMessage response;
        try
        {
            response = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(ControlRequestTimeoutSeconds), ct);
        }
        finally
        {
            _rootManageWaiters.TryRemove(requestId, out _);
        }

        if (response.FileRootManageResponse?.ErrorMessage is string err)
            throw new IOException($"Add root failed: {err}");

        return response.FileRootManageResponse?.Roots ?? [];
    }

    public async Task<IReadOnlyList<FileSharedRoot>> RemoveRemoteRootAsync(string rootId, CancellationToken ct)
    {
        var requestId = Guid.NewGuid().ToString("N");
        var tcs = new TaskCompletionSource<RemexMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        _rootManageWaiters[requestId] = tcs;
        using var reg = ct.Register(() => tcs.TrySetCanceled(ct));

        await _connection.SendAsync(new RemexMessage
        {
            Type = MessageTypes.FileRootManageRequest,
            FileRootManageRequest = new FileRootManageRequest { RequestId = requestId, Operation = "remove", RootId = rootId }
        });

        RemexMessage response;
        try
        {
            response = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(ControlRequestTimeoutSeconds), ct);
        }
        finally
        {
            _rootManageWaiters.TryRemove(requestId, out _);
        }

        if (response.FileRootManageResponse?.ErrorMessage is string err)
            throw new IOException($"Remove root failed: {err}");

        return response.FileRootManageResponse?.Roots ?? [];
    }

    public async Task UploadAsync(string localPath, string remoteRootId, string remoteRelativePath, IProgress<double>? progress, CancellationToken ct)
    {
        var transferId = Guid.NewGuid().ToString("N");

        await using var fileStream = new FileStream(localPath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, useAsync: true);
        var totalBytes = fileStream.Length;

        var tcs = new TaskCompletionSource<RemexMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        _transferEndWaiters[transferId] = tcs;
        _progressReporters[transferId] = progress;

        using var reg = ct.Register(() => tcs.TrySetCanceled(ct));

        // Send Start with empty hash — hash is computed incrementally and sent in End
        await _connection.SendAsync(new RemexMessage
        {
            Type = MessageTypes.FileTransferStart,
            FileTransferStart = new FileTransferStart
            {
                TransferId = transferId,
                Direction = "upload",
                RemotePath = remoteRelativePath,
                RemoteRootId = remoteRootId,
                RemoteRelativePath = remoteRelativePath,
                FileName = Path.GetFileName(localPath),
                TotalBytes = totalBytes,
                Sha256Base64 = string.Empty
            }
        });

        const int chunkSize = 65536;
        var buffer = new byte[chunkSize];
        int read;
        long offset = 0;

        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        while ((read = await fileStream.ReadAsync(buffer.AsMemory(0, chunkSize), ct)) > 0)
        {
            hasher.AppendData(buffer, 0, read);
            await _connection.SendAsync(new RemexMessage
            {
                Type = MessageTypes.FileTransferChunk,
                FileTransferChunk = new FileTransferChunk
                {
                    TransferId = transferId,
                    Offset = offset,
                    DataBase64 = Convert.ToBase64String(buffer.AsSpan(0, read))
                }
            });
            offset += read;
        }

        var sha256Base64 = Convert.ToBase64String(hasher.GetCurrentHash());

        await _connection.SendAsync(new RemexMessage
        {
            Type = MessageTypes.FileTransferEnd,
            FileTransferEnd = new FileTransferEnd { TransferId = transferId, Success = true, Sha256Base64 = sha256Base64 }
        });

        var result = await tcs.Task;
        _transferEndWaiters.TryRemove(transferId, out _);
        _progressReporters.TryRemove(transferId, out _);

        if (result.FileTransferEnd?.Success == false)
            throw new IOException($"Upload failed: {result.FileTransferEnd.ErrorMessage}");
    }

    public async Task DownloadAsync(string remoteRootId, string remoteRelativePath, string localPath, IProgress<double>? progress, CancellationToken ct)
    {
        var transferId = Guid.NewGuid().ToString("N");
        var tcs = new TaskCompletionSource<RemexMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        _transferEndWaiters[transferId] = tcs;
        _progressReporters[transferId] = progress;

        using var reg = ct.Register(() =>
        {
            tcs.TrySetCanceled(ct);
            _ = _connection.SendAsync(new RemexMessage
            {
                Type = MessageTypes.FileTransferCancel,
                FileTransferCancel = new FileTransferCancel { TransferId = transferId }
            });
        });

        await using var fileStream = new FileStream(localPath, FileMode.Create, FileAccess.Write, FileShare.None, 65536, useAsync: true);

        // Unbounded channel ensures chunk ordering: the consumer writes sequentially,
        // eliminating the race condition that would exist with fire-and-forget WriteAsync.
        var channel = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true });
        _downloadChannels[transferId] = channel;
        var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        _downloadHashers[transferId] = hasher;

        var writeTask = Task.Run(async () =>
        {
            await foreach (var data in channel.Reader.ReadAllAsync())
                await fileStream.WriteAsync(data);
        }, ct);

        await _connection.SendAsync(new RemexMessage
        {
            Type = MessageTypes.FileTransferStart,
            FileTransferStart = new FileTransferStart
            {
                TransferId = transferId,
                Direction = "download",
                RemotePath = remoteRelativePath,
                RemoteRootId = remoteRootId,
                RemoteRelativePath = remoteRelativePath,
                FileName = Path.GetFileName(remoteRelativePath),
                TotalBytes = 0,
                Sha256Base64 = string.Empty
            }
        });

        var result = await tcs.Task;

        // Signal the consumer that no more chunks are coming, then wait for all writes to complete
        // before the fileStream is disposed — prevents ObjectDisposedException on pending writes.
        channel.Writer.TryComplete();
        try { await writeTask; } catch (OperationCanceledException) { }

        _downloadChannels.TryRemove(transferId, out _);
        _transferEndWaiters.TryRemove(transferId, out _);
        _progressReporters.TryRemove(transferId, out _);
        _downloadHashers.TryRemove(transferId, out _);

        if (result.FileTransferEnd?.Success == false)
        {
            hasher.Dispose();
            try { File.Delete(localPath); } catch { /* best-effort */ }
            throw new IOException($"Download failed: {result.FileTransferEnd.ErrorMessage}");
        }

        // Verify the host-supplied SHA-256 against the bytes we actually received.
        // Master plan §996 calls this out as the recommended download-side parity
        // with upload integrity verification.
        var expectedHash = result.FileTransferEnd?.Sha256Base64;
        var actualHash = Convert.ToBase64String(hasher.GetHashAndReset());
        hasher.Dispose();
        if (!string.IsNullOrEmpty(expectedHash) && expectedHash != actualHash)
        {
            try { File.Delete(localPath); } catch { /* best-effort */ }
            throw new IOException("Download failed: SHA-256 integrity check failed.");
        }
    }

    private void OnFileTransferMessage(RemexMessage message)
    {
        switch (message.Type)
        {
            case MessageTypes.FileRootsResponse:
                _rootsWaiter?.TrySetResult(message);
                break;

            case MessageTypes.FileBrowseResponse when message.FileBrowseResponse is { } resp:
                if (_browseWaiters.TryGetValue(resp.RequestId, out var browseTcs))
                    browseTcs.TrySetResult(message);
                break;

            case MessageTypes.FileTransferChunk when message.FileTransferChunk is { } chunk:
                if (_downloadChannels.TryGetValue(chunk.TransferId, out var ch))
                {
                    var bytes = Convert.FromBase64String(chunk.DataBase64);
                    if (_downloadHashers.TryGetValue(chunk.TransferId, out var hasher))
                        hasher.AppendData(bytes);
                    ch.Writer.TryWrite(bytes);
                }
                break;

            case MessageTypes.FileTransferProgress when message.FileTransferProgress is { } prog:
                if (_progressReporters.TryGetValue(prog.TransferId, out var reporter) && prog.TotalBytes > 0)
                    reporter?.Report((double)prog.BytesTransferred / prog.TotalBytes);
                break;

            case MessageTypes.FileTransferEnd when message.FileTransferEnd is { } end:
                if (_transferEndWaiters.TryGetValue(end.TransferId, out var endTcs))
                    endTcs.TrySetResult(message);
                break;

            case MessageTypes.FileManageResponse when message.FileManageResponse is { } manage:
                if (_manageWaiters.TryGetValue(manage.RequestId, out var manageTcs))
                    manageTcs.TrySetResult(message);
                break;

            case MessageTypes.FileHashResponse when message.FileHashResponse is { } hashResp:
                if (_hashWaiters.TryGetValue(hashResp.RequestId, out var hashTcs))
                    hashTcs.TrySetResult(message);
                break;

            case MessageTypes.FileRootManageResponse when message.FileRootManageResponse is { } rootManage:
                if (_rootManageWaiters.TryGetValue(rootManage.RequestId, out var rootManageTcs))
                    rootManageTcs.TrySetResult(message);
                break;

            // ── 2.1 File Sharing Overhaul (protocolVersion 3) responses ──
            case MessageTypes.FileVolumesResponse when message.FileVolumesResponse is { } volumes:
                if (_volumesWaiters.TryGetValue(volumes.RequestId, out var volumesTcs))
                    volumesTcs.TrySetResult(message);
                break;

            case MessageTypes.FileSearchResponse when message.FileSearchResponse is { } search:
                if (_searchWaiters.TryGetValue(search.RequestId, out var searchTcs))
                    searchTcs.TrySetResult(message);
                break;

            case MessageTypes.FileMetadataResponse when message.FileMetadataResponse is { } metadata:
                if (_metadataWaiters.TryGetValue(metadata.RequestId, out var metadataTcs))
                    metadataTcs.TrySetResult(message);
                break;

            case MessageTypes.FileThumbnailResponse when message.FileThumbnailResponse is { } thumbnail:
                if (_thumbnailWaiters.TryGetValue(thumbnail.RequestId, out var thumbnailTcs))
                    thumbnailTcs.TrySetResult(message);
                break;
        }
    }

    public void Dispose()
    {
        _connection.FileTransferMessageReceived -= OnFileTransferMessage;
    }
}
