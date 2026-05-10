using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Remex.Core.Services.Security;
using Remex.Host.Services.Security;

namespace Remex.Host.Tests;

public sealed class PairingServiceTests
{
    private readonly Mock<ICertificateService> _certSvc = new();
    private readonly string _fakeSpkiBase64;

    public PairingServiceTests()
    {
        _fakeSpkiBase64 = Convert.ToBase64String(new byte[32]);
        _certSvc.Setup(c => c.GetSpkiSha256Base64()).Returns(_fakeSpkiBase64);
    }

    private PairingService CreateService() =>
        new(NullLogger<PairingService>.Instance, _certSvc.Object);

    // ── StartPairingAsync ──────────────────────────────────────────────────────

    [Fact]
    public async Task StartPairing_ReturnsValidState_WithNonEmptyPin()
    {
        var svc = CreateService();

        var state = await svc.StartPairingAsync(CancellationToken.None);

        Assert.NotNull(state.HostPublicKeyBase64);
        Assert.Equal(6, state.Pin.Length);
        Assert.True(state.Pin.All(char.IsDigit));
        Assert.True(state.ExpiresAtUnixMs > DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }

    [Fact]
    public async Task StartPairing_SetsIsPairingActive()
    {
        var svc = CreateService();

        await svc.StartPairingAsync(CancellationToken.None);

        Assert.True(svc.IsPairingActive);
    }

    [Fact]
    public async Task StartPairing_WhenSessionAlreadyActive_ThrowsInvalidOperation()
    {
        var svc = CreateService();
        await svc.StartPairingAsync(CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.StartPairingAsync(CancellationToken.None));
    }

    // ── Expiry ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task IsPairingActive_ReturnsFalse_WhenSessionExpired()
    {
        var svc = CreateService();
        await svc.StartPairingAsync(CancellationToken.None);

        var field = typeof(PairingService).GetField(
            "_expiresAtUnixMs", BindingFlags.NonPublic | BindingFlags.Instance)!;
        field.SetValue(svc, DateTimeOffset.UtcNow.AddSeconds(-1).ToUnixTimeMilliseconds());

        Assert.False(svc.IsPairingActive);
    }

    [Fact]
    public async Task VerifyClientHmac_ReturnsFalse_WhenSessionExpired()
    {
        var svc = CreateService();
        await svc.StartPairingAsync(CancellationToken.None);

        var field = typeof(PairingService).GetField(
            "_expiresAtUnixMs", BindingFlags.NonPublic | BindingFlags.Instance)!;
        field.SetValue(svc, DateTimeOffset.UtcNow.AddSeconds(-1).ToUnixTimeMilliseconds());

        var result = await svc.VerifyClientHmacAsync("anyhmac", CancellationToken.None);

        Assert.False(result);
    }

    // ── ECDH round-trip ───────────────────────────────────────────────────────

    [Fact]
    public async Task DeriveSessionKey_AndVerifyClientHmac_FullRoundTrip()
    {
        var svc = CreateService();
        var state = await svc.StartPairingAsync(CancellationToken.None);

        using var clientEcdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var clientPubBase64 = Convert.ToBase64String(
            clientEcdh.PublicKey.ExportSubjectPublicKeyInfo());

        var hostHmacBase64 = await svc.DeriveSessionKeyAsync(clientPubBase64, CancellationToken.None);

        // Derive the same session key client-side
        using var hostPeer = ECDiffieHellman.Create();
        hostPeer.ImportSubjectPublicKeyInfo(
            Convert.FromBase64String(state.HostPublicKeyBase64), out _);
        var clientSharedSecret = clientEcdh.DeriveRawSecretAgreement(hostPeer.PublicKey);
        var certSpkiHash = Convert.FromBase64String(_fakeSpkiBase64);
        var clientSessionKey = HKDF.DeriveKey(
            HashAlgorithmName.SHA256,
            clientSharedSecret,
            outputLength: 32,
            salt: certSpkiHash,
            info: Encoding.UTF8.GetBytes("remex-pair-v1"));

        // Verify host HMAC: HMAC(sessionKey, PIN)
        var expectedHostHmac = Convert.ToBase64String(
            HMACSHA256.HashData(clientSessionKey, Encoding.UTF8.GetBytes(state.Pin)));
        Assert.Equal(expectedHostHmac, hostHmacBase64);

        // Client computes ack HMAC: HMAC(sessionKey, "ack:" + PIN)
        var clientHmac = Convert.ToBase64String(
            HMACSHA256.HashData(clientSessionKey, Encoding.UTF8.GetBytes("ack:" + state.Pin)));

        var verified = await svc.VerifyClientHmacAsync(clientHmac, CancellationToken.None);

        Assert.True(verified);
        Assert.False(svc.IsPairingActive);
    }

    [Fact]
    public async Task VerifyClientHmac_WithWrongHmac_ReturnsFalse()
    {
        var svc = CreateService();
        await svc.StartPairingAsync(CancellationToken.None);

        using var clientEcdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var clientPubBase64 = Convert.ToBase64String(
            clientEcdh.PublicKey.ExportSubjectPublicKeyInfo());
        await svc.DeriveSessionKeyAsync(clientPubBase64, CancellationToken.None);

        var result = await svc.VerifyClientHmacAsync(
            Convert.ToBase64String(new byte[32]), CancellationToken.None);

        Assert.False(result);
    }

    [Fact]
    public async Task VerifyClientHmac_BeforeSessionKeyDerived_ReturnsFalse()
    {
        var svc = CreateService();
        await svc.StartPairingAsync(CancellationToken.None);

        var result = await svc.VerifyClientHmacAsync("anyhmac", CancellationToken.None);

        Assert.False(result);
    }

    // ── CancelPairing ─────────────────────────────────────────────────────────

    [Fact]
    public async Task CancelPairing_ClearsActiveSession()
    {
        var svc = CreateService();
        await svc.StartPairingAsync(CancellationToken.None);

        svc.CancelPairing();

        Assert.False(svc.IsPairingActive);
    }

    [Fact]
    public async Task CancelPairing_AllowsNewSessionToStart()
    {
        var svc = CreateService();
        await svc.StartPairingAsync(CancellationToken.None);
        svc.CancelPairing();

        var ex = await Record.ExceptionAsync(
            () => svc.StartPairingAsync(CancellationToken.None));

        Assert.Null(ex);
    }

    // ── Events ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task StartPairing_FiresPinDisplayedEvent()
    {
        var svc = CreateService();
        string? capturedPin = null;
        svc.PinDisplayed += (pin, _) => capturedPin = pin;

        var state = await svc.StartPairingAsync(CancellationToken.None);

        Assert.Equal(state.Pin, capturedPin);
    }

    [Fact]
    public async Task CancelPairing_FiresPinClearedEvent()
    {
        var svc = CreateService();
        await svc.StartPairingAsync(CancellationToken.None);
        var cleared = false;
        svc.PinCleared += () => cleared = true;

        svc.CancelPairing();

        Assert.True(cleared);
    }
}
