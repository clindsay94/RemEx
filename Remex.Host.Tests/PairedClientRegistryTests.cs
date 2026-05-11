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
}
