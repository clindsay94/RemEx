using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Remex.Core.Guards;
using Remex.Core.Services.Security;

namespace Remex.Agent.Services.Security;

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

    // PIN brute-force throttle: a 6-digit PIN has only 1,000,000 possibilities, so an
    // attacker who can submit pairing_complete repeatedly within a single live session can
    // grind it down. We cap failed PIN attempts per session and burn the session (forcing a
    // brand-new PIN) once the cap is hit. Reset whenever a new session is started/consumed.
    private int _failedHmacAttempts;
    private const int MaxFailedHmacAttempts = 5;

    // Short session lifetime limits the window for online brute-force. A legitimate user
    // receiving the PIN out-of-band and entering it on Android comfortably fits in ~2 minutes;
    // the session can always be restarted. Previously 600s, which left a 10-minute grinding window.
    private const int PairingTimeoutSeconds = 120;

    public event Action<string, long>? PinDisplayed;
    public event Action? PinCleared;

    public PairingService(
        ILogger<PairingService> logger,
        ICertificateService certificateService)
    {
        _logger = Guard.NotNull(logger);
        _certificateService = Guard.NotNull(certificateService);
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

    // Constant-time comparison of the client-supplied acknowledgement HMAC against the expected
    // value. The client sends its HMAC base64-encoded; we decode to RAW bytes and compare those
    // (PAIR-6 / RemEx-29e). Comparing the base64 TEXT instead would compare encoding artifacts,
    // and malformed base64 must fail closed (return false) rather than throw out of the verify
    // path. Mirrors the client side in PairingClient. expectedHmac is always 32 bytes; FixedTimeEquals
    // returns false on any length mismatch without leaking timing.
    private bool HmacMatches(byte[] expectedHmac, string clientHmacBase64)
    {
        byte[] clientHmac;
        try
        {
            clientHmac = Convert.FromBase64String(clientHmacBase64);
        }
        catch (FormatException)
        {
            _logger.LogWarning("Pairing verification failed: client acknowledgement HMAC was not valid base64.");
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(expectedHmac, clientHmac);
    }

    public async Task<PairingVerificationResult> VerifyClientHmacAndCaptureSecretAsync(
        string clientHmacBase64, CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            if (_activePin is null || !IsPairingActive)
            {
                _logger.LogWarning("Pairing verification attempted but no active session or session expired.");
                return new PairingVerificationResult(false, []);
            }

            if (_sessionKey is null)
            {
                _logger.LogWarning("Pairing verification attempted but session key not yet derived (client public key not received).");
                return new PairingVerificationResult(false, []);
            }

            // Compute expected HMAC: HMAC-SHA256(sessionKey, "ack:" + PIN)
            var ackMessage = Encoding.UTF8.GetBytes("ack:" + _activePin);
            var expectedHmac = HMACSHA256.HashData(_sessionKey, ackMessage);
            var match = HmacMatches(expectedHmac, clientHmacBase64);

            if (match)
            {
                // Capture a copy of the session key BEFORE CancelPairingCore zeroes it. This becomes
                // the per-client reconnect secret persisted by the registry — the client derives the
                // identical key during pairing, so no secret is ever transmitted over the wire.
                var reconnectSecret = (byte[])_sessionKey.Clone();
                _logger.LogInformation("Pairing verification successful; reconnect secret captured.");
                CancelPairingCore(); // Consume the session.
                return new PairingVerificationResult(true, reconnectSecret);
            }

            _failedHmacAttempts++;
            _logger.LogWarning(
                "Pairing verification failed — HMAC mismatch ({Attempts}/{Max}).",
                _failedHmacAttempts,
                MaxFailedHmacAttempts);

            if (_failedHmacAttempts >= MaxFailedHmacAttempts)
            {
                _logger.LogWarning(
                    "Maximum failed PIN attempts reached — burning the pairing session. A fresh PIN must be generated to retry.");
                CancelPairingCore();
            }

            return new PairingVerificationResult(false, []);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during pairing verification.");
            return new PairingVerificationResult(false, []);
        }
        finally
        {
            _lock.Release();
        }
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
            var match = HmacMatches(expectedHmac, clientHmacBase64);

            if (match)
            {
                _logger.LogInformation("Pairing verification successful.");
                CancelPairingCore(); // Consume the session
            }
            else
            {
                _failedHmacAttempts++;
                _logger.LogWarning(
                    "Pairing verification failed — HMAC mismatch ({Attempts}/{Max}).",
                    _failedHmacAttempts,
                    MaxFailedHmacAttempts);

                if (_failedHmacAttempts >= MaxFailedHmacAttempts)
                {
                    _logger.LogWarning(
                        "Maximum failed PIN attempts reached — burning the pairing session. A fresh PIN must be generated to retry.");
                    CancelPairingCore(); // Burns the session and forces a brand-new PIN.
                }
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
        _failedHmacAttempts = 0;
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
        _failedHmacAttempts = 0;

        _clientPublicKeyBase64 = null;

        // Never log the PIN value: the retained in-memory log buffer is a disclosure surface
        // (VULN-1, RemEx-s032.1). The PIN is surfaced to the user via the PinDisplayed event below.
        _logger.LogInformation("Pairing session started. PIN is displayed on the host screen (not logged). Expires at: {Expiry}.",
            DateTimeOffset.FromUnixTimeMilliseconds(_expiresAtUnixMs));

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
