using System.Security.Cryptography;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Remex.Agent.Services.FileTransfer;
using Remex.Agent.Services.Security;
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
    {
        // The receiver state machine under test never resolves a read root, so a resolver over the same
        // (throwing) fake is enough here. The download-side tests below build a real one.
        var resolver = new SharedRootReadResolver(
            files, new Mock<IFileTrustService>().Object, new VolumeEnumerator(NullLogger<VolumeEnumerator>.Instance));
        return new TransferSessionManager(NullLogger<TransferSessionManager>.Instance, files, resolver, stagingDir);
    }

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

            // Staging is cleared on success. NOTE the partial assertion is now satisfied by the
            // promotion itself — a rename consumes it — so the MANIFEST assertion below is the one that
            // still proves DeleteStaging ran (RemEx-fq6f).
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

    // ──────────────────────────────────────────────────────────────────────────
    // v3 DOWNLOAD source resolution (RemEx-hb1t.6).
    //
    // The host sender resolves the offer's DestRoot through SharedRootReadResolver, so a file reached by
    // full-device browsing — a bare mounted volume that is NOT a pinned shared root — downloads instead of
    // failing with "Unknown shared root". RemEx-39jw added that fallback to the legacy v2 handler only; every
    // real Android download uses this v3 path, so it stayed broken on device until these tests existed.
    //
    // These build the REAL FileTransferService / FileTrustService / VolumeEnumerator rather than the fake
    // above: the bug was precisely that the fake stubbed both read entry points as NotSupportedException, so
    // resolution was never exercised. The volume under test is the temp directory's own drive root, which is
    // "C:\" on Windows and "/" on Linux — keeping these green on both CI platforms.
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Builds a manager whose download path resolves against a real volume enumerator and trust store.
    /// <paramref name="pinVolumeRootAsSharedRoot"/> seeds the temp folder as a configured shared root, so the
    /// same call can be driven down the pinned branch instead of the volume branch.
    /// </summary>
    private static (TransferSessionManager mgr, string baseTemp) NewDownloadManager(
        string clientId, bool grantFullBrowse, bool pinVolumeRootAsSharedRoot = false)
    {
        var baseTemp = Directory.CreateTempSubdirectory("remex-v3dl-").FullName;

        var files = new FileTransferService(
            NullLogger<FileTransferService>.Instance, Path.Combine(baseTemp, "roots.json"));
        files.SeedRootsForTests(pinVolumeRootAsSharedRoot
            ? [("pinned-root", "Pinned", baseTemp, true, true, true, true, true)]
            : []);

        var registry = new PairedClientRegistry(
            NullLogger<PairedClientRegistry>.Instance, Path.Combine(baseTemp, "paired_clients.json"));
        registry.RegisterClient(clientId);
        var trust = new FileTrustService(
            NullLogger<FileTrustService>.Instance, registry,
            FileTrustServiceTests.ConnectedSession(clientId),
            Path.Combine(baseTemp, "file_transfer_trust.json"), TimeSpan.FromSeconds(5));
        if (grantFullBrowse)
            trust.SetFullBrowseGrantedAsync(clientId, true, CancellationToken.None).GetAwaiter().GetResult();

        var resolver = new SharedRootReadResolver(
            files, trust, new VolumeEnumerator(NullLogger<VolumeEnumerator>.Instance));
        var mgr = new TransferSessionManager(
            NullLogger<TransferSessionManager>.Instance, files, resolver,
            Path.Combine(baseTemp, "staging"));
        return (mgr, baseTemp);
    }

    /// <summary>Writes a file under <paramref name="baseTemp"/> and returns its drive root + volume-relative path.</summary>
    private static async Task<(string volumeRoot, string relativePath, byte[] content)> SeedVolumeFileAsync(string baseTemp)
    {
        var content = RandomBytes(4096);
        var filePath = Path.Combine(baseTemp, "downloaded.bin");
        await File.WriteAllBytesAsync(filePath, content);
        var volumeRoot = Path.GetPathRoot(baseTemp)!;
        return (volumeRoot, Path.GetRelativePath(volumeRoot, filePath).Replace('\\', '/'), content);
    }

    [Fact]
    public async Task Download_FromUnpinnedFullDeviceVolume_WithGrantedConsent_OpensTheFile()
    {
        var (mgr, baseTemp) = NewDownloadManager(ClientId, grantFullBrowse: true);
        try
        {
            using (mgr)
            {
                var (volumeRoot, relativePath, expected) = await SeedVolumeFileAsync(baseTemp);

                // volumeRoot is deliberately NOT a configured shared root: this is the exact user repro from
                // RemEx-hb1t.4 — browse bare C:\, walk into an un-pinned folder, download. Before the fix this
                // threw UnauthorizedAccessException("Unknown shared root 'C:\'").
                await using var source = await mgr.OpenDownloadSourceAsync(ClientId, volumeRoot, relativePath, default);

                var actual = new byte[expected.Length];
                await source.ReadExactlyAsync(actual);
                Assert.Equal(expected, actual);
            }
        }
        finally
        {
            Directory.Delete(baseTemp, recursive: true);
        }
    }

    [Fact]
    public async Task Download_FromUnpinnedFullDeviceVolume_WithoutConsent_IsRefused()
    {
        var (mgr, baseTemp) = NewDownloadManager(ClientId, grantFullBrowse: false);
        try
        {
            using (mgr)
            {
                var (volumeRoot, relativePath, _) = await SeedVolumeFileAsync(baseTemp);

                // Fail-closed: the rootId names a real volume, but this device was never granted full browse.
                var ex = await Assert.ThrowsAsync<UnauthorizedAccessException>(
                    () => mgr.OpenDownloadSourceAsync(ClientId, volumeRoot, relativePath, default));
                Assert.Contains("Full-device browsing has not been granted", ex.Message);
            }
        }
        finally
        {
            Directory.Delete(baseTemp, recursive: true);
        }
    }

    [Fact]
    public async Task Download_WithUnidentifiedClient_IsRefusedEvenWhenAnotherDeviceHasConsent()
    {
        var (mgr, baseTemp) = NewDownloadManager(ClientId, grantFullBrowse: true);
        try
        {
            using (mgr)
            {
                var (volumeRoot, relativePath, _) = await SeedVolumeFileAsync(baseTemp);

                // HandleOfferAsync passes `clientId ?? string.Empty`, so an unidentified peer reaches here with
                // an empty id. It must never inherit another device's grant.
                var ex = await Assert.ThrowsAsync<UnauthorizedAccessException>(
                    () => mgr.OpenDownloadSourceAsync(string.Empty, volumeRoot, relativePath, default));
                Assert.Contains("Full-device browsing has not been granted", ex.Message);
            }
        }
        finally
        {
            Directory.Delete(baseTemp, recursive: true);
        }
    }

    [Fact]
    public async Task Download_FromPinnedSharedRoot_StillResolvesThroughTheConfiguredRoot()
    {
        var (mgr, baseTemp) = NewDownloadManager(ClientId, grantFullBrowse: false, pinVolumeRootAsSharedRoot: true);
        try
        {
            using (mgr)
            {
                var content = RandomBytes(2048);
                await File.WriteAllBytesAsync(Path.Combine(baseTemp, "pinned.bin"), content);

                // No full-browse grant needed: the pinned branch must be unaffected by the volume fallback.
                await using var source = await mgr.OpenDownloadSourceAsync(ClientId, "pinned-root", "pinned.bin", default);

                var actual = new byte[content.Length];
                await source.ReadExactlyAsync(actual);
                Assert.Equal(content, actual);
            }
        }
        finally
        {
            Directory.Delete(baseTemp, recursive: true);
        }
    }
}
