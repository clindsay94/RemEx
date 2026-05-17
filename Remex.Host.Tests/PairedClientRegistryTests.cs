using Microsoft.Extensions.Logging.Abstractions;
using Remex.Host.Services.Security;

namespace Remex.Host.Tests;

public sealed class PairedClientRegistryTests
{
    [Fact]
    public void RegisterClient_PersistsAcrossInstances()
    {
        var tempDirectory = Directory.CreateTempSubdirectory();
        try
        {
            var storePath = Path.Combine(tempDirectory.FullName, "paired_clients.json");

            var registry = new PairedClientRegistry(NullLogger<PairedClientRegistry>.Instance, storePath);
            registry.RegisterClient("client-a");

            var reloaded = new PairedClientRegistry(NullLogger<PairedClientRegistry>.Instance, storePath);

            Assert.True(reloaded.IsClientPaired("client-a"));
        }
        finally
        {
            tempDirectory.Delete(recursive: true);
        }
    }

    [Fact]
    public void UnregisterClient_PersistsRemovalAcrossInstances()
    {
        var tempDirectory = Directory.CreateTempSubdirectory();
        try
        {
            var storePath = Path.Combine(tempDirectory.FullName, "paired_clients.json");

            var registry = new PairedClientRegistry(NullLogger<PairedClientRegistry>.Instance, storePath);
            registry.RegisterClient("client-a");
            registry.UnregisterClient("client-a");

            var reloaded = new PairedClientRegistry(NullLogger<PairedClientRegistry>.Instance, storePath);

            Assert.False(reloaded.IsClientPaired("client-a"));
        }
        finally
        {
            tempDirectory.Delete(recursive: true);
        }
    }

    [Fact]
    public void RegisteredClient_SurvivesSimulatedHostRestart()
    {
        // End-to-end persistence: register, drop the registry instance entirely,
        // construct a fresh instance against the same store path, and confirm the
        // pairing record is recognized. This is the contract the host depends on
        // when the desktop process exits and starts again — the on-disk file is
        // the only shared state. The two-instance round-trip simulates exactly
        // that "process restarted" path.
        var tempDirectory = Directory.CreateTempSubdirectory();
        try
        {
            var storePath = Path.Combine(tempDirectory.FullName, "paired_clients.json");

            var beforeRestart = new PairedClientRegistry(NullLogger<PairedClientRegistry>.Instance, storePath);
            beforeRestart.RegisterClient("android-device-1");
            beforeRestart.RegisterClient("android-device-2");

            Assert.True(File.Exists(storePath), "Persistence file should exist after RegisterClient.");

            // Simulate desktop host process restart by abandoning the first instance and
            // constructing a brand new one. No in-memory state is shared.
            var afterRestart = new PairedClientRegistry(NullLogger<PairedClientRegistry>.Instance, storePath);

            Assert.True(afterRestart.IsClientPaired("android-device-1"));
            Assert.True(afterRestart.IsClientPaired("android-device-2"));
            Assert.False(afterRestart.IsClientPaired("never-paired"));
        }
        finally
        {
            tempDirectory.Delete(recursive: true);
        }
    }
}
