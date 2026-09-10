using Microsoft.Extensions.Logging.Abstractions;
using Remex.Agent.Services.FileTransfer;
using Remex.Agent.Services.Security;
using Remex.Core.Models;

namespace Remex.Agent.Tests;

/// <summary>
/// Proves each host-state store stages its writes through a per-write temporary file rather than the
/// fixed sibling <c>&lt;store&gt;.tmp</c> it used to (RemEx-kow1).
///
/// <para>
/// The fixed name belonged to the store, not to the writer, so any second writer of the same file
/// collided with the first: one truncates the other's staging file, and whichever renames last
/// publishes bytes it did not write. Two writers is ordinary — a test run and an installed agent
/// share the machine-wide directory, and every host inside one test assembly shares one redirected
/// directory. What is at stake is the pairing registry (corrupt it and every device unpairs) and the
/// trust store (corrupt it and standing filesystem grants are lost or mis-stated).
/// </para>
///
/// <para>
/// Each test puts a DIRECTORY at the old fixed path. That stands in for the concurrent writer and
/// makes the regression deterministic: a directory can never be opened for writing, so the old code
/// fails here on every run rather than one in a hundred. Note that two of these stores swallow write
/// failures by design — the assertion is therefore on what reached disk, not on an exception.
/// </para>
/// </summary>
public sealed class HostStateAtomicWriteTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "remex-host-state-atomic", Guid.NewGuid().ToString("N"));

    public HostStateAtomicWriteTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp directory is not worth failing a test over.
        }
    }

    /// <summary>Store path inside this test's directory, with the old fixed staging path occupied.</summary>
    private string OccupiedStore(string fileName)
    {
        var storePath = Path.Combine(_directory, fileName);
        Directory.CreateDirectory(storePath + ".tmp");
        return storePath;
    }

    [Fact]
    public void PairingRegistry_PersistsWhileTheFixedStagingPathIsOccupied()
    {
        var storePath = OccupiedStore("paired_clients.json");

        var registry = new PairedClientRegistry(NullLogger<PairedClientRegistry>.Instance, storePath);
        registry.RegisterClient("phone-1", new byte[32]);

        Assert.True(
            new PairedClientRegistry(NullLogger<PairedClientRegistry>.Instance, storePath)
                .IsClientPaired("phone-1"),
            "The pairing did not survive a restart — it never reached disk.");
    }

    [Fact]
    public void PairedClientNames_PersistWhileTheFixedStagingPathIsOccupied()
    {
        var storePath = OccupiedStore("paired_client_names.json");

        new PairedClientNameStore(NullLogger<PairedClientNameStore>.Instance, storePath)
            .Remember("phone-1", "Connor's Pixel");

        Assert.Equal(
            "Connor's Pixel",
            new PairedClientNameStore(NullLogger<PairedClientNameStore>.Instance, storePath)
                .Resolve("phone-1"));
    }

    [Fact]
    public async Task TrustStore_PersistsWhileTheFixedStagingPathIsOccupied()
    {
        var storePath = OccupiedStore("file_transfer_trust.json");
        var pairedClients = new PairedClientRegistry(
            NullLogger<PairedClientRegistry>.Instance, Path.Combine(_directory, "paired_clients.json"));
        pairedClients.RegisterClient("phone-1");

        await NewTrustService(pairedClients, storePath)
            .SetFullBrowseGrantedAsync("phone-1", granted: true, CancellationToken.None);

        Assert.True(
            await NewTrustService(pairedClients, storePath)
                .IsFullBrowseGrantedAsync("phone-1", CancellationToken.None),
            "The grant did not survive a restart — it never reached disk.");
    }

    [Fact]
    public void TransferQueue_PersistsWhileTheFixedStagingPathIsOccupied()
    {
        var storePath = OccupiedStore("transfer_queue.json");

        NewQueue(storePath).Enqueue(new TransferQueueItem
        {
            TransferId = "transfer-1",
            Mode = FileTransferModes.Download,
            FileName = "holiday.jpg",
        });

        Assert.Contains(
            NewQueue(storePath).GetAll(),
            item => item.TransferId == "transfer-1");
    }

    private static FileTrustService NewTrustService(PairedClientRegistry pairedClients, string storePath)
        => new(
            NullLogger<FileTrustService>.Instance,
            pairedClients,
            new Remex.Agent.Services.ClientSessionRegistry(),
            storePath,
            consentTimeout: null);

    private static TransferQueueService NewQueue(string storePath)
        => new(NullLogger<TransferQueueService>.Instance, storePath);
}
