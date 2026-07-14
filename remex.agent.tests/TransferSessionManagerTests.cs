using System.Security.Cryptography;
using Microsoft.Extensions.Logging.Abstractions;
using Remex.Agent.Services.FileTransfer;
using Remex.Core.Models;
using Remex.Core.Services.FileTransfer;

namespace Remex.Agent.Tests;

/// <summary>
/// WP4 coverage for <see cref="TransferSessionManager"/>'s receiver state machine (plan §1.3): the happy
/// path (verified + promoted to the destination root), offset-based resume across a "process restart"
/// (manifest identity match vs. mismatch), cancel-deletes-partial, and the mismatch-deletes-file semantics.
/// The bulk WebSocket data plane is not exercised here — the pure staging state machine is; the frame codec
/// itself is covered by <c>FileFrameEnvelopeCodecTests</c> in remex.core.tests.
/// </summary>
public sealed class TransferSessionManagerTests
{
    private const string ClientId = "paired-android-device";
    private const string DestRoot = "root-a";

    private static TransferSessionManager NewManager(string stagingDir, FakeFileTransferService files)
        => new(NullLogger<TransferSessionManager>.Instance, files, stagingDir);

    private static FileTransferOffer Offer(
        string transferId, long size, bool resume = false, string mode = "upload",
        string? sourcePath = "/phone/DCIM/photo.bin", string fileName = "photo.bin")
        => new()
        {
            TransferId = transferId,
            Mode = mode,
            SourcePath = sourcePath,
            DestRoot = DestRoot,
            DestRelativePath = null,
            FileName = fileName,
            Size = size,
            ResumeRequested = resume,
        };

    private static string Sha256B64(byte[] bytes) => Convert.ToBase64String(SHA256.HashData(bytes));

    private static byte[] RandomBytes(int length)
    {
        var b = new byte[length];
        RandomNumberGenerator.Fill(b);
        return b;
    }

