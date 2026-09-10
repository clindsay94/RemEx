using System.Reflection;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Remex.Agent.Services.FileTransfer;
using Remex.Core.Models;
using Remex.Core.Services.FileTransfer;
using Xunit;

namespace Remex.Agent.Tests;

/// <summary>
/// Proves that <c>transfer_queue.json</c> is actually WRITTEN by the live transfer path and READ BACK
/// on a restart — the join that <see cref="TransferQueueService"/> spent two beads without.
/// </summary>
/// <remarks>
/// <para>
/// WHY THIS FILE EXISTS. <c>TransferQueueService</c> was registered as a singleton in
/// <c>HostBootstrapper</c> and resolved by nothing. <c>TransferQueueServiceTests</c> and
/// <c>HostStateAtomicWriteTests</c> both instantiate it directly, so FIFO ordering, atomic writes and
/// orphan sweeping were all covered and all green — for a file that no running host ever created.
/// That is exactly the failure mode AGENTS.md describes under "Splitting a bead": the tested half was
/// unreachable, so the tests gave a false impression of coverage.
/// </para>
/// <para>
/// So these tests deliberately do NOT call the queue directly. They drive
/// <see cref="TransferSessionManager"/> — the real transfer path — and then look at the FILE. A test
/// that called <c>Enqueue</c> itself would pass just as happily with the wiring removed again, which
/// is the whole thing being guarded against.
/// </para>
/// </remarks>
public sealed class TransferQueueWiringTests
{
    private const string ClientId = "paired-android-device";
    private const string DestRoot = "root-a";

    private static TransferSessionManager NewManager(
        string stagingDir, FakeFileTransferService files, TransferQueueService queue)
    {
        var resolver = new SharedRootReadResolver(
            files, new Mock<IFileTrustService>().Object, new VolumeEnumerator(NullLogger<VolumeEnumerator>.Instance));
        return new TransferSessionManager(
            NullLogger<TransferSessionManager>.Instance, files, resolver, stagingDir, queue);
    }

    private static TransferQueueService NewQueue(string storePath)
        => new(NullLogger<TransferQueueService>.Instance, storePath);

    private static FileTransferOffer Offer(string transferId, long size) => new()
    {
        TransferId = transferId,
        Mode = FileTransferModes.Upload,
        SourcePath = "/phone/DCIM/photo.bin",
        DestRoot = DestRoot,
        DestRelativePath = null,
        FileName = "photo.bin",
        Size = size,
    };

    private static byte[] RandomBytes(int length)
    {
        var b = new byte[length];
        RandomNumberGenerator.Fill(b);
        return b;
    }

