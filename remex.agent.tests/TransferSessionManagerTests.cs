using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
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

    private static TransferSessionManager NewManager(
        string stagingDir, FakeFileTransferService files, ILogger<TransferSessionManager>? logger = null)
    {
        // The receiver state machine under test never resolves a read root, so a resolver over the same
        // (throwing) fake is enough here. The download-side tests below build a real one.
        var resolver = new SharedRootReadResolver(
            files, new Mock<IFileTrustService>().Object, new VolumeEnumerator(NullLogger<VolumeEnumerator>.Instance));
        return new TransferSessionManager(logger ?? NullLogger<TransferSessionManager>.Instance, files, resolver, stagingDir);
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

    // ── The offered-size range check (RemEx-9xs1) ──────────────────────────────
    // TransferSessionManager.BeginReceiveAsync refuses `Size < 0 || Size > MaxTransferBytes` before it
    // stages anything. That guard had no test, so deleting the line would have left every other test in
    // this file green — they all offer honest sizes. The over-tightening direction needs no test of its
    // own here: every other test in this class calls BeginReceiveAsync with an ordinary size and asserts
    // it was accepted, so a guard that refused too much would take the whole file down with it.

    [Theory]
    [InlineData(-1)]
    [InlineData(long.MinValue)]
    public async Task ANegativeOfferedSizeIsRefusedAndStagesNothing(long size)
    {
        // A negative offered size is not a size a real client can produce; it is a value that exists to
        // see what arithmetic downstream of it does. Refusing at the door is why nothing downstream has
        // to be careful about it.
        var staging = Directory.CreateTempSubdirectory();
        var dest = Directory.CreateTempSubdirectory();
        try
        {
            var files = new FakeFileTransferService(dest.FullName);
            using var mgr = NewManager(staging.FullName, files);

            var acceptance = await mgr.BeginReceiveAsync(ClientId, Offer(Guid.NewGuid().ToString("N"), size), default);

            Assert.False(acceptance.Accepted);
            Assert.False(string.IsNullOrWhiteSpace(acceptance.DeclineReason));

            // A refusal that had already created the staging file would be a way to write into the
            // staging directory without ever sending a byte.
            Assert.Empty(Directory.GetFileSystemEntries(staging.FullName));
        }
        finally
        {
            staging.Delete(recursive: true);
            dest.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task AnOfferedSizeAboveTheHardCeilingIsRefused()
    {
        // The ceiling is 5 GB, matching FileTransferService.MaxUploadBytes. Offering more is refused up
        // front rather than discovered 5 GB into a transfer.
        var staging = Directory.CreateTempSubdirectory();
        var dest = Directory.CreateTempSubdirectory();
        try
        {
            var files = new FakeFileTransferService(dest.FullName);
            using var mgr = NewManager(staging.FullName, files);

            var acceptance = await mgr.BeginReceiveAsync(
                ClientId, Offer(Guid.NewGuid().ToString("N"), 5_000_000_001L), default);

            Assert.False(acceptance.Accepted);
            Assert.False(string.IsNullOrWhiteSpace(acceptance.DeclineReason));
            Assert.Empty(Directory.GetFileSystemEntries(staging.FullName));
        }
        finally
        {
            staging.Delete(recursive: true);
            dest.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task DeclaringZeroBytesThenStreamingPastTheCap_IsRefusedMidTransfer()
    {
        // THE CASE THE OFFER-TIME CHECK CANNOT CATCH, and the reason the running cap exists (review of
        // RemEx-9xs1). Size 0 is legitimate — a phone reports it for a content URI whose length it cannot
        // read — and it is neither negative nor over the ceiling, so BeginReceiveAsync accepts. Zero then
        // disables the declared-size bound (`ExpectedSize > 0 && ...`) AND the completion check in
        // CompleteReceiveAsync, so the running cap is the only thing left. This is the exact twin of
        // Upload_DeclaringZeroBytesThenStreamingPastTheCap_IsAbortedAndSaysSo on the legacy v2 path.
        var staging = Directory.CreateTempSubdirectory();
        var dest = Directory.CreateTempSubdirectory();
        try
        {
            var files = new FakeFileTransferService(dest.FullName);
            var resolver = new SharedRootReadResolver(
                files, new Mock<IFileTrustService>().Object, new VolumeEnumerator(NullLogger<VolumeEnumerator>.Instance));
            using var mgr = new TransferSessionManager(
                NullLogger<TransferSessionManager>.Instance, files, resolver, staging.FullName)
            {
                MaxTransferBytes = 1024,
            };

            var tid = Guid.NewGuid().ToString("N");
            var acceptance = await mgr.BeginReceiveAsync(ClientId, Offer(tid, 0), default);
            Assert.True(acceptance.Accepted, "size 0 is a legitimate offer and must still be accepted");

            // Under the cap: accepted, because a guard that refused everything would pass the assertion
            // below while breaking every unknown-length send.
            var after = await mgr.WriteChunkAsync(tid, 0, new byte[512], default);
            Assert.Equal(512, after);

            // Over the cap: refused, even though the peer never declared a size to overshoot.
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => mgr.WriteChunkAsync(tid, 512, new byte[1024], default));
        }
        finally
        {
            staging.Delete(recursive: true);
            dest.Delete(recursive: true);
        }
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

    // --- RemEx-0719: a transferId is not a bearer capability -------------------------------------
    // Every inbound-frame case in RunChannelAsync keyed on envelope.TransferId ALONE, with no check
    // that the session belonged to the socket presenting the frame. RemEx-4u0d closed channel
    // DISPLACEMENT by refusing a loopback caller that claims a paired id - but it deliberately still
    // admits a loopback connection with a BLANK id, and such a connection needs no identity at all to
    // reach the frame loop. Only a transferId. So it could cancel or inject into a paired phone's
    // in-flight transfer without claiming anything at all.

    [Fact]
    public async Task AFrameOnItsOwnClientsChannel_IsNotForeign()
    {
        var staging = Directory.CreateTempSubdirectory();
        var dest = Directory.CreateTempSubdirectory();
        try
        {
            var files = new FakeFileTransferService(dest.FullName);
            using var mgr = NewManager(staging.FullName, files);
            var tid = Guid.NewGuid().ToString("N");

            await mgr.BeginReceiveAsync(ClientId, Offer(tid, 1024), default);

            Assert.False(mgr.IsForeignTransfer(ClientId, tid));
        }
        finally { staging.Delete(true); dest.Delete(true); }
    }

    [Fact]
    public async Task AFrameOnAnotherClientsChannel_IsForeign()
    {
        var staging = Directory.CreateTempSubdirectory();
        var dest = Directory.CreateTempSubdirectory();
        try
        {
            var files = new FakeFileTransferService(dest.FullName);
            using var mgr = NewManager(staging.FullName, files);
            var tid = Guid.NewGuid().ToString("N");

            await mgr.BeginReceiveAsync(ClientId, Offer(tid, 1024), default);

            Assert.True(mgr.IsForeignTransfer("some-other-device", tid));
        }
        finally { staging.Delete(true); dest.Delete(true); }
    }

    [Fact]
    public async Task AFrameOnAnIdentitylessLoopbackChannel_IsForeign()
    {
        // The attack RemEx-4u0d leaves open by design: a loopback connection with a blank clientId is
        // still admitted, because refusing it would break the TestServer and buy nothing. This is the
        // check that stops it touching a phone's transfer anyway.
        var staging = Directory.CreateTempSubdirectory();
        var dest = Directory.CreateTempSubdirectory();
        try
        {
            var files = new FakeFileTransferService(dest.FullName);
            using var mgr = NewManager(staging.FullName, files);
            var tid = Guid.NewGuid().ToString("N");

            await mgr.BeginReceiveAsync(ClientId, Offer(tid, 1024), default);

            Assert.True(mgr.IsForeignTransfer(string.Empty, tid));
        }
        finally { staging.Delete(true); dest.Delete(true); }
    }

    [Fact]
    public async Task AnUnknownTransferId_IsNotForeign()
    {
        // The direction that would be a behaviour change smuggled in beside a security fix. An
        // unrecognised id is already handled downstream - WriteChunkAsync ignores it, the ack and
        // error cases TryGet and no-op - and that is what makes late-arriving and post-completion
        // frames harmless. Treating unknown as foreign would silently change all of that.
        var staging = Directory.CreateTempSubdirectory();
        var dest = Directory.CreateTempSubdirectory();
        try
        {
            var files = new FakeFileTransferService(dest.FullName);
            using var mgr = NewManager(staging.FullName, files);
            await mgr.BeginReceiveAsync(ClientId, Offer(Guid.NewGuid().ToString("N"), 1024), default);

            Assert.False(mgr.IsForeignTransfer(ClientId, "a-transfer-nobody-has"));
            Assert.False(mgr.IsForeignTransfer("some-other-device", "a-transfer-nobody-has"));
        }
        finally { staging.Delete(true); dest.Delete(true); }
    }

    // --- RemEx-juas: the control plane is bound to an identity too ---------------------------------
    // RemEx-0719 bound the binary channel. These are the JSON entry points, which took no identity at
    // all - and were the cheaper attack, because reaching them needs no binary channel and nothing
    // claimed. PingPongHandler seeds isPaired = isLoopback, so an unelevated local process could open
    // ws://127.0.0.1/ws and send one message.

    [Fact]
    public async Task ControlCancelFromAnotherClient_IsRefused()
    {
        var staging = Directory.CreateTempSubdirectory();
        var dest = Directory.CreateTempSubdirectory();
        try
        {
            var files = new FakeFileTransferService(dest.FullName);
            using var mgr = NewManager(staging.FullName, files);
            var tid = Guid.NewGuid().ToString("N");
            await mgr.BeginReceiveAsync(ClientId, Offer(tid, 4096), default);
            await mgr.WriteChunkAsync(tid, 0, new byte[128], default);

            var partial = Path.Combine(staging.FullName, tid + ".remexpart");
            Assert.True(File.Exists(partial));

            mgr.HandleControl(
                new FileTransferControl { TransferId = tid, Action = FileTransferControlActions.Cancel },
                channelKey: string.Empty);

            Assert.True(File.Exists(partial), "a cancel from an unbound connection must not delete the partial");
        }
        finally { staging.Delete(true); dest.Delete(true); }
    }

    [Fact]
    public async Task ControlCancelFromItsOwnClient_IsHonoured()
    {
        // The direction that matters if the guard is too broad: the owner must still be able to cancel.
        var staging = Directory.CreateTempSubdirectory();
        var dest = Directory.CreateTempSubdirectory();
        try
        {
            var files = new FakeFileTransferService(dest.FullName);
            using var mgr = NewManager(staging.FullName, files);
            var tid = Guid.NewGuid().ToString("N");
            await mgr.BeginReceiveAsync(ClientId, Offer(tid, 4096), default);
            await mgr.WriteChunkAsync(tid, 0, new byte[128], default);

            var partial = Path.Combine(staging.FullName, tid + ".remexpart");
            Assert.True(File.Exists(partial));

            mgr.HandleControl(
                new FileTransferControl { TransferId = tid, Action = FileTransferControlActions.Cancel },
                channelKey: ClientId);

            Assert.False(File.Exists(partial), "the owning client's cancel must still delete the partial");
        }
        finally { staging.Delete(true); dest.Delete(true); }
    }

    [Fact]
    public async Task AnOfferReusingAnotherClientsLiveTransferId_IsDeclined()
    {
        var staging = Directory.CreateTempSubdirectory();
        var dest = Directory.CreateTempSubdirectory();
        try
        {
            var files = new FakeFileTransferService(dest.FullName);
            using var mgr = NewManager(staging.FullName, files);
            var tid = Guid.NewGuid().ToString("N");
            await mgr.BeginReceiveAsync(ClientId, Offer(tid, 4096), default);

            // Superseding used to take whatever id the offer named, so this re-owned the victim's
            // transfer and pointed it at the attacker's destination.
            var hijack = await mgr.BeginReceiveAsync("some-other-device", Offer(tid, 4096), default);

            Assert.False(hijack.Accepted);
            Assert.False(string.IsNullOrWhiteSpace(hijack.DeclineReason));
            Assert.False(mgr.IsForeignTransfer(ClientId, tid), "the original owner must still hold it");
        }
        finally { staging.Delete(true); dest.Delete(true); }
    }

    [Fact]
    public async Task AReOfferFromTheSameClient_StillSupersedes()
    {
        // A client retry, or a re-offer after a dropped socket, is a legitimate supersede and must not
        // be caught by the guard above.
        var staging = Directory.CreateTempSubdirectory();
        var dest = Directory.CreateTempSubdirectory();
        try
        {
            var files = new FakeFileTransferService(dest.FullName);
            using var mgr = NewManager(staging.FullName, files);
            var tid = Guid.NewGuid().ToString("N");
            await mgr.BeginReceiveAsync(ClientId, Offer(tid, 4096), default);

            var again = await mgr.BeginReceiveAsync(ClientId, Offer(tid, 4096), default);

            Assert.True(again.Accepted);
        }
        finally { staging.Delete(true); dest.Delete(true); }
    }

    /// <summary>
    /// Records warnings so a refusal can be OBSERVED. The first draft of the HandleResult tests below
    /// asserted on IsForeignTransfer instead, which holds whether or not HandleResult consults it - so
    /// they passed with the guard deleted. Caught by injection, and it is the same mistake the review
    /// of this bead failed the first pass for.
    /// </summary>
    private sealed class WarningCapturingLogger<T> : ILogger<T>
    {
        public List<string> Warnings { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(
            LogLevel level, EventId id, TState state, Exception? ex, Func<TState, Exception?, string> formatter)
        {
            if (level == LogLevel.Warning) Warnings.Add(formatter(state, ex));
        }
    }

    /// <summary>A socket that records sends, and can be told that any send at all is a failure.</summary>
    private sealed class RecordingSocket(bool failOnSend = false) : System.Net.WebSockets.WebSocket
    {
        public int Sends { get; private set; }

        public override Task SendAsync(
            ArraySegment<byte> buffer, System.Net.WebSockets.WebSocketMessageType type, bool end, CancellationToken ct)
        {
            Sends++;
            Assert.False(failOnSend, "the guard should have returned before anything was written to the socket");
            return Task.CompletedTask;
        }

        public override System.Net.WebSockets.WebSocketState State => System.Net.WebSockets.WebSocketState.Open;
        public override System.Net.WebSockets.WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override string? SubProtocol => null;
        public override void Abort() { }
        public override Task CloseAsync(System.Net.WebSockets.WebSocketCloseStatus s, string? d, CancellationToken c) => Task.CompletedTask;
        public override Task CloseOutputAsync(System.Net.WebSockets.WebSocketCloseStatus s, string? d, CancellationToken c) => Task.CompletedTask;
        public override Task<System.Net.WebSockets.WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> b, CancellationToken c)
            => Task.FromResult(new System.Net.WebSockets.WebSocketReceiveResult(0, System.Net.WebSockets.WebSocketMessageType.Close, true));
        public override void Dispose() { }
    }

    // The two guards the first pass shipped without any coverage at all - found in review, and the
    // reason "guards removed" broke only 3 of 4 tests rather than all of them. HandleCompleteAsync is
    // the highest-impact of the four: it runs the hash verification and promotes the file out of
    // staging into the destination root.

    [Fact]
    public async Task CompleteFromAnotherClient_IsRefused()
    {
        var staging = Directory.CreateTempSubdirectory();
        var dest = Directory.CreateTempSubdirectory();
        try
        {
            var files = new FakeFileTransferService(dest.FullName);
            using var mgr = NewManager(staging.FullName, files);
            var tid = Guid.NewGuid().ToString("N");
            await mgr.BeginReceiveAsync(ClientId, Offer(tid, 4096), default);
            await mgr.WriteChunkAsync(tid, 0, new byte[128], default);

            var partial = Path.Combine(staging.FullName, tid + ".remexpart");
            var socket = new RecordingSocket(failOnSend: true);

            await mgr.HandleCompleteAsync(
                new FileTransferComplete { TransferId = tid, Sha256Base64 = "" },
                socket, isLoopback: false, channelKey: string.Empty, default);

            Assert.Equal(0, socket.Sends);
            Assert.True(File.Exists(partial), "the victim's partial must survive a foreign complete");
            Assert.Empty(Directory.GetFiles(dest.FullName));
        }
        finally { staging.Delete(true); dest.Delete(true); }
    }

    [Fact]
    public async Task CompleteFromItsOwnClient_IsHonoured()
    {
        // The over-broad direction: the owner must still be able to finish its own transfer.
        var staging = Directory.CreateTempSubdirectory();
        var dest = Directory.CreateTempSubdirectory();
        try
        {
            var files = new FakeFileTransferService(dest.FullName);
            using var mgr = NewManager(staging.FullName, files);
            var payload = RandomBytes(256);
            var tid = Guid.NewGuid().ToString("N");
            await mgr.BeginReceiveAsync(ClientId, Offer(tid, payload.Length), default);
            await mgr.WriteChunkAsync(tid, 0, payload, default);

            var socket = new RecordingSocket();
            await mgr.HandleCompleteAsync(
                new FileTransferComplete { TransferId = tid, Sha256Base64 = Sha256B64(payload) },
                socket, isLoopback: false, channelKey: ClientId, default);

            Assert.True(socket.Sends > 0, "the owner's complete must be answered with a result");
        }
        finally { staging.Delete(true); dest.Delete(true); }
    }

    [Fact]
    public async Task ResultFromAnotherClient_IsRefused()
    {
        var staging = Directory.CreateTempSubdirectory();
        var dest = Directory.CreateTempSubdirectory();
        try
        {
            var files = new FakeFileTransferService(dest.FullName);
            var logger = new WarningCapturingLogger<TransferSessionManager>();
            using var mgr = NewManager(staging.FullName, files, logger);
            var tid = Guid.NewGuid().ToString("N");
            await mgr.BeginReceiveAsync(ClientId, Offer(tid, 4096), default);

            mgr.HandleResult(new FileTransferResult { TransferId = tid, Verified = false }, channelKey: string.Empty);

            Assert.Contains(logger.Warnings, w => w.Contains("Refusing a file_transfer_result", StringComparison.Ordinal));
        }
        finally { staging.Delete(true); dest.Delete(true); }
    }

    [Fact]
    public async Task ResultFromItsOwnClient_IsAccepted()
    {
        var staging = Directory.CreateTempSubdirectory();
        var dest = Directory.CreateTempSubdirectory();
        try
        {
            var files = new FakeFileTransferService(dest.FullName);
            var logger = new WarningCapturingLogger<TransferSessionManager>();
            using var mgr = NewManager(staging.FullName, files, logger);
            var tid = Guid.NewGuid().ToString("N");
            await mgr.BeginReceiveAsync(ClientId, Offer(tid, 4096), default);

            mgr.HandleResult(new FileTransferResult { TransferId = tid, Verified = true }, channelKey: ClientId);

            // The over-broad direction: the owner's result must NOT be refused.
            Assert.DoesNotContain(logger.Warnings, w => w.Contains("Refusing a file_transfer_result", StringComparison.Ordinal));
        }
        finally { staging.Delete(true); dest.Delete(true); }
    }
}
