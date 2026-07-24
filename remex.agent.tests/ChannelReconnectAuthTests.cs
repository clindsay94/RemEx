using System.Security.Cryptography;
using Microsoft.Extensions.Logging.Abstractions;
using Remex.Agent.Services.Security;
using Remex.Core.Models;

namespace Remex.Agent.Tests;

/// <summary>
/// VULN-2 (RemEx-s032.2): the secondary channels (/ws/desktop, /ws/files) now require reconnect
/// proof-of-possession, not bare clientId presence. These lock the crypto core of that handshake —
/// <see cref="ChannelReconnectAuth.Verify"/> — which mirrors the /ws PAIR-1 verification.
/// </summary>
public sealed class ChannelReconnectAuthTests
{
    private static PairedClientRegistry NewRegistry(out string storePath)
    {
        var dir = Directory.CreateTempSubdirectory();
        storePath = Path.Combine(dir.FullName, "paired_clients.json");
        return new PairedClientRegistry(NullLogger<PairedClientRegistry>.Instance, storePath);
    }

    private static ReconnectProof ProofFor(byte[] secret, byte[] nonce, string clientId) => new()
    {
        ClientId = clientId,
        ProofHmacBase64 = Convert.ToBase64String(HMACSHA256.HashData(secret, nonce)),
    };

    [Fact]
    public void Verify_CorrectProof_ReturnsTrue()
    {
        var registry = NewRegistry(out _);
        var secret = RandomNumberGenerator.GetBytes(32);
        registry.RegisterClient("client-a", secret);
        var nonce = RandomNumberGenerator.GetBytes(32);

        var ok = ChannelReconnectAuth.Verify(
            "client-a", nonce, ProofFor(secret, nonce, "client-a"), registry, NullLogger.Instance);

        Assert.True(ok);
    }

    [Fact]
    public void Verify_WrongSecret_ReturnsFalse()
    {
        var registry = NewRegistry(out _);
        registry.RegisterClient("client-a", RandomNumberGenerator.GetBytes(32));
        var nonce = RandomNumberGenerator.GetBytes(32);

        // Attacker knows the clientId (it's not secret) but not the reconnect secret.
        var forgedSecret = RandomNumberGenerator.GetBytes(32);
        var ok = ChannelReconnectAuth.Verify(
            "client-a", nonce, ProofFor(forgedSecret, nonce, "client-a"), registry, NullLogger.Instance);

        Assert.False(ok);
    }

    [Fact]
    public void Verify_ProofOverDifferentNonce_ReturnsFalse()
    {
        var registry = NewRegistry(out _);
        var secret = RandomNumberGenerator.GetBytes(32);
        registry.RegisterClient("client-a", secret);

        // Proof computed over a replayed/old nonce must not satisfy the fresh challenge nonce.
        var freshNonce = RandomNumberGenerator.GetBytes(32);
        var staleNonce = RandomNumberGenerator.GetBytes(32);
        var ok = ChannelReconnectAuth.Verify(
            "client-a", freshNonce, ProofFor(secret, staleNonce, "client-a"), registry, NullLogger.Instance);

        Assert.False(ok);
    }

    [Fact]
    public void Verify_UnknownClient_ReturnsFalse()
    {
        var registry = NewRegistry(out _);
        var nonce = RandomNumberGenerator.GetBytes(32);

        var ok = ChannelReconnectAuth.Verify(
            "ghost", nonce, ProofFor(RandomNumberGenerator.GetBytes(32), nonce, "ghost"), registry, NullLogger.Instance);

        Assert.False(ok);
    }

    [Fact]
    public void Verify_SecretlessLegacyEntry_ReturnsFalse()
    {
        var registry = NewRegistry(out _);
        // A presence-only paired entry (no reconnect secret) — the pre-PAIR-1 shape — must not pass.
        registry.RegisterClient("legacy-client");
        var nonce = RandomNumberGenerator.GetBytes(32);

        var ok = ChannelReconnectAuth.Verify(
            "legacy-client", nonce, ProofFor(RandomNumberGenerator.GetBytes(32), nonce, "legacy-client"),
            registry, NullLogger.Instance);

        Assert.False(ok);
    }

    [Fact]
    public void Verify_MalformedProofBase64_ReturnsFalse()
    {
        var registry = NewRegistry(out _);
        registry.RegisterClient("client-a", RandomNumberGenerator.GetBytes(32));
        var nonce = RandomNumberGenerator.GetBytes(32);

        var ok = ChannelReconnectAuth.Verify(
            "client-a", nonce, new ReconnectProof { ClientId = "client-a", ProofHmacBase64 = "not*base64!" },
            registry, NullLogger.Instance);

        Assert.False(ok);
    }
}
