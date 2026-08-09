using System.Net.WebSockets;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Remex.Core.Messages;
using Remex.Core.Models;
using Remex.Core.Services.FileTransfer;
using Remex.Agent.Handlers;
using Remex.Agent.Services.FileTransfer;
using Remex.Agent.Services.Security;
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

    private static FileTransferHandler CreateHandler(IFileTransferService svc)
    {
        var trust = new Mock<IFileTrustService>().Object;
        var volumes = new VolumeEnumerator(NullLogger<VolumeEnumerator>.Instance);
        return new FileTransferHandler(
            NullLogger<FileTransferHandler>.Instance, svc, trust, volumes,
            new SharedRootReadResolver(svc, trust, volumes));
    }

    // ── The running upload cap (RemEx-9xs1) ────────────────────────────────────
    // THE GUARD THESE PIN HAD NO COVERAGE AT ALL. FileTransferService checks the DECLARED size at open
    // time, but the comment on FileTransferHandler.MaxUploadBytes says exactly why that is not enough:
    // "mobile clients send 0 for unknown content URIs". A declared 0 sails past the open-time check, so
    // the running cap in HandleFileTransferChunkAsync is the only thing between a peer and an unbounded
    // write to a shared root — and it was a const, so proving it meant streaming 5 GB and nobody did.
    //
    // This is the legacy v2 path (file_transfer_start), which bypasses TransferSessionManager entirely.
    // The v3 path has the same two-guard shape — an offer-time range check and a running cap — and both
    // are pinned in TransferSessionManagerTests, including the twin of the declare-zero case below.

    [Fact]
    public async Task Upload_DeclaringZeroBytesThenStreamingPastTheCap_IsAbortedAndSaysSo()
    {
        var (handler, ws, written) = CreateCappedUploadHandler(cap: 1024);

        await StartZeroByteUploadAsync(handler, ws);
        await SendChunkAsync(handler, ws, new byte[2048]);

        var end = ws.ReceivedMessages.LastOrDefault(m => m.Type == MessageTypes.FileTransferEnd);
        Assert.NotNull(end?.FileTransferEnd);
        Assert.False(end!.FileTransferEnd!.Success);
        Assert.Contains("cap", end.FileTransferEnd.ErrorMessage, StringComparison.OrdinalIgnoreCase);

        // THE ASSERTION THAT MATTERS. A refusal that still wrote the bytes would be no refusal at all,
        // and the error message alone cannot tell the difference.
        Assert.Empty(written.ToArray());
    }

    [Fact]
    public async Task Upload_DeclaringZeroBytesAndStayingUnderTheCap_StillWrites()
    {
        // THE OVER-TIGHTENING CONTROL, and not hypothetical: totalBytes=0 is what a phone legitimately
        // sends for a content URI whose length it cannot read. A cap that refused every unknown-length
        // upload would break the ordinary share-sheet send while passing the test above.
        var (handler, ws, written) = CreateCappedUploadHandler(cap: 1024);

        await StartZeroByteUploadAsync(handler, ws);
        await SendChunkAsync(handler, ws, new byte[512]);

        Assert.DoesNotContain(
            ws.ReceivedMessages,
            m => m.Type == MessageTypes.FileTransferEnd && m.FileTransferEnd?.Success == false);
        Assert.Equal(512, written.ToArray().Length);
    }

    private static (FileTransferHandler Handler, FakeWebSocket Socket, MemoryStream Written)
        CreateCappedUploadHandler(long cap)
    {
        var written = new MemoryStream();
        var files = new Mock<IFileTransferService>();
        files.Setup(s => s.ListRootsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<FileSharedRoot>
            {
                new() { RootId = "root-1", DisplayName = "Root 1", IsWritable = true },
            });
        // Declared size 0, so the open-time cap in FileTransferService cannot object — which is the
        // whole premise. The running cap is on its own from here.
        files.Setup(s => s.OpenForWriteAsync("root-1", "in/file.bin", 0L, It.IsAny<CancellationToken>()))
            .ReturnsAsync(written);

        var trust = new Mock<IFileTrustService>().Object;
        var volumes = new VolumeEnumerator(NullLogger<VolumeEnumerator>.Instance);
        var handler = new FileTransferHandler(
            NullLogger<FileTransferHandler>.Instance, files.Object, trust, volumes,
            new SharedRootReadResolver(files.Object, trust, volumes))
        {
            MaxUploadBytes = cap,
        };

        return (handler, new FakeWebSocket(), written);
    }

    private static Task StartZeroByteUploadAsync(FileTransferHandler handler, FakeWebSocket ws)
        => handler.HandleFileTransferStartAsync(new RemexMessage
        {
            Type = MessageTypes.FileTransferStart,
            FileTransferStart = new FileTransferStart
            {
                TransferId = "tx-cap",
                Direction = "upload",
                RemotePath = "in/file.bin",
                RemoteRootId = "root-1",
                RemoteRelativePath = "in/file.bin",
                FileName = "file.bin",
                TotalBytes = 0,
                Sha256Base64 = string.Empty,
            }
        }, ws, "client-a", CancellationToken.None);

    private static Task SendChunkAsync(FileTransferHandler handler, FakeWebSocket ws, byte[] payload)
        => handler.HandleFileTransferChunkAsync(new RemexMessage
        {
            Type = MessageTypes.FileTransferChunk,
            FileTransferChunk = new FileTransferChunk
            {
                TransferId = "tx-cap",
                Offset = 0,
                DataBase64 = Convert.ToBase64String(payload),
            }
        }, ws, CancellationToken.None);

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
            .Setup(s => s.ListRootsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<FileSharedRoot> { new() { RootId = "root-1", DisplayName = "Root 1", IsWritable = true } });
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
        }, ws, null, ct);

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
        }, ws, null, ct);

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
        }, ws, null, ct);

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
        string op, string rel, string? dest = null, string? newName = null, bool overwrite = false,
        string rootId = "root-1", string? conflictResolution = null)
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
                ConflictResolution = conflictResolution,
            }
        };

    private static FileManageResponse LastManage(FakeWebSocket ws)
        => ws.ReceivedMessages.Last(m => m.Type == MessageTypes.FileManageResponse).FileManageResponse!;

    private static FileSearchResponse LastSearch(FakeWebSocket ws)
        => ws.ReceivedMessages.Last(m => m.Type == MessageTypes.FileSearchResponse).FileSearchResponse!;

    private static FileMetadataResponse LastMetadata(FakeWebSocket ws)
        => ws.ReceivedMessages.Last(m => m.Type == MessageTypes.FileMetadataResponse).FileMetadataResponse!;

    // ── Conflict codes on the wire (RemEx-6vd8) ───────────────────────────────
    //
    // THESE EXIST BECAUSE THE SERVICE-LEVEL TESTS COULD NOT FAIL FOR THE RIGHT REASON. Review
    // measured it: deleting the three assignments in the handler's response left the whole suite
    // green, because everything else asserts against FileTransferService directly. The bead's
    // central warning is about a field that ships unset, and only a test reading the RESPONSE can
    // detect that.

    [Fact]
    public async Task Copy_Collision_ResponseCarriesTheMachineReadableCode()
    {
        var (handler, _, rootDir) = CreateRealServiceHandler();
        await File.WriteAllTextAsync(Path.Combine(rootDir, "a.txt"), "source");
        await File.WriteAllTextAsync(Path.Combine(rootDir, "b.txt"), "victim");
        var ws = new FakeWebSocket();

        await handler.HandleFileManageRequestAsync(
            Manage(FileManageOperations.Copy, "a.txt", dest: "b.txt"), ws, CancellationToken.None);

        var resp = LastManage(ws);
        Assert.False(resp.Success);
        Assert.Equal(FileTransferErrorCodes.DestinationExists, resp.ErrorCode);
        Assert.Equal("b.txt", resp.ConflictingName);

        // The prose is kept alongside, never replaced - it is what an older client still shows.
        Assert.False(string.IsNullOrWhiteSpace(resp.ErrorMessage));
    }

    [Fact]
    public async Task Copy_KeepBoth_ResponseReportsTheNameTheHostActuallyUsed()
    {
        var (handler, _, rootDir) = CreateRealServiceHandler();
        await File.WriteAllTextAsync(Path.Combine(rootDir, "a.txt"), "source");
        await File.WriteAllTextAsync(Path.Combine(rootDir, "b.txt"), "keep me");
        var ws = new FakeWebSocket();

        await handler.HandleFileManageRequestAsync(
            Manage(FileManageOperations.Copy, "a.txt", dest: "b.txt",
                conflictResolution: FileConflictResolutions.KeepBoth),
            ws, CancellationToken.None);

        var resp = LastManage(ws);
        Assert.True(resp.Success, resp.ErrorMessage);

        // A keep-both that succeeded SILENTLY would leave the user believing they have b.txt when
        // the file on disk is b (2).txt.
        Assert.Equal("b (2).txt", resp.ResolvedName);
        Assert.Equal("keep me", await File.ReadAllTextAsync(Path.Combine(rootDir, "b.txt")));
        Assert.Equal("source", await File.ReadAllTextAsync(Path.Combine(rootDir, "b (2).txt")));
    }

    [Fact]
    public async Task Copy_NoCollision_ReportsNeitherCodeNorResolvedName()
    {
        // The negative control. Without it, a handler that stamped a code onto every response would
        // satisfy the two tests above.
        var (handler, _, rootDir) = CreateRealServiceHandler();
        await File.WriteAllTextAsync(Path.Combine(rootDir, "a.txt"), "source");
        var ws = new FakeWebSocket();

        await handler.HandleFileManageRequestAsync(
            Manage(FileManageOperations.Copy, "a.txt", dest: "c.txt",
                conflictResolution: FileConflictResolutions.KeepBoth),
            ws, CancellationToken.None);

        var resp = LastManage(ws);
        Assert.True(resp.Success, resp.ErrorMessage);
        Assert.Null(resp.ErrorCode);
        Assert.Null(resp.ConflictingName);
        Assert.Null(resp.ResolvedName);
    }

    [Fact]
    public async Task Copy_FolderStandingWhereAFileGoes_IsADifferentCode()
    {
        // "Replace" here would delete a directory tree to make room for one file, so a client must
        // be able to tell this apart and withhold the button.
        var (handler, _, rootDir) = CreateRealServiceHandler();
        await File.WriteAllTextAsync(Path.Combine(rootDir, "a.txt"), "source");
        Directory.CreateDirectory(Path.Combine(rootDir, "b.txt"));
        var ws = new FakeWebSocket();

        await handler.HandleFileManageRequestAsync(
            Manage(FileManageOperations.Copy, "a.txt", dest: "b.txt"), ws, CancellationToken.None);

        Assert.Equal(FileTransferErrorCodes.DestinationIsDifferentKind, LastManage(ws).ErrorCode);
    }

    [Fact]
    public async Task Move_FileStandingWhereAFolderGoes_IsTheSameDifferentKindCode()
    {
        // The mirror direction, which review found reporting the plain collision code. Both ways
        // round, "replace" means destroying the other kind of thing.
        var (handler, _, rootDir) = CreateRealServiceHandler();
        Directory.CreateDirectory(Path.Combine(rootDir, "src"));
        await File.WriteAllTextAsync(Path.Combine(rootDir, "src", "inner.txt"), "x");
        await File.WriteAllTextAsync(Path.Combine(rootDir, "dst"), "a file");
        var ws = new FakeWebSocket();

        await handler.HandleFileManageRequestAsync(
            Manage(FileManageOperations.Move, "src", dest: "dst"), ws, CancellationToken.None);

        Assert.Equal(FileTransferErrorCodes.DestinationIsDifferentKind, LastManage(ws).ErrorCode);
    }

    [Fact]
    public async Task Copy_KeepBothOntoTheRootItself_IsRefusedAndWritesNothingOutsideTheShare()
    {
        // THE ESCAPE REVIEW FOUND, ASSERTED WHERE IT ACTUALLY MATTERS. "/" resolves to the root
        // itself, whose parent is outside the share, so renaming a sibling of the root produced a
        // real file NEXT TO the share. A move would have relocated the whole tree out of it.
        var (handler, _, rootDir) = CreateRealServiceHandler();
        await File.WriteAllTextAsync(Path.Combine(rootDir, "a.txt"), "source");
        var ws = new FakeWebSocket();

        await handler.HandleFileManageRequestAsync(
            Manage(FileManageOperations.Copy, "a.txt", dest: "/",
                conflictResolution: FileConflictResolutions.KeepBoth),
            ws, CancellationToken.None);

        Assert.False(LastManage(ws).Success);

        var parent = Directory.GetParent(rootDir)!.FullName;
        var strays = Directory.EnumerateFileSystemEntries(parent, Path.GetFileName(rootDir) + " (*").ToList();
        Assert.Empty(strays);
    }

    [Fact]
    public async Task Mkdir_BlockedByAFile_IsTheDifferentKindCode()
    {
        // The same physical situation as the copy/move branches, so it must carry the same code.
        // Review caught it reporting the plain collision code, which would have had the sheet offer
        // "Replace" for a mkdir blocked by a file - i.e. deleting the file to make a folder.
        var (handler, _, rootDir) = CreateRealServiceHandler();
        await File.WriteAllTextAsync(Path.Combine(rootDir, "docs"), "a file");
        var ws = new FakeWebSocket();

        await handler.HandleFileManageRequestAsync(
            Manage(FileManageOperations.Mkdir, "", newName: "docs"), ws, CancellationToken.None);

        Assert.Equal(FileTransferErrorCodes.DestinationIsDifferentKind, LastManage(ws).ErrorCode);
    }

    [Fact]
    public async Task Move_FolderOntoAFile_IsRefusedEvenWithReplace()
    {
        // COPY AND MOVE MUST AGREE. Copy already refused this outright; move honoured overwrite by
        // DELETING the user's file to make room for a directory, so the same request destroyed data
        // on one path and was rejected on the other. Refusing host-side is the reading that cannot
        // lose data, and it does not depend on the client withholding a button.
        var (handler, _, rootDir) = CreateRealServiceHandler();
        Directory.CreateDirectory(Path.Combine(rootDir, "src"));
        await File.WriteAllTextAsync(Path.Combine(rootDir, "src", "inner.txt"), "x");
        await File.WriteAllTextAsync(Path.Combine(rootDir, "dst"), "do not delete me");
        var ws = new FakeWebSocket();

        await handler.HandleFileManageRequestAsync(
            Manage(FileManageOperations.Move, "src", dest: "dst", overwrite: true,
                conflictResolution: FileConflictResolutions.Replace),
            ws, CancellationToken.None);

        var resp = LastManage(ws);
        Assert.False(resp.Success);
        Assert.Equal(FileTransferErrorCodes.DestinationIsDifferentKind, resp.ErrorCode);
        Assert.Equal("do not delete me", await File.ReadAllTextAsync(Path.Combine(rootDir, "dst")));
        Assert.True(Directory.Exists(Path.Combine(rootDir, "src")));
    }

    [Fact]
    public async Task Move_FileOntoAFileWithOverwrite_StillReplacesIt()
    {
        // NEWLY LOAD-BEARING. Refusing the folder-onto-file case left these two as the ONLY move
        // paths that delete anything, and review found neither was covered - so a future edit that
        // over-broadened the refusal the way this one deliberately did not would break move-with-
        // overwrite silently.
        var (handler, _, rootDir) = CreateRealServiceHandler();
        await File.WriteAllTextAsync(Path.Combine(rootDir, "a.txt"), "source");
        await File.WriteAllTextAsync(Path.Combine(rootDir, "b.txt"), "victim");
        var ws = new FakeWebSocket();

        await handler.HandleFileManageRequestAsync(
            Manage(FileManageOperations.Move, "a.txt", dest: "b.txt", overwrite: true), ws, CancellationToken.None);

        Assert.True(LastManage(ws).Success);
        Assert.False(File.Exists(Path.Combine(rootDir, "a.txt")));
        Assert.Equal("source", await File.ReadAllTextAsync(Path.Combine(rootDir, "b.txt")));
    }

    [Fact]
    public async Task Move_FolderOntoAFolderWithOverwrite_StillReplacesIt()
    {
        // The same-kind case, which must keep working: replacing a folder with a folder is what the
        // user asked for, and it is the branch beside the one now refused.
        var (handler, _, rootDir) = CreateRealServiceHandler();
        Directory.CreateDirectory(Path.Combine(rootDir, "src"));
        await File.WriteAllTextAsync(Path.Combine(rootDir, "src", "inner.txt"), "new");
        Directory.CreateDirectory(Path.Combine(rootDir, "dst"));
        await File.WriteAllTextAsync(Path.Combine(rootDir, "dst", "old.txt"), "old");
        var ws = new FakeWebSocket();

        await handler.HandleFileManageRequestAsync(
            Manage(FileManageOperations.Move, "src", dest: "dst", overwrite: true), ws, CancellationToken.None);

        Assert.True(LastManage(ws).Success);
        Assert.False(Directory.Exists(Path.Combine(rootDir, "src")));
        Assert.Equal("new", await File.ReadAllTextAsync(Path.Combine(rootDir, "dst", "inner.txt")));
        Assert.False(File.Exists(Path.Combine(rootDir, "dst", "old.txt")));
    }

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
        }, ws, null, CancellationToken.None);

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
        }, ws, null, CancellationToken.None);

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
        }, ws, null, CancellationToken.None);

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
        }, ws, null, CancellationToken.None);

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
        }, ws, null, CancellationToken.None);

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
        }, ws, null, CancellationToken.None);

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
        }, ws, null, CancellationToken.None);

        var resp = LastThumbnail(ws);
        Assert.Null(resp.ErrorMessage);
        Assert.Null(resp.JpegBase64);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Full-device volume READ fallback (RemEx-39jw): RootId isn't a configured
    // shared root, but is a genuine, consent-granted mounted volume. Read-only ops
    // (download/metadata/hash/search/thumbnail) fall back to VolumeEnumerator +
    // IFileTrustService.IsFullBrowseGrantedAsync, mirroring HandleFileBrowseRequestAsync.
    // Write/delete ops deliberately do NOT get this fallback (RemEx-hb1t) — see
    // HandleFileTransferStartAsync's upload branch, which is intentionally untouched.
    // ──────────────────────────────────────────────────────────────────────────

    private async Task<(FileTransferHandler handler, VolumeEnumerator volumes, string baseTemp)> CreateVolumeTestHandlerAsync(
        string clientId, bool grantFullBrowse,
        Func<string, (string rootId, string displayName, string absolutePath,
                bool isWritable, bool canRename, bool canMove, bool canDelete, bool canRemoveRoot)[]>? seedRoots = null)
    {
        var baseTemp = Path.Combine(Path.GetTempPath(), "remex-vol-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(baseTemp);
        _tempDirs.Add(baseTemp);

        var configPath = Path.Combine(baseTemp, "roots.json");
        var service = new FileTransferService(NullLogger<FileTransferService>.Instance, configPath);
        // Empty by default — forces every RootId below through the volume fallback. A test that needs a
        // pinned root inside the temp area passes a factory keyed on baseTemp (RemEx-hb1t.3 parity tests).
        service.SeedRootsForTests(seedRoots?.Invoke(baseTemp) ?? []);

        var registry = new PairedClientRegistry(
            NullLogger<PairedClientRegistry>.Instance, Path.Combine(baseTemp, "paired_clients.json"));
        registry.RegisterClient(clientId);
        var trust = new FileTrustService(
            NullLogger<FileTrustService>.Instance, registry,
            FileTrustServiceTests.ConnectedSession(clientId),
            Path.Combine(baseTemp, "file_transfer_trust.json"), TimeSpan.FromSeconds(5));
        if (grantFullBrowse)
            await trust.SetFullBrowseGrantedAsync(clientId, true, CancellationToken.None);

        var volumes = new VolumeEnumerator(NullLogger<VolumeEnumerator>.Instance);
        var handler = new FileTransferHandler(
            NullLogger<FileTransferHandler>.Instance, service, trust, volumes,
            new SharedRootReadResolver(service, trust, volumes));
        return (handler, volumes, baseTemp);
    }

    [Fact]
    public async Task Metadata_FromFullDeviceVolume_WithGrantedConsent_Succeeds()
    {
        var (handler, volumes, baseTemp) = await CreateVolumeTestHandlerAsync("client-a", grantFullBrowse: true);
        var volumeRoot = Path.GetPathRoot(baseTemp)!;
        Assert.Contains(volumes.Enumerate(), v => v.Id == volumeRoot); // sanity: host must expose this drive

        var filePath = Path.Combine(baseTemp, "drive-file.txt");
        await File.WriteAllBytesAsync(filePath, new byte[42]);
        var relativePath = Path.GetRelativePath(volumeRoot, filePath).Replace('\\', '/');

        var ws = new FakeWebSocket();
        await handler.HandleFileMetadataRequestAsync(new RemexMessage
        {
            Type = MessageTypes.FileMetadataRequest,
            FileMetadataRequest = new FileMetadataRequest { RequestId = "m-vol", RootId = volumeRoot, RelativePath = relativePath }
        }, ws, "client-a", CancellationToken.None);

        var resp = LastMetadata(ws);
        Assert.Null(resp.ErrorMessage);
        Assert.Equal(42, resp.Size);
    }

    [Fact]
    public async Task Metadata_FromFullDeviceVolume_WithoutConsent_ReturnsError()
    {
        var (handler, _, baseTemp) = await CreateVolumeTestHandlerAsync("client-a", grantFullBrowse: false);
        var volumeRoot = Path.GetPathRoot(baseTemp)!;

        var ws = new FakeWebSocket();
        await handler.HandleFileMetadataRequestAsync(new RemexMessage
        {
            Type = MessageTypes.FileMetadataRequest,
            FileMetadataRequest = new FileMetadataRequest { RequestId = "m-vol", RootId = volumeRoot, RelativePath = "anything.txt" }
        }, ws, "client-a", CancellationToken.None);

        var resp = LastMetadata(ws);
        Assert.NotNull(resp.ErrorMessage);
        Assert.Contains("full-device browsing", resp.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Metadata_FromUnknownVolumeId_ReturnsError()
    {
        var (handler, _, _) = await CreateVolumeTestHandlerAsync("client-a", grantFullBrowse: true);

        var ws = new FakeWebSocket();
        await handler.HandleFileMetadataRequestAsync(new RemexMessage
        {
            Type = MessageTypes.FileMetadataRequest,
            FileMetadataRequest = new FileMetadataRequest
            {
                RequestId = "m-vol", RootId = "nonexistent-volume-id-property-of-no-drive", RelativePath = "anything.txt"
            }
        }, ws, "client-a", CancellationToken.None);

        var resp = LastMetadata(ws);
        Assert.NotNull(resp.ErrorMessage);
        Assert.Contains("Unknown shared root", resp.ErrorMessage);
    }

    [Fact]
    public async Task Download_FromFullDeviceVolume_WithGrantedConsent_Succeeds()
    {
        // This is the exact scenario from the bug report: browsing a bare drive (not a pinned
        // shared root) works, but downloading a file found there threw "Unknown shared root".
        var (handler, volumes, baseTemp) = await CreateVolumeTestHandlerAsync("client-a", grantFullBrowse: true);
        var volumeRoot = Path.GetPathRoot(baseTemp)!;
        Assert.Contains(volumes.Enumerate(), v => v.Id == volumeRoot);

        var filePath = Path.Combine(baseTemp, "drive-file.bin");
        await File.WriteAllBytesAsync(filePath, new byte[4096]);
        var relativePath = Path.GetRelativePath(volumeRoot, filePath).Replace('\\', '/');

        var ws = new FakeWebSocket();
        await handler.HandleFileTransferStartAsync(new RemexMessage
        {
            Type = MessageTypes.FileTransferStart,
            FileTransferStart = new FileTransferStart
            {
                TransferId = "tx-volume-download",
                Direction = "download",
                RemotePath = filePath,
                RemoteRootId = volumeRoot,
                RemoteRelativePath = relativePath,
                FileName = "drive-file.bin",
                TotalBytes = 0,
                Sha256Base64 = string.Empty,
            }
        }, ws, "client-a", CancellationToken.None);

        // The download body streams on a detached Task (HandleFileTransferStartAsync only
        // synchronously resolves the root and opens the stream) — poll briefly for completion.
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!ws.ReceivedMessages.Any(m => m.Type == MessageTypes.FileTransferEnd) && DateTime.UtcNow < deadline)
            await Task.Delay(25);

        var end = ws.ReceivedMessages.LastOrDefault(m => m.Type == MessageTypes.FileTransferEnd)?.FileTransferEnd;
        Assert.NotNull(end);
        Assert.True(end!.Success, $"Expected the volume download to succeed but got: {end.ErrorMessage}");
    }

    [Fact]
    public async Task Upload_ToUnpinnedVolumePath_RefusesWithSharedFolderHint()
    {
        // Full browse never exposes write on the volume itself (RemEx-hb1t). Since RemEx-hb1t.3 an
        // unconfigured RootId is re-mapped when the target sits inside a pinned root — but this target
        // is genuinely un-pinned (empty roots list), so the upload must refuse, pointing the user at
        // the shared-folders list.
        var (handler, _, baseTemp) = await CreateVolumeTestHandlerAsync("client-a", grantFullBrowse: true);
        var volumeRoot = Path.GetPathRoot(baseTemp)!;

        var ws = new FakeWebSocket();
        await handler.HandleFileTransferStartAsync(new RemexMessage
        {
            Type = MessageTypes.FileTransferStart,
            FileTransferStart = new FileTransferStart
            {
                TransferId = "tx-volume-upload",
                Direction = "upload",
                RemotePath = "new-file.bin",
                RemoteRootId = volumeRoot,
                RemoteRelativePath = "new-file.bin",
                FileName = "new-file.bin",
                TotalBytes = 4,
                Sha256Base64 = Convert.ToBase64String(new byte[32]),
            }
        }, ws, "client-a", CancellationToken.None);

        var end = ws.ReceivedMessages.LastOrDefault(m => m.Type == MessageTypes.FileTransferEnd)?.FileTransferEnd;
        Assert.NotNull(end);
        Assert.False(end!.Success);
        Assert.Contains("shared folders", end.ErrorMessage);
    }

    [Fact]
    public async Task Upload_ToVolumePathInsidePinnedRoot_RemapsAndSucceeds()
    {
        // RemEx-hb1t.3 write-op parity: the SAME folder must accept an upload whether addressed by its
        // pinned rootId or reached via a full-device volume browse. The volume-mode reference here
        // resolves inside the pinned root, so the handler re-maps it and the write proceeds.
        var (handler, volumes, baseTemp) = await CreateVolumeTestHandlerAsync("client-a", grantFullBrowse: true,
            temp => [("root-pinned", "Pinned", Path.Combine(temp, "pinned"), true, true, true, true, false)]);
        var volumeRoot = Path.GetPathRoot(baseTemp)!;
        Assert.Contains(volumes.Enumerate(), v => v.Id == volumeRoot); // sanity: host must expose this drive

        var pinnedDir = Path.Combine(baseTemp, "pinned");
        Directory.CreateDirectory(pinnedDir);

        var relativePath = Path.GetRelativePath(volumeRoot, Path.Combine(pinnedDir, "incoming.bin")).Replace('\\', '/');
        var ws = new FakeWebSocket();
        await handler.HandleFileTransferStartAsync(new RemexMessage
        {
            Type = MessageTypes.FileTransferStart,
            FileTransferStart = new FileTransferStart
            {
                TransferId = "tx-volume-upload-pinned",
                Direction = "upload",
                RemotePath = "incoming.bin",
                RemoteRootId = volumeRoot,
                RemoteRelativePath = relativePath,
                FileName = "incoming.bin",
                TotalBytes = 4,
                Sha256Base64 = Convert.ToBase64String(new byte[32]),
            }
        }, ws, "client-a", CancellationToken.None);

        var end = ws.ReceivedMessages.LastOrDefault(m => m.Type == MessageTypes.FileTransferEnd)?.FileTransferEnd;
        Assert.Null(end); // no failure reply — the write stream opened inside the pinned root
        Assert.True(File.Exists(Path.Combine(pinnedDir, "incoming.bin")));

        await handler.HandleFileTransferCancelAsync(new RemexMessage
        {
            Type = MessageTypes.FileTransferCancel,
            FileTransferCancel = new FileTransferCancel { TransferId = "tx-volume-upload-pinned" }
        });
    }

    // ──────────────────────────────────────────────────────────────────────────
    // 2.1 File Sharing Overhaul (protocolVersion 3) — WP-jjdb: inbound consent-
    // response routing + incoming push-offer consent gate. These drive a REAL
    // FileTrustService (temp store + paired registry) so the consent round-trip
    // through RequestConsentAsync / ResolveConsent is exercised end-to-end.
    // ──────────────────────────────────────────────────────────────────────────

    /// <param name="askerConnected">
    /// False builds the handler over an EMPTY session registry, which is what "the phone that asked
    /// has gone" looks like to <c>ConsentRoutePolicy</c> — the deny-without-asking path (RemEx-l580).
    /// </param>
    private (FileTransferHandler handler, FileTrustService trust) CreateTrustHandler(
        string clientId, bool askerConnected = true)
    {
        var baseTemp = Path.Combine(Path.GetTempPath(), "remex-jjdb-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(baseTemp);
        _tempDirs.Add(baseTemp);

        var registry = new PairedClientRegistry(
            NullLogger<PairedClientRegistry>.Instance,
            Path.Combine(baseTemp, "paired_clients.json"));
        registry.RegisterClient(clientId);

        var trust = new FileTrustService(
            NullLogger<FileTrustService>.Instance,
            registry,
            askerConnected
                ? FileTrustServiceTests.ConnectedSession(clientId)
                : new Remex.Agent.Services.ClientSessionRegistry(),
            Path.Combine(baseTemp, "file_transfer_trust.json"),
            TimeSpan.FromSeconds(5));

        var files = new Mock<IFileTransferService>().Object;
        var volumes = new VolumeEnumerator(NullLogger<VolumeEnumerator>.Instance);
        var handler = new FileTransferHandler(
            NullLogger<FileTransferHandler>.Instance,
            files,
            trust,
            volumes,
            new SharedRootReadResolver(files, trust, volumes));

        return (handler, trust);
    }

    [Fact]
    public async Task ConsentResponse_ResolvesPendingConsent()
    {
        var (handler, trust) = CreateTrustHandler("client-a");
        var consentId = Guid.NewGuid().ToString("N");

        // Host raises a consent (as file_volumes_request would) and awaits the peer's decision.
        var pending = trust.RequestConsentAsync(
            "client-a",
            new FileConsentRequest { ConsentId = consentId, Kind = FileConsentKinds.FullBrowse },
            CancellationToken.None);

        // The inbound file_consent_response must resolve that exact pending prompt.
        handler.HandleFileConsentResponse(new RemexMessage
        {
            Type = MessageTypes.FileConsentResponse,
            FileConsentResponse = new FileConsentResponse
            {
                ConsentId = consentId,
                Granted = true,
                Remember = false,
            }
        }, "client-a");

        var decision = await pending;
        Assert.True(decision.Granted);
        Assert.False(decision.Remember);
    }

    [Fact]
    public async Task ConsentResponse_UnknownConsentId_IsNoOp()
    {
        var (handler, _) = CreateTrustHandler("client-a");

        // No pending prompt for this id — must not throw and must send nothing.
        var ws = new FakeWebSocket();
        handler.HandleFileConsentResponse(new RemexMessage
        {
            Type = MessageTypes.FileConsentResponse,
            FileConsentResponse = new FileConsentResponse
            {
                ConsentId = "ghost-consent",
                Granted = true,
                Remember = true,
            }
        }, "client-a");

        Assert.Empty(ws.ReceivedMessages);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task VolumesRequest_DeniedBecauseTheAskerIsGone_SaysSoOnTheWire()
    {
        // The full-browse half of the same bead. fullBrowseGranted=false with errorMessage=null was
        // the entire answer, so "we could not reach you" and "the user said no" arrived identical.
        var (handler, _) = CreateTrustHandler("client-a", askerConnected: false);

        var ws = new FakeWebSocket();
        await handler.HandleFileVolumesRequestAsync(new RemexMessage
        {
            Type = MessageTypes.FileVolumesRequest,
            FileVolumesRequest = new FileVolumesRequest { RequestId = "vol-1" }
        }, ws, "client-a", CancellationToken.None);

        var resp = ws.ReceivedMessages.Last(m => m.Type == MessageTypes.FileVolumesResponse).FileVolumesResponse!;
        Assert.Equal("vol-1", resp.RequestId);
        Assert.False(resp.FullBrowseGranted);
        Assert.Empty(resp.Volumes);
        Assert.Equal(FileConsentDenyReasons.ClientUnreachable, resp.DenyReason);

        // NOT AN ERROR, STILL. errorMessage is what the desktop client throws on, so routing a deny
        // through it would turn a refusal into a host exception on a peer that behaved correctly.
        Assert.Null(resp.ErrorMessage);
    }

    [Fact]
    public async Task VolumesRequest_DeniedByTheUser_CarriesNoReasonCode()
    {
        var (handler, trust) = CreateTrustHandler("client-a");
        trust.ConsentRequested += prompt =>
            trust.ResolveConsent(prompt.Request.ConsentId, granted: false, remember: false);

        var ws = new FakeWebSocket();
        await handler.HandleFileVolumesRequestAsync(new RemexMessage
        {
            Type = MessageTypes.FileVolumesRequest,
            FileVolumesRequest = new FileVolumesRequest { RequestId = "vol-2" }
        }, ws, "client-a", CancellationToken.None);

        var resp = ws.ReceivedMessages.Last(m => m.Type == MessageTypes.FileVolumesResponse).FileVolumesResponse!;
        Assert.False(resp.FullBrowseGranted);
        Assert.Null(resp.DenyReason);
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

    // ── RemEx-9i4b: a request whose body did not bind is answered, not ignored ─────────────────

    [Fact]
    public async Task VolumesRequest_WithNoBody_IsAnsweredWithAnErrorRatherThanSilence()
    {
        // The branch used to be `if (req is null) return;` - no response, no error response, nothing.
        // The phone has no timeout of its own on this call, so it sat on "Requesting volumes..."
        // forever and the user saw "Peer did not respond" with nothing in the log pointing at the
        // cause. That silent shape is what bricked v3 file transfer (RemEx-y6x6), which is why an
        // unbindable body is an answer the host owes rather than an impossible state.
        // The branch returns before touching any of these, so a bare mock is honest here - wiring a
        // real service would suggest the test depends on it.
        var handler = CreateHandler(new Mock<IFileTransferService>().Object);
        var ws = new FakeWebSocket();

        // A file_volumes_request with the wrapper absent: exactly what a peer that spells the body
        // differently across a protocol change puts on the wire.
        var message = new RemexMessage
        {
            Type = MessageTypes.FileVolumesRequest,
            FileVolumesRequest = null,
        };

        await handler.HandleFileVolumesRequestAsync(message, ws, "paired-android-device", default);

        var sent = Assert.Single(ws.ReceivedMessages);
        Assert.Equal(MessageTypes.FileVolumesResponse, sent.Type);
        Assert.NotNull(sent.FileVolumesResponse);
        Assert.False(sent.FileVolumesResponse!.FullBrowseGranted);
        Assert.Empty(sent.FileVolumesResponse.Volumes);
        Assert.Equal(string.Empty, sent.FileVolumesResponse.RequestId);
        Assert.False(string.IsNullOrWhiteSpace(sent.FileVolumesResponse.ErrorMessage));

        // ErrorMessage, not DenyReason. DenyReason means the host declined without asking a person
        // (RemEx-l580); routing a malformed request through it would tell the phone someone said no.
        Assert.Null(sent.FileVolumesResponse.DenyReason);
    }

    // ── RemEx-rie6: every sibling handler answers a bodyless request too ──────────────────────
    // RemEx-9i4b fixed file_volumes_request; these are the eight siblings that had the identical
    // `if (req is null) return;`. Each asserts the SHAPE of the answer, not just that something was
    // sent: the right response type, an errorMessage the peer can show, and the empty/false payload
    // that says nothing was done. A test that only counted messages would pass on a handler that
    // answered with somebody else's response type.
    //
    // HandleFileTransferCancelAsync and HandleFileConsentResponse are deliberately NOT here: they are
    // notifications, not requests, and answering them would invent traffic the protocol does not have.
    // HandleFilePushOfferAsync is also absent because it no longer exists - RemEx-e11w deleted the
    // PC's inbound push-consent path rather than fixing it, so there is no bodyless case to answer.
    // An inbound file_push_offer is now not answered at all, deliberately; the guard for that lives
    // in LoopbackIdentityClaimTests, not here.

    [Fact]
    public async Task BrowseRequest_WithNoBody_IsAnsweredWithAnErrorRatherThanSilence()
    {
        var handler = CreateHandler(new Mock<IFileTransferService>().Object);
        var ws = new FakeWebSocket();

        var message = new RemexMessage { Type = MessageTypes.FileBrowseRequest, FileBrowseRequest = null };

        await handler.HandleFileBrowseRequestAsync(message, ws, "paired-android-device", default);

        var sent = Assert.Single(ws.ReceivedMessages);
        Assert.Equal(MessageTypes.FileBrowseResponse, sent.Type);
        Assert.NotNull(sent.FileBrowseResponse);
        Assert.Empty(sent.FileBrowseResponse!.Entries);
        Assert.Equal(string.Empty, sent.FileBrowseResponse!.RequestId);
        Assert.False(string.IsNullOrWhiteSpace(sent.FileBrowseResponse!.ErrorMessage));
    }

    [Fact]
    public async Task TransferStartRequest_WithNoBody_IsAnsweredWithAnErrorRatherThanSilence()
    {
        var handler = CreateHandler(new Mock<IFileTransferService>().Object);
        var ws = new FakeWebSocket();

        var message = new RemexMessage { Type = MessageTypes.FileTransferStart, FileTransferStart = null };

        await handler.HandleFileTransferStartAsync(message, ws, "paired-android-device", default);

        var sent = Assert.Single(ws.ReceivedMessages);
        Assert.Equal(MessageTypes.FileTransferEnd, sent.Type);
        Assert.NotNull(sent.FileTransferEnd);
        Assert.False(sent.FileTransferEnd!.Success);
        Assert.Equal(string.Empty, sent.FileTransferEnd!.TransferId);
        Assert.False(string.IsNullOrWhiteSpace(sent.FileTransferEnd!.ErrorMessage));
    }

    [Fact]
    public async Task ManageRequest_WithNoBody_IsAnsweredWithAnErrorRatherThanSilence()
    {
        var handler = CreateHandler(new Mock<IFileTransferService>().Object);
        var ws = new FakeWebSocket();

        var message = new RemexMessage { Type = MessageTypes.FileManageRequest, FileManageRequest = null };

        await handler.HandleFileManageRequestAsync(message, ws, default);

        var sent = Assert.Single(ws.ReceivedMessages);
        Assert.Equal(MessageTypes.FileManageResponse, sent.Type);
        Assert.NotNull(sent.FileManageResponse);
        Assert.False(sent.FileManageResponse!.Success);
        Assert.Equal(string.Empty, sent.FileManageResponse!.RequestId);
        Assert.False(string.IsNullOrWhiteSpace(sent.FileManageResponse!.ErrorMessage));
    }

    [Fact]
    public async Task HashRequest_WithNoBody_IsAnsweredWithAnErrorRatherThanSilence()
    {
        var handler = CreateHandler(new Mock<IFileTransferService>().Object);
        var ws = new FakeWebSocket();

        var message = new RemexMessage { Type = MessageTypes.FileHashRequest, FileHashRequest = null };

        await handler.HandleFileHashRequestAsync(message, ws, "paired-android-device", default);

        var sent = Assert.Single(ws.ReceivedMessages);
        Assert.Equal(MessageTypes.FileHashResponse, sent.Type);
        Assert.NotNull(sent.FileHashResponse);
        Assert.Null(sent.FileHashResponse!.Sha256Base64);
        Assert.Equal(string.Empty, sent.FileHashResponse!.RequestId);
        Assert.False(string.IsNullOrWhiteSpace(sent.FileHashResponse!.ErrorMessage));
    }

    [Fact]
    public async Task RootManageRequest_WithNoBody_IsAnsweredWithAnErrorRatherThanSilence()
    {
        var handler = CreateHandler(new Mock<IFileTransferService>().Object);
        var ws = new FakeWebSocket();

        var message = new RemexMessage { Type = MessageTypes.FileRootManageRequest, FileRootManageRequest = null };

        await handler.HandleFileRootManageRequestAsync(message, ws, default);

        var sent = Assert.Single(ws.ReceivedMessages);
        Assert.Equal(MessageTypes.FileRootManageResponse, sent.Type);
        Assert.NotNull(sent.FileRootManageResponse);
        Assert.Empty(sent.FileRootManageResponse!.Roots);
        Assert.Equal(string.Empty, sent.FileRootManageResponse!.RequestId);
        Assert.False(string.IsNullOrWhiteSpace(sent.FileRootManageResponse!.ErrorMessage));
    }

    [Fact]
    public async Task SearchRequest_WithNoBody_IsAnsweredWithAnErrorRatherThanSilence()
    {
        var handler = CreateHandler(new Mock<IFileTransferService>().Object);
        var ws = new FakeWebSocket();

        var message = new RemexMessage { Type = MessageTypes.FileSearchRequest, FileSearchRequest = null };

        await handler.HandleFileSearchRequestAsync(message, ws, "paired-android-device", default);

        var sent = Assert.Single(ws.ReceivedMessages);
        Assert.Equal(MessageTypes.FileSearchResponse, sent.Type);
        Assert.NotNull(sent.FileSearchResponse);
        Assert.Empty(sent.FileSearchResponse!.Entries);
        Assert.Equal(string.Empty, sent.FileSearchResponse!.RequestId);
        Assert.False(string.IsNullOrWhiteSpace(sent.FileSearchResponse!.ErrorMessage));
    }

    [Fact]
    public async Task MetadataRequest_WithNoBody_IsAnsweredWithAnErrorRatherThanSilence()
    {
        var handler = CreateHandler(new Mock<IFileTransferService>().Object);
        var ws = new FakeWebSocket();

        var message = new RemexMessage { Type = MessageTypes.FileMetadataRequest, FileMetadataRequest = null };

        await handler.HandleFileMetadataRequestAsync(message, ws, "paired-android-device", default);

        var sent = Assert.Single(ws.ReceivedMessages);
        Assert.Equal(MessageTypes.FileMetadataResponse, sent.Type);
        Assert.NotNull(sent.FileMetadataResponse);
        Assert.Equal(0, sent.FileMetadataResponse!.Size);
        Assert.Equal(string.Empty, sent.FileMetadataResponse!.RequestId);
        Assert.False(string.IsNullOrWhiteSpace(sent.FileMetadataResponse!.ErrorMessage));
    }

    [Fact]
    public async Task ThumbnailRequest_WithNoBody_IsAnsweredWithAnErrorRatherThanSilence()
    {
        var handler = CreateHandler(new Mock<IFileTransferService>().Object);
        var ws = new FakeWebSocket();

        var message = new RemexMessage { Type = MessageTypes.FileThumbnailRequest, FileThumbnailRequest = null };

        await handler.HandleFileThumbnailRequestAsync(message, ws, "paired-android-device", default);

        var sent = Assert.Single(ws.ReceivedMessages);
        Assert.Equal(MessageTypes.FileThumbnailResponse, sent.Type);
        Assert.NotNull(sent.FileThumbnailResponse);
        Assert.Null(sent.FileThumbnailResponse!.JpegBase64);
        Assert.Equal(string.Empty, sent.FileThumbnailResponse!.RequestId);
        Assert.False(string.IsNullOrWhiteSpace(sent.FileThumbnailResponse!.ErrorMessage));
    }
}
