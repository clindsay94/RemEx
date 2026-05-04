using System;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using NSec.Cryptography;
using Remex.Core.Services.Security;

namespace Remex.Host.Services.Security;

/// <summary>
/// Implements the 2.0 pairing protocol with ECDH X25519:
/// 1. Client sends PairingRequest with its ECDH public key.
/// 2. Host generates ephemeral ECDH keypair, derives shared secret via X25519.
/// 3. Host generates 6-digit PIN, computes pinHmac = HMAC-SHA256(sessionKey, PIN).
/// 4. Host responds with PairingResponse (host pub key, pinHmac, certSpki).
/// 5. User enters PIN on client; client verifies pinHmac, sends PairingComplete with clientPinHmac.
/// 6. Host verifies clientPinHmac, records client as paired.
/// </summary>
public sealed class PairingService : IPairingService
{
    private readonly ILogger<PairingService> _logger;
    private readonly ICertificateService _certificateService;
    private readonly SemaphoreSlim _lock = new(1, 1);

    // Pairing session state (only one active session at a time)
    private SharedSecret? _sharedSecret; // ECDH-derived shared secret
    private byte[]? _sessionKey;   // HKDF-derived session key
    private string? _activePin;
    private long _expiresAtUnixMs;
    private NSec.Cryptography.Key? _hostPrivateKey; // X25519 private key
    private string? _hostPublicKeyBase64;
    private string? _clientPublicKeyBase64;

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
            // Generate ephemeral X25519 keypair via NSec
            var keyAgreementAlgorithm = KeyAgreementAlgorithm.X25519;
            _hostPrivateKey = NSec.Cryptography.Key.Create(keyAgreementAlgorithm, new KeyCreationParameters { ExportPolicy = KeyExportPolicies.AllowPlaintextExport });

            // Export public key
            var hostPubBytes = _hostPrivateKey.PublicKey.Export(KeyBlobFormat.RawPublicKey);
            _hostPublicKeyBase64 = Convert.ToBase64String(hostPubBytes);

            // Generate a 6-digit PIN
            _activePin = GeneratePin();
            _expiresAtUnixMs = DateTimeOffset.UtcNow.AddSeconds(PairingTimeoutSeconds).ToUnixTimeMilliseconds();

            // Shared secret and session key will be derived when client public key arrives
            _sharedSecret = null;
            _sessionKey = null;
            _clientPublicKeyBase64 = null;

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

        if (_sessionKey is null)
        {
            _logger.LogWarning("Pairing verification attempted but session key not yet derived (client public key not received).");
            return Task.FromResult(false);
        }

        try
        {
            // Compute expected HMAC: HMAC-SHA256(sessionKey, "ack:" + PIN)
            var ackMessage = Encoding.UTF8.GetBytes("ack:" + _activePin);
            var expectedHmac = HMACSHA256.HashData(_sessionKey, ackMessage);
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
        _sharedSecret?.Dispose();
        _sharedSecret = null;
        _sessionKey = null;
        _hostPublicKeyBase64 = null;
        _clientPublicKeyBase64 = null;
        _hostPrivateKey?.Dispose();
        _hostPrivateKey = null;
        _expiresAtUnixMs = 0;
        _logger.LogInformation("Pairing session cancelled/consumed.");

        try { PinCleared?.Invoke(); }
        catch (Exception ex) { _logger.LogWarning(ex, "PinCleared handler threw."); }
    }

    /// <summary>
    /// Derives the session key from the client's public key and the host's private key.
    /// Must be called after receiving the client's PairingRequest and before sending PairingResponse.
    /// </summary>
    public async Task<string> DeriveSessionKeyAsync(string clientPublicKeyBase64, CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            if (_hostPrivateKey is null || _activePin is null)
                throw new InvalidOperationException("No active pairing session.");

            _clientPublicKeyBase64 = clientPublicKeyBase64;

            // Import client public key
            var clientPubBytes = Convert.FromBase64String(clientPublicKeyBase64);
            var keyAgreementAlgorithm = KeyAgreementAlgorithm.X25519;
            var clientPublicKey = PublicKey.Import(keyAgreementAlgorithm, clientPubBytes, KeyBlobFormat.RawPublicKey);

            // Perform X25519 key agreement
            _sharedSecret = keyAgreementAlgorithm.Agree(_hostPrivateKey, clientPublicKey);
            if (_sharedSecret == null)
            {
                _logger.LogError("Key agreement failed - shared secret is null.");
                throw new InvalidOperationException("ECDH key agreement failed.");
            }

            // Derive session key via HKDF-SHA256
            // Salt = certificate SPKI hash (binds to the TLS cert)
            // Info = "remex-pair-v1" (domain separation)
            var certSpkiHash = Convert.FromBase64String(_certificateService.GetSpkiSha256Base64());
            var kdf = KeyDerivationAlgorithm.HkdfSha256;
            var sessionKeyBytes = kdf.DeriveBytes(
                _sharedSecret,
                certSpkiHash,
                Encoding.UTF8.GetBytes("remex-pair-v1"),
                32);
            _sessionKey = sessionKeyBytes;

            _logger.LogInformation("Session key derived from ECDH X25519.");
            return ComputeHostHmac();
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Compute the host-side HMAC for the PairingResponse.
    /// Uses the ECDH-derived session key and the PIN.
    /// </summary>
    public string ComputeHostHmac()
    {
        if (_activePin is null || _sessionKey is null)
            throw new InvalidOperationException("No active pairing session or session key not derived.");

        // Compute HMAC-SHA256(sessionKey, PIN)
        var pinBytes = Encoding.UTF8.GetBytes(_activePin);
        var hmac = HMACSHA256.HashData(_sessionKey, pinBytes);
        return Convert.ToBase64String(hmac);
    }

    private static string GeneratePin()
    {
        // Generates a cryptographically secure 6-digit PIN
        var pin = RandomNumberGenerator.GetInt32(0, 1_000_000);
        return pin.ToString("D6");
    }
}
