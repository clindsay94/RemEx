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
using Remex.Client.ViewModels;

namespace Remex.Client.Services.FileTransfer;

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

        var response = await tcs.Task;
        if (ReferenceEquals(_rootsWaiter, tcs))
            _rootsWaiter = null;

        if (response.FileRootsResponse?.ErrorMessage is string err && !string.IsNullOrWhiteSpace(err))
            throw new IOException($"Root listing error: {err}");

        return response.FileRootsResponse?.Roots ?? [];
    }

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

        var response = await tcs.Task;
        _browseWaiters.TryRemove(requestId, out _);

        if (response.FileBrowseResponse?.ErrorMessage is string err)
            throw new IOException($"Browse error: {err}");

        return response.FileBrowseResponse?.Entries ?? [];
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

        if (result.FileTransferEnd?.Success == false)
        {
            try { File.Delete(localPath); } catch { /* best-effort */ }
            throw new IOException($"Download failed: {result.FileTransferEnd.ErrorMessage}");
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
                    ch.Writer.TryWrite(Convert.FromBase64String(chunk.DataBase64));
                break;

            case MessageTypes.FileTransferProgress when message.FileTransferProgress is { } prog:
                if (_progressReporters.TryGetValue(prog.TransferId, out var reporter) && prog.TotalBytes > 0)
                    reporter?.Report((double)prog.BytesTransferred / prog.TotalBytes);
                break;

            case MessageTypes.FileTransferEnd when message.FileTransferEnd is { } end:
                if (_transferEndWaiters.TryGetValue(end.TransferId, out var endTcs))
                    endTcs.TrySetResult(message);
                break;
        }
    }

    public void Dispose()
    {
        _connection.FileTransferMessageReceived -= OnFileTransferMessage;
    }
}
