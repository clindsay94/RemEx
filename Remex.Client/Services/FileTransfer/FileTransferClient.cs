using System.Collections.Concurrent;
using System.IO;
using System.Security.Cryptography;
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
    private readonly ConcurrentDictionary<string, TaskCompletionSource<RemexMessage>> _browseWaiters = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<RemexMessage>> _transferEndWaiters = new();
    private readonly ConcurrentDictionary<string, IProgress<double>?> _progressReporters = new();

    public FileTransferClient(ConnectionViewModel connection)
    {
        _connection = connection;
        _connection.FileTransferMessageReceived += OnFileTransferMessage;
    }

    public async Task<IReadOnlyList<FileEntry>> BrowseRemoteAsync(string path, CancellationToken ct)
    {
        var requestId = Guid.NewGuid().ToString("N");
        var tcs = new TaskCompletionSource<RemexMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        _browseWaiters[requestId] = tcs;

        using var reg = ct.Register(() => tcs.TrySetCanceled(ct));

        await _connection.SendAsync(new RemexMessage
        {
            Type = MessageTypes.FileBrowseRequest,
            FileBrowseRequest = new FileBrowseRequest { RequestId = requestId, Path = path }
        });

        var response = await tcs.Task;
        _browseWaiters.TryRemove(requestId, out _);

        if (response.FileBrowseResponse?.ErrorMessage is string err)
            throw new IOException($"Browse error: {err}");

        return response.FileBrowseResponse?.Entries ?? [];
    }

    public async Task UploadAsync(string localPath, string remotePath, IProgress<double>? progress, CancellationToken ct)
    {
        var transferId = Guid.NewGuid().ToString("N");

        await using var fileStream = new FileStream(localPath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, useAsync: true);
        var totalBytes = fileStream.Length;

        string sha256Base64;
        using (var hasher = SHA256.Create())
        {
            var hashBytes = await hasher.ComputeHashAsync(fileStream, ct);
            sha256Base64 = Convert.ToBase64String(hashBytes);
            fileStream.Seek(0, SeekOrigin.Begin);
        }

        var tcs = new TaskCompletionSource<RemexMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        _transferEndWaiters[transferId] = tcs;
        _progressReporters[transferId] = progress;

        using var reg = ct.Register(() => tcs.TrySetCanceled(ct));

        await _connection.SendAsync(new RemexMessage
        {
            Type = MessageTypes.FileTransferStart,
            FileTransferStart = new FileTransferStart
            {
                TransferId = transferId,
                Direction = "upload",
                RemotePath = remotePath,
                FileName = Path.GetFileName(localPath),
                TotalBytes = totalBytes,
                Sha256Base64 = sha256Base64
            }
        });

        const int chunkSize = 65536;
        var buffer = new byte[chunkSize];
        int read;
        long offset = 0;

        while ((read = await fileStream.ReadAsync(buffer.AsMemory(0, chunkSize), ct)) > 0)
        {
            await _connection.SendAsync(new RemexMessage
            {
                Type = MessageTypes.FileTransferChunk,
                FileTransferChunk = new FileTransferChunk
                {
                    TransferId = transferId,
                    Offset = offset,
                    DataBase64 = Convert.ToBase64String(buffer[..read])
                }
            });
            offset += read;
        }

        await _connection.SendAsync(new RemexMessage
        {
            Type = MessageTypes.FileTransferEnd,
            FileTransferEnd = new FileTransferEnd { TransferId = transferId, Success = true }
        });

        var result = await tcs.Task;
        _transferEndWaiters.TryRemove(transferId, out _);
        _progressReporters.TryRemove(transferId, out _);

        if (result.FileTransferEnd?.Success == false)
            throw new IOException($"Upload failed: {result.FileTransferEnd.ErrorMessage}");
    }

    public async Task DownloadAsync(string remotePath, string localPath, IProgress<double>? progress, CancellationToken ct)
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

        // Install a chunk handler for this transfer
        var chunkWaiters = new ConcurrentDictionary<string, FileStream>();
        chunkWaiters[transferId] = fileStream;
        _downloadStreams[transferId] = fileStream;

        await _connection.SendAsync(new RemexMessage
        {
            Type = MessageTypes.FileTransferStart,
            FileTransferStart = new FileTransferStart
            {
                TransferId = transferId,
                Direction = "download",
                RemotePath = remotePath,
                FileName = Path.GetFileName(remotePath),
                TotalBytes = 0,
                Sha256Base64 = string.Empty
            }
        });

        var result = await tcs.Task;
        _downloadStreams.TryRemove(transferId, out _);
        _transferEndWaiters.TryRemove(transferId, out _);
        _progressReporters.TryRemove(transferId, out _);

        if (result.FileTransferEnd?.Success == false)
        {
            try { File.Delete(localPath); } catch { /* best-effort */ }
            throw new IOException($"Download failed: {result.FileTransferEnd.ErrorMessage}");
        }
    }

    private readonly ConcurrentDictionary<string, FileStream> _downloadStreams = new();

    private void OnFileTransferMessage(RemexMessage message)
    {
        switch (message.Type)
        {
            case MessageTypes.FileBrowseResponse when message.FileBrowseResponse is { } resp:
                if (_browseWaiters.TryGetValue(resp.RequestId, out var browseTcs))
                    browseTcs.TrySetResult(message);
                break;

            case MessageTypes.FileTransferChunk when message.FileTransferChunk is { } chunk:
                if (_downloadStreams.TryGetValue(chunk.TransferId, out var stream))
                {
                    var data = Convert.FromBase64String(chunk.DataBase64);
                    _ = stream.WriteAsync(data).AsTask();
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
        }
    }

    public void Dispose()
    {
        _connection.FileTransferMessageReceived -= OnFileTransferMessage;
    }
}
