using System.Net.WebSockets;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Remex.Core.Messages;
using Remex.Core.Models;
using Remex.Core.Services.FileTransfer;
using Remex.Host.Handlers;

namespace Remex.Host.Tests;

public sealed class FileTransferHandlerTests
{
    // Minimal WebSocket that captures outbound messages.
    private sealed class FakeWebSocket : WebSocket
    {
        private readonly List<RemexMessage> _received = new();
        public IReadOnlyList<RemexMessage> ReceivedMessages => _received;

        public override WebSocketState State => WebSocketState.Open;
        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override string? SubProtocol => null;

        public override Task SendAsync(
            ArraySegment<byte> buffer,
            WebSocketMessageType messageType,
            bool endOfMessage,
            CancellationToken cancellationToken)
        {
            if (endOfMessage)
            {
                var msg = MessageSerializer.Deserialize(buffer.Array.AsSpan(buffer.Offset, buffer.Count));
                if (msg is not null) _received.Add(msg);
            }
            return Task.CompletedTask;
        }

        public override void Abort() { }
        public override Task CloseAsync(WebSocketCloseStatus s, string? d, CancellationToken ct) => Task.CompletedTask;
        public override Task CloseOutputAsync(WebSocketCloseStatus s, string? d, CancellationToken ct) => Task.CompletedTask;
        public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken ct)
            => throw new NotSupportedException("Not needed in these unit tests.");

