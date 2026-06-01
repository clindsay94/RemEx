using System;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Remex.Core.Services.Security;

namespace Remex.Host.Services.Security;

/// <summary>
/// Implements the 2.0 pairing protocol with ECDH P-256:
/// 1. Client sends PairingRequest with its ECDH public key.
/// 2. Host generates ephemeral ECDH keypair, derives shared secret via P-256.
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

    // Pairing session state (only one active session at a time).
    // Design note: PairingService is registered as a singleton. To avoid concurrent
    // sessions corrupting each other's ECDH material, we enforce a single-active-session
    // constraint via a semaphore that rejects (rather than queues) concurrent StartPairingAsync
    // calls. This is simpler than a per-connection ConcurrentDictionary because RemEx is a
    // single-host / single-client product — simultaneous pairing from two clients is always a
    // misuse and should be surfaced as an error, not silently interleaved.
    private byte[]? _sharedSecret; // ECDH-derived shared secret
    private byte[]? _sessionKey;   // HKDF-derived session key
    private string? _activePin;
    private long _expiresAtUnixMs;
    private ECDiffieHellman? _hostEcdh; // P-256 private key
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
        // Acquire the lock, allowing up to 1000ms to resolve any transient background operations
        // (like a cancellation from a previously disconnected socket) without throwing false errors.
        if (!await _lock.WaitAsync(1000, ct))
        {
            throw new InvalidOperationException(
                "A pairing session is already in progress. " +
                "Only one pairing session is permitted at a time.");
        }
        try
        {
            if (IsPairingActive)
            {
                throw new InvalidOperationException(
                    "A pairing session is already active. " +
                    "Wait for the current session to complete or expire before starting a new one.");
            }

            return StartPairingCore();
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<PairingState> GetOrStartPairingAsync(CancellationToken ct)
    {
        var acquisition = await AcquirePairingSessionAsync(ct);
        return acquisition.State;
    }

    public async Task<PairingSessionAcquisition> AcquirePairingSessionAsync(CancellationToken ct)
    {
        if (!await _lock.WaitAsync(1000, ct))
        {
            throw new InvalidOperationException(
                "A pairing session is already in progress. " +
                "Only one pairing session is permitted at a time.");
        }

        try
        {
            if (IsPairingActive)
            {
                return new PairingSessionAcquisition(GetActiveStateCore(), StartedNewSession: false);
            }

            return new PairingSessionAcquisition(StartPairingCore(), StartedNewSession: true);
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

    public bool TryGetActivePinInfo(out string pin, out long expiresAtUnixMs)
    {
        if (!_lock.Wait(0))
        {
            pin = string.Empty;
            expiresAtUnixMs = 0;
            return false;
        }

        try
        {
            if (_activePin is not null && IsPairingActive)
            {
                pin = _activePin;
                expiresAtUnixMs = _expiresAtUnixMs;
                return true;
            }

            pin = string.Empty;
            expiresAtUnixMs = 0;
            return false;
        }
        finally
        {
            _lock.Release();
        }
    }

    public Task<bool> VerifyClientHmacAsync(string clientHmacBase64, CancellationToken ct)
    {
        return VerifyClientHmacCoreAsync(clientHmacBase64, ct);
    }

    private async Task<bool> VerifyClientHmacCoreAsync(string clientHmacBase64, CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            if (_activePin is null || !IsPairingActive)
            {
                _logger.LogWarning("Pairing verification attempted but no active session or session expired.");
                return false;
            }

            if (_sessionKey is null)
            {
                _logger.LogWarning("Pairing verification attempted but session key not yet derived (client public key not received).");
                return false;
            }

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
                CancelPairingCore(); // Consume the session
            }
            else
            {
                _logger.LogWarning("Pairing verification failed — HMAC mismatch.");
            }

            return match;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during pairing verification.");
            return false;
        }
        finally
        {
            _lock.Release();
        }
    }

    public void CancelPairing()
    {
        _lock.Wait();
        try
        {
            CancelPairingCore();
        }
        finally
        {
            _lock.Release();
        }
    }

    private void CancelPairingCore()
    {
        _activePin = null;
        if (_sharedSecret != null)
        {
            CryptographicOperations.ZeroMemory(_sharedSecret);
            _sharedSecret = null;
        }
        if (_sessionKey != null)
        {
            CryptographicOperations.ZeroMemory(_sessionKey);
            _sessionKey = null;
        }
        _hostPublicKeyBase64 = null;
        _clientPublicKeyBase64 = null;
        _hostEcdh?.Dispose();
        _hostEcdh = null;
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
            if (_hostEcdh is null || _activePin is null)
                throw new InvalidOperationException("No active pairing session.");

            if (_clientPublicKeyBase64 is not null)
            {
                if (string.Equals(_clientPublicKeyBase64, clientPublicKeyBase64, StringComparison.Ordinal))
                {
                    if (_sessionKey is not null)
                    {
                        _logger.LogInformation("Pairing request reused the active client binding; returning existing host HMAC.");
                        return ComputeHostHmac();
                    }
                }
                else
                {
                    throw new InvalidOperationException(
                        "The active pairing session is already bound to a different client. " +
                        "Wait for it to complete, expire, or cancel before retrying.");
                }
            }

            // Import client public key
            using var clientPeer = ECDiffieHellman.Create();
            clientPeer.ImportSubjectPublicKeyInfo(Convert.FromBase64String(clientPublicKeyBase64), out _);

            if (_sharedSecret != null)
            {
                CryptographicOperations.ZeroMemory(_sharedSecret);
                _sharedSecret = null;
            }

            // Perform P-256 key agreement
            var sharedSecret = _hostEcdh.DeriveRawSecretAgreement(clientPeer.PublicKey);

            // Derive session key via HKDF-SHA256
            // Salt = certificate SPKI hash (binds to the TLS cert)
            // Info = "remex-pair-v1" (domain separation)
            var certSpkiHash = Convert.FromBase64String(_certificateService.GetSpkiSha256Base64());
            var sessionKeyBytes = HKDF.DeriveKey(
                HashAlgorithmName.SHA256,
                sharedSecret,
                outputLength: 32,
                salt: certSpkiHash,
                info: Encoding.UTF8.GetBytes("remex-pair-v1"));
            _sharedSecret = sharedSecret;
            _clientPublicKeyBase64 = clientPublicKeyBase64;
            _sessionKey = sessionKeyBytes;

            _logger.LogInformation("Session key derived from ECDH P-256.");
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

    private PairingState StartPairingCore()
    {
        if (_sharedSecret != null)
        {
            CryptographicOperations.ZeroMemory(_sharedSecret);
            _sharedSecret = null;
        }

        if (_sessionKey != null)
        {
            CryptographicOperations.ZeroMemory(_sessionKey);
            _sessionKey = null;
        }

        _hostEcdh?.Dispose();
        _hostEcdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);

        var hostPubDer = _hostEcdh.PublicKey.ExportSubjectPublicKeyInfo();
        _hostPublicKeyBase64 = Convert.ToBase64String(hostPubDer);

        _activePin = GeneratePin();
        _expiresAtUnixMs = DateTimeOffset.UtcNow.AddSeconds(PairingTimeoutSeconds).ToUnixTimeMilliseconds();

        _clientPublicKeyBase64 = null;

        _logger.LogInformation("Pairing session started. PIN: {Pin}, Expires at: {Expiry}",
            _activePin, DateTimeOffset.FromUnixTimeMilliseconds(_expiresAtUnixMs));

        try { PinDisplayed?.Invoke(_activePin, _expiresAtUnixMs); }
        catch (Exception ex) { _logger.LogWarning(ex, "PinDisplayed handler threw."); }

        return new PairingState(_hostPublicKeyBase64, _activePin, _expiresAtUnixMs);
    }

    private PairingState GetActiveStateCore()
    {
        if (_activePin is null || _hostPublicKeyBase64 is null || !IsPairingActive)
        {
            throw new InvalidOperationException("No active pairing session.");
        }

        return new PairingState(_hostPublicKeyBase64, _activePin, _expiresAtUnixMs);
    }
}

public sealed record PairingSessionAcquisition(PairingState State, bool StartedNewSession);
