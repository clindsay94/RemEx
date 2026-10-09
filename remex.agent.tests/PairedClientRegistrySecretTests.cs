using System.Security.Cryptography;
using Microsoft.Extensions.Logging.Abstractions;
using Remex.Agent.Services.Security;

namespace Remex.Agent.Tests;

/// <summary>
/// Reconnect-secret storage, revoke, lookup and bad-store handling of <see cref="PairedClientRegistry"/>
/// (PAIR-1/PAIR-4). Presence-only persistence lives in <see cref="PairedClientRegistryTests"/>.
/// </summary>
public sealed class PairedClientRegistrySecretTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory();

    private string StorePath => Path.Combine(_dir.FullName, "paired_clients.json");

    private PairedClientRegistry Open() => new(NullLogger<PairedClientRegistry>.Instance, StorePath);

    public void Dispose() => _dir.Delete(recursive: true);

    [Fact]
    public void ReconnectSecret_SurvivesRestartByteForByte()
    {
        var secret = RandomNumberGenerator.GetBytes(32);
        Open().RegisterClient("client-a", secret);

        Assert.True(Open().TryGetReconnectSecret("client-a", out var loaded));
        Assert.Equal(secret, loaded);
    }

    [Fact]
    public void TryGetReconnectSecret_ReturnsACopy_SoZeroingItDoesNotCorruptTheStore()
    {
        var secret = RandomNumberGenerator.GetBytes(32);
        var registry = Open();
        registry.RegisterClient("client-a", secret);

        registry.TryGetReconnectSecret("client-a", out var first);
        CryptographicOperations.ZeroMemory(first);

        Assert.True(registry.TryGetReconnectSecret("client-a", out var second));
        Assert.Equal(secret, second);
    }

    [Fact]
    public void RegisterClient_SecondPairing_ReplacesTheOldSecret()
    {
        var registry = Open();
        registry.RegisterClient("client-a", RandomNumberGenerator.GetBytes(32));
        var fresh = RandomNumberGenerator.GetBytes(32);
        registry.RegisterClient("client-a", fresh);

        Assert.True(Open().TryGetReconnectSecret("client-a", out var loaded));
        Assert.Equal(fresh, loaded);
    }

    [Fact]
    public void RegisterClient_PresenceOnlyDoesNotDowngradeAnExistingSecret()
    {
        var secret = RandomNumberGenerator.GetBytes(32);
        var registry = Open();
        registry.RegisterClient("client-a", secret);

        registry.RegisterClient("client-a");

        Assert.True(registry.TryGetReconnectSecret("client-a", out var loaded));
        Assert.Equal(secret, loaded);
    }

    [Fact]
    public void RegisterClient_EmptySecret_ThrowsAndRegistersNothing()
    {
        var registry = Open();

        Assert.Throws<ArgumentException>(() => registry.RegisterClient("client-a", []));
        Assert.Throws<ArgumentNullException>(() => registry.RegisterClient("client-a", null!));
        Assert.False(registry.IsClientPaired("client-a"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void RegisterClient_BlankId_IsIgnored(string blank)
    {
        var registry = Open();

        registry.RegisterClient(blank);
        registry.RegisterClient(blank, RandomNumberGenerator.GetBytes(32));

        Assert.Empty(registry.PairedClientIds());
        Assert.False(File.Exists(StorePath));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void BlankClientId_IsNeverPairedAndHasNoSecret(string? blank)
    {
        var registry = Open();
        registry.RegisterClient("client-a", RandomNumberGenerator.GetBytes(32));

        Assert.False(registry.IsClientPaired(blank));
        Assert.False(registry.TryGetReconnectSecret(blank, out var secret));
        Assert.Empty(secret);
    }

    [Fact]
    public void ClientIdLookup_IsCaseSensitive()
    {
        var registry = Open();
        registry.RegisterClient("Client-A", RandomNumberGenerator.GetBytes(32));

        Assert.False(registry.IsClientPaired("client-a"));
        Assert.False(registry.TryGetReconnectSecret("client-a", out _));
    }

    [Fact]
    public void SecretlessEntry_IsPairedButYieldsNoSecret()
    {
        var registry = Open();
        registry.RegisterClient("legacy");

        Assert.True(registry.IsClientPaired("legacy"));
        Assert.False(registry.TryGetReconnectSecret("legacy", out var secret));
        Assert.Empty(secret);
    }

    [Fact]
    public void UnregisterClient_RevokesIdAndSecretAndSurvivesRestart()
    {
        var registry = Open();
        registry.RegisterClient("client-a", RandomNumberGenerator.GetBytes(32));
        registry.RegisterClient("client-b", RandomNumberGenerator.GetBytes(32));

        registry.UnregisterClient("client-a");

        var reloaded = Open();
        Assert.False(reloaded.IsClientPaired("client-a"));
        Assert.False(reloaded.TryGetReconnectSecret("client-a", out _));
        Assert.True(reloaded.IsClientPaired("client-b"));
    }

    [Fact]
    public void UnregisterClient_UnknownId_IsANoOpThatDoesNotTouchTheStore()
    {
        var registry = Open();
        registry.RegisterClient("client-a", RandomNumberGenerator.GetBytes(32));
        var before = File.ReadAllText(StorePath);
        var stamp = File.GetLastWriteTimeUtc(StorePath);

        registry.UnregisterClient("ghost");

        Assert.Equal(before, File.ReadAllText(StorePath));
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(StorePath));
        Assert.True(registry.IsClientPaired("client-a"));
    }

    [Fact]
    public void PairedClientIds_AreSortedOrdinalAndNeverContainSecrets()
    {
        var registry = Open();
        var secret = RandomNumberGenerator.GetBytes(32);
        registry.RegisterClient("b", secret);
        registry.RegisterClient("B", secret);
        registry.RegisterClient("a", secret);

        var ids = registry.PairedClientIds();

        Assert.Equal(["B", "a", "b"], ids);
        Assert.DoesNotContain(ids, id => id.Contains(Convert.ToBase64String(secret)));
    }

    [Fact]
    public void PairedClientIds_IsASnapshot()
    {
        var registry = Open();
        registry.RegisterClient("a", RandomNumberGenerator.GetBytes(32));
        var snapshot = registry.PairedClientIds();

        registry.RegisterClient("b", RandomNumberGenerator.GetBytes(32));

        Assert.Equal(["a"], snapshot);
    }

    [Fact]
    public void Load_LegacyArrayStore_YieldsSecretlessPairedEntries_AndSkipsBlankIds()
    {
        File.WriteAllText(StorePath, "  [\"old-1\", \"\", \"  \", \"old-2\"]");

        var registry = Open();

        Assert.Equal(["old-1", "old-2"], registry.PairedClientIds());
        Assert.False(registry.TryGetReconnectSecret("old-1", out _));
    }

    [Fact]
    public void Load_LegacyArrayStore_IsRewrittenInNewFormatOnNextWrite()
    {
        File.WriteAllText(StorePath, "[\"old-1\"]");
        var registry = Open();
        var secret = RandomNumberGenerator.GetBytes(32);

        registry.RegisterClient("new", secret);

        var reloaded = Open();
        Assert.True(reloaded.IsClientPaired("old-1"));
        Assert.True(reloaded.TryGetReconnectSecret("new", out var loaded));
        Assert.Equal(secret, loaded);
    }

    [Theory]
    [InlineData("{ this is not json")]
    [InlineData("[1, 2, 3]")]
    [InlineData("{\"client-a\": 5}")]
    public void Load_CorruptStore_StartsEmptyInsteadOfThrowing(string content)
    {
        File.WriteAllText(StorePath, content);

        var registry = Open();

        Assert.Empty(registry.PairedClientIds());
    }

    [Fact]
    public void Load_CorruptStore_DoesNotBlockPairingANewClient()
    {
        File.WriteAllText(StorePath, "{ this is not json");
        var registry = Open();
        var secret = RandomNumberGenerator.GetBytes(32);

        registry.RegisterClient("client-a", secret);

        Assert.True(Open().TryGetReconnectSecret("client-a", out var loaded));
        Assert.Equal(secret, loaded);
    }

    [Fact]
    public void Load_EntryWithNullOrGarbageSecret_IsPairedButCannotAuthenticate()
    {
        File.WriteAllText(StorePath, "{\"null-secret\": null, \"bad-b64\": \"***\", \"blank\": \" \"}");

        var registry = Open();

        foreach (var id in new[] { "null-secret", "bad-b64", "blank" })
        {
            Assert.True(registry.IsClientPaired(id));
            Assert.False(registry.TryGetReconnectSecret(id, out var secret));
            Assert.Empty(secret);
        }
    }

    [Fact]
    public void Load_BlankKeyInStore_IsDropped()
    {
        File.WriteAllText(StorePath, "{\"\": \"AAAA\", \"ok\": \"AAAA\"}");

        Assert.Equal(["ok"], Open().PairedClientIds());
    }

    [Fact]
    public void RegisterClient_WhenStoreCannotBeWritten_Throws_SoPairingDoesNotSilentlySucceed()
    {
        // A directory squatting on the store path makes the atomic write impossible on every OS.
        Directory.CreateDirectory(StorePath);
        var registry = Open();

        Assert.ThrowsAny<Exception>(() => registry.RegisterClient("client-a", RandomNumberGenerator.GetBytes(32)));
    }

    [Fact]
    public void TryMigrateLegacyStore_NullOrMissingLegacyPath_IsNoOp()
    {
        var target = Path.Combine(_dir.FullName, "new", "paired_clients.json");

        Assert.False(PairedClientRegistry.TryMigrateLegacyStore(target, null, NullLogger.Instance));
        Assert.False(PairedClientRegistry.TryMigrateLegacyStore(
            target, Path.Combine(_dir.FullName, "absent.json"), NullLogger.Instance));
        Assert.False(File.Exists(target));
    }

    [Fact]
    public void TryMigrateLegacyStore_UnreadableLegacy_ReturnsFalseInsteadOfThrowing()
    {
        var target = Path.Combine(_dir.FullName, "paired_clients.json");
        var legacyDir = Path.Combine(_dir.FullName, "legacy");
        Directory.CreateDirectory(legacyDir);
        // A regular file where the target's parent directory should be makes the copy fail.
        var blocker = Path.Combine(_dir.FullName, "blocker");
        File.WriteAllText(blocker, "x");
        var legacy = Path.Combine(legacyDir, "paired_clients.json");
        File.WriteAllText(legacy, "{}");

        var migrated = PairedClientRegistry.TryMigrateLegacyStore(
            Path.Combine(blocker, "sub", "paired_clients.json"), legacy, NullLogger.Instance);

        Assert.False(migrated);
        Assert.False(File.Exists(target));
    }
}
