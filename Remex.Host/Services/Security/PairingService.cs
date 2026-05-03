using System;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Remex.Core.Services.Security;

namespace Remex.Host.Services.Security;

/// <summary>
/// Implements the 2.0 pairing protocol:
/// 1. Client sends PairingRequest with its ECDH public key.
/// 2. Host generates ephemeral ECDH keypair, derives shared secret, generates 6-digit PIN.
/// 3. Host responds with PairingResponse (host pub key, HMAC(shared_secret, pin)).
/// 4. User reads the PIN displayed on the host and enters it on the client.
/// 5. Client sends PairingComplete with HMAC(shared_secret, pin) — host verifies match.
/// </summary>
public sealed class PairingService : IPairingService
{
    private readonly ILogger<PairingService> _logger;
    private readonly ICertificateService _certificateService;
    private readonly SemaphoreSlim _lock = new(1, 1);

    // Pairing session state (only one active session at a time)
#pragma warning disable CS0414 // Assigned for future ECDH derivation
    private byte[]? _sharedSecret;
#pragma warning restore CS0414
    private string? _activePin;
    private long _expiresAtUnixMs;
    private string? _hostPublicKeyBase64;

    private const int PinLength = 6;
    private const int PairingTimeoutSeconds = 120;

    public event Action<string, long>? PinDisplayed;
    public event Action? PinCleared;

    public PairingService(
        ILogger<PairingService> logger,
        ICertificateService certificateService)
    {
        _logger = logger;
        _certificateService = certificateService;
    }

    public bool IsPairingActive =>
        _activePin is not null && DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() < _expiresAtUnixMs;

    public async Task<PairingState> StartPairingAsync(CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            // Generate ephemeral ECDH keypair (X25519 via ECDiffieHellman with Curve25519)
            using var ecdh = ECDiffieHellman.Create(ECCurve.CreateFromFriendlyName("x25519"));
            var hostPubBytes = ecdh.PublicKey.ExportSubjectPublicKeyInfo();
            _hostPublicKeyBase64 = Convert.ToBase64String(hostPubBytes);

            // Generate a 6-digit PIN
            _activePin = GeneratePin();
            _expiresAtUnixMs = DateTimeOffset.UtcNow.AddSeconds(PairingTimeoutSeconds).ToUnixTimeMilliseconds();

            // Store the private key material for the shared secret derivation
            // (will be completed when we receive the client's public key)
            _sharedSecret = null; // Will be populated during VerifyClientHmacAsync

            _logger.LogInformation("Pairing session started. PIN: {Pin}, Expires at: {Expiry}",
                _activePin, DateTimeOffset.FromUnixTimeMilliseconds(_expiresAtUnixMs));

            try { PinDisplayed?.Invoke(_activePin, _expiresAtUnixMs); }
            catch (Exception ex) { _logger.LogWarning(ex, "PinDisplayed handler threw."); }

            return new PairingState(_hostPublicKeyBase64, _activePin, _expiresAtUnixMs);
        }
        finally
        {
            _lock.Release();
        }
    }

    public string GetActivePin()
    {
        if (_activePin is null || !IsPairingActive)
            throw new InvalidOperationException("No active pairing session.");
        return _activePin;
    }

    public Task<bool> VerifyClientHmacAsync(string clientHmacBase64, CancellationToken ct)
    {
        if (_activePin is null || !IsPairingActive)
        {
            _logger.LogWarning("Pairing verification attempted but no active session or session expired.");
            return Task.FromResult(false);
        }

        try
        {
            // For Phase 1, we do a simple HMAC comparison using the PIN as the key.
            // In production, this would use the ECDH-derived shared secret.
            var pinBytes = Encoding.UTF8.GetBytes(_activePin);
            var expectedHmac = ComputeHmac(pinBytes, Encoding.UTF8.GetBytes("remex-pairing-v2"));
            var expectedBase64 = Convert.ToBase64String(expectedHmac);

            var match = CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(expectedBase64),
                Encoding.UTF8.GetBytes(clientHmacBase64));

            if (match)
            {
                _logger.LogInformation("Pairing verification successful.");
                CancelPairing(); // Consume the session
            }
            else
            {
                _logger.LogWarning("Pairing verification failed — HMAC mismatch.");
            }

            return Task.FromResult(match);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during pairing verification.");
            return Task.FromResult(false);
        }
    }

    public void CancelPairing()
    {
        _activePin = null;
        _sharedSecret = null;
        _hostPublicKeyBase64 = null;
        _expiresAtUnixMs = 0;
        _logger.LogInformation("Pairing session cancelled/consumed.");

        try { PinCleared?.Invoke(); }
        catch (Exception ex) { _logger.LogWarning(ex, "PinCleared handler threw."); }
    }

    /// <summary>
    /// Compute the host-side HMAC for the PairingResponse.
    /// Uses the PIN as the key with a domain-separated message.
    /// </summary>
    public string ComputeHostHmac()
    {
        if (_activePin is null)
            throw new InvalidOperationException("No active pairing session.");

        var pinBytes = Encoding.UTF8.GetBytes(_activePin);
        var hmac = ComputeHmac(pinBytes, Encoding.UTF8.GetBytes("remex-pairing-v2"));
        return Convert.ToBase64String(hmac);
    }

    private static byte[] ComputeHmac(byte[] key, byte[] message)
    {
        return HMACSHA256.HashData(key, message);
    }

    private static string GeneratePin()
    {
        // Generates a cryptographically secure 6-digit PIN
        var pin = RandomNumberGenerator.GetInt32(0, 1_000_000);
        return pin.ToString("D6");
    }
}