    /// <summary>Runs <paramref name="body"/> against a fresh staging dir, destination dir and queue path.</summary>
    private static async Task WithTempHost(Func<string, string, FakeFileTransferService, Task> body)
    {
        var staging = Directory.CreateTempSubdirectory();
        var dest = Directory.CreateTempSubdirectory();
        var queueDir = Directory.CreateTempSubdirectory();
        try
        {
            await body(staging.FullName, Path.Combine(queueDir.FullName, "transfer_queue.json"), new FakeFileTransferService(dest.FullName));
        }
        finally
        {
            staging.Delete(recursive: true);
            dest.Delete(recursive: true);
            queueDir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task AcceptingAnInboundOfferWritesTheQueueFileToDisk()
    {
        await WithTempHost(async (staging, queuePath, files) =>
        {
            var transferId = Guid.NewGuid().ToString("N");
            var queue = NewQueue(queuePath);
            using var mgr = NewManager(staging, files, queue);

            // The file must not exist yet, or "it exists afterwards" proves nothing.
            Assert.False(File.Exists(queuePath));

            var acceptance = await mgr.BeginReceiveAsync(ClientId, Offer(transferId, 1024), default);
            Assert.True(acceptance.Accepted);

            Assert.True(
                File.Exists(queuePath),
                "accepting a transfer wrote no transfer_queue.json — the queue is unwired again");

            // On the FILE's contents, not on the in-memory service: an Enqueue that never reached disk
            // would satisfy GetAll() and still lose everything on a restart.
            var json = File.ReadAllText(queuePath);
            Assert.Contains(transferId, json, StringComparison.Ordinal);
            Assert.Contains("photo.bin", json, StringComparison.Ordinal);

            var entry = Assert.Single(queue.GetAll());
            Assert.Equal(TransferState.Active, entry.State);
            Assert.Equal(ClientId, entry.PeerClientId);
            Assert.Equal(DestRoot, entry.DestRoot);
            Assert.Equal(TransferDirection.Inbound, TransferQueueService.DirectionOf(entry.Mode));
        });
    }

    [Fact]
    public async Task AMidFlightTransferIsReadBackOnRestart_AsResumableRatherThanPhantomActive()
    {
        await WithTempHost(async (staging, queuePath, files) =>
        {
            var transferId = Guid.NewGuid().ToString("N");
            var payload = RandomBytes(4096);

            // ── First "process": accept the offer and stream half the bytes, then die. ──
            {
                var queue = NewQueue(queuePath);
                using var mgr = NewManager(staging, files, queue);
                await mgr.BeginReceiveAsync(ClientId, Offer(transferId, payload.Length), default);
                await mgr.WriteChunkAsync(transferId, 0, payload.AsMemory(0, 2048), default);
            }

            // ── Second "process": a brand new service over the same path, reading only the file. ──
            var restarted = NewQueue(queuePath);

            var entry = Assert.Single(restarted.GetAll());
            Assert.Equal(transferId, entry.TransferId);
            Assert.Equal("photo.bin", entry.FileName);
            Assert.Equal(payload.Length, entry.Size);

            // PAUSED, not Active. A transfer that was mid-flight when the process died has no live
            // socket, so surfacing it as Active would be a phantom the UI could never advance — the
            // normalization LoadFromDisk does on the way in. This is the assertion that says the file
            // is a real cross-restart record and not just a write nobody reads.
            Assert.Equal(TransferState.Paused, entry.State);

            // And it is genuinely resumable: the direction is free again, so the next queued transfer
            // in it can start.
            Assert.False(restarted.IsDirectionBusy(TransferDirection.Inbound));
        });
    }

    [Fact]
    public async Task AVerifiedTransferLeavesNothingBehindInTheQueue()
    {
        await WithTempHost(async (staging, queuePath, files) =>
        {
            var transferId = Guid.NewGuid().ToString("N");
            var payload = RandomBytes(2048);
            var queue = NewQueue(queuePath);
            using var mgr = NewManager(staging, files, queue);

            await mgr.BeginReceiveAsync(ClientId, Offer(transferId, payload.Length), default);
            await mgr.WriteChunkAsync(transferId, 0, payload, default);
            var result = await mgr.CompleteReceiveAsync(
                transferId, Convert.ToBase64String(SHA256.HashData(payload)), default);

            Assert.True(result.Verified);

            // A completed transfer is finished work, not a queue item. Left in place it would grow
            // transfer_queue.json for the life of the machine and re-offer a resume for a file that
            // already landed.
            Assert.Empty(queue.GetAll());
            Assert.Empty(NewQueue(queuePath).GetAll());
        });
    }

    [Fact]
    public async Task AFailedVerificationIsKeptWithItsReason_AndSurvivesARestart()
    {
        await WithTempHost(async (staging, queuePath, files) =>
        {
            var transferId = Guid.NewGuid().ToString("N");
            var payload = RandomBytes(2048);
            var queue = NewQueue(queuePath);
            using var mgr = NewManager(staging, files, queue);

            await mgr.BeginReceiveAsync(ClientId, Offer(transferId, payload.Length), default);
            await mgr.WriteChunkAsync(transferId, 0, payload, default);

            // A hash the bytes do not produce: the mismatch-deletes-the-file path.
            var result = await mgr.CompleteReceiveAsync(
                transferId, Convert.ToBase64String(SHA256.HashData(RandomBytes(16))), default);

            Assert.False(result.Verified);

            // KEPT, unlike Done: a failure is the outcome the user may want to see after a restart, and
            // it is the one state where "nothing in the queue" and "it worked" would be indistinguishable.
            var entry = Assert.Single(NewQueue(queuePath).GetAll());
            Assert.Equal(TransferState.Failed, entry.State);
            Assert.False(string.IsNullOrWhiteSpace(entry.Error));
        });
    }

    [Fact]
    public async Task CancellingATransferRemovesItsQueueEntry()
    {
        await WithTempHost(async (staging, queuePath, files) =>
        {
            var transferId = Guid.NewGuid().ToString("N");
            var queue = NewQueue(queuePath);
            using var mgr = NewManager(staging, files, queue);

            await mgr.BeginReceiveAsync(ClientId, Offer(transferId, 4096), default);
            Assert.Single(queue.GetAll());

            mgr.CancelReceive(transferId);

            // Cancel deletes the .remexpart, so there is nothing left to resume from — an entry that
            // outlived it would offer the user a resume that cannot possibly work.
            Assert.Empty(NewQueue(queuePath).GetAll());
        });
    }

    [Fact]
    public void TheProductionConstructorTakesTheQueue()
    {
        // THE JOIN ITSELF, pinned structurally. Every test above passes the queue in through the
        // internal test constructor, so all of them would stay green if the production constructor
        // dropped its parameter and DI went back to never resolving the service — which is precisely
        // the state this work found the code in. Reflection over the public constructor is the only
        // assertion that fails in that case.
        var publicCtors = typeof(TransferSessionManager)
            .GetConstructors(BindingFlags.Public | BindingFlags.Instance);

        var ctor = Assert.Single(publicCtors);
        Assert.Contains(ctor.GetParameters(), p => p.ParameterType == typeof(TransferQueueService));
    }
}