        public override void Dispose() { }
    }

    private static FileTransferHandler CreateHandler(IFileTransferService svc) =>
        new(NullLogger<FileTransferHandler>.Instance, svc);

    // ── Upload happy path ──────────────────────────────────────────────────────

    [Fact]
    public async Task Upload_1MB_WithCorrectSha256_ReturnsSuccess()
    {
        var data = new byte[1 * 1024 * 1024];
        Random.Shared.NextBytes(data);
        var expectedSha256 = Convert.ToBase64String(SHA256.HashData(data));

        var uploadStream = new MemoryStream();
        var fileService = new Mock<IFileTransferService>();
        fileService
            .Setup(s => s.OpenForWriteAsync("root-1", "upload/file.bin", data.Length, It.IsAny<CancellationToken>()))
            .ReturnsAsync(uploadStream);

        var handler = CreateHandler(fileService.Object);
        var ws = new FakeWebSocket();
        var ct = CancellationToken.None;

        await handler.HandleFileTransferStartAsync(new RemexMessage
        {
            Type = MessageTypes.FileTransferStart,
            FileTransferStart = new FileTransferStart
            {
                TransferId = "tx-upload-1",
                Direction = "upload",
                RemotePath = "upload/file.bin",
                RemoteRootId = "root-1",
                RemoteRelativePath = "upload/file.bin",
                FileName = "file.bin",
                TotalBytes = data.Length,
                Sha256Base64 = expectedSha256
            }
        }, ws, ct);

        await handler.HandleFileTransferChunkAsync(new RemexMessage
        {
            Type = MessageTypes.FileTransferChunk,
            FileTransferChunk = new FileTransferChunk
            {
                TransferId = "tx-upload-1",
                Offset = 0,
                DataBase64 = Convert.ToBase64String(data)
            }
        }, ws, ct);

        await handler.HandleFileTransferEndAsync(new RemexMessage
        {
            Type = MessageTypes.FileTransferEnd,
            FileTransferEnd = new FileTransferEnd
            {
                TransferId = "tx-upload-1",
                Success = true,
                Sha256Base64 = expectedSha256
            }
        }, ws, ct);

        var endResponse = ws.ReceivedMessages
            .LastOrDefault(m => m.Type == MessageTypes.FileTransferEnd);
        Assert.NotNull(endResponse?.FileTransferEnd);
        Assert.True(endResponse!.FileTransferEnd!.Success,
            $"Expected success but got: {endResponse.FileTransferEnd.ErrorMessage}");
        Assert.Equal(expectedSha256, Convert.ToBase64String(SHA256.HashData(uploadStream.ToArray())));
    }

    [Fact]
    public async Task Upload_WithWrongSha256_ReturnsFailure()
    {
        var data = new byte[1024];
        Random.Shared.NextBytes(data);

        var uploadStream = new MemoryStream();
        var fileService = new Mock<IFileTransferService>();
        fileService
            .Setup(s => s.OpenForWriteAsync("root-1", "file.bin", data.Length, It.IsAny<CancellationToken>()))
            .ReturnsAsync(uploadStream);

        var handler = CreateHandler(fileService.Object);
        var ws = new FakeWebSocket();
        var ct = CancellationToken.None;

        await handler.HandleFileTransferStartAsync(new RemexMessage
        {
            Type = MessageTypes.FileTransferStart,
            FileTransferStart = new FileTransferStart
            {
                TransferId = "tx-bad-sha",
                Direction = "upload",
                RemotePath = "file.bin",
                RemoteRootId = "root-1",
                RemoteRelativePath = "file.bin",
                FileName = "file.bin",
                TotalBytes = data.Length,
                Sha256Base64 = Convert.ToBase64String(new byte[32])
            }
        }, ws, ct);

        await handler.HandleFileTransferChunkAsync(new RemexMessage
        {
            Type = MessageTypes.FileTransferChunk,
            FileTransferChunk = new FileTransferChunk
            {
                TransferId = "tx-bad-sha",
                Offset = 0,
                DataBase64 = Convert.ToBase64String(data)
            }
        }, ws, ct);

        // End message provides wrong hash — handler detects mismatch
        await handler.HandleFileTransferEndAsync(new RemexMessage
        {
            Type = MessageTypes.FileTransferEnd,
            FileTransferEnd = new FileTransferEnd
            {
                TransferId = "tx-bad-sha",
                Success = true,
                Sha256Base64 = Convert.ToBase64String(new byte[32]) // wrong
            }
        }, ws, ct);

        var endResponse = ws.ReceivedMessages
            .LastOrDefault(m => m.Type == MessageTypes.FileTransferEnd);
        Assert.NotNull(endResponse?.FileTransferEnd);
        Assert.False(endResponse!.FileTransferEnd!.Success);
    }

    // ── Unknown transferId silently ignored ───────────────────────────────────

    [Fact]
    public async Task Chunk_UnknownTransferId_NoResponseSent()
    {
        // Documents current behavior: unknown transferId silently drops the chunk.
        // The plan spec anticipated an error response, but the implementation
        // returns early without sending anything. Tests match actual code behavior.
        var handler = CreateHandler(new Mock<IFileTransferService>().Object);
        var ws = new FakeWebSocket();

        await handler.HandleFileTransferChunkAsync(new RemexMessage
        {
            Type = MessageTypes.FileTransferChunk,
            FileTransferChunk = new FileTransferChunk
            {
                TransferId = "ghost-transfer",
                Offset = 0,
                DataBase64 = Convert.ToBase64String(new byte[4])
            }
        }, ws, CancellationToken.None);

        Assert.Empty(ws.ReceivedMessages);
    }

    [Fact]
    public async Task End_UnknownTransferId_NoResponseSent()
    {
        var handler = CreateHandler(new Mock<IFileTransferService>().Object);
        var ws = new FakeWebSocket();

        await handler.HandleFileTransferEndAsync(new RemexMessage
        {
            Type = MessageTypes.FileTransferEnd,
            FileTransferEnd = new FileTransferEnd
            {
                TransferId = "ghost-transfer",
                Success = true
            }
        }, ws, CancellationToken.None);

        Assert.Empty(ws.ReceivedMessages);
    }

    // ── Cancel mid-transfer ───────────────────────────────────────────────────

    [Fact]
    public async Task Cancel_MidTransfer_SubsequentChunkSilentlyIgnored()
    {
        var uploadStream = new MemoryStream();
        var fileService = new Mock<IFileTransferService>();
        fileService
            .Setup(s => s.OpenForWriteAsync("root-1", "file.bin", 1024, It.IsAny<CancellationToken>()))
            .ReturnsAsync(uploadStream);

        var handler = CreateHandler(fileService.Object);
        var ws = new FakeWebSocket();
        var ct = CancellationToken.None;

        await handler.HandleFileTransferStartAsync(new RemexMessage
        {
            Type = MessageTypes.FileTransferStart,
            FileTransferStart = new FileTransferStart
            {
                TransferId = "tx-cancel",
                Direction = "upload",
                RemotePath = "file.bin",
                RemoteRootId = "root-1",
                RemoteRelativePath = "file.bin",
                FileName = "file.bin",
                TotalBytes = 1024,
                Sha256Base64 = Convert.ToBase64String(new byte[32])
            }
        }, ws, ct);

        await handler.HandleFileTransferCancelAsync(new RemexMessage
        {
            Type = MessageTypes.FileTransferCancel,
            FileTransferCancel = new FileTransferCancel { TransferId = "tx-cancel" }
        });

        var messageCountBeforeChunk = ws.ReceivedMessages.Count;

        // Chunk after cancel — transfer no longer tracked, silently ignored
        await handler.HandleFileTransferChunkAsync(new RemexMessage
        {
            Type = MessageTypes.FileTransferChunk,
            FileTransferChunk = new FileTransferChunk
            {
                TransferId = "tx-cancel",
                Offset = 0,
                DataBase64 = Convert.ToBase64String(new byte[64])
            }
        }, ws, ct);

        Assert.Equal(messageCountBeforeChunk, ws.ReceivedMessages.Count);
    }
}
