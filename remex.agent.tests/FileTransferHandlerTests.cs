using System.Net.WebSockets;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Remex.Core.Messages;
using Remex.Core.Models;
using Remex.Core.Services.FileTransfer;
using Remex.Agent.Handlers;
using Remex.Agent.Services.FileTransfer;
using SkiaSharp;

namespace Remex.Agent.Tests;

public sealed class FileTransferHandlerTests : IDisposable
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
        new(NullLogger<FileTransferHandler>.Instance, svc,
            new Mock<IFileTrustService>().Object,
            new VolumeEnumerator(NullLogger<VolumeEnumerator>.Instance));

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

    // ──────────────────────────────────────────────────────────────────────────
    // 2.1 File Sharing Overhaul (protocolVersion 3) — WP3: file-manager ops.
    // These exercise the real FileTransferService against a temp shared root (the
    // copy/move/mkdir/search/metadata logic lives in the service, not the handler),
    // driven end-to-end through the handler's message dispatch + error wrapping.
    // ──────────────────────────────────────────────────────────────────────────

    private readonly List<string> _tempDirs = new();

    private (FileTransferHandler handler, FileTransferService service, string rootDir) CreateRealServiceHandler(
        bool isWritable = true, bool canRename = true, bool canMove = true, bool canDelete = true)
    {
        var baseTemp = Path.Combine(Path.GetTempPath(), "remex-wp3-" + Guid.NewGuid().ToString("N"));
        var rootDir = Path.Combine(baseTemp, "root");
        Directory.CreateDirectory(rootDir);
        _tempDirs.Add(baseTemp);

        var configPath = Path.Combine(baseTemp, "roots.json");
        var service = new FileTransferService(NullLogger<FileTransferService>.Instance, configPath);
        service.SeedRootsForTests(("root-1", "Test Root", rootDir, isWritable, canRename, canMove, canDelete, false));

        return (CreateHandler(service), service, rootDir);
    }

    private static RemexMessage Manage(
        string op, string rel, string? dest = null, string? newName = null, bool overwrite = false, string rootId = "root-1")
        => new()
        {
            Type = MessageTypes.FileManageRequest,
            FileManageRequest = new FileManageRequest
            {
                RequestId = "req-1",
                RootId = rootId,
                RelativePath = rel,
                Operation = op,
                NewName = newName,
                DestinationPath = dest,
                Overwrite = overwrite,
            }
        };

    private static FileManageResponse LastManage(FakeWebSocket ws)
        => ws.ReceivedMessages.Last(m => m.Type == MessageTypes.FileManageResponse).FileManageResponse!;

    private static FileSearchResponse LastSearch(FakeWebSocket ws)
        => ws.ReceivedMessages.Last(m => m.Type == MessageTypes.FileSearchResponse).FileSearchResponse!;

    private static FileMetadataResponse LastMetadata(FakeWebSocket ws)
        => ws.ReceivedMessages.Last(m => m.Type == MessageTypes.FileMetadataResponse).FileMetadataResponse!;

    // ── Copy ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Copy_File_HappyPath_CreatesDestinationAndKeepsSource()
    {
        var (handler, _, rootDir) = CreateRealServiceHandler();
        const string content = "hello wp3 copy";
        await File.WriteAllTextAsync(Path.Combine(rootDir, "a.txt"), content);
        var ws = new FakeWebSocket();

        await handler.HandleFileManageRequestAsync(
            Manage(FileManageOperations.Copy, "a.txt", dest: "b.txt"), ws, CancellationToken.None);

        var resp = LastManage(ws);
        Assert.True(resp.Success, resp.ErrorMessage);
        Assert.True(File.Exists(Path.Combine(rootDir, "a.txt")));
        Assert.True(File.Exists(Path.Combine(rootDir, "b.txt")));
        Assert.Equal(content, await File.ReadAllTextAsync(Path.Combine(rootDir, "b.txt")));
    }

    [Fact]
    public async Task Copy_DestinationEscapesRoot_ReturnsFailure()
    {
        var (handler, _, rootDir) = CreateRealServiceHandler();
        await File.WriteAllTextAsync(Path.Combine(rootDir, "a.txt"), "x");
        var ws = new FakeWebSocket();

        await handler.HandleFileManageRequestAsync(
            Manage(FileManageOperations.Copy, "a.txt", dest: "../escaped.txt"), ws, CancellationToken.None);

        var resp = LastManage(ws);
        Assert.False(resp.Success);
        // The escaped destination must NOT have been written above the root.
        Assert.False(File.Exists(Path.Combine(rootDir, "..", "escaped.txt")));
    }

    [Fact]
    public async Task Copy_OverwriteConflict_RespectsOverwriteFlag()
    {
        var (handler, _, rootDir) = CreateRealServiceHandler();
        await File.WriteAllTextAsync(Path.Combine(rootDir, "a.txt"), "source");
        await File.WriteAllTextAsync(Path.Combine(rootDir, "b.txt"), "existing");
        var ws = new FakeWebSocket();

        // overwrite = false → conflict, fails, existing content preserved.
        await handler.HandleFileManageRequestAsync(
            Manage(FileManageOperations.Copy, "a.txt", dest: "b.txt", overwrite: false), ws, CancellationToken.None);
        Assert.False(LastManage(ws).Success);
        Assert.Equal("existing", await File.ReadAllTextAsync(Path.Combine(rootDir, "b.txt")));

        // overwrite = true → succeeds, destination replaced.
        var ws2 = new FakeWebSocket();
        await handler.HandleFileManageRequestAsync(
            Manage(FileManageOperations.Copy, "a.txt", dest: "b.txt", overwrite: true), ws2, CancellationToken.None);
        Assert.True(LastManage(ws2).Success);
        Assert.Equal("source", await File.ReadAllTextAsync(Path.Combine(rootDir, "b.txt")));
    }

    // ── Move ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Move_File_HappyPath_RemovesSource()
    {
        var (handler, _, rootDir) = CreateRealServiceHandler();
        await File.WriteAllTextAsync(Path.Combine(rootDir, "a.txt"), "movable");
        var ws = new FakeWebSocket();

        await handler.HandleFileManageRequestAsync(
            Manage(FileManageOperations.Move, "a.txt", dest: "sub/b.txt"), ws, CancellationToken.None);

        var resp = LastManage(ws);
        Assert.True(resp.Success, resp.ErrorMessage);
        Assert.False(File.Exists(Path.Combine(rootDir, "a.txt")));
        Assert.True(File.Exists(Path.Combine(rootDir, "sub", "b.txt")));
        Assert.Equal("movable", await File.ReadAllTextAsync(Path.Combine(rootDir, "sub", "b.txt")));
    }

    [Fact]
    public async Task Move_SourceEscapesRoot_ReturnsFailure()
    {
        var (handler, _, _) = CreateRealServiceHandler();
        var ws = new FakeWebSocket();

        await handler.HandleFileManageRequestAsync(
            Manage(FileManageOperations.Move, "../secret.txt", dest: "b.txt"), ws, CancellationToken.None);

        Assert.False(LastManage(ws).Success);
    }

    [Fact]
    public async Task Move_OnReadOnlyRoot_IsDenied()
    {
        var (handler, _, rootDir) = CreateRealServiceHandler(canMove: false);
        await File.WriteAllTextAsync(Path.Combine(rootDir, "a.txt"), "x");
        var ws = new FakeWebSocket();

        await handler.HandleFileManageRequestAsync(
            Manage(FileManageOperations.Move, "a.txt", dest: "b.txt"), ws, CancellationToken.None);

        Assert.False(LastManage(ws).Success);
        Assert.True(File.Exists(Path.Combine(rootDir, "a.txt")));
    }

    // ── Mkdir ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Mkdir_HappyPath_CreatesFolder()
    {
        var (handler, _, rootDir) = CreateRealServiceHandler();
        var ws = new FakeWebSocket();

        await handler.HandleFileManageRequestAsync(
            Manage(FileManageOperations.Mkdir, rel: "", newName: "newfolder"), ws, CancellationToken.None);

        var resp = LastManage(ws);
        Assert.True(resp.Success, resp.ErrorMessage);
        Assert.True(Directory.Exists(Path.Combine(rootDir, "newfolder")));
    }

    [Fact]
    public async Task Mkdir_NestedUnderParent_CreatesFolder()
    {
        var (handler, _, rootDir) = CreateRealServiceHandler();
        Directory.CreateDirectory(Path.Combine(rootDir, "parent"));
        var ws = new FakeWebSocket();

        await handler.HandleFileManageRequestAsync(
            Manage(FileManageOperations.Mkdir, rel: "parent", newName: "child"), ws, CancellationToken.None);

        Assert.True(LastManage(ws).Success);
        Assert.True(Directory.Exists(Path.Combine(rootDir, "parent", "child")));
    }

    [Fact]
    public async Task Mkdir_NameWithTraversal_ReturnsFailure()
    {
        var (handler, _, rootDir) = CreateRealServiceHandler();
        var ws = new FakeWebSocket();

        await handler.HandleFileManageRequestAsync(
            Manage(FileManageOperations.Mkdir, rel: "", newName: "../escaped"), ws, CancellationToken.None);

        Assert.False(LastManage(ws).Success);
        Assert.False(Directory.Exists(Path.Combine(rootDir, "..", "escaped")));
    }

    // ── Search ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Search_MoreThanCap_ReturnsCappedTruncatedResults()
    {
        var (handler, _, rootDir) = CreateRealServiceHandler();
        for (var i = 0; i < 250; i++)
            await File.WriteAllTextAsync(Path.Combine(rootDir, $"match_{i}.dat"), "x");
        var ws = new FakeWebSocket();

        await handler.HandleFileSearchRequestAsync(new RemexMessage
        {
            Type = MessageTypes.FileSearchRequest,
            FileSearchRequest = new FileSearchRequest
            {
                RequestId = "s1",
                RootId = "root-1",
                RelativePath = "",
                Query = "match",
                MaxResults = FileTransferLimits.SearchMaxResults,
            }
        }, ws, CancellationToken.None);

        var resp = LastSearch(ws);
        Assert.Equal(FileTransferLimits.SearchMaxResults, resp.Entries.Length);
        Assert.True(resp.Truncated);
    }

    [Fact]
    public async Task Search_NestedMatch_ReturnsForwardSlashRelativePath()
    {
        var (handler, _, rootDir) = CreateRealServiceHandler();
        Directory.CreateDirectory(Path.Combine(rootDir, "sub"));
        await File.WriteAllTextAsync(Path.Combine(rootDir, "sub", "deepfile.txt"), "x");
        var ws = new FakeWebSocket();

        await handler.HandleFileSearchRequestAsync(new RemexMessage
        {
            Type = MessageTypes.FileSearchRequest,
            FileSearchRequest = new FileSearchRequest
            {
                RequestId = "s1",
                RootId = "root-1",
                RelativePath = "",
                Query = "deepfile",
                MaxResults = 50,
            }
        }, ws, CancellationToken.None);

        var resp = LastSearch(ws);
        Assert.False(resp.Truncated);
        var hit = Assert.Single(resp.Entries);
        Assert.Equal("deepfile.txt", hit.Name);
        Assert.Equal("sub/deepfile.txt", hit.RelativePath);
        Assert.False(hit.IsDirectory);
    }

    // ── Metadata ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Metadata_File_ReturnsSizeAndMimeType()
    {
        var (handler, _, rootDir) = CreateRealServiceHandler();
        var bytes = new byte[1234];
        await File.WriteAllBytesAsync(Path.Combine(rootDir, "doc.txt"), bytes);
        var ws = new FakeWebSocket();

        await handler.HandleFileMetadataRequestAsync(new RemexMessage
        {
            Type = MessageTypes.FileMetadataRequest,
            FileMetadataRequest = new FileMetadataRequest { RequestId = "m1", RootId = "root-1", RelativePath = "doc.txt" }
        }, ws, CancellationToken.None);

        var resp = LastMetadata(ws);
        Assert.Null(resp.ErrorMessage);
        Assert.Equal(1234, resp.Size);
        Assert.False(resp.IsDirectory);
        Assert.Null(resp.ItemCount);
        Assert.Equal("text/plain", resp.MimeType);
        Assert.False(resp.ReadOnly);
    }

    [Fact]
    public async Task Metadata_Directory_ReturnsItemCount()
    {
        var (handler, _, rootDir) = CreateRealServiceHandler();
        var subDir = Path.Combine(rootDir, "folder");
        Directory.CreateDirectory(subDir);
        await File.WriteAllTextAsync(Path.Combine(subDir, "one.txt"), "1");
        await File.WriteAllTextAsync(Path.Combine(subDir, "two.txt"), "2");
        Directory.CreateDirectory(Path.Combine(subDir, "three"));
        var ws = new FakeWebSocket();

        await handler.HandleFileMetadataRequestAsync(new RemexMessage
        {
            Type = MessageTypes.FileMetadataRequest,
            FileMetadataRequest = new FileMetadataRequest { RequestId = "m1", RootId = "root-1", RelativePath = "folder" }
        }, ws, CancellationToken.None);

        var resp = LastMetadata(ws);
        Assert.Null(resp.ErrorMessage);
        Assert.True(resp.IsDirectory);
        Assert.Equal(3, resp.ItemCount);
        Assert.Equal(0, resp.Size);
    }

    [Fact]
    public async Task Metadata_MissingFile_ReturnsError()
    {
        var (handler, _, _) = CreateRealServiceHandler();
        var ws = new FakeWebSocket();

        await handler.HandleFileMetadataRequestAsync(new RemexMessage
        {
            Type = MessageTypes.FileMetadataRequest,
            FileMetadataRequest = new FileMetadataRequest { RequestId = "m1", RootId = "root-1", RelativePath = "nope.txt" }
        }, ws, CancellationToken.None);

        Assert.NotNull(LastMetadata(ws).ErrorMessage);
    }

    // ── Thumbnails (images only in v1) ────────────────────────────────────────

    private static FileThumbnailResponse LastThumbnail(FakeWebSocket ws)
        => ws.ReceivedMessages.Last(m => m.Type == MessageTypes.FileThumbnailResponse).FileThumbnailResponse!;

    [Fact]
    public async Task Thumbnail_ForImage_ReturnsScaledJpegUnderBudget()
    {
        var (handler, _, rootDir) = CreateRealServiceHandler();
        var imagePath = Path.Combine(rootDir, "pic.png");
        using (var bmp = new SKBitmap(300, 200))
        {
            using (var canvas = new SKCanvas(bmp))
            {
                canvas.Clear(SKColors.CornflowerBlue);
                using var paint = new SKPaint { Color = SKColors.Orange };
                canvas.DrawCircle(150, 100, 60, paint);
            }
            using var img = SKImage.FromBitmap(bmp);
            using var data = img.Encode(SKEncodedImageFormat.Png, 100);
            await File.WriteAllBytesAsync(imagePath, data.ToArray());
        }
        var ws = new FakeWebSocket();

        await handler.HandleFileThumbnailRequestAsync(new RemexMessage
        {
            Type = MessageTypes.FileThumbnailRequest,
            FileThumbnailRequest = new FileThumbnailRequest
            {
                RequestId = "t1",
                RootId = "root-1",
                RelativePath = "pic.png",
                MaxDim = 128,
            }
        }, ws, CancellationToken.None);

        var resp = LastThumbnail(ws);
        Assert.Null(resp.ErrorMessage);
        Assert.NotNull(resp.JpegBase64);
        var jpeg = Convert.FromBase64String(resp.JpegBase64!);
        Assert.True(jpeg.Length <= FileTransferLimits.ThumbnailMaxBytes);
        using var thumb = SKBitmap.Decode(jpeg);
        Assert.NotNull(thumb);
        Assert.True(Math.Max(thumb!.Width, thumb.Height) <= 128);
    }

    [Fact]
    public async Task Thumbnail_ForNonImage_ReturnsNullNotError()
    {
        var (handler, _, rootDir) = CreateRealServiceHandler();
        await File.WriteAllTextAsync(Path.Combine(rootDir, "notes.txt"), "definitely not an image");
        var ws = new FakeWebSocket();

        await handler.HandleFileThumbnailRequestAsync(new RemexMessage
        {
            Type = MessageTypes.FileThumbnailRequest,
            FileThumbnailRequest = new FileThumbnailRequest
            {
                RequestId = "t1",
                RootId = "root-1",
                RelativePath = "notes.txt",
                MaxDim = 128,
            }
        }, ws, CancellationToken.None);

        var resp = LastThumbnail(ws);
        Assert.Null(resp.ErrorMessage);
        Assert.Null(resp.JpegBase64);
    }

    public void Dispose()
    {
        foreach (var dir in _tempDirs)
        {
            try
            {
                if (Directory.Exists(dir))
                    Directory.Delete(dir, recursive: true);
            }
            catch
            {
                // Best-effort cleanup of temp fixtures.
            }
        }
    }
}
