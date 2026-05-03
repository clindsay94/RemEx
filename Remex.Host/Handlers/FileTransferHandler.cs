using System.Collections.Concurrent;
using System.IO;
using System.Net.WebSockets;
using System.Security.Cryptography;
using Remex.Core.Messages;
using Remex.Core.Models;
using Remex.Core.Services.FileTransfer;

namespace Remex.Host.Handlers;

public sealed class FileTransferHandler(
    ILogger<FileTransferHandler> logger,
    IFileTransferService fileTransferService)
{
    private const int ProgressChunkInterval = 10;

    private sealed class FileTransferState
    {
        public required string TransferId { get; init; }
        public required string Direction { get; init; }
        public required string RemotePath { get; init; }
        public required string ExpectedSha256 { get; init; }
        public required long TotalBytes { get; init; }
        public required Stream FileStream { get; init; }
        public required SHA256 Hasher { get; init; }
        public long BytesTransferred { get; set; }
        public int ChunkCount { get; set; }
    }

    private readonly ConcurrentDictionary<string, FileTransferState> _activeTransfers = new();

    public async Task HandleFileBrowseRequestAsync(RemexMessage message, WebSocket ws, CancellationToken ct)
    {
        var req = message.FileBrowseRequest;
        if (req is null) return;

        RemexMessage response;
        try
        {
            var entries = await fileTransferService.BrowseAsync(req.Path, ct);
            response = new RemexMessage
            {
                Type = MessageTypes.FileBrowseResponse,
                FileBrowseResponse = new FileBrowseResponse
                {
                    RequestId = req.RequestId,
                    Path = req.Path,
                    Entries = [.. entries]
                }
            };
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Browse failed for path: {Path}", req.Path);
            response = new RemexMessage
            {
                Type = MessageTypes.FileBrowseResponse,
                FileBrowseResponse = new FileBrowseResponse
                {
                    RequestId = req.RequestId,
                    Path = req.Path,
                    Entries = [],
                    ErrorMessage = ex.Message
                }
            };
        }

        await MessageSerializer.SendAsync(ws, response, ct);
    }

    public async Task HandleFileTransferStartAsync(RemexMessage message, WebSocket ws, CancellationToken ct)
    {
        var start = message.FileTransferStart;
        if (start is null) return;

        try
        {
            Stream stream = start.Direction == "upload"
                ? await fileTransferService.OpenForWriteAsync(start.RemotePath, start.TotalBytes, ct)
                : await fileTransferService.OpenForReadAsync(start.RemotePath, ct);

            var state = new FileTransferState
            {
                TransferId = start.TransferId,
                Direction = start.Direction,
                RemotePath = start.RemotePath,
                ExpectedSha256 = start.Sha256Base64,
                TotalBytes = start.TotalBytes,
                FileStream = stream,
                Hasher = SHA256.Create()
            };
            _activeTransfers[start.TransferId] = state;

            if (start.Direction == "download")
                _ = StreamDownloadAsync(state, ws, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "FileTransferStart failed for {TransferId}", start.TransferId);
            await MessageSerializer.SendAsync(ws, new RemexMessage
            {
                Type = MessageTypes.FileTransferEnd,
                FileTransferEnd = new FileTransferEnd
                {
                    TransferId = start.TransferId,
                    Success = false,
                    ErrorMessage = ex.Message
                }
            }, ct);
        }
    }

    public async Task HandleFileTransferChunkAsync(RemexMessage message, WebSocket ws, CancellationToken ct)
    {
        var chunk = message.FileTransferChunk;
        if (chunk is null || !_activeTransfers.TryGetValue(chunk.TransferId, out var state)) return;

        try
        {
            var data = Convert.FromBase64String(chunk.DataBase64);
            await state.FileStream.WriteAsync(data, ct);
            state.Hasher.TransformBlock(data, 0, data.Length, null, 0);
            state.BytesTransferred += data.Length;
            state.ChunkCount++;

            if (state.ChunkCount % ProgressChunkInterval == 0)
            {
                await MessageSerializer.SendAsync(ws, new RemexMessage
                {
                    Type = MessageTypes.FileTransferProgress,
                    FileTransferProgress = new FileTransferProgress
                    {
                        TransferId = chunk.TransferId,
                        BytesTransferred = state.BytesTransferred,
                        TotalBytes = state.TotalBytes
                    }
                }, ct);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Chunk write failed for {TransferId}", chunk.TransferId);
            await CleanupTransferAsync(chunk.TransferId, deleteFile: true);
        }
    }

    public async Task HandleFileTransferEndAsync(RemexMessage message, WebSocket ws, CancellationToken ct)
    {
        var end = message.FileTransferEnd;
        if (end is null || !_activeTransfers.TryGetValue(end.TransferId, out var state)) return;

        try
        {
            state.Hasher.TransformFinalBlock([], 0, 0);
            var actualHash = Convert.ToBase64String(state.Hasher.Hash!);
            // Accept hash from End message (incremental path) or from Start (legacy path)
            var expectedHash = !string.IsNullOrEmpty(end.Sha256Base64) ? end.Sha256Base64 : state.ExpectedSha256;
            var success = string.IsNullOrEmpty(expectedHash) || actualHash == expectedHash;

            await state.FileStream.FlushAsync(ct);
            await CleanupTransferAsync(end.TransferId, deleteFile: !success);

            await MessageSerializer.SendAsync(ws, new RemexMessage
            {
                Type = MessageTypes.FileTransferEnd,
                FileTransferEnd = new FileTransferEnd
                {
                    TransferId = end.TransferId,
                    Success = success,
                    ErrorMessage = success ? null : "SHA-256 mismatch — file corrupted in transit."
                }
            }, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "FileTransferEnd failed for {TransferId}", end.TransferId);
            await CleanupTransferAsync(end.TransferId, deleteFile: true);
        }
    }

    public async Task HandleFileTransferCancelAsync(RemexMessage message)
    {
        var cancel = message.FileTransferCancel;
        if (cancel is null) return;

        await CleanupTransferAsync(cancel.TransferId, deleteFile: true);
    }

    public async Task CleanupAllTransfersAsync()
    {
        foreach (var id in _activeTransfers.Keys.ToList())
            await CleanupTransferAsync(id, deleteFile: true);
    }

    private async Task CleanupTransferAsync(string transferId, bool deleteFile)
    {
        if (!_activeTransfers.TryRemove(transferId, out var state)) return;

        try { await state.FileStream.DisposeAsync(); } catch { /* best-effort */ }
        state.Hasher.Dispose();

        if (deleteFile && state.Direction == "upload" && File.Exists(state.RemotePath))
        {
            try { File.Delete(state.RemotePath); }
            catch (Exception ex) { logger.LogWarning(ex, "Failed to delete partial upload: {Path}", state.RemotePath); }
        }
    }

    private async Task StreamDownloadAsync(FileTransferState state, WebSocket ws, CancellationToken ct)
    {
        const int chunkSize = 65536;
        var buffer = new byte[chunkSize];
        int read;
        int chunkCount = 0;

        try
        {
            while ((read = await state.FileStream.ReadAsync(buffer.AsMemory(0, chunkSize), ct)) > 0)
            {
                var slice = buffer[..read];
                state.Hasher.TransformBlock(slice, 0, read, null, 0);
                state.BytesTransferred += read;
                chunkCount++;

                await MessageSerializer.SendAsync(ws, new RemexMessage
                {
                    Type = MessageTypes.FileTransferChunk,
                    FileTransferChunk = new FileTransferChunk
                    {
                        TransferId = state.TransferId,
                        Offset = state.BytesTransferred - read,
                        DataBase64 = Convert.ToBase64String(slice)
                    }
                }, ct);

                if (chunkCount % ProgressChunkInterval == 0)
                {
                    await MessageSerializer.SendAsync(ws, new RemexMessage
                    {
                        Type = MessageTypes.FileTransferProgress,
                        FileTransferProgress = new FileTransferProgress
                        {
                            TransferId = state.TransferId,
                            BytesTransferred = state.BytesTransferred,
                            TotalBytes = state.TotalBytes
                        }
                    }, ct);
                }
            }

            state.Hasher.TransformFinalBlock([], 0, 0);
            var hash = Convert.ToBase64String(state.Hasher.Hash!);

            await MessageSerializer.SendAsync(ws, new RemexMessage
            {
                Type = MessageTypes.FileTransferEnd,
                FileTransferEnd = new FileTransferEnd
                {
                    TransferId = state.TransferId,
                    Success = true
                }
            }, ct);

            // Send sha256 in a follow-up: embed it in the FileTransferStart that the client sent
            // (sha256 is already included in the initial start message from host perspective)
            logger.LogInformation("Download complete for {TransferId}, sha256={Hash}", state.TransferId, hash);
        }
        catch (OperationCanceledException)
        {
            logger.LogInformation("Download cancelled for {TransferId}", state.TransferId);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Download failed for {TransferId}", state.TransferId);
            try
            {
                await MessageSerializer.SendAsync(ws, new RemexMessage
                {
                    Type = MessageTypes.FileTransferEnd,
                    FileTransferEnd = new FileTransferEnd
                    {
                        TransferId = state.TransferId,
                        Success = false,
                        ErrorMessage = ex.Message
                    }
                }, ct);
            }
            catch { /* connection already gone */ }
        }
        finally
        {
            await CleanupTransferAsync(state.TransferId, deleteFile: false);
        }
    }
}
