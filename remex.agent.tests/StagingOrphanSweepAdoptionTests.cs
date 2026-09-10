using Microsoft.Extensions.Logging.Abstractions;
using Remex.Agent.Services;
using Remex.Agent.Services.FileTransfer;
using Remex.Agent.Services.Security;
using Xunit;

namespace Remex.Agent.Tests;

/// <summary>
/// Every host-state store collects the staging copy a killed process left behind (RemEx-njzcx).
/// </summary>
/// <remarks>
/// <para>
/// RemEx-jegp built the sweep and wired ONE store — the pairing registry, where an orphan is a
/// complete copy of every reconnect secret carrying the inherited directory ACL rather than the
/// hardened one. The rest kept accumulating. <c>file_transfer_trust.json</c> is the one that mattered
/// most of those: it holds standing filesystem authorisation, which devices may browse the whole PC.
/// </para>
/// <para>
/// **WITH NO STORE ON DISK, WHICH IS WHAT PINS THE PLACEMENT RATHER THAN THE CALL.** The review of
/// RemEx-jegp caught this: a test that creates the store first passes just as happily with the sweep
/// moved BELOW the load's existence check — and the store-absent case is exactly what a first write
/// killed between staging and rename produces. A sweep behind an early return is walked past on every
/// startup of the machine that most needs it.
/// </para>
/// <para>
/// The orphans are backdated because the sweep deliberately spares anything younger than its
/// threshold: a file that age is an in-flight write, and deleting one breaks that writer on Linux.
/// </para>
/// </remarks>
public sealed class StagingOrphanSweepAdoptionTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory();

    public void Dispose()
    {
        _root.Delete(recursive: true);
        GC.SuppressFinalize(this);
    }

    [Theory]
    [InlineData("trust")]
    [InlineData("queue")]
    [InlineData("names")]
    [InlineData("activity")]
    [InlineData("overrides")]
    [InlineData("roots")]
    public void ConstructingAStoreCollectsItsAbandonedStagingCopy(string store)
    {
        var storePath = Path.Combine(_root.FullName, $"{store}.json");
        var orphan = Path.Combine(_root.FullName, $".{store}.json.deadbeef.tmp");
        File.WriteAllText(orphan, "{\"left\":\"by a killed process\"}");
        File.SetLastWriteTimeUtc(orphan, DateTime.UtcNow.AddHours(-1));

        Construct(store, storePath);

        Assert.False(File.Exists(orphan), $"the {store} store walked past an abandoned copy of itself");
        Assert.False(File.Exists(storePath), "nothing here should have created a store");
    }

    /// <summary>
    /// The sweep is anchored on the store it is named for, so eight stores in one directory do not
    /// collect each other's in-flight staging files.
    /// </summary>
    [Fact]
    public void OneStoresSweepLeavesTheOthersStagingFilesAlone()
    {
        var neighbour = Path.Combine(_root.FullName, ".activity.json.deadbeef.tmp");
        File.WriteAllText(neighbour, "someone else's write");
        File.SetLastWriteTimeUtc(neighbour, DateTime.UtcNow.AddHours(-1));

        Construct("trust", Path.Combine(_root.FullName, "trust.json"));

        Assert.True(File.Exists(neighbour), "a neighbour's staging file is not this store's business");
    }

    private static void Construct(string store, string storePath)
    {
        switch (store)
        {
            case "trust":
                _ = new FileTrustService(
                    NullLogger<FileTrustService>.Instance,
                    new PairedClientRegistry(NullLogger<PairedClientRegistry>.Instance, storePath + ".pairing"),
                    new ClientSessionRegistry(), storePath, consentTimeout: null);
                break;
            case "queue":
                _ = new TransferQueueService(NullLogger<TransferQueueService>.Instance, storePath);
                break;
            case "names":
                _ = new PairedClientNameStore(NullLogger<PairedClientNameStore>.Instance, storePath);
                break;
            case "activity":
                _ = new PairedDeviceActivityStore(NullLogger<PairedDeviceActivityStore>.Instance, storePath);
                break;
            case "overrides":
                _ = new PairedDeviceNameOverrideStore(
                    NullLogger<PairedDeviceNameOverrideStore>.Instance, storePath);
                break;
            case "roots":
                _ = new FileTransferService(NullLogger<FileTransferService>.Instance, storePath);
                break;
            default:
                Assert.Fail($"unknown store '{store}' — add it to Construct or drop the InlineData");
                break;
        }
    }
}