    [Fact]
    public async Task HappyPath_ReceivesVerifiesAndPromotesToDestinationRoot()
    {
        var staging = Directory.CreateTempSubdirectory();
        var dest = Directory.CreateTempSubdirectory();
        try
        {
            var files = new FakeFileTransferService(dest.FullName);
            using var mgr = NewManager(staging.FullName, files);

            var payload = RandomBytes(300_000); // spans more than one 256 KB frame's worth of content
            var tid = Guid.NewGuid().ToString("N");

            var acceptance = await mgr.BeginReceiveAsync(ClientId, Offer(tid, payload.Length), default);
            Assert.True(acceptance.Accepted);
            Assert.Equal(0, acceptance.StartOffset);

            // Feed the bytes in two segments to exercise offset tracking.
            var half = payload.Length / 2;
            var after = await mgr.WriteChunkAsync(tid, 0, payload.AsMemory(0, half), default);
            Assert.Equal(half, after);
            after = await mgr.WriteChunkAsync(tid, half, payload.AsMemory(half), default);
            Assert.Equal(payload.Length, after);

            var result = await mgr.CompleteReceiveAsync(tid, Sha256B64(payload), default);

            Assert.True(result.Verified);
            Assert.Equal(Sha256B64(payload), result.Sha256Base64);
            Assert.NotNull(files.LastWrittenPath);
            Assert.True(File.Exists(files.LastWrittenPath!));
            Assert.Equal(payload, await File.ReadAllBytesAsync(files.LastWrittenPath!));

            // Staging is cleared on success.
            Assert.False(File.Exists(Path.Combine(staging.FullName, tid + ".remexpart")));
            Assert.False(File.Exists(Path.Combine(staging.FullName, tid + ".manifest.json")));
        }
        finally
        {
            staging.Delete(recursive: true);
            dest.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Resume_WithMatchingManifest_ContinuesFromPartialOffset()
    {
        var staging = Directory.CreateTempSubdirectory();
        var dest = Directory.CreateTempSubdirectory();
        try
        {
            var files = new FakeFileTransferService(dest.FullName);
            var payload = RandomBytes(400_000);
            var half = payload.Length / 2;
            var tid = Guid.NewGuid().ToString("N");

            // First "process": accept, write the first half, then drop (Dispose keeps the partial + manifest).
            var mgrA = NewManager(staging.FullName, files);
            await mgrA.BeginReceiveAsync(ClientId, Offer(tid, payload.Length), default);
            await mgrA.WriteChunkAsync(tid, 0, payload.AsMemory(0, half), default);
            mgrA.Dispose(); // simulate a dropped socket / restart — partial survives for resume.

            Assert.True(File.Exists(Path.Combine(staging.FullName, tid + ".remexpart")));

            // Second "process": resume with matching identity → sender is told to continue from the partial.
            using var mgrB = NewManager(staging.FullName, files);
            var acceptance = await mgrB.BeginReceiveAsync(ClientId, Offer(tid, payload.Length, resume: true), default);

            Assert.True(acceptance.Accepted);
            Assert.Equal(half, acceptance.StartOffset);

            await mgrB.WriteChunkAsync(tid, half, payload.AsMemory(half), default);
            var result = await mgrB.CompleteReceiveAsync(tid, Sha256B64(payload), default);

            Assert.True(result.Verified);
            Assert.Equal(payload, await File.ReadAllBytesAsync(files.LastWrittenPath!));
        }
        finally
        {
            staging.Delete(recursive: true);
            dest.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Resume_WithMismatchedIdentity_RestartsFromZero()
    {
        var staging = Directory.CreateTempSubdirectory();
        var dest = Directory.CreateTempSubdirectory();
        try
        {
            var files = new FakeFileTransferService(dest.FullName);
            var payload = RandomBytes(200_000);
            var half = payload.Length / 2;
            var tid = Guid.NewGuid().ToString("N");

            var mgrA = NewManager(staging.FullName, files);
            await mgrA.BeginReceiveAsync(ClientId, Offer(tid, payload.Length), default);
            await mgrA.WriteChunkAsync(tid, 0, payload.AsMemory(0, half), default);
            mgrA.Dispose();

            // Resume requested, but a DIFFERENT source path → manifest identity fails → discard + offset 0.
            using var mgrB = NewManager(staging.FullName, files);
            var acceptance = await mgrB.BeginReceiveAsync(
                ClientId, Offer(tid, payload.Length, resume: true, sourcePath: "/phone/OTHER/file.bin"), default);

            Assert.True(acceptance.Accepted);
            Assert.Equal(0, acceptance.StartOffset);

            // A clean full send from 0 still verifies.
            await mgrB.WriteChunkAsync(tid, 0, payload, default);
            var result = await mgrB.CompleteReceiveAsync(tid, Sha256B64(payload), default);
            Assert.True(result.Verified);
        }
        finally
        {
            staging.Delete(recursive: true);
            dest.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task CancelReceive_DeletesPartialAndManifest()
    {
        var staging = Directory.CreateTempSubdirectory();
        var dest = Directory.CreateTempSubdirectory();
        try
        {
            var files = new FakeFileTransferService(dest.FullName);
            using var mgr = NewManager(staging.FullName, files);

            var payload = RandomBytes(100_000);
            var tid = Guid.NewGuid().ToString("N");

            await mgr.BeginReceiveAsync(ClientId, Offer(tid, payload.Length), default);
            await mgr.WriteChunkAsync(tid, 0, payload.AsMemory(0, 40_000), default);

            mgr.CancelReceive(tid);

            Assert.False(File.Exists(Path.Combine(staging.FullName, tid + ".remexpart")));
            Assert.False(File.Exists(Path.Combine(staging.FullName, tid + ".manifest.json")));
            Assert.Null(files.LastWrittenPath); // never promoted to the destination.
        }
        finally
        {
            staging.Delete(recursive: true);
            dest.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Complete_WithHashMismatch_DeletesPartialAndDoesNotSave()
    {
        var staging = Directory.CreateTempSubdirectory();
        var dest = Directory.CreateTempSubdirectory();
        try
        {
            var files = new FakeFileTransferService(dest.FullName);
            using var mgr = NewManager(staging.FullName, files);

            var payload = RandomBytes(150_000);
            var tid = Guid.NewGuid().ToString("N");

            await mgr.BeginReceiveAsync(ClientId, Offer(tid, payload.Length), default);
            await mgr.WriteChunkAsync(tid, 0, payload, default);

            // Claim a hash that does not match the received bytes → mismatch deletes the file.
            var wrongHash = Sha256B64(RandomBytes(150_000));
            var result = await mgr.CompleteReceiveAsync(tid, wrongHash, default);

            Assert.False(result.Verified);
            Assert.NotNull(result.Error);
            Assert.Contains("mismatch", result.Error, StringComparison.OrdinalIgnoreCase);
            Assert.False(File.Exists(Path.Combine(staging.FullName, tid + ".remexpart")));
            Assert.Null(files.LastWrittenPath); // corrupted file must never reach the destination root.
        }
        finally
        {
            staging.Delete(recursive: true);
            dest.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task WriteChunk_OutOfOrderOffset_Throws()
    {
        var staging = Directory.CreateTempSubdirectory();
        var dest = Directory.CreateTempSubdirectory();
        try
        {
            var files = new FakeFileTransferService(dest.FullName);
            using var mgr = NewManager(staging.FullName, files);

            var tid = Guid.NewGuid().ToString("N");
            await mgr.BeginReceiveAsync(ClientId, Offer(tid, 100_000), default);

            // A gap in the stream (offset 500 when 0 bytes are committed) is a protocol violation.
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => mgr.WriteChunkAsync(tid, 500, RandomBytes(1000), default));
        }
        finally
        {
            staging.Delete(recursive: true);
            dest.Delete(recursive: true);
        }
    }

    /// <summary>
    /// Minimal <see cref="IFileTransferService"/> test double: only <see cref="OpenForWriteAsync"/> is real
    /// (writes the verified partial to a temp destination directory and records the path); every other member
    /// throws, since the receiver state machine under test never touches them.
    /// </summary>
    private sealed class FakeFileTransferService(string destDir) : IFileTransferService
    {
        public string? LastWrittenPath { get; private set; }

        public Task<Stream> OpenForWriteAsync(string rootId, string relativePath, long expectedBytes, CancellationToken ct)
        {
            var full = Path.Combine(destDir, relativePath.Replace('/', Path.DirectorySeparatorChar));
            var parent = Path.GetDirectoryName(full);
            if (!string.IsNullOrEmpty(parent))
                Directory.CreateDirectory(parent);
            LastWrittenPath = full;
            Stream stream = new FileStream(full, FileMode.Create, FileAccess.Write, FileShare.None);
            return Task.FromResult(stream);
        }

        public Task<IReadOnlyList<FileSharedRoot>> ListRootsAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<FileEntry>> BrowseAsync(string rootId, string relativePath, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<FileEntry>> BrowseVolumeAsync(string volumeAbsolutePath, string relativePath, CancellationToken ct) => throw new NotSupportedException();
        public Task<Stream> OpenForReadAsync(string rootId, string relativePath, CancellationToken ct) => throw new NotSupportedException();
        public Task DeleteAsync(string rootId, string relativePath, CancellationToken ct) => throw new NotSupportedException();
        public Task RenameAsync(string rootId, string relativePath, string newName, CancellationToken ct) => throw new NotSupportedException();
        public Task<string> ComputeSha256Async(string rootId, string relativePath, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<FileSharedRoot>> AddRootFromPathAsync(string sourceRootId, string sourceRelativePath, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<FileSharedRoot>> RemoveRootAsync(string rootId, CancellationToken ct) => throw new NotSupportedException();
        public Task CopyAsync(string rootId, string relativePath, string destinationRelativePath, bool overwrite, CancellationToken ct) => throw new NotSupportedException();
        public Task MoveAsync(string rootId, string relativePath, string destinationRelativePath, bool overwrite, CancellationToken ct) => throw new NotSupportedException();
        public Task CreateDirectoryAsync(string rootId, string relativePath, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<FileSearchEntry>> SearchAsync(string rootId, string relativePath, string query, int maxResults, CancellationToken ct) => throw new NotSupportedException();
        public Task<FileMetadata> GetMetadataAsync(string rootId, string relativePath, CancellationToken ct) => throw new NotSupportedException();
        public Task<string?> GetThumbnailBase64Async(string rootId, string relativePath, int maxDim, CancellationToken ct) => throw new NotSupportedException();
    }
}
