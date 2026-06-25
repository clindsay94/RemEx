using Microsoft.Extensions.Logging.Abstractions;
using Remex.Agent.Services.Security;

namespace Remex.Agent.Tests;

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

    [Fact]
    public void TryMigrateLegacyStore_CopiesLegacyFile_WhenTargetMissing()
    {
        // Regression: switching the host to the LocalSystem Windows Service changed the registry
        // path from per-user LocalAppData to machine-wide ProgramData, orphaning prior pairings.
        // When the legacy file is still readable by the current account, it should migrate forward
        // so the operator does not have to re-pair.
        var tempDirectory = Directory.CreateTempSubdirectory();
        try
        {
            var legacyPath = Path.Combine(tempDirectory.FullName, "legacy", "paired_clients.json");
            var targetPath = Path.Combine(tempDirectory.FullName, "machinewide", "paired_clients.json");

            // Seed a legacy registry, then construct against it so it writes a real file.
            new PairedClientRegistry(NullLogger<PairedClientRegistry>.Instance, legacyPath)
                .RegisterClient("android-device-1");
            Assert.True(File.Exists(legacyPath));

            var migrated = PairedClientRegistry.TryMigrateLegacyStore(
                targetPath, legacyPath, NullLogger<PairedClientRegistry>.Instance);

            Assert.True(migrated);
            Assert.True(File.Exists(targetPath));

            var registry = new PairedClientRegistry(NullLogger<PairedClientRegistry>.Instance, targetPath);
            Assert.True(registry.IsClientPaired("android-device-1"));
        }
        finally
        {
            tempDirectory.Delete(recursive: true);
        }
    }

    [Fact]
    public void TryMigrateLegacyStore_IsNoOp_WhenTargetAlreadyExists()
    {
        // Never clobber an existing machine-wide registry with a stale per-user one.
        var tempDirectory = Directory.CreateTempSubdirectory();
        try
        {
            var legacyPath = Path.Combine(tempDirectory.FullName, "legacy", "paired_clients.json");
            var targetPath = Path.Combine(tempDirectory.FullName, "machinewide", "paired_clients.json");

            new PairedClientRegistry(NullLogger<PairedClientRegistry>.Instance, legacyPath)
                .RegisterClient("legacy-client");
            new PairedClientRegistry(NullLogger<PairedClientRegistry>.Instance, targetPath)
                .RegisterClient("current-client");

            var migrated = PairedClientRegistry.TryMigrateLegacyStore(
                targetPath, legacyPath, NullLogger<PairedClientRegistry>.Instance);

            Assert.False(migrated);

            var registry = new PairedClientRegistry(NullLogger<PairedClientRegistry>.Instance, targetPath);
            Assert.True(registry.IsClientPaired("current-client"));
            Assert.False(registry.IsClientPaired("legacy-client"));
        }
        finally
        {
            tempDirectory.Delete(recursive: true);
        }
    }

    [Fact]
    public void TryMigrateLegacyStore_IsNoOp_WhenLegacyPathEqualsTarget()
    {
        // On platforms where the path did not change, the legacy path equals the target and
        // migration must not attempt to copy a file onto itself.
        var tempDirectory = Directory.CreateTempSubdirectory();
        try
        {
            var path = Path.Combine(tempDirectory.FullName, "paired_clients.json");
            new PairedClientRegistry(NullLogger<PairedClientRegistry>.Instance, path)
                .RegisterClient("client-a");

            var migrated = PairedClientRegistry.TryMigrateLegacyStore(
                path, path, NullLogger<PairedClientRegistry>.Instance);

            Assert.False(migrated);
        }
        finally
        {
            tempDirectory.Delete(recursive: true);
        }
    }
}
