using System;
using System.Threading;
using System.Threading.Tasks;

namespace Remex.Core.Services.Security;

public interface IPairingService
{
    Task<PairingState> StartPairingAsync(CancellationToken ct);
    Task<PairingState> GetOrStartPairingAsync(CancellationToken ct);
    Task<string> DeriveSessionKeyAsync(string clientPublicKeyBase64, CancellationToken ct);
    string GetActivePin();
    bool TryGetActivePinInfo(out string pin, out long expiresAtUnixMs);
    bool IsPairingActive { get; }
    Task<bool> VerifyClientHmacAsync(string clientHmacBase64, CancellationToken ct);
    void CancelPairing();

    /// <summary>
    /// Raised when a new pairing PIN becomes active. The desktop UI subscribes to this
    /// to show the PIN to the user so they can enter it on their phone.
    /// Args: (pin, expiresAtUnixMs).
    /// </summary>
    event Action<string, long>? PinDisplayed;

    /// <summary>
    /// Raised when an active PIN session ends (consumed, expired, or cancelled).
    /// </summary>
    event Action? PinCleared;
}

public sealed record PairingState(string HostPublicKeyBase64, string Pin, long ExpiresAtUnixMs);

/// <summary>
/// Result of a PIN-HMAC verification that also captures the reconnect secret on success.
/// <see cref="ReconnectSecret"/> is the ECDH/HKDF-derived session key (32 bytes) bound to the
/// paired client; it is empty when <see cref="Verified"/> is false.
/// </summary>
public sealed record PairingVerificationResult(bool Verified, byte[] ReconnectSecret);
